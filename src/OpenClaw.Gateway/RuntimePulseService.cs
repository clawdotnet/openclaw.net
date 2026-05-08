using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Sessions;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Mcp;

namespace OpenClaw.Gateway;

internal sealed class RuntimePulseService : BackgroundService
{
    private const string Component = "runtime_pulse";
    private const int MaxHeartbeatChars = 12_000;
    private const int MaxAlertCount = 20;
    private readonly GatewayConfig _config;
    private readonly GatewayStartupContext _startup;
    private readonly GatewayRuntimeHolder _runtimeHolder;
    private readonly RuntimeEventStore _events;
    private readonly RuntimeMetrics _metrics;
    private readonly ILogger<RuntimePulseService> _logger;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly Lock _stateGate = new();
    private readonly string _heartbeatPath;
    private readonly string _statePath;

    public RuntimePulseService(
        GatewayConfig config,
        GatewayStartupContext startup,
        GatewayRuntimeHolder runtimeHolder,
        RuntimeEventStore events,
        RuntimeMetrics metrics,
        ILogger<RuntimePulseService> logger)
    {
        _config = config;
        _startup = startup;
        _runtimeHolder = runtimeHolder;
        _events = events;
        _metrics = metrics;
        _logger = logger;

        var workspace = string.IsNullOrWhiteSpace(startup.WorkspacePath)
            ? Directory.GetCurrentDirectory()
            : startup.WorkspacePath!;
        _heartbeatPath = Path.Combine(Path.GetFullPath(workspace), "HEARTBEAT.md");

        var storagePath = Path.IsPathRooted(config.Memory.StoragePath)
            ? config.Memory.StoragePath
            : Path.GetFullPath(config.Memory.StoragePath);
        _statePath = Path.Combine(storagePath, "admin", "pulse-state.json");
    }

    public string HeartbeatPath => _heartbeatPath;
    public string StatePath => _statePath;

    public PulseStatusResponse GetStatus()
    {
        var normalized = Normalize(_config.Pulse);
        var state = LoadState();
        var heartbeat = ReadHeartbeatFile();
        return new PulseStatusResponse
        {
            Config = normalized,
            HeartbeatPath = _heartbeatPath,
            HeartbeatExists = heartbeat.Exists,
            HeartbeatEmpty = heartbeat.Exists && IsEffectivelyEmpty(heartbeat.Content),
            Enabled = normalized.Enabled && TryParseEvery(normalized.Every, out var interval) && interval > TimeSpan.Zero,
            Interval = normalized.Every,
            LastRunAtUtc = state.LastRunAtUtc,
            LastCompletedAtUtc = state.LastCompletedAtUtc,
            NextRunAtUtc = state.NextRunAtUtc,
            LastResult = state.LastResult,
            LastSkipReason = state.LastSkipReason,
            RecentAlertCount = state.RecentAlerts.Count,
            RecentOkCount = state.RecentOkCount,
            RecentAlerts = state.RecentAlerts,
            PendingManualText = state.PendingManualText
        };
    }

    public async Task<PulseRunResponse> RunNowAsync(string? text, CancellationToken ct)
    {
        AppendEvent(PulseEventActions.ManualWake, "Manual Runtime Pulse wake requested.", severity: "info");
        return await RunPulseAsync(manualText: text, scheduled: false, ct);
    }

    public PulseRunResponse EnqueueForNextPulse(string text)
    {
        var state = LoadState();
        state.PendingManualText = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        SaveState(state);
        AppendEvent(PulseEventActions.ManualWake, "Manual Runtime Pulse text queued for next scheduled pulse.", severity: "info");
        return new PulseRunResponse { Success = true, Outcome = "queued", MessagePreview = state.PendingManualText };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var config = Normalize(_config.Pulse);
            if (!TryParseEvery(config.Every, out var interval) || !config.Enabled || interval <= TimeSpan.Zero)
            {
                RecordSkip(PulseSkipReasons.Disabled, "Runtime Pulse disabled by configuration.");
                await DelayQuietly(TimeSpan.FromMinutes(1), stoppingToken);
                continue;
            }

            var state = LoadState();
            var now = DateTimeOffset.UtcNow;
            state.NextRunAtUtc ??= now.Add(interval);
            SaveState(state);

            var delay = state.NextRunAtUtc.Value - now;
            if (delay > TimeSpan.Zero)
                await DelayQuietly(delay, stoppingToken);
            if (stoppingToken.IsCancellationRequested)
                break;

            await RunPulseAsync(manualText: null, scheduled: true, stoppingToken);

            state = LoadState();
            state.NextRunAtUtc = DateTimeOffset.UtcNow.Add(interval);
            SaveState(state);
        }
    }

    private async Task<PulseRunResponse> RunPulseAsync(string? manualText, bool scheduled, CancellationToken ct)
    {
        var config = Normalize(_config.Pulse);
        if (!config.Enabled || !TryParseEvery(config.Every, out var interval) || interval <= TimeSpan.Zero)
            return RecordSkip(PulseSkipReasons.Disabled, "Runtime Pulse disabled by configuration.");

        if (config.Visibility is { ShowOk: false, ShowAlerts: false, UseIndicator: false })
            return RecordSkip(PulseSkipReasons.VisibilityDisabled, "Runtime Pulse visibility is fully disabled.");

        if (!IsWithinActiveHours(config.ActiveHours, DateTimeOffset.UtcNow))
            return RecordSkip(PulseSkipReasons.OutsideActiveHours, "Runtime Pulse skipped outside active hours.");

        if (!_runGate.Wait(0))
            return RecordSkip(PulseSkipReasons.Busy, "Runtime Pulse skipped because a prior pulse is still running.");

        try
        {
            var runtime = _runtimeHolder.Runtime;
            if (config.SkipWhenBusy && runtime.SessionLocks.Values.Any(static gate => gate.CurrentCount == 0))
                return RecordSkip(PulseSkipReasons.Busy, "Runtime Pulse skipped because runtime session lanes are busy.");

            var heartbeat = ReadHeartbeatFile();
            if (heartbeat.Exists && IsEffectivelyEmpty(heartbeat.Content))
                return RecordSkip(PulseSkipReasons.EmptyHeartbeatFile, "Runtime Pulse skipped because HEARTBEAT.md has no actionable content.");

            var state = LoadState();
            var tasks = heartbeat.Exists ? ParseTasks(heartbeat.Content) : [];
            var dueTasks = SelectDueTasks(tasks, state, DateTimeOffset.UtcNow);
            var freeform = heartbeat.Exists ? RemoveTasksBlock(heartbeat.Content) : "";
            if (heartbeat.Exists && dueTasks.Count == 0 && IsEffectivelyEmpty(freeform))
                return RecordSkip(PulseSkipReasons.EmptyHeartbeatFile, "Runtime Pulse skipped because HEARTBEAT.md only contains non-due tasks.");

            var pendingText = string.IsNullOrWhiteSpace(manualText) ? state.PendingManualText : manualText;
            var prompt = BuildPrompt(config, heartbeat.Content, dueTasks, pendingText);
            if (prompt.Length > MaxHeartbeatChars + config.Prompt.Length + 2_000)
                prompt = prompt[..(MaxHeartbeatChars + config.Prompt.Length + 2_000)];

            var sessionId = ResolveSessionId(config);
            if (string.IsNullOrWhiteSpace(sessionId))
                return RecordSkip(PulseSkipReasons.NoSession, "Runtime Pulse has no usable session id.");

            AppendEvent(PulseEventActions.RunStarted, "Runtime Pulse run started.", sessionId: sessionId, severity: "info");
            _metrics.IncrementPulseRuns();
            var sw = Stopwatch.StartNew();
            var session = await runtime.SessionManager.GetOrCreateByIdAsync(sessionId, "pulse", "runtime-pulse", ct);
            await using var sessionLock = await runtime.SessionManager.AcquireSessionLockAsync(session.Id, ct);

            List<ChatTurn>? isolatedHistory = null;
            string? originalModel = null;
            if (config.IsolatedSession || config.LightContext)
            {
                isolatedHistory = [.. session.History];
                session.History.Clear();
            }
            if (!string.IsNullOrWhiteSpace(config.Model))
            {
                originalModel = session.ModelOverride;
                session.ModelOverride = config.Model;
            }

            string output;
            long inputBefore = session.TotalInputTokens;
            long outputBefore = session.TotalOutputTokens;
            try
            {
                output = await runtime.AgentRuntime.RunAsync(session, prompt, ct);
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(config.Model))
                    session.ModelOverride = originalModel;
                if (isolatedHistory is not null)
                {
                    session.History.Clear();
                    session.History.AddRange(isolatedHistory);
                }
            }

            await runtime.SessionManager.PersistAsync(session, ct, sessionLockHeld: true);
            sw.Stop();
            _metrics.SetPulseLastRunDuration(sw.ElapsedMilliseconds);

            state.LastRunAtUtc = DateTimeOffset.UtcNow;
            state.LastCompletedAtUtc = DateTimeOffset.UtcNow;
            state.PendingManualText = null;
            foreach (var task in dueTasks)
                state.TaskLastRunUtc[task.Name] = DateTimeOffset.UtcNow;

            var ack = IsAck(output, config.AckToken, config.AckMaxChars);
            if (ack)
            {
                state.LastResult = "ok";
                state.LastSkipReason = null;
                state.RecentOkCount++;
                SaveState(state);
                _metrics.IncrementPulseOkSuppressed();
                AppendEvent(PulseEventActions.OkSuppressed, "Runtime Pulse returned HEARTBEAT_OK and visible delivery was suppressed.", sessionId: session.Id, severity: "info");
                AppendEvent(PulseEventActions.RunCompleted, "Runtime Pulse run completed with no alert.", sessionId: session.Id, severity: "info");
                return new PulseRunResponse { Success = true, Outcome = "ok", SessionId = session.Id };
            }

            var preview = Truncate((output ?? "").Trim(), 500);
            state.LastResult = "alert";
            state.LastSkipReason = null;
            state.RecentAlerts.Insert(0, new PulseAlertDto { TimestampUtc = DateTimeOffset.UtcNow, Text = preview, Severity = "warning" });
            if (state.RecentAlerts.Count > MaxAlertCount)
                state.RecentAlerts.RemoveRange(MaxAlertCount, state.RecentAlerts.Count - MaxAlertCount);
            SaveState(state);
            _metrics.IncrementPulseAlerts();
            AppendEvent(PulseEventActions.Alert, preview, sessionId: session.Id, severity: "warning");
            if (string.Equals(config.Target, "none", StringComparison.OrdinalIgnoreCase) || !config.Visibility.ShowAlerts)
            {
                AppendEvent(PulseEventActions.DeliverySkipped, "Runtime Pulse alert retained for operator visibility; external delivery skipped.", sessionId: session.Id, severity: "info");
            }
            else
            {
                AppendEvent(PulseEventActions.DeliverySkipped, $"Runtime Pulse alert retained for operator visibility; delivery target '{config.Target}' is not wired in this vertical slice. reason={PulseSkipReasons.DeliveryBlocked}", sessionId: session.Id, severity: "warning");
            }
            AppendEvent(PulseEventActions.RunCompleted, "Runtime Pulse run completed with alert.", sessionId: session.Id, severity: "warning");

            _logger.LogInformation(
                "Runtime Pulse completed: outcome=alert inputTokens={InputTokens} outputTokens={OutputTokens}",
                session.TotalInputTokens - inputBefore,
                session.TotalOutputTokens - outputBefore);
            return new PulseRunResponse { Success = true, Outcome = "alert", SessionId = session.Id, MessagePreview = preview };
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("provider", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("model", StringComparison.OrdinalIgnoreCase))
        {
            _metrics.IncrementPulseErrors();
            AppendEvent(PulseEventActions.Error, ex.Message, severity: "warning");
            return RecordSkip(PulseSkipReasons.ModelUnavailable, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _metrics.IncrementPulseErrors();
            AppendEvent(PulseEventActions.Error, ex.Message, severity: "warning");
            _logger.LogWarning(ex, "Runtime Pulse failed.");
            return RecordSkip(PulseSkipReasons.Error, ex.Message);
        }
        finally
        {
            _runGate.Release();
        }
    }

    private static string BuildPrompt(PulseConfig config, string heartbeat, IReadOnlyList<PulseTaskDefinition> dueTasks, string? manualText)
    {
        var sb = new StringBuilder();
        sb.AppendLine(config.Prompt);
        sb.AppendLine();
        sb.AppendLine("Runtime Pulse rules:");
        sb.AppendLine("- Observe, summarize, notify, and propose only.");
        sb.AppendLine("- Do not silently mutate durable runtime state.");
        sb.AppendLine("- Durable learning/profile/skill/automation changes must remain review-first proposals.");
        sb.AppendLine("- If nothing needs attention, reply HEARTBEAT_OK.");

        if (!string.IsNullOrWhiteSpace(manualText))
        {
            sb.AppendLine();
            sb.AppendLine("Manual wake text:");
            sb.AppendLine(manualText.Trim());
        }

        if (!string.IsNullOrWhiteSpace(heartbeat))
        {
            sb.AppendLine();
            sb.AppendLine("HEARTBEAT.md:");
            sb.AppendLine(Truncate(heartbeat, MaxHeartbeatChars));
        }

        if (dueTasks.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Due heartbeat tasks:");
            foreach (var task in dueTasks)
                sb.AppendLine($"- {task.Name}: {task.Prompt}");
        }

        return sb.ToString();
    }

    internal static PulseConfig Normalize(PulseConfig? config)
        => new()
        {
            Enabled = config?.Enabled ?? true,
            Every = string.IsNullOrWhiteSpace(config?.Every) ? "30m" : config!.Every.Trim(),
            Model = string.IsNullOrWhiteSpace(config?.Model) ? null : config!.Model.Trim(),
            Prompt = string.IsNullOrWhiteSpace(config?.Prompt) ? PulseDefaults.Prompt : config!.Prompt.Trim(),
            AckToken = string.IsNullOrWhiteSpace(config?.AckToken) ? PulseDefaults.AckToken : config!.AckToken.Trim(),
            AckMaxChars = Math.Clamp(config?.AckMaxChars ?? 300, 1, 4000),
            Target = string.IsNullOrWhiteSpace(config?.Target) ? "none" : config!.Target.Trim(),
            To = string.IsNullOrWhiteSpace(config?.To) ? null : config!.To.Trim(),
            AccountId = string.IsNullOrWhiteSpace(config?.AccountId) ? null : config!.AccountId.Trim(),
            DirectPolicy = string.Equals(config?.DirectPolicy, "block", StringComparison.OrdinalIgnoreCase) ? "block" : "allow",
            IncludeReasoning = config?.IncludeReasoning ?? false,
            LightContext = config?.LightContext ?? false,
            IsolatedSession = config?.IsolatedSession ?? false,
            SkipWhenBusy = config?.SkipWhenBusy ?? true,
            SuppressToolErrorWarnings = config?.SuppressToolErrorWarnings ?? true,
            Session = string.IsNullOrWhiteSpace(config?.Session) ? "main" : config!.Session.Trim(),
            ActiveHours = config?.ActiveHours,
            Visibility = config?.Visibility ?? new PulseVisibilityConfig()
        };

    internal static bool TryParseEvery(string? value, out TimeSpan interval)
    {
        interval = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (TimeSpan.TryParse(trimmed, CultureInfo.InvariantCulture, out interval))
            return true;

        if (trimmed.Length < 2)
            return false;

        var suffix = trimmed[^1];
        var numberText = trimmed[..^1];
        if (!double.TryParse(numberText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
            return false;

        interval = suffix switch
        {
            's' or 'S' => TimeSpan.FromSeconds(number),
            'm' or 'M' => TimeSpan.FromMinutes(number),
            'h' or 'H' => TimeSpan.FromHours(number),
            'd' or 'D' => TimeSpan.FromDays(number),
            _ => TimeSpan.Zero
        };
        return interval >= TimeSpan.Zero;
    }

    internal static bool IsAck(string? output, string ackToken, int maxChars)
    {
        var text = (output ?? "").Trim();
        if (text.Length == 0)
            return false;
        if (string.Equals(text, ackToken, StringComparison.Ordinal))
            return true;
        if (text.Length > maxChars)
            return false;
        if (text.StartsWith(ackToken, StringComparison.Ordinal))
            return true;
        if (text.EndsWith(ackToken, StringComparison.Ordinal))
            return true;
        return false;
    }

    internal static bool IsEffectivelyEmpty(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return true;

        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("<!--", StringComparison.Ordinal) || line.StartsWith('#'))
                continue;
            if (string.Equals(line, "tasks:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (line.StartsWith("- name:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("interval:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("prompt:", StringComparison.OrdinalIgnoreCase))
            {
                if (line.Contains("prompt:", StringComparison.OrdinalIgnoreCase) && ExtractValue(line["prompt:".Length..]).Length > 0)
                    return false;
                continue;
            }
            return false;
        }

        return true;
    }

    internal static IReadOnlyList<PulseTaskDefinition> ParseTasks(string markdown)
    {
        var tasks = new List<PulseTaskDefinition>();
        var inTasks = false;
        string? name = null;
        string? interval = null;
        string? prompt = null;

        void Flush()
        {
            if (!string.IsNullOrWhiteSpace(name) &&
                !string.IsNullOrWhiteSpace(interval) &&
                !string.IsNullOrWhiteSpace(prompt))
            {
                tasks.Add(new PulseTaskDefinition
                {
                    Name = name!.Trim(),
                    Interval = interval!.Trim(),
                    Prompt = prompt!.Trim()
                });
            }
            name = null;
            interval = null;
            prompt = null;
        }

        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.Trim();
            if (!inTasks)
            {
                inTasks = string.Equals(line, "tasks:", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (line.StartsWith('#'))
            {
                Flush();
                break;
            }

            if (line.StartsWith("- name:", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                name = ExtractValue(line["- name:".Length..]);
                continue;
            }
            if (line.StartsWith("interval:", StringComparison.OrdinalIgnoreCase))
            {
                interval = ExtractValue(line["interval:".Length..]);
                continue;
            }
            if (line.StartsWith("prompt:", StringComparison.OrdinalIgnoreCase))
            {
                prompt = ExtractValue(line["prompt:".Length..]);
                continue;
            }
        }

        Flush();
        return tasks;
    }

    internal static string RemoveTasksBlock(string markdown)
    {
        var sb = new StringBuilder();
        var inTasks = false;
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.Trim();
            if (!inTasks && string.Equals(line, "tasks:", StringComparison.OrdinalIgnoreCase))
            {
                inTasks = true;
                continue;
            }
            if (inTasks)
            {
                if (line.StartsWith('#'))
                {
                    inTasks = false;
                    sb.AppendLine(raw);
                }
                continue;
            }
            sb.AppendLine(raw);
        }
        return sb.ToString();
    }

    internal static IReadOnlyList<PulseTaskDefinition> SelectDueTasks(IReadOnlyList<PulseTaskDefinition> tasks, PulseState state, DateTimeOffset nowUtc)
    {
        var due = new List<PulseTaskDefinition>();
        foreach (var task in tasks)
        {
            if (!TryParseEvery(task.Interval, out var interval) || interval <= TimeSpan.Zero)
                continue;
            if (!state.TaskLastRunUtc.TryGetValue(task.Name, out var lastRun) || nowUtc - lastRun >= interval)
                due.Add(task);
        }
        return due;
    }

    private static string ExtractValue(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 &&
            ((trimmed[0] == '"' && trimmed[^1] == '"') || (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            return trimmed[1..^1];
        }
        return trimmed;
    }

    private static bool IsWithinActiveHours(PulseActiveHoursConfig? activeHours, DateTimeOffset nowUtc)
    {
        if (activeHours is null)
            return true;
        if (!TryParseClock(activeHours.Start, out var start) || !TryParseClock(activeHours.End, out var end))
            return true;

        var zone = ResolveTimeZone(activeHours.Timezone);
        var local = TimeZoneInfo.ConvertTime(nowUtc, zone).TimeOfDay;
        if (end == TimeSpan.FromHours(24))
            end = TimeSpan.FromDays(1);
        return start <= end
            ? local >= start && local < end
            : local >= start || local < end;
    }

    private static bool TryParseClock(string? value, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        if (string.Equals(value, "24:00", StringComparison.Ordinal))
        {
            time = TimeSpan.FromHours(24);
            return true;
        }
        return TimeSpan.TryParseExact(value, "hh\\:mm", CultureInfo.InvariantCulture, out time);
    }

    private static TimeZoneInfo ResolveTimeZone(string? timezone)
    {
        if (string.IsNullOrWhiteSpace(timezone) ||
            string.Equals(timezone, "local", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(timezone, "user", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch
        {
            return TimeZoneInfo.Local;
        }
    }

    private string ResolveSessionId(PulseConfig config)
    {
        if (config.IsolatedSession)
            return $"pulse:isolated:{Guid.NewGuid():N}";
        if (string.Equals(config.Session, "main", StringComparison.OrdinalIgnoreCase))
            return "main";
        return config.Session;
    }

    private (bool Exists, string Content) ReadHeartbeatFile()
    {
        try
        {
            if (!File.Exists(_heartbeatPath))
                return (false, "");
            var text = File.ReadAllText(_heartbeatPath);
            return (true, text.Length <= MaxHeartbeatChars ? text : text[..MaxHeartbeatChars]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read HEARTBEAT.md from {Path}", _heartbeatPath);
            return (false, "");
        }
    }

    private PulseRunResponse RecordSkip(string reason, string summary)
    {
        var state = LoadState();
        var shouldEmit = !string.Equals(state.LastSkipReason, reason, StringComparison.Ordinal);
        state.LastSkipReason = reason;
        state.LastResult = "skipped";
        SaveState(state);
        _metrics.IncrementPulseSkips();
        if (shouldEmit)
            AppendEvent(PulseEventActions.Skipped, $"{summary} reason={reason}", severity: "info");
        return new PulseRunResponse { Success = false, Outcome = "skipped", SkipReason = reason, MessagePreview = Truncate(summary, 500) };
    }

    private PulseState LoadState()
    {
        lock (_stateGate)
        {
            try
            {
                if (!File.Exists(_statePath))
                    return new PulseState();
                var json = File.ReadAllText(_statePath);
                return JsonSerializer.Deserialize(json, CoreJsonContext.Default.PulseState) ?? new PulseState();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load Runtime Pulse state from {Path}", _statePath);
                return new PulseState();
            }
        }
    }

    private void SaveState(PulseState state)
    {
        lock (_stateGate)
        {
            var directory = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(state, CoreJsonContext.Default.PulseState);
            var temp = $"{_statePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _statePath, overwrite: true);
        }
    }

    private void AppendEvent(string action, string summary, string? sessionId = null, string severity = "info")
    {
        _events.Append(new RuntimeEventEntry
        {
            Id = $"evt_{Guid.NewGuid():N}"[..20],
            Component = Component,
            Action = action,
            Summary = summary,
            SessionId = sessionId,
            ChannelId = "pulse",
            SenderId = "runtime-pulse",
            Severity = severity
        });
    }

    private static async Task DelayQuietly(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private static string Truncate(string? value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var trimmed = value.Trim();
        return trimmed.Length <= maxChars ? trimmed : trimmed[..maxChars];
    }
}

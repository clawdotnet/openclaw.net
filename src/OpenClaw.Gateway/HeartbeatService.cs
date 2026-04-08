using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Sessions;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway;

internal sealed class HeartbeatService
{
    private const string ConfigFileName = "heartbeat.json";
    private const string StatusFileName = "heartbeat-status.json";
    private const string ManagedJobName = "heartbeat.default";
    private const string ManagedSessionId = "heartbeat:default";
    private const int MaxRenderedChars = 12_000;
    private const int BaselineWrapperChars = 450;
    private const int EstimatedOkOutputTokens = 12;
    private const int EstimatedAlertOutputTokens = 180;
    private static readonly Regex UrlRegex = new(@"https?://[^\s""'<>]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EmailRegex = new(@"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PathRegex = new(@"(?:~|\.{1,2}|/)[A-Za-z0-9._/\-]+", RegexOptions.Compiled);
    private static readonly TemplateDefinition[] TemplateCatalog =
    [
        new(
            "email_check",
            "Email Check",
            "Check email for important senders, subjects, or inbox changes.",
            ["email", "inbox_zero"],
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["sender"] = ["contains", "equals", "any_of"],
                ["subject"] = ["contains", "equals", "any_of"],
                ["body"] = ["contains"],
                ["is_vip"] = ["is_true"]
            }),
        new(
            "calendar_alert",
            "Calendar Alert",
            "Check for upcoming meetings, calendar changes, or urgent events.",
            ["calendar"],
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["title"] = ["contains", "equals", "any_of"],
                ["location"] = ["contains", "equals"],
                ["body"] = ["contains"]
            }),
        new(
            "website_monitoring",
            "Website Monitoring",
            "Check a website or page for meaningful updates.",
            ["web_fetch", "web_search", "browser"],
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["url"] = ["contains", "equals"],
                ["title"] = ["contains", "equals"],
                ["body"] = ["contains"]
            }),
        new(
            "api_health_check",
            "API Health Check",
            "Check an API endpoint for outages, degraded responses, or changed payloads.",
            ["web_fetch"],
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["url"] = ["contains", "equals"],
                ["status_code"] = ["equals", "any_of"],
                ["body"] = ["contains"]
            }),
        new(
            "file_folder_watch",
            "File/Folder Watch",
            "Check a file or folder for new alerts, new files, or changed contents.",
            ["read_file"],
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["path"] = ["contains", "equals"],
                ["file_name"] = ["contains", "equals", "any_of"],
                ["content"] = ["contains"]
            }),
        new(
            "custom",
            "Custom Task",
            "Free-form heartbeat task rendered in the same deterministic structure.",
            [],
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["sender"] = ["contains", "equals", "any_of"],
                ["subject"] = ["contains", "equals", "any_of"],
                ["body"] = ["contains"],
                ["title"] = ["contains", "equals", "any_of"],
                ["url"] = ["contains", "equals"],
                ["path"] = ["contains", "equals"],
                ["content"] = ["contains"],
                ["status_code"] = ["equals", "any_of"],
                ["is_vip"] = ["is_true"]
            })
    ];

    private readonly GatewayConfig _config;
    private readonly IMemoryStore _memoryStore;
    private readonly SessionManager _sessionManager;
    private readonly ILogger<HeartbeatService> _logger;
    private readonly string _configPath;
    private readonly string _heartbeatPath;
    private readonly string _memoryMarkdownPath;
    private readonly string _statusPath;
    private readonly Lock _gate = new();
    private HeartbeatConfigDto? _cachedConfig;
    private HeartbeatRunStatusDto? _cachedStatus;

    public HeartbeatService(
        GatewayConfig config,
        IMemoryStore memoryStore,
        SessionManager sessionManager,
        ILogger<HeartbeatService> logger)
    {
        _config = config;
        _memoryStore = memoryStore;
        _sessionManager = sessionManager;
        _logger = logger;

        var storagePath = config.Memory.StoragePath;
        if (!Path.IsPathRooted(storagePath))
            storagePath = Path.GetFullPath(storagePath);

        _configPath = Path.Combine(storagePath, "admin", ConfigFileName);
        _statusPath = Path.Combine(storagePath, "admin", StatusFileName);
        _heartbeatPath = Path.Combine(storagePath, "HEARTBEAT.md");
        _memoryMarkdownPath = Path.Combine(storagePath, "memory.md");
    }

    public string ConfigPath => _configPath;
    public string HeartbeatPath => _heartbeatPath;
    public string MemoryMarkdownPath => _memoryMarkdownPath;
    public string StatusPath => _statusPath;

    public HeartbeatConfigDto LoadConfig()
    {
        lock (_gate)
        {
            _cachedConfig ??= LoadConfigUnsafe();
            return _cachedConfig;
        }
    }

    public HeartbeatRunStatusDto? LoadStatus()
    {
        lock (_gate)
        {
            _cachedStatus ??= LoadStatusUnsafe();
            return _cachedStatus;
        }
    }

    public HeartbeatConfigDto SaveConfig(HeartbeatConfigDto config)
    {
        var normalized = Normalize(config);
        var rendered = RenderMarkdown(normalized);

        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
            WriteJsonAtomic(_configPath, normalized, CoreJsonContext.Default.HeartbeatConfigDto);
            WriteTextAtomic(_heartbeatPath, rendered);
            _cachedConfig = normalized;
            return normalized;
        }
    }

    public async ValueTask<HeartbeatPreviewResponse> BuildPreviewAsync(HeartbeatConfigDto config, GatewayAppRuntime runtime, CancellationToken ct)
    {
        var normalized = Normalize(config);
        var issues = Validate(normalized, runtime);
        var markdown = RenderMarkdown(normalized);
        var promptPreview = BuildManagedPrompt(normalized, markdown);
        var templates = BuildTemplates(runtime);
        var suggestions = await BuildSuggestionsAsync(runtime, ct);
        var estimate = EstimateCost(normalized, runtime);

        return new HeartbeatPreviewResponse
        {
            Config = normalized,
            ConfigPath = _configPath,
            HeartbeatPath = _heartbeatPath,
            MemoryMarkdownPath = _memoryMarkdownPath,
            HeartbeatMarkdown = markdown,
            PromptPreview = promptPreview,
            DriftDetected = DetectDrift(markdown),
            ManagedJobActive = issues.All(issue => !string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase)) && normalized.Enabled,
            Issues = issues,
            AvailableTemplates = templates,
            Suggestions = suggestions,
            CostEstimate = estimate
        };
    }

    public async ValueTask<HeartbeatStatusResponse> BuildStatusAsync(GatewayAppRuntime runtime, CancellationToken ct)
    {
        var config = LoadConfig();
        var issues = Validate(config, runtime);
        var templates = BuildTemplates(runtime);
        var suggestions = await BuildSuggestionsAsync(runtime, ct);
        var estimate = EstimateCost(config, runtime);

        return new HeartbeatStatusResponse
        {
            Config = config,
            ConfigPath = _configPath,
            HeartbeatPath = _heartbeatPath,
            MemoryMarkdownPath = _memoryMarkdownPath,
            ConfigExists = File.Exists(_configPath),
            HeartbeatExists = File.Exists(_heartbeatPath),
            DriftDetected = DetectDrift(RenderMarkdown(config)),
            LastRun = LoadStatus(),
            Issues = issues,
            AvailableTemplates = templates,
            Suggestions = suggestions,
            CostEstimate = estimate
        };
    }

    public CronJobConfig? BuildManagedJob()
    {
        var config = LoadConfig();
        if (!config.Enabled)
            return null;

        var validationErrors = ValidateForJob(config);
        if (validationErrors.Count > 0)
        {
            _logger.LogWarning("Managed heartbeat job is disabled by validation errors: {Errors}", string.Join("; ", validationErrors.Select(static item => item.Message)));
            return null;
        }

        var markdown = RenderMarkdown(config);
        return new CronJobConfig
        {
            Name = ManagedJobName,
            CronExpression = config.CronExpression,
            Prompt = BuildManagedPrompt(config, markdown),
            RunOnStartup = false,
            SessionId = ManagedSessionId,
            ChannelId = config.DeliveryChannelId,
            RecipientId = string.IsNullOrWhiteSpace(config.DeliveryRecipientId) ? ManagedSessionId : config.DeliveryRecipientId,
            Subject = string.IsNullOrWhiteSpace(config.DeliverySubject) ? "OpenClaw Heartbeat" : config.DeliverySubject,
            Timezone = config.Timezone
        };
    }

    public bool IsManagedHeartbeatJob(string? jobName)
        => string.Equals(jobName, ManagedJobName, StringComparison.Ordinal);

    public bool ShouldSuppressResult(string? jobName, string? responseText)
        => IsManagedHeartbeatJob(jobName)
            && string.Equals((responseText ?? "").Trim(), "HEARTBEAT_OK", StringComparison.Ordinal);

    public void RecordResult(Session session, string responseText, bool suppressed, long inputTokenDelta, long outputTokenDelta)
    {
        var preview = string.IsNullOrWhiteSpace(responseText)
            ? null
            : Truncate(responseText.Trim(), 400);

        var status = new HeartbeatRunStatusDto
        {
            Outcome = suppressed ? "ok" : "alert",
            LastRunAtUtc = DateTimeOffset.UtcNow,
            LastDeliveredAtUtc = null,
            DeliverySuppressed = suppressed,
            InputTokens = Math.Max(0, inputTokenDelta),
            OutputTokens = Math.Max(0, outputTokenDelta),
            SessionId = session.Id,
            MessagePreview = preview
        };

        SaveStatus(status);
    }

    public void RecordDeliverySucceeded(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        lock (_gate)
        {
            _cachedStatus ??= LoadStatusUnsafe();
            if (_cachedStatus is null ||
                !string.Equals(_cachedStatus.SessionId, sessionId, StringComparison.Ordinal) ||
                _cachedStatus.DeliverySuppressed ||
                !string.Equals(_cachedStatus.Outcome, "alert", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var updated = new HeartbeatRunStatusDto
            {
                Outcome = _cachedStatus.Outcome,
                LastRunAtUtc = _cachedStatus.LastRunAtUtc,
                LastDeliveredAtUtc = DateTimeOffset.UtcNow,
                DeliverySuppressed = _cachedStatus.DeliverySuppressed,
                InputTokens = _cachedStatus.InputTokens,
                OutputTokens = _cachedStatus.OutputTokens,
                SessionId = _cachedStatus.SessionId,
                MessagePreview = _cachedStatus.MessagePreview
            };

            WriteJsonAtomic(_statusPath, updated, CoreJsonContext.Default.HeartbeatRunStatusDto);
            _cachedStatus = updated;
        }
    }

    public void RecordError(Session? session, Exception ex, long inputTokenDelta = 0, long outputTokenDelta = 0)
    {
        var status = new HeartbeatRunStatusDto
        {
            Outcome = "error",
            LastRunAtUtc = DateTimeOffset.UtcNow,
            LastDeliveredAtUtc = null,
            DeliverySuppressed = true,
            InputTokens = Math.Max(0, inputTokenDelta),
            OutputTokens = Math.Max(0, outputTokenDelta),
            SessionId = session?.Id,
            MessagePreview = Truncate(ex.Message, 400)
        };

        SaveStatus(status);
    }

    public string RenderMarkdown(HeartbeatConfigDto config)
    {
        var normalized = Normalize(config);
        var canonicalJson = JsonSerializer.Serialize(normalized, CoreJsonContext.Default.HeartbeatConfigDto);
        var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson))).ToLowerInvariant();
        var sb = new StringBuilder(capacity: Math.Max(1_024, normalized.Tasks.Count * 280));

        sb.AppendLine("<!-- managed_by: openclaw_heartbeat_wizard -->");
        sb.AppendLine($"<!-- source_path: {_configPath} -->");
        sb.AppendLine($"<!-- source_hash: {sourceHash} -->");
        sb.AppendLine();
        sb.AppendLine("# HEARTBEAT");
        sb.AppendLine();
        sb.AppendLine("This file is generated by OpenClaw's managed heartbeat configuration.");
        sb.AppendLine("Manual edits are not preserved and may be overwritten.");
        sb.AppendLine();
        sb.AppendLine("## Runtime");
        sb.AppendLine($"- absolute_path: {_heartbeatPath}");
        sb.AppendLine($"- schedule: {normalized.CronExpression}");
        sb.AppendLine($"- timezone: {normalized.Timezone ?? "UTC"}");
        sb.AppendLine($"- delivery_channel: {normalized.DeliveryChannelId}");
        sb.AppendLine($"- delivery_recipient: {normalized.DeliveryRecipientId ?? "(none)"}");
        sb.AppendLine($"- model: {normalized.ModelId ?? _config.Llm.Model}");
        sb.AppendLine();
        sb.AppendLine("## Contract");
        sb.AppendLine("- If there is nothing actionable for the user, the assistant must return exactly HEARTBEAT_OK.");
        sb.AppendLine("- If there is something actionable, the assistant must return only a terse alert summary.");
        sb.AppendLine("- Prefer high-signal findings and avoid repeating unchanged low-priority items.");
        sb.AppendLine();
        sb.AppendLine("## Tasks");

        if (normalized.Tasks.Count == 0)
        {
            sb.AppendLine("- No managed tasks configured yet.");
        }
        else
        {
            var taskNumber = 1;
            foreach (var task in normalized.Tasks.Where(static task => task.Enabled))
            {
                var label = ResolveTemplate(task.TemplateKey).Label;
                sb.AppendLine($"{taskNumber}. [{label}] {task.Title}");
                if (!string.IsNullOrWhiteSpace(task.Target))
                    sb.AppendLine($"   - target: {task.Target}");
                sb.AppendLine($"   - priority: {task.Priority}");
                sb.AppendLine($"   - instruction: {BuildTaskInstruction(task)}");
                if (task.Conditions.Count > 0)
                {
                    sb.AppendLine($"   - notify_when: {task.ConditionMode.ToUpperInvariant()} of");
                    foreach (var condition in task.Conditions)
                        sb.AppendLine($"     - {FormatCondition(condition)}");
                }
                taskNumber++;
            }
        }

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    public string BuildManagedPrompt(HeartbeatConfigDto config, string markdown)
    {
        var recipient = string.IsNullOrWhiteSpace(config.DeliveryRecipientId) ? "configured recipient" : config.DeliveryRecipientId;
        return
            $"""
            You are executing the OpenClaw managed heartbeat.
            Work only from the generated heartbeat specification below.
            Prioritize actionable findings, avoid noise, and only report items that truly need the user's attention.
            If there is nothing actionable, respond with exactly HEARTBEAT_OK and nothing else.
            If there is something actionable, respond with a terse alert summary suitable for channel "{config.DeliveryChannelId}" and recipient "{recipient}".
            Keep alert output concise and high signal.

            {markdown}
            """;
    }

    private void SaveStatus(HeartbeatRunStatusDto status)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statusPath)!);
            WriteJsonAtomic(_statusPath, status, CoreJsonContext.Default.HeartbeatRunStatusDto);
            _cachedStatus = status;
        }
    }

    private HeartbeatConfigDto LoadConfigUnsafe()
    {
        try
        {
            if (!File.Exists(_configPath))
                return DefaultConfig();

            var json = File.ReadAllText(_configPath);
            return Normalize(JsonSerializer.Deserialize(json, CoreJsonContext.Default.HeartbeatConfigDto) ?? DefaultConfig());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load heartbeat config from {Path}", _configPath);
            return DefaultConfig();
        }
    }

    private HeartbeatRunStatusDto? LoadStatusUnsafe()
    {
        try
        {
            if (!File.Exists(_statusPath))
                return null;

            var json = File.ReadAllText(_statusPath);
            return JsonSerializer.Deserialize(json, CoreJsonContext.Default.HeartbeatRunStatusDto);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load heartbeat status from {Path}", _statusPath);
            return null;
        }
    }

    private HeartbeatConfigDto DefaultConfig()
        => new()
        {
            Enabled = false,
            CronExpression = "@hourly",
            Timezone = "UTC",
            DeliveryChannelId = "cron",
            DeliverySubject = "OpenClaw Heartbeat",
            Tasks = []
        };

    private static HeartbeatConfigDto Normalize(HeartbeatConfigDto? config)
    {
        var tasks = (config?.Tasks ?? [])
            .Select(NormalizeTask)
            .ToArray();

        return new HeartbeatConfigDto
        {
            Enabled = config?.Enabled ?? false,
            CronExpression = string.IsNullOrWhiteSpace(config?.CronExpression) ? "@hourly" : config!.CronExpression.Trim(),
            Timezone = string.IsNullOrWhiteSpace(config?.Timezone) ? "UTC" : config!.Timezone.Trim(),
            DeliveryChannelId = string.IsNullOrWhiteSpace(config?.DeliveryChannelId) ? "cron" : config!.DeliveryChannelId.Trim().ToLowerInvariant(),
            DeliveryRecipientId = string.IsNullOrWhiteSpace(config?.DeliveryRecipientId) ? null : config!.DeliveryRecipientId.Trim(),
            DeliverySubject = string.IsNullOrWhiteSpace(config?.DeliverySubject) ? "OpenClaw Heartbeat" : config!.DeliverySubject.Trim(),
            ModelId = string.IsNullOrWhiteSpace(config?.ModelId) ? null : config!.ModelId.Trim(),
            Tasks = tasks
        };
    }

    private static HeartbeatTaskDto NormalizeTask(HeartbeatTaskDto task)
    {
        var conditions = (task.Conditions ?? [])
            .Select(static condition => new HeartbeatConditionDto
            {
                Field = (condition.Field ?? "").Trim().ToLowerInvariant(),
                Operator = (condition.Operator ?? "").Trim().ToLowerInvariant(),
                Values = (condition.Values ?? [])
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .Select(static value => value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            })
            .ToArray();

        return new HeartbeatTaskDto
        {
            Id = string.IsNullOrWhiteSpace(task.Id) ? $"task-{Guid.NewGuid():N}"[..12] : task.Id.Trim(),
            TemplateKey = string.IsNullOrWhiteSpace(task.TemplateKey) ? "custom" : task.TemplateKey.Trim().ToLowerInvariant(),
            Title = string.IsNullOrWhiteSpace(task.Title) ? "Untitled task" : task.Title.Trim(),
            Target = string.IsNullOrWhiteSpace(task.Target) ? null : task.Target.Trim(),
            Instruction = string.IsNullOrWhiteSpace(task.Instruction) ? null : task.Instruction.Trim(),
            Priority = string.IsNullOrWhiteSpace(task.Priority) ? "normal" : task.Priority.Trim().ToLowerInvariant(),
            Enabled = task.Enabled,
            ConditionMode = string.Equals(task.ConditionMode, "or", StringComparison.OrdinalIgnoreCase) ? "or" : "and",
            Conditions = conditions
        };
    }

    private IReadOnlyList<HeartbeatValidationIssueDto> Validate(HeartbeatConfigDto config, GatewayAppRuntime runtime)
    {
        var issues = new List<HeartbeatValidationIssueDto>();

        if (!config.Enabled)
            return issues;

        issues.AddRange(ValidateForJob(config));

        if (config.Tasks.Count == 0 || config.Tasks.All(static task => !task.Enabled))
        {
            issues.Add(new HeartbeatValidationIssueDto
            {
                Severity = "error",
                Code = "no_tasks",
                Message = "At least one enabled heartbeat task is required."
            });
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in config.Tasks)
        {
            if (!ids.Add(task.Id))
            {
                issues.Add(new HeartbeatValidationIssueDto
                {
                    Severity = "error",
                    Code = "duplicate_task_id",
                    Message = $"Task id '{task.Id}' is duplicated.",
                    TaskId = task.Id
                });
            }

            var template = ResolveTemplate(task.TemplateKey);
            if (!TemplateCatalog.Any(item => string.Equals(item.Key, task.TemplateKey, StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add(new HeartbeatValidationIssueDto
                {
                    Severity = "error",
                    Code = "unknown_template",
                    Message = $"Task '{task.Title}' uses unsupported template '{task.TemplateKey}'.",
                    TaskId = task.Id
                });
                continue;
            }

            if (string.IsNullOrWhiteSpace(task.Title))
            {
                issues.Add(new HeartbeatValidationIssueDto
                {
                    Severity = "error",
                    Code = "missing_title",
                    Message = "Every task needs a title.",
                    TaskId = task.Id
                });
            }

            if (task.TemplateKey is not "custom" && string.IsNullOrWhiteSpace(task.Target))
            {
                issues.Add(new HeartbeatValidationIssueDto
                {
                    Severity = "error",
                    Code = "missing_target",
                    Message = $"Task '{task.Title}' requires a target.",
                    TaskId = task.Id
                });
            }

            if (task.Priority is not ("low" or "normal" or "high"))
            {
                issues.Add(new HeartbeatValidationIssueDto
                {
                    Severity = "error",
                    Code = "invalid_priority",
                    Message = $"Task '{task.Title}' has invalid priority '{task.Priority}'.",
                    TaskId = task.Id
                });
            }

            if (task.ConditionMode is not ("and" or "or"))
            {
                issues.Add(new HeartbeatValidationIssueDto
                {
                    Severity = "error",
                    Code = "invalid_condition_mode",
                    Message = $"Task '{task.Title}' has invalid condition mode '{task.ConditionMode}'.",
                    TaskId = task.Id
                });
            }

            foreach (var condition in task.Conditions)
            {
                ValidateCondition(template, task, condition, issues);
            }

            foreach (var warning in BuildApprovalWarnings(task, runtime))
                issues.Add(warning);
        }

        var rendered = RenderMarkdown(config);
        if (rendered.Length > MaxRenderedChars)
        {
            issues.Add(new HeartbeatValidationIssueDto
            {
                Severity = "error",
                Code = "render_too_large",
                Message = $"Rendered HEARTBEAT.md is too large ({rendered.Length} chars). Reduce task complexity to stay under {MaxRenderedChars} chars."
            });
        }

        return issues;
    }

    private IReadOnlyList<HeartbeatValidationIssueDto> ValidateForJob(HeartbeatConfigDto config)
    {
        var issues = new List<HeartbeatValidationIssueDto>();

        if (!IsValidCronExpression(config.CronExpression))
        {
            issues.Add(new HeartbeatValidationIssueDto
            {
                Severity = "error",
                Code = "invalid_cron",
                Message = $"Heartbeat cron expression '{config.CronExpression}' is invalid."
            });
        }

        if (!string.IsNullOrWhiteSpace(config.Timezone))
        {
            try
            {
                _ = TimeZoneInfo.FindSystemTimeZoneById(config.Timezone);
            }
            catch (TimeZoneNotFoundException)
            {
                issues.Add(new HeartbeatValidationIssueDto
                {
                    Severity = "error",
                    Code = "invalid_timezone",
                    Message = $"Timezone '{config.Timezone}' is invalid on this host."
                });
            }
        }

        if (string.IsNullOrWhiteSpace(config.DeliveryChannelId))
        {
            issues.Add(new HeartbeatValidationIssueDto
            {
                Severity = "error",
                Code = "missing_delivery_channel",
                Message = "Delivery channel is required."
            });
        }
        else
        {
            issues.AddRange(ValidateDelivery(config.DeliveryChannelId, config.DeliveryRecipientId));
        }

        return issues;
    }

    private IReadOnlyList<HeartbeatValidationIssueDto> ValidateDelivery(string channelId, string? recipientId)
    {
        var issues = new List<HeartbeatValidationIssueDto>();
        var channel = channelId.Trim().ToLowerInvariant();

        switch (channel)
        {
            case "cron":
                return issues;
            case "email":
                if (!_config.Plugins.Native.Email.Enabled)
                {
                    issues.Add(new HeartbeatValidationIssueDto
                    {
                        Severity = "error",
                        Code = "email_delivery_unavailable",
                        Message = "Email delivery requires Plugins.Native.Email.Enabled=true."
                    });
                }
                break;
            case "sms":
            case "telegram":
            case "whatsapp":
                var readiness = ChannelReadinessEvaluator.Evaluate(_config, !GatewaySecurity.IsLoopbackBind(_config.BindAddress)).FirstOrDefault(item => string.Equals(item.ChannelId, channel, StringComparison.OrdinalIgnoreCase));
                if (readiness is null || !readiness.Ready)
                {
                    issues.Add(new HeartbeatValidationIssueDto
                    {
                        Severity = "error",
                        Code = "channel_not_ready",
                        Message = $"Delivery channel '{channel}' is not ready."
                    });
                }
                break;
            default:
                issues.Add(new HeartbeatValidationIssueDto
                {
                    Severity = "error",
                    Code = "unsupported_delivery_channel",
                    Message = $"Delivery channel '{channel}' is not supported for managed heartbeat."
                });
                break;
        }

        if (!string.Equals(channel, "cron", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(recipientId))
        {
            issues.Add(new HeartbeatValidationIssueDto
            {
                Severity = "error",
                Code = "missing_delivery_recipient",
                Message = $"Delivery recipient is required for channel '{channel}'."
            });
        }

        return issues;
    }

    private static void ValidateCondition(
        TemplateDefinition template,
        HeartbeatTaskDto task,
        HeartbeatConditionDto condition,
        List<HeartbeatValidationIssueDto> issues)
    {
        if (string.IsNullOrWhiteSpace(condition.Field))
        {
            issues.Add(new HeartbeatValidationIssueDto
            {
                Severity = "error",
                Code = "missing_condition_field",
                Message = $"Task '{task.Title}' has a condition with no field.",
                TaskId = task.Id
            });
            return;
        }

        if (!template.AvailableFields.TryGetValue(condition.Field, out var allowedOperators))
        {
            issues.Add(new HeartbeatValidationIssueDto
            {
                Severity = "error",
                Code = "unsupported_condition_field",
                Message = $"Task '{task.Title}' uses unsupported field '{condition.Field}' for template '{task.TemplateKey}'.",
                TaskId = task.Id
            });
            return;
        }

        if (!allowedOperators.Contains(condition.Operator, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new HeartbeatValidationIssueDto
            {
                Severity = "error",
                Code = "unsupported_condition_operator",
                Message = $"Task '{task.Title}' uses unsupported operator '{condition.Operator}' on field '{condition.Field}'.",
                TaskId = task.Id
            });
            return;
        }

        if (!string.Equals(condition.Operator, "is_true", StringComparison.OrdinalIgnoreCase) &&
            (condition.Values is null || condition.Values.Count == 0))
        {
            issues.Add(new HeartbeatValidationIssueDto
            {
                Severity = "error",
                Code = "missing_condition_value",
                Message = $"Task '{task.Title}' condition '{condition.Field}' needs at least one value.",
                TaskId = task.Id
            });
        }
    }

    private IReadOnlyList<HeartbeatValidationIssueDto> BuildApprovalWarnings(HeartbeatTaskDto task, GatewayAppRuntime runtime)
    {
        var warnings = new List<HeartbeatValidationIssueDto>();
        if (!runtime.EffectiveRequireToolApproval)
            return warnings;

        var candidateTools = ResolveTemplate(task.TemplateKey).RequiredAnyTools;
        if (candidateTools.Length == 0)
            return warnings;

        var blockingTool = candidateTools.FirstOrDefault(tool =>
            runtime.RegisteredToolNames.Contains(tool)
            && runtime.EffectiveApprovalRequiredTools.Contains(tool, StringComparer.OrdinalIgnoreCase));

        if (blockingTool is null)
            return warnings;

        warnings.Add(new HeartbeatValidationIssueDto
        {
            Severity = "warning",
            Code = "approval_required",
            Message = $"Task '{task.Title}' likely needs tool '{blockingTool}', which currently requires approval. Unattended heartbeat runs may stall or time out.",
            TaskId = task.Id
        });

        return warnings;
    }

    private IReadOnlyList<HeartbeatTemplateDto> BuildTemplates(GatewayAppRuntime runtime)
    {
        return TemplateCatalog
            .Select(template =>
            {
                var available = template.RequiredAnyTools.Length == 0
                    || template.RequiredAnyTools.Any(runtime.RegisteredToolNames.Contains);
                var reason = available
                    ? null
                    : $"Requires one of: {string.Join(", ", template.RequiredAnyTools)}";

                return new HeartbeatTemplateDto
                {
                    Key = template.Key,
                    Label = template.Label,
                    Description = template.Description,
                    Available = available,
                    Reason = reason
                };
            })
            .ToArray();
    }

    private async ValueTask<IReadOnlyList<HeartbeatSuggestionDto>> BuildSuggestionsAsync(GatewayAppRuntime runtime, CancellationToken ct)
    {
        var suggestions = new Dictionary<string, SuggestionAccumulator>(StringComparer.OrdinalIgnoreCase);
        var texts = new List<(string Source, string Text)>();

        var memoryMarkdown = LoadOptionalMemoryMarkdown();
        if (!string.IsNullOrWhiteSpace(memoryMarkdown))
            texts.Add(("memory.md", memoryMarkdown));

        if (_memoryStore is ISessionAdminStore sessionAdminStore)
        {
            var page = await sessionAdminStore.ListSessionsAsync(1, 20, new SessionListQuery(), ct);
            foreach (var summary in page.Items)
            {
                var session = await _sessionManager.LoadAsync(summary.Id, ct);
                if (session is null)
                    continue;

                foreach (var turn in session.History
                    .Where(static turn => string.Equals(turn.Role, "user", StringComparison.OrdinalIgnoreCase))
                    .TakeLast(6))
                {
                    if (!string.IsNullOrWhiteSpace(turn.Content))
                        texts.Add(($"session:{summary.Id}", Truncate(turn.Content, 2_000)));
                }
            }
        }

        var noteKeys = await _memoryStore.ListNotesWithPrefixAsync("", ct);
        foreach (var key in noteKeys.Take(20))
        {
            var note = await _memoryStore.LoadNoteAsync(key, ct);
            if (!string.IsNullOrWhiteSpace(note))
                texts.Add(($"note:{key}", Truncate(note!, 2_000)));
        }

        foreach (var (source, text) in texts)
        {
            foreach (Match match in UrlRegex.Matches(text))
            {
                if (!match.Success)
                    continue;

                var url = match.Value.TrimEnd('.', ',', ';', ')');
                var template = LooksLikeApi(url) ? "api_health_check" : "website_monitoring";
                AccumulateSuggestion(suggestions, template, source, url);
            }

            foreach (Match match in EmailRegex.Matches(text))
            {
                if (!match.Success)
                    continue;

                AccumulateSuggestion(suggestions, "email_check", source, match.Value);
            }

            foreach (Match match in PathRegex.Matches(text))
            {
                if (!match.Success)
                    continue;

                var value = match.Value;
                if (value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (value.Length >= 4 && value.Contains('/'))
                    AccumulateSuggestion(suggestions, "file_folder_watch", source, value);
            }

            var lower = text.ToLowerInvariant();
            if (lower.Contains("calendar", StringComparison.Ordinal) ||
                lower.Contains("meeting", StringComparison.Ordinal) ||
                lower.Contains("appointment", StringComparison.Ordinal))
            {
                AccumulateSuggestion(suggestions, "calendar_alert", source, "upcoming calendar events");
            }
        }

        return suggestions.Values
            .Where(static item => item.Evidence.Count >= 2)
            .Select(item => new HeartbeatSuggestionDto
            {
                TemplateKey = item.TemplateKey,
                Title = SuggestionTitle(item.TemplateKey, item.Target),
                Target = item.Target,
                Reason = BuildSuggestionReason(item),
                EvidenceCount = item.Evidence.Count
            })
            .OrderByDescending(static item => item.EvidenceCount)
            .ThenBy(static item => item.TemplateKey, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
    }

    private static void AccumulateSuggestion(
        Dictionary<string, SuggestionAccumulator> suggestions,
        string templateKey,
        string source,
        string target)
    {
        var key = $"{templateKey}:{target}";
        if (!suggestions.TryGetValue(key, out var current))
        {
            current = new SuggestionAccumulator(templateKey, target);
            suggestions[key] = current;
        }

        current.Evidence.Add(source);
    }

    private HeartbeatCostEstimateDto EstimateCost(HeartbeatConfigDto config, GatewayAppRuntime runtime)
    {
        var modelId = string.IsNullOrWhiteSpace(config.ModelId) ? _config.Llm.Model : config.ModelId!;
        var providerId = _config.Llm.Provider;
        var rate = ResolveRate(providerId, modelId);
        var rendered = RenderMarkdown(config);
        var estimatedInputTokens = EstimateTokenCount(BaselineWrapperChars + rendered.Length + runtime.EstimatedSkillPromptChars);
        var runsPerMonth = EstimateRunsPerMonth(config.CronExpression, config.Timezone);
        var okPerRun = (estimatedInputTokens + EstimatedOkOutputTokens) * rate / 1000m;
        var alertPerRun = (estimatedInputTokens + EstimatedAlertOutputTokens) * rate / 1000m;

        return new HeartbeatCostEstimateDto
        {
            ProviderId = providerId,
            ModelId = modelId,
            EstimatedSkillPromptChars = runtime.EstimatedSkillPromptChars,
            EstimatedInputTokensPerRun = estimatedInputTokens,
            EstimatedOkOutputTokensPerRun = EstimatedOkOutputTokens,
            EstimatedAlertOutputTokensPerRun = EstimatedAlertOutputTokens,
            EstimatedRunsPerMonth = runsPerMonth,
            EstimatedOkCostUsdPerRun = okPerRun,
            EstimatedAlertCostUsdPerRun = alertPerRun,
            EstimatedOkCostUsdPerMonth = okPerRun * runsPerMonth,
            EstimatedAlertCostUsdPerMonth = alertPerRun * runsPerMonth
        };
    }

    private int EstimateRunsPerMonth(string expression, string? timezoneId)
    {
        if (!IsValidCronExpression(expression))
            return 0;

        TimeZoneInfo? timezone = null;
        if (!string.IsNullOrWhiteSpace(timezoneId))
        {
            try
            {
                timezone = TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
            }
            catch (TimeZoneNotFoundException)
            {
            }
        }

        var utcNow = DateTimeOffset.UtcNow;
        var start = new DateTimeOffset(utcNow.Year, utcNow.Month, utcNow.Day, utcNow.Hour, utcNow.Minute, 0, TimeSpan.Zero);
        var count = 0;
        for (var i = 0; i < 43_200; i++)
        {
            var utc = start.AddMinutes(i);
            var local = timezone is null ? utc : TimeZoneInfo.ConvertTime(utc, timezone);
            if (CronScheduler.IsTime(expression, local))
                count++;
        }

        return count;
    }

    private bool DetectDrift(string expectedContent)
    {
        if (!File.Exists(_heartbeatPath))
            return false;

        try
        {
            var existing = File.ReadAllText(_heartbeatPath);
            return !string.Equals(existing, expectedContent, StringComparison.Ordinal);
        }
        catch
        {
            return true;
        }
    }

    private decimal ResolveRate(string providerId, string modelId)
    {
        var rate = TokenCostRateResolver.Resolve(_config, providerId, modelId);
        return Math.Max(rate.InputUsdPer1K, rate.OutputUsdPer1K);
    }

    private static bool LooksLikeApi(string value)
        => value.Contains("/api/", StringComparison.OrdinalIgnoreCase)
            || value.Contains("/health", StringComparison.OrdinalIgnoreCase)
            || value.Contains("/status", StringComparison.OrdinalIgnoreCase)
            || value.Contains("api.", StringComparison.OrdinalIgnoreCase);

    private static string BuildSuggestionReason(SuggestionAccumulator item)
        => $"Seen in {item.Evidence.Count} sources: {string.Join(", ", item.Evidence.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).Take(3))}";

    private static string SuggestionTitle(string templateKey, string target)
        => templateKey switch
        {
            "email_check" => $"Monitor email from {target}",
            "calendar_alert" => "Watch calendar for important events",
            "website_monitoring" => $"Monitor website {target}",
            "api_health_check" => $"Check API {target}",
            "file_folder_watch" => $"Watch file or folder {target}",
            _ => $"Heartbeat task for {target}"
        };

    private static string BuildTaskInstruction(HeartbeatTaskDto task)
    {
        var suffix = string.IsNullOrWhiteSpace(task.Instruction) ? "" : $" {task.Instruction!.Trim()}";
        return task.TemplateKey switch
        {
            "email_check" => $"Check the configured inbox or mailbox target for important new messages.{suffix}".Trim(),
            "calendar_alert" => $"Check calendar events for urgent changes, soon-starting events, or high-priority updates.{suffix}".Trim(),
            "website_monitoring" => $"Check the target website for meaningful updates, changes, or notable new information.{suffix}".Trim(),
            "api_health_check" => $"Check the target API for health, availability, degraded responses, or changed payloads.{suffix}".Trim(),
            "file_folder_watch" => $"Check the target file or folder for new alerts, new files, or changed contents.{suffix}".Trim(),
            _ => (task.Instruction ?? "Perform the configured custom check.").Trim()
        };
    }

    private static string FormatCondition(HeartbeatConditionDto condition)
    {
        if (string.Equals(condition.Operator, "is_true", StringComparison.OrdinalIgnoreCase))
            return $"{condition.Field} is true";

        var values = string.Join(", ", condition.Values);
        return $"{condition.Field} {condition.Operator} {values}";
    }

    private static TemplateDefinition ResolveTemplate(string key)
        => TemplateCatalog.FirstOrDefault(template => string.Equals(template.Key, key, StringComparison.OrdinalIgnoreCase))
            ?? TemplateCatalog[^1];

    private static bool IsValidCronExpression(string expression)
    {
        var probeConfig = new GatewayConfig
        {
            Cron = new CronConfig
            {
                Enabled = true,
                Jobs =
                [
                    new CronJobConfig
                    {
                        Name = "heartbeat-probe",
                        CronExpression = expression,
                        Prompt = "probe"
                    }
                ]
            }
        };

        return !OpenClaw.Core.Validation.ConfigValidator.Validate(probeConfig)
            .Any(static error => error.Contains("invalid CronExpression", StringComparison.Ordinal));
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "…";

    private static int EstimateTokenCount(int charCount)
        => charCount <= 0 ? 0 : Math.Max(1, (charCount + 3) / 4);

    private string? LoadOptionalMemoryMarkdown()
    {
        try
        {
            if (!File.Exists(_memoryMarkdownPath))
                return null;

            return Truncate(File.ReadAllText(_memoryMarkdownPath), 16_000);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read optional memory markdown from {Path}", _memoryMarkdownPath);
            return null;
        }
    }

    private static void WriteTextAtomic(string path, string contents)
    {
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, contents, Encoding.UTF8);
        File.Move(tempPath, path, overwrite: true);
    }

    private static void WriteJsonAtomic<T>(string path, T payload, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(payload, typeInfo), Encoding.UTF8);
        File.Move(tempPath, path, overwrite: true);
    }

    private sealed record TemplateDefinition(
        string Key,
        string Label,
        string Description,
        string[] RequiredAnyTools,
        Dictionary<string, string[]> AvailableFields);

    private sealed class SuggestionAccumulator(string templateKey, string target)
    {
        public string TemplateKey { get; } = templateKey;
        public string Target { get; } = target;
        public HashSet<string> Evidence { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent.Execution;

internal interface IExecutionProcessBackend
{
    string Name { get; }
    ExecutionBackendCapabilities Capabilities { get; }
    Task<ManagedExecutionProcess> StartProcessAsync(ExecutionProcessStartRequest request, CancellationToken cancellationToken = default);
}

internal sealed class ManagedExecutionProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Lock _stdoutGate = new();
    private readonly Lock _stderrGate = new();
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private readonly TaskCompletionSource<int> _exitTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int? _exitCode;
    private bool _killed;
    private bool _timedOut;

    public ManagedExecutionProcess(
        string processId,
        ExecutionProcessStartRequest request,
        Process process,
        int? nativePid,
        bool supportsPty)
    {
        ProcessId = processId;
        BackendName = request.BackendName;
        OwnerSessionId = request.OwnerSessionId;
        OwnerChannelId = request.OwnerChannelId;
        OwnerSenderId = request.OwnerSenderId;
        CreatedAtUtc = DateTimeOffset.UtcNow;
        ProcessStartedAtUtc = DateTimeOffset.UtcNow;
        CommandPreview = BuildCommandPreview(request.Command, request.Arguments);
        SupportsPty = supportsPty;
        Pty = request.Pty;
        NativeProcessId = nativePid;
        TimeoutSeconds = request.TimeoutSeconds;
        _process = process;

        _process.EnableRaisingEvents = true;
        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;

            lock (_stdoutGate)
                _stdout.AppendLine(e.Data);
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;

            lock (_stderrGate)
                _stderr.AppendLine(e.Data);
        };
        _process.Exited += (_, _) =>
        {
            _exitCode = _process.ExitCode;
            CompletedAtUtc = DateTimeOffset.UtcNow;
            _exitTcs.TrySetResult(_process.ExitCode);
            OnExited?.Invoke(ProcessId, _exitCode == 0 ? "completed" : "failed");
        };
    }

    public string ProcessId { get; }
    public string BackendName { get; }
    public string OwnerSessionId { get; }
    public string OwnerChannelId { get; }
    public string OwnerSenderId { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset? ProcessStartedAtUtc { get; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public string CommandPreview { get; }
    public bool SupportsPty { get; }
    public bool Pty { get; }
    public int? NativeProcessId { get; }
    public int? TimeoutSeconds { get; }

    /// <summary>Optional callback invoked when the process exits (normally, killed, or timed out).</summary>
    internal Action<string, string>? OnExited { get; set; }

    public void BeginCapture()
    {
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public void StartTimeoutMonitor(ILogger? logger)
    {
        if (TimeoutSeconds is null or <= 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await _exitTcs.Task.WaitAsync(TimeSpan.FromSeconds(TimeoutSeconds.Value));
            }
            catch (TimeoutException)
            {
                _timedOut = true;
                logger?.LogWarning("Background process {ProcessId} timed out after {TimeoutSeconds}s.", ProcessId, TimeoutSeconds.Value);
                await KillAsync(CancellationToken.None);
                OnExited?.Invoke(ProcessId, "timed_out");
            }
            catch
            {
            }
        });
    }

    public ExecutionProcessStatus GetStatus()
    {
        var state = !_process.HasExited
            ? ExecutionProcessState.Running
            : _timedOut
                ? ExecutionProcessState.TimedOut
                : _killed
                    ? ExecutionProcessState.Killed
                    : _exitCode == 0
                        ? ExecutionProcessState.Completed
                        : ExecutionProcessState.Failed;

        int stdoutLength;
        lock (_stdoutGate)
            stdoutLength = _stdout.Length;

        int stderrLength;
        lock (_stderrGate)
            stderrLength = _stderr.Length;

        return new ExecutionProcessStatus
        {
            ProcessId = ProcessId,
            BackendName = BackendName,
            OwnerSessionId = OwnerSessionId,
            State = state,
            ExitCode = _exitCode,
            TimedOut = _timedOut,
            Pty = Pty,
            NativeProcessId = NativeProcessId,
            CreatedAtUtc = CreatedAtUtc,
            StartedAtUtc = ProcessStartedAtUtc,
            CompletedAtUtc = CompletedAtUtc,
            DurationMs = ((CompletedAtUtc ?? DateTimeOffset.UtcNow) - CreatedAtUtc).TotalMilliseconds,
            StdoutBytes = stdoutLength,
            StderrBytes = stderrLength,
            CommandPreview = CommandPreview
        };
    }

    public ExecutionProcessLogResult ReadLog(ExecutionProcessLogRequest request)
    {
        var stdout = ReadSlice(_stdout, _stdoutGate, request.StdoutOffset, request.MaxChars, out var nextStdout);
        var stderr = ReadSlice(_stderr, _stderrGate, request.StderrOffset, request.MaxChars, out var nextStderr);
        return new ExecutionProcessLogResult
        {
            ProcessId = ProcessId,
            Stdout = stdout,
            Stderr = stderr,
            NextStdoutOffset = nextStdout,
            NextStderrOffset = nextStderr,
            Completed = _process.HasExited
        };
    }

    public async Task WaitAsync(CancellationToken ct)
    {
        if (_process.HasExited)
            return;

        await _process.WaitForExitAsync(ct);
    }

    public async Task WriteAsync(string data, CancellationToken ct)
    {
        if (_process.HasExited)
            throw new InvalidOperationException("The process has already exited.");

        await _process.StandardInput.WriteAsync(data.AsMemory(), ct);
        await _process.StandardInput.FlushAsync();
    }

    public async Task KillAsync(CancellationToken ct)
    {
        if (_process.HasExited)
            return;

        _killed = true;
        try
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(ct);
        }
        catch (InvalidOperationException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await KillAsync(CancellationToken.None);
        }
        catch
        {
        }

        _process.Dispose();
    }

    private static string BuildCommandPreview(string command, IReadOnlyList<string> arguments)
    {
        var preview = arguments.Count == 0
            ? command
            : $"{command} {string.Join(' ', arguments)}";
        return preview.Length <= 240 ? preview : preview[..240] + "…";
    }

    private static string ReadSlice(StringBuilder builder, Lock gate, int offset, int maxChars, out int nextOffset)
    {
        lock (gate)
        {
            var safeOffset = Math.Clamp(offset, 0, builder.Length);
            var length = Math.Min(Math.Max(0, maxChars), builder.Length - safeOffset);
            nextOffset = safeOffset + length;
            return length == 0 ? "" : builder.ToString(safeOffset, length);
        }
    }
}

public sealed class ExecutionProcessService : IAsyncDisposable
{
    private readonly ToolExecutionRouter _router;
    private readonly ILogger<ExecutionProcessService>? _logger;
    private readonly ConcurrentDictionary<string, ManagedExecutionProcess> _processes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optional callback for emitting runtime events. Parameters: component, action, summary.
    /// Wired by the Gateway to feed into <c>RuntimeEventStore</c>.
    /// </summary>
    public Action<string, string, string>? OnRuntimeEvent { get; set; }

    public ExecutionProcessService(ToolExecutionRouter router, ILogger<ExecutionProcessService>? logger = null)
    {
        _router = router;
        _logger = logger;
    }

    public async Task<ExecutionProcessHandle> StartAsync(ExecutionProcessStartRequest request, CancellationToken ct)
    {
        var route = _router.ResolveBackendForProcess();
        var backendName = string.IsNullOrWhiteSpace(request.BackendName) ? route.BackendName : request.BackendName;
        if (!_router.TryGetProcessBackend(backendName, out var backend))
            throw new InvalidOperationException($"Execution backend '{backendName}' does not support background processes.");
        if (backend is null)
            throw new InvalidOperationException($"Execution backend '{backendName}' is unavailable.");

        if (request.Pty && !backend.Capabilities.SupportsPty)
            throw new InvalidOperationException($"Execution backend '{backendName}' does not support PTY mode.");

        var normalized = new ExecutionProcessStartRequest
        {
            ToolName = request.ToolName,
            BackendName = backendName,
            OwnerSessionId = request.OwnerSessionId,
            OwnerChannelId = request.OwnerChannelId,
            OwnerSenderId = request.OwnerSenderId,
            Command = request.Command,
            Arguments = request.Arguments,
            WorkingDirectory = request.WorkingDirectory,
            Environment = new Dictionary<string, string>(request.Environment, StringComparer.Ordinal),
            TimeoutSeconds = request.TimeoutSeconds,
            Pty = request.Pty,
            Template = request.Template ?? route.Template,
            RequireWorkspace = request.RequireWorkspace
        };

        var process = await backend.StartProcessAsync(normalized, ct);
        _processes[process.ProcessId] = process;
        process.OnExited = (pid, outcome) =>
            OnRuntimeEvent?.Invoke("process", outcome, $"Process {pid} {outcome} (exit code: {process.GetStatus().ExitCode}).");
        process.StartTimeoutMonitor(_logger);
        OnRuntimeEvent?.Invoke("process", "started", $"Process {process.ProcessId} started on '{backendName}': {process.CommandPreview}");

        return new ExecutionProcessHandle
        {
            ProcessId = process.ProcessId,
            BackendName = process.BackendName,
            OwnerSessionId = process.OwnerSessionId,
            OwnerChannelId = process.OwnerChannelId,
            OwnerSenderId = process.OwnerSenderId,
            CommandPreview = process.CommandPreview,
            CreatedAtUtc = process.CreatedAtUtc,
            ExpiresAtUtc = process.CreatedAtUtc.AddHours(6),
            Pty = process.Pty
        };
    }

    public IReadOnlyList<ExecutionProcessStatus> List(string? ownerSessionId = null)
        => _processes.Values
            .Where(p => string.IsNullOrWhiteSpace(ownerSessionId) || string.Equals(p.OwnerSessionId, ownerSessionId, StringComparison.Ordinal))
            .OrderByDescending(static p => p.CreatedAtUtc)
            .Select(static p => p.GetStatus())
            .ToArray();

    public ExecutionProcessStatus? GetStatus(string processId, string? ownerSessionId)
        => TryGetOwnedProcess(processId, ownerSessionId, out var process) ? process.GetStatus() : null;

    public ExecutionProcessLogResult? ReadLog(ExecutionProcessLogRequest request)
        => TryGetOwnedProcess(request.ProcessId, request.OwnerSessionId, out var process) ? process.ReadLog(request) : null;

    public async Task<ExecutionProcessStatus?> WaitAsync(string processId, string? ownerSessionId, CancellationToken ct)
    {
        if (!TryGetOwnedProcess(processId, ownerSessionId, out var process))
            return null;

        await process.WaitAsync(ct);
        return process.GetStatus();
    }

    public async Task<bool> WriteAsync(ExecutionProcessInputRequest request, CancellationToken ct)
    {
        if (!TryGetOwnedProcess(request.ProcessId, request.OwnerSessionId, out var process))
            return false;

        await process.WriteAsync(request.Data, ct);
        OnRuntimeEvent?.Invoke("process", "input", $"Input written to process {request.ProcessId} ({request.Data.Length} chars).");
        return true;
    }

    public async Task<bool> KillAsync(string processId, string? ownerSessionId, CancellationToken ct)
    {
        if (!TryGetOwnedProcess(processId, ownerSessionId, out var process))
            return false;

        await process.KillAsync(ct);
        OnRuntimeEvent?.Invoke("process", "killed", $"Process {processId} terminated.");
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_processes.IsEmpty)
            return;

        _logger?.LogWarning("Shutting down with {Count} active background process(es); terminating.", _processes.Count);
        foreach (var process in _processes.Values)
        {
            var status = process.GetStatus();
            var duration = (DateTimeOffset.UtcNow - process.CreatedAtUtc).TotalSeconds;
            _logger?.LogWarning(
                "Orphaned process {ProcessId} (owner={OwnerSession}, backend={Backend}, state={State}, uptime={Duration:F0}s): {Command}",
                process.ProcessId, process.OwnerSessionId, process.BackendName, status.State, duration, process.CommandPreview);
            OnRuntimeEvent?.Invoke("process", "orphaned", $"Process {process.ProcessId} orphaned on shutdown (owner={process.OwnerSessionId}, uptime={duration:F0}s).");
            await process.DisposeAsync();
        }

        _processes.Clear();
    }

    private bool TryGetOwnedProcess(string processId, string? ownerSessionId, out ManagedExecutionProcess process)
    {
        if (_processes.TryGetValue(processId, out process!))
        {
            if (string.IsNullOrWhiteSpace(ownerSessionId) ||
                string.Equals(process.OwnerSessionId, ownerSessionId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        process = null!;
        return false;
    }
}

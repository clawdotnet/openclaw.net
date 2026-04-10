using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Backends;

internal sealed class CodingBackendProcessHost
{
    private readonly ILogger<CodingBackendProcessHost> _logger;
    private readonly ConcurrentDictionary<string, ActiveBackendProcess> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public CodingBackendProcessHost(ILogger<CodingBackendProcessHost> logger)
        => _logger = logger;

    public async Task<CodingBackendProcessResult> ExecuteAsync(CodingBackendProcessSpec spec, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = CreateStartInfo(spec)
        };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var startedAt = Stopwatch.StartNew();
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (spec.TimeoutSeconds > 0)
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(spec.TimeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            return new CodingBackendProcessResult
            {
                ExitCode = -1,
                TimedOut = true,
                DurationMs = startedAt.Elapsed.TotalMilliseconds,
                Stdout = await stdoutTask,
                Stderr = await stderrTask
            };
        }

        stdout.Append(await stdoutTask);
        stderr.Append(await stderrTask);
        return new CodingBackendProcessResult
        {
            ExitCode = process.ExitCode,
            TimedOut = false,
            DurationMs = startedAt.Elapsed.TotalMilliseconds,
            Stdout = stdout.ToString(),
            Stderr = stderr.ToString()
        };
    }

    public async Task StartAsync(
        CodingBackendProcessSpec spec,
        Func<string, IEnumerable<BackendEvent>> stdoutParser,
        Func<string, IEnumerable<BackendEvent>> stderrParser,
        IBackendSessionRuntime runtime,
        CancellationToken ct)
    {
        var process = new Process
        {
            StartInfo = CreateStartInfo(spec),
            EnableRaisingEvents = true
        };
        process.Start();

        var active = new ActiveBackendProcess(spec, process, runtime, stdoutParser, stderrParser, RemoveSession, _logger);
        if (!_sessions.TryAdd(spec.SessionId, active))
        {
            TryKill(process);
            throw new InvalidOperationException($"Backend session '{spec.SessionId}' is already active.");
        }

        await active.StartAsync(ct);
    }

    public Task WriteInputAsync(string sessionId, BackendInput input, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(sessionId, out var active))
            throw new InvalidOperationException($"Backend session '{sessionId}' is not active.");

        return active.WriteInputAsync(input, ct);
    }

    public Task StopAsync(string sessionId, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(sessionId, out var active))
            return Task.CompletedTask;

        return active.StopAsync(ct);
    }

    private void RemoveSession(string sessionId)
        => _sessions.TryRemove(sessionId, out _);

    private static ProcessStartInfo CreateStartInfo(CodingBackendProcessSpec spec)
    {
        var psi = new ProcessStartInfo
        {
            FileName = spec.Command,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (!string.IsNullOrWhiteSpace(spec.WorkingDirectory))
            psi.WorkingDirectory = spec.WorkingDirectory;

        foreach (var arg in spec.Arguments)
            psi.ArgumentList.Add(arg);

        foreach (var pair in spec.Environment)
            psi.Environment[pair.Key] = pair.Value;

        return psi;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private sealed class ActiveBackendProcess
    {
        private readonly CodingBackendProcessSpec _spec;
        private readonly Process _process;
        private readonly IBackendSessionRuntime _runtime;
        private readonly Func<string, IEnumerable<BackendEvent>> _stdoutParser;
        private readonly Func<string, IEnumerable<BackendEvent>> _stderrParser;
        private readonly Action<string> _onClosed;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _stopCts = new();
        private int _completed;

        public ActiveBackendProcess(
            CodingBackendProcessSpec spec,
            Process process,
            IBackendSessionRuntime runtime,
            Func<string, IEnumerable<BackendEvent>> stdoutParser,
            Func<string, IEnumerable<BackendEvent>> stderrParser,
            Action<string> onClosed,
            ILogger logger)
        {
            _spec = spec;
            _process = process;
            _runtime = runtime;
            _stdoutParser = stdoutParser;
            _stderrParser = stderrParser;
            _onClosed = onClosed;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken ct)
        {
            var running = _runtime.Session with
            {
                State = BackendSessionState.Running,
                StartedAtUtc = DateTimeOffset.UtcNow
            };
            await _runtime.UpdateSessionAsync(running, ct);

            _ = Task.Run(() => PumpOutputAsync(_process.StandardOutput, _stdoutParser, _stopCts.Token));
            _ = Task.Run(() => PumpOutputAsync(_process.StandardError, _stderrParser, _stopCts.Token));
            _ = Task.Run(() => MonitorExitAsync(_stopCts.Token));
            if (_spec.TimeoutSeconds > 0)
                _ = Task.Run(() => MonitorTimeoutAsync(_stopCts.Token));
        }

        public async Task WriteInputAsync(BackendInput input, CancellationToken ct)
        {
            if (_process.HasExited)
                throw new InvalidOperationException($"Backend session '{_spec.SessionId}' has already exited.");

            if (!string.IsNullOrEmpty(input.Text))
            {
                var text = input.AppendNewline ? input.Text + Environment.NewLine : input.Text;
                await _process.StandardInput.WriteAsync(text.AsMemory(), ct);
                await _process.StandardInput.FlushAsync();
            }

            if (input.CloseInput)
                _process.StandardInput.Close();
        }

        public async Task StopAsync(CancellationToken ct)
        {
            _stopCts.Cancel();
            CodingBackendProcessHost.TryKill(_process);
            try
            {
                await _process.WaitForExitAsync(ct);
            }
            catch
            {
            }
        }

        private async Task PumpOutputAsync(
            StreamReader reader,
            Func<string, IEnumerable<BackendEvent>> parser,
            CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null)
                        break;

                    foreach (var evt in parser(line))
                        await _runtime.AppendEventAsync(evt, ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Backend output pump failed for {SessionId}", _spec.SessionId);
            }
        }

        private async Task MonitorExitAsync(CancellationToken ct)
        {
            try
            {
                await _process.WaitForExitAsync(ct);
                await CompleteAsync(
                    _process.ExitCode == 0 ? BackendSessionState.Completed : BackendSessionState.Failed,
                    _process.ExitCode,
                    _process.ExitCode == 0 ? "process_exited" : "process_failed",
                    CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task MonitorTimeoutAsync(CancellationToken ct)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_spec.TimeoutSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_process.HasExited)
                return;

            await _runtime.AppendEventAsync(new BackendErrorEvent
            {
                SessionId = _spec.SessionId,
                Message = $"Backend session timed out after {_spec.TimeoutSeconds} seconds."
            }, CancellationToken.None);

            await StopAsync(CancellationToken.None);
            await CompleteAsync(BackendSessionState.Failed, -1, "timed_out", CancellationToken.None);
        }

        private async Task CompleteAsync(string state, int? exitCode, string? reason, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;

            var completed = _runtime.Session with
            {
                State = state,
                ExitCode = exitCode,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                LastError = state == BackendSessionState.Failed ? reason : _runtime.Session.LastError
            };
            await _runtime.UpdateSessionAsync(completed, ct);
            await _runtime.AppendEventAsync(new BackendSessionCompletedEvent
            {
                SessionId = _spec.SessionId,
                ExitCode = exitCode,
                Reason = reason
            }, ct);

            _onClosed(_spec.SessionId);
            _stopCts.Cancel();
            _process.Dispose();
        }
    }
}

internal sealed class CodingBackendProcessSpec
{
    public required string SessionId { get; init; }
    public required string BackendId { get; init; }
    public required string Command { get; init; }
    public string[] Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public Dictionary<string, string> Environment { get; init; } = new(StringComparer.Ordinal);
    public int TimeoutSeconds { get; init; } = 600;
}

internal sealed class CodingBackendProcessResult
{
    public int ExitCode { get; init; }
    public bool TimedOut { get; init; }
    public double DurationMs { get; init; }
    public string Stdout { get; init; } = string.Empty;
    public string Stderr { get; init; } = string.Empty;
}

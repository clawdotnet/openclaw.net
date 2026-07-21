using System.Diagnostics;
using System.Text;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent.Execution;

internal abstract class ProcessExecutionBackendBase : IExecutionBackend, IExecutionProcessBackend
{
    public abstract string Name { get; }
    public virtual ExecutionBackendCapabilities Capabilities { get; } = new()
    {
        SupportsOneShotCommands = true,
        SupportsProcesses = true,
        SupportsPty = false,
        SupportsInteractiveInput = true
    };

    public abstract Task<ExecutionResult> ExecuteAsync(
        ExecutionRequest request,
        CancellationToken cancellationToken = default);

    public virtual Task<ManagedExecutionProcess> StartProcessAsync(
        ExecutionProcessStartRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var psi = CreateProcessStartInfo(request);
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;

        var process = new Process { StartInfo = psi };
        process.Start();
        var managed = new ManagedExecutionProcess(
            $"proc_{Guid.NewGuid():N}"[..21],
            request,
            process,
            nativePid: TryGetProcessId(process),
            supportsPty: Capabilities.SupportsPty);
        managed.BeginCapture();
        return Task.FromResult(managed);
    }

    protected abstract ProcessStartInfo CreateProcessStartInfo(ExecutionRequest request);

    protected virtual ProcessStartInfo CreateProcessStartInfo(ExecutionProcessStartRequest request)
        => CreateProcessStartInfo(new ExecutionRequest
        {
            ToolName = request.ToolName,
            BackendName = request.BackendName,
            Command = request.Command,
            Arguments = request.Arguments,
            WorkingDirectory = request.WorkingDirectory,
            Environment = request.Environment,
            Template = request.Template,
            RequireWorkspace = request.RequireWorkspace
        });

    protected static async Task<ExecutionResult> ExecuteProcessAsync(
        string backendName,
        ProcessStartInfo startInfo,
        string? standardInput,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        startInfo.RedirectStandardInput = !string.IsNullOrEmpty(standardInput);
        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stderr.AppendLine(e.Data);
        };

        var sw = Stopwatch.StartNew();
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!string.IsNullOrEmpty(standardInput))
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
        }

        // Ensure the spawned process is killed when user-cancellation fires
        // (/stop, /cancel, /abort). Without this the OS process becomes an orphan
        // and continues consuming resources in the container — particularly harmful
        // in low-memory (500 MB) environments where every MB counts.
        using var cancelProcessReg = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The process may already have exited between the HasExited check
                // and Kill; swallow the InvalidOperationException.
            }
        });

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeoutSeconds > 0)
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            process.WaitForExit();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            return new ExecutionResult
            {
                BackendName = backendName,
                ExitCode = -1,
                Stdout = stdout.ToString(),
                Stderr = stderr.ToString(),
                TimedOut = true,
                FallbackUsed = false,
                DurationMs = sw.Elapsed.TotalMilliseconds
            };
        }
        finally
        {
            TryCancelRead(process, isError: false);
            TryCancelRead(process, isError: true);
        }

        return new ExecutionResult
        {
            BackendName = backendName,
            ExitCode = process.ExitCode,
            Stdout = stdout.ToString(),
            Stderr = stderr.ToString(),
            TimedOut = false,
            FallbackUsed = false,
            DurationMs = sw.Elapsed.TotalMilliseconds
        };
    }

    private static int? TryGetProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch
        {
            return null;
        }
    }

    private static void TryCancelRead(Process process, bool isError)
    {
        try
        {
            if (isError)
                process.CancelErrorRead();
            else
                process.CancelOutputRead();
        }
        catch
        {
        }
    }
}

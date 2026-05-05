using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenClaw.Payments.Core;

namespace OpenClaw.Payments.StripeLink;

public sealed record LinkCliCommandResult
{
    public int ExitCode { get; init; }
    public string Stdout { get; init; } = "";
    public string Stderr { get; init; } = "";
    public bool TimedOut { get; init; }
}

public interface ILinkCliCommandRunner
{
    ValueTask<LinkCliCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken ct);
}

public sealed class LinkCliProcessRunner : ILinkCliCommandRunner
{
    private readonly PaymentSensitiveDataRedactor _redactor = new();
    private readonly ILogger<LinkCliProcessRunner>? _logger;

    public LinkCliProcessRunner(ILogger<LinkCliProcessRunner>? logger = null)
    {
        _logger = logger;
    }

    public async ValueTask<LinkCliCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
            startInfo.WorkingDirectory = workingDirectory;
        foreach (var arg in arguments)
            startInfo.ArgumentList.Add(arg);
        foreach (var item in environment)
            startInfo.Environment[item.Key] = item.Value;

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                return new LinkCliCommandResult
                {
                    ExitCode = -1,
                    Stderr = "Failed to start link-cli process."
                };
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return new LinkCliCommandResult
            {
                ExitCode = -1,
                Stderr = ex.Message
            };
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout > TimeSpan.Zero)
            timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            return new LinkCliCommandResult
            {
                ExitCode = -1,
                Stdout = _redactor.Redact(await ReadBestEffortAsync(stdoutTask)),
                Stderr = _redactor.Redact(await ReadBestEffortAsync(stderrTask)),
                TimedOut = true
            };
        }

        var stdout = _redactor.Redact(await stdoutTask);
        var stderr = _redactor.Redact(await stderrTask);
        if (process.ExitCode != 0)
            _logger?.LogWarning("link-cli exited with {ExitCode}: {Stderr}", process.ExitCode, stderr);

        return new LinkCliCommandResult
        {
            ExitCode = process.ExitCode,
            Stdout = stdout,
            Stderr = stderr,
            TimedOut = false
        };
    }

    private static async Task<string> ReadBestEffortAsync(Task<string> task)
    {
        try { return await task; } catch { return ""; }
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
}

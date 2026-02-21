using System.Diagnostics;
using System.Text;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Executes shell commands locally. Sandboxed with timeout and output limits.
/// </summary>
public sealed class ShellTool : ITool
{
    private readonly ToolingConfig _config;

    public ShellTool(ToolingConfig config) => _config = config;

    public string Name => "shell";
    public string Description => "Execute a shell command on the local machine. Use for file operations, system queries, and automation.";
    public string ParameterSchema => """{"type":"object","properties":{"command":{"type":"string","description":"The shell command to execute"},"timeout_seconds":{"type":"integer","default":30}},"required":["command"]}""";

    private const int MaxOutputBytes = 64 * 1024; // 64KB output cap

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        if (!_config.AllowShell)
            return "Error: Shell execution is disabled by configuration.";

        using var args = System.Text.Json.JsonDocument.Parse(argumentsJson);
        var command = args.RootElement.GetProperty("command").GetString()!;
        var timeoutSec = args.RootElement.TryGetProperty("timeout_seconds", out var t) ? t.GetInt32() : 30;
        timeoutSec = Math.Clamp(timeoutSec, 1, 600);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

        var isWindows = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (isWindows)
        {
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command);
        }
        else
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }

        using var process = Process.Start(psi)!;
        using var _ = cts.Token.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
        });

        var stdoutTask = ReadCappedAsync(process.StandardOutput.BaseStream, MaxOutputBytes, cts.Token);
        var stderrTask = ReadCappedAsync(process.StandardError.BaseStream, MaxOutputBytes, cts.Token);

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return "[exit: timeout]\n[truncated]";
        }

        var (stdoutBytes, stdoutTruncated) = await stdoutTask;
        var (stderrBytes, stderrTruncated) = await stderrTask;

        var stdout = Encoding.UTF8.GetString(stdoutBytes);
        var stderr = Encoding.UTF8.GetString(stderrBytes);

        var output = string.IsNullOrEmpty(stderr)
            ? stdout
            : $"{stdout}\n[stderr]: {stderr}";

        if (stdoutTruncated || stderrTruncated)
            output += "\n[truncated]";

        return $"[exit: {process.ExitCode}]\n{output}";
    }

    private static async Task<(byte[] Bytes, bool Truncated)> ReadCappedAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var ms = new MemoryStream(capacity: Math.Min(maxBytes, 16 * 1024));
        var remaining = maxBytes;
        var truncated = false;

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, Math.Max(1, remaining))), ct);
            if (read == 0)
                break;

            if (read > remaining)
            {
                ms.Write(buffer, 0, remaining);
                truncated = true;
                break;
            }

            ms.Write(buffer, 0, read);
            remaining -= read;

            if (remaining == 0)
            {
                truncated = true;
                break;
            }
        }

        return (ms.ToArray(), truncated);
    }
}

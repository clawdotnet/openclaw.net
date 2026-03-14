using System.Diagnostics;
using System.Text;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Executes shell commands locally. Sandboxed with timeout and output limits.
/// </summary>
public sealed class ShellTool : ITool, ISandboxCapableTool
{
    private readonly ToolingConfig _config;

    public ShellTool(ToolingConfig config) => _config = config;

    public string Name => "shell";
    public string Description => "Execute a shell command on the local machine. Use for file operations, system queries, and automation.";
    public string ParameterSchema => """{"type":"object","properties":{"command":{"type":"string","description":"The shell command to execute"},"timeout_seconds":{"type":"integer","default":30}},"required":["command"]}""";
    public ToolSandboxMode DefaultSandboxMode => ToolSandboxMode.Prefer;

    private const int MaxOutputBytes = 64 * 1024; // 64KB output cap

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        if (!TryParseArguments(argumentsJson, out var command, out var timeoutSec, out var error))
            return error!;

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

    public SandboxExecutionRequest CreateSandboxRequest(string argumentsJson)
    {
        if (!TryParseArguments(argumentsJson, out var command, out var timeoutSec, out var error))
            throw new ToolSandboxException(error!);

        return new SandboxExecutionRequest
        {
            Command = "/bin/sh",
            Arguments =
            [
                "-lc",
                SandboxCommandLine.WrapWithTimeout("/bin/sh", ["-lc", command], timeoutSec)
            ]
        };
    }

    public string FormatSandboxResult(string argumentsJson, SandboxResult result)
    {
        if (result.ExitCode == 124)
            return "[exit: timeout]\n[truncated]";

        var output = string.IsNullOrEmpty(result.Stderr)
            ? result.Stdout
            : $"{result.Stdout}\n[stderr]: {result.Stderr}";

        return $"[exit: {result.ExitCode}]\n{output}";
    }

    private bool TryParseArguments(
        string argumentsJson,
        out string command,
        out int timeoutSec,
        out string? error)
    {
        command = string.Empty;
        timeoutSec = 30;
        error = null;

        if (_config.ReadOnlyMode)
        {
            error = "Error: shell is disabled because Tooling.ReadOnlyMode is enabled.";
            return false;
        }

        if (!_config.AllowShell)
        {
            error = "Error: Shell execution is disabled by configuration.";
            return false;
        }

        using var args = System.Text.Json.JsonDocument.Parse(argumentsJson);
        if (!args.RootElement.TryGetProperty("command", out var commandEl) ||
            commandEl.ValueKind != System.Text.Json.JsonValueKind.String)
        {
            error = "Error: 'command' is required.";
            return false;
        }

        command = commandEl.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(command))
        {
            error = "Error: 'command' is required.";
            return false;
        }

        timeoutSec = args.RootElement.TryGetProperty("timeout_seconds", out var timeoutElement)
            ? timeoutElement.GetInt32()
            : 30;
        timeoutSec = Math.Clamp(timeoutSec, 1, 600);
        return true;
    }

    private static async Task<(byte[] Bytes, bool Truncated)> ReadCappedAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var ms = new MemoryStream(capacity: Math.Min(maxBytes, 16 * 1024));
        var remaining = maxBytes;
        var truncated = false;

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read == 0)
                break;

            if (remaining <= 0)
            {
                truncated = true;
                continue;
            }

            if (read > remaining)
            {
                ms.Write(buffer, 0, remaining);
                remaining = 0;
                truncated = true;
                continue;
            }

            ms.Write(buffer, 0, read);
            remaining -= read;
        }

        return (ms.ToArray(), truncated);
    }
}

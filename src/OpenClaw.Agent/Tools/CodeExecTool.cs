using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Native replica of the OpenClaw code-exec plugin.
/// Runs code snippets in an isolated environment (Docker container or local process).
/// Supports Python, JavaScript (Node.js), and Bash.
/// </summary>
public sealed class CodeExecTool : ITool, ISandboxCapableTool
{
    private readonly CodeExecConfig _config;
    private readonly ToolingConfig? _toolingConfig;
    private static readonly Lazy<ResolvedProcessCommand?> BashProcessCommand = new(ResolveBashProcessCommand);
    private const int ShellProbeTimeoutMs = 10_000;

    public CodeExecTool(CodeExecConfig config, ToolingConfig? toolingConfig = null)
    {
        _config = config;
        _toolingConfig = toolingConfig;
    }

    public string Name => "code_exec";
    public string Description =>
        "Execute a code snippet and return the output. " +
        "Supports python, javascript, and bash. Use for calculations, data processing, and automation.";
    public string ParameterSchema => """
        {
          "type": "object",
          "properties": {
            "language": {
              "type": "string",
              "description": "Programming language: python, javascript, or bash",
              "enum": ["python", "javascript", "bash"]
            },
            "code": {
              "type": "string",
              "description": "The code to execute"
            },
            "timeout_seconds": {
              "type": "integer",
              "description": "Execution timeout (default: from config)"
            }
          },
          "required": ["language", "code"]
        }
        """;
    public ToolSandboxMode DefaultSandboxMode => ToolSandboxMode.Prefer;

    private const int MaxOutputBytes = 64 * 1024;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        if (!TryParseArguments(argumentsJson, out var language, out var code, out var timeoutSec, out var error))
            return error!;

        return _config.Backend.ToLowerInvariant() switch
        {
            "docker" => await RunInDockerAsync(language, code, timeoutSec, ct),
            "process" => await RunInProcessAsync(language, code, timeoutSec, ct),
            _ => $"Error: Unsupported backend '{_config.Backend}'. Use 'docker' or 'process'."
        };
    }

    public SandboxExecutionRequest CreateSandboxRequest(string argumentsJson)
    {
        if (!TryParseArguments(argumentsJson, out var language, out var code, out var timeoutSec, out var error))
            throw new ToolSandboxException(error!);

        var (interpreter, arguments) = GetSandboxCommand(language, code);
        if (interpreter is null || arguments is null)
            throw new ToolSandboxException($"Error: Unsupported language '{language}'.");

        return new SandboxExecutionRequest
        {
            Command = "/bin/sh",
            Arguments =
            [
                "-lc",
                SandboxCommandLine.WrapWithTimeout(interpreter, arguments, timeoutSec)
            ]
        };
    }

    public string FormatSandboxResult(string argumentsJson, SandboxResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Exit code: {result.ExitCode}");

        if (!string.IsNullOrWhiteSpace(result.Stdout))
        {
            sb.AppendLine("--- stdout ---");
            sb.Append(result.Stdout);
        }

        if (!string.IsNullOrWhiteSpace(result.Stderr))
        {
            if (sb.Length > 0 && sb[^1] != '\n')
                sb.AppendLine();

            sb.AppendLine("--- stderr ---");
            sb.Append(result.Stderr);
        }

        return sb.ToString();
    }

    private async Task<string> RunInDockerAsync(string language, string code, int timeoutSec, CancellationToken ct)
    {
        var (interpreter, flags) = GetInterpreter(language);
        if (interpreter is null)
            return $"Error: Unsupported language '{language}'.";

        // Write code to a temp file that Docker can mount
        var tempFile = Path.GetTempFileName();
        var ext = language switch { "python" => ".py", "javascript" => ".js", "bash" => ".sh", _ => ".txt" };
        var codeFile = tempFile + ext;
        await File.WriteAllTextAsync(codeFile, code, ct);

        try
        {
            var dockerArgs = new List<string>
            {
                "run",
                "--rm",
                "--network",
                "none",
                "--memory=256m",
                "--cpus=1",
                "-v",
                $"{codeFile}:/code{ext}:ro",
                "-w",
                "/tmp",
                _config.DockerImage,
                interpreter
            };

            AddArgumentTokens(dockerArgs, flags);
            dockerArgs.Add($"/code{ext}");

            return await RunProcessAsync("docker", dockerArgs, timeoutSec, ct);
        }
        finally
        {
            TryDeleteFile(tempFile);
            TryDeleteFile(codeFile);
        }
    }

    private async Task<string> RunInProcessAsync(string language, string code, int timeoutSec, CancellationToken ct)
    {
        if (language == "bash")
        {
            var command = BashProcessCommand.Value;
            if (command is null)
                return "Error: Bash execution is not available on this host.";

            return await RunProcessAsync(command.Executable, [.. command.PrefixArguments, code], timeoutSec, ct);
        }

        var (interpreter, flags) = GetInterpreter(language);
        if (interpreter is null)
            return $"Error: Unsupported language '{language}'.";

        // Write code to a temp file
        var ext = language switch { "python" => ".py", "javascript" => ".js", "bash" => ".sh", _ => ".txt" };
        var codeFile = Path.Combine(Path.GetTempPath(), $"openclaw-exec-{Guid.NewGuid():N}{ext}");
        await File.WriteAllTextAsync(codeFile, code, ct);

        try
        {
            var args = new List<string>();
            AddArgumentTokens(args, flags);
            args.Add(codeFile);
            return await RunProcessAsync(interpreter, args, timeoutSec, ct);
        }
        finally
        {
            TryDeleteFile(codeFile);
        }
    }

    private async Task<string> RunProcessAsync(string exe, IReadOnlyList<string> arguments, int timeoutSec, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            return $"Error: Failed to start execution process ({ex.GetType().Name}).";
        }

        if (process is null)
            return "Error: Failed to start execution process.";
        using var processToDispose = process;

        using var _ = cts.Token.Register(() =>
        {
            try { processToDispose.Kill(entireProcessTree: true); } catch { }
        });

        var maxBytes = Math.Min(_config.MaxOutputBytes, MaxOutputBytes);
        var stdoutTask = ReadLimitedAsync(processToDispose.StandardOutput, maxBytes);
        var stderrTask = ReadLimitedAsync(processToDispose.StandardError, 8192);

        try
        {
            await processToDispose.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return "Error: Execution timed out.";
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        var sb = new StringBuilder();
        sb.AppendLine($"Exit code: {processToDispose.ExitCode}");

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            sb.AppendLine("--- stdout ---");
            sb.Append(stdout);
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine("--- stderr ---");
            sb.Append(stderr);
        }

        return sb.ToString();
    }

    private static (string? interpreter, string flags) GetInterpreter(string language) => language switch
    {
        "python" => ("python3", ""),
        "javascript" => ("node", ""),
        "bash" => ("bash", ""),
        _ => (null, "")
    };

    private static ResolvedProcessCommand? ResolveBashProcessCommand()
    {
        if (OperatingSystem.IsWindows())
        {
            if (CanRunProcess("wsl.exe", ["-e", "sh", "-lc", "exit 0"]))
                return new("wsl.exe", ["-e", "sh", "-lc"]);

            if (CanRunProcess("bash", ["-lc", "exit 0"]))
                return new("bash", ["-lc"]);

            return null;
        }

        if (CanRunProcess("bash", ["-lc", "exit 0"]))
            return new("bash", ["-lc"]);

        return null;
    }

    private static bool CanRunProcess(string executable, IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return false;

            if (!process.WaitForExit(ShellProbeTimeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static (string? Interpreter, string[]? Arguments) GetSandboxCommand(string language, string code) => language switch
    {
        "python" => ("python3", ["-c", code]),
        "javascript" => ("node", ["-e", code]),
        "bash" => ("bash", ["-lc", code]),
        _ => (null, null)
    };

    private bool TryParseArguments(
        string argumentsJson,
        out string language,
        out string code,
        out int timeoutSec,
        out string? error)
    {
        language = string.Empty;
        code = string.Empty;
        timeoutSec = _config.TimeoutSeconds;
        error = null;

        if (_toolingConfig?.ReadOnlyMode == true)
        {
            error = "Error: code_exec is disabled because Tooling.ReadOnlyMode is enabled.";
            return false;
        }

        using var args = JsonDocument.Parse(argumentsJson);
        if (!args.RootElement.TryGetProperty("language", out var languageEl) || languageEl.ValueKind != JsonValueKind.String)
        {
            error = "Error: 'language' is required.";
            return false;
        }

        var languageRaw = languageEl.GetString();
        if (string.IsNullOrWhiteSpace(languageRaw))
        {
            error = "Error: 'language' is required.";
            return false;
        }

        language = languageRaw.ToLowerInvariant();

        if (!args.RootElement.TryGetProperty("code", out var codeEl) || codeEl.ValueKind != JsonValueKind.String)
        {
            error = "Error: 'code' is required.";
            return false;
        }

        code = codeEl.GetString() ?? string.Empty;
        timeoutSec = args.RootElement.TryGetProperty("timeout_seconds", out var timeoutElement)
            ? timeoutElement.GetInt32()
            : _config.TimeoutSeconds;
        timeoutSec = Math.Clamp(timeoutSec, 1, 300);

        if (_config.AllowedLanguages.Length > 0 &&
            !_config.AllowedLanguages.Contains(language, StringComparer.OrdinalIgnoreCase))
        {
            error = $"Error: Language '{language}' is not allowed. Allowed: {string.Join(", ", _config.AllowedLanguages)}";
            return false;
        }

        return true;
    }

    private static async Task<string> ReadLimitedAsync(System.IO.StreamReader reader, int maxBytes)
    {
        var buffer = ArrayPool<char>.Shared.Rent(maxBytes);
        try
        {
            var totalRead = await reader.ReadAsync(buffer.AsMemory(0, maxBytes));
            var result = new string(buffer, 0, totalRead);

            if (totalRead == maxBytes)
            {
                var drain = new char[4096];
                while (await reader.ReadAsync(drain.AsMemory()) > 0) { }
                result += "\n... (output truncated)";
            }

            return result;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static void AddArgumentTokens(List<string> output, string flags)
    {
        if (string.IsNullOrWhiteSpace(flags))
            return;

        var currentToken = new StringBuilder();
        var inQuotes = false;
        var quoteChar = '\0';

        for (var i = 0; i < flags.Length; i++)
        {
            var c = flags[i];

            if (inQuotes)
            {
                if (c == quoteChar)
                {
                    inQuotes = false;
                }
                else
                {
                    currentToken.Append(c);
                }
            }
            else
            {
                if (c == '"' || c == '\'')
                {
                    inQuotes = true;
                    quoteChar = c;
                }
                else if (char.IsWhiteSpace(c))
                {
                    if (currentToken.Length > 0)
                    {
                        output.Add(currentToken.ToString());
                        currentToken.Clear();
                    }
                }
                else
                {
                    currentToken.Append(c);
                }
            }
        }

        if (currentToken.Length > 0)
            output.Add(currentToken.ToString());
    }

    private sealed record ResolvedProcessCommand(string Executable, string[] PrefixArguments);
}

using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Native replica of the OpenClaw code-exec plugin.
/// Runs code snippets in an isolated environment (Docker container or local process).
/// Supports Python, JavaScript (Node.js), and Bash.
/// </summary>
public sealed class CodeExecTool : ITool
{
    private readonly CodeExecConfig _config;

    public CodeExecTool(CodeExecConfig config) => _config = config;

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

    private const int MaxOutputBytes = 64 * 1024;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var args = JsonDocument.Parse(argumentsJson);
        var language = args.RootElement.GetProperty("language").GetString()!.ToLowerInvariant();
        var code = args.RootElement.GetProperty("code").GetString()!;
        var timeoutSec = args.RootElement.TryGetProperty("timeout_seconds", out var t)
            ? t.GetInt32()
            : _config.TimeoutSeconds;
        timeoutSec = Math.Clamp(timeoutSec, 1, 300);

        if (_config.AllowedLanguages.Length > 0 &&
            !_config.AllowedLanguages.Contains(language, StringComparer.OrdinalIgnoreCase))
        {
            return $"Error: Language '{language}' is not allowed. Allowed: {string.Join(", ", _config.AllowedLanguages)}";
        }

        return _config.Backend.ToLowerInvariant() switch
        {
            "docker" => await RunInDockerAsync(language, code, timeoutSec, ct),
            "process" => await RunInProcessAsync(language, code, timeoutSec, ct),
            _ => $"Error: Unsupported backend '{_config.Backend}'. Use 'docker' or 'process'."
        };
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
}

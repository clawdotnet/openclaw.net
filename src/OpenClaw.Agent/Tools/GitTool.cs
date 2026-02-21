using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Native replica of the OpenClaw git-tools plugin.
/// Supports status, diff, log, add, commit, branch operations.
/// Push/reset --hard are gated behind <see cref="GitToolsConfig.AllowPush"/>.
/// </summary>
public sealed class GitTool : ITool
{
    private readonly GitToolsConfig _config;

    public GitTool(GitToolsConfig config) => _config = config;

    public string Name => "git";
    public string Description =>
        "Run git operations on the local repository. " +
        "Supports: status, diff, log, add, commit, branch, checkout, stash.";
    public string ParameterSchema => """
        {
          "type": "object",
          "properties": {
            "subcommand": {
              "type": "string",
              "description": "Git subcommand: status, diff, log, add, commit, branch, checkout, stash, show",
              "enum": ["status", "diff", "log", "add", "commit", "branch", "checkout", "stash", "show", "push", "pull", "reset"]
            },
            "args": {
              "type": "string",
              "description": "Additional arguments for the git command (optional)",
              "default": ""
            },
            "cwd": {
              "type": "string",
              "description": "Working directory (optional, defaults to current)"
            }
          },
          "required": ["subcommand"]
        }
        """;

    private static readonly HashSet<string> SafeSubcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "diff", "log", "add", "commit", "branch", "checkout",
        "stash", "show", "fetch", "merge", "rebase", "cherry-pick", "tag"
    };

    private static readonly HashSet<string> DestructiveSubcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "push", "pull", "reset"
    };

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var args = JsonDocument.Parse(argumentsJson);
        var subcommand = args.RootElement.GetProperty("subcommand").GetString()!.ToLowerInvariant();
        var extraArgs = args.RootElement.TryGetProperty("args", out var a) ? a.GetString() ?? "" : "";
        var cwd = args.RootElement.TryGetProperty("cwd", out var c) ? c.GetString() : null;

        // Safety check
        if (DestructiveSubcommands.Contains(subcommand) && !_config.AllowPush)
            return $"Error: '{subcommand}' is disabled. Set GitTools.AllowPush = true to enable destructive operations.";

        if (!SafeSubcommands.Contains(subcommand) && !DestructiveSubcommands.Contains(subcommand))
            return $"Error: Unsupported git subcommand '{subcommand}'.";

        // Block dangerous flag combinations even when destructive ops are allowed
        if (subcommand == "reset" && extraArgs.Contains("--hard", StringComparison.OrdinalIgnoreCase) && !_config.AllowPush)
            return "Error: 'git reset --hard' is disabled. Set GitTools.AllowPush = true to enable.";

        // Validate extraArgs against shell metacharacter injection
        if (!string.IsNullOrWhiteSpace(extraArgs))
        {
            var metaError = InputSanitizer.CheckShellMetaChars(extraArgs, "args");
            if (metaError is not null)
                return metaError;
        }

        // Build the git command
        var fullArgs = string.IsNullOrWhiteSpace(extraArgs)
            ? subcommand
            : $"{subcommand} {extraArgs}";

        // For log, add sensible defaults if no format specified
        if (subcommand == "log" && !extraArgs.Contains("--format", StringComparison.OrdinalIgnoreCase)
                                && !extraArgs.Contains("--oneline", StringComparison.OrdinalIgnoreCase))
        {
            fullArgs = $"log --oneline -20 {extraArgs}".Trim();
        }

        // For diff, use --stat first if no specific options
        if (subcommand == "diff" && string.IsNullOrWhiteSpace(extraArgs))
        {
            fullArgs = "diff --stat";
        }

        return await RunGitAsync(fullArgs, cwd, ct);
    }

    private async Task<string> RunGitAsync(string gitArgs, string? cwd, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Pass args directly to git via ArgumentList — no shell intermediary.
        // This prevents any shell metacharacter injection regardless of sanitizer gaps.
        foreach (var token in TokenizeGitArgs(gitArgs))
            psi.ArgumentList.Add(token);

        if (!string.IsNullOrEmpty(cwd) && Directory.Exists(cwd))
            psi.WorkingDirectory = cwd;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        using var process = Process.Start(psi)!;
        using var _ = cts.Token.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        });

        var stdoutTask = ReadLimitedAsync(process.StandardOutput, _config.MaxDiffBytes);
        var stderrTask = ReadLimitedAsync(process.StandardError, 8192);

        await process.WaitForExitAsync(cts.Token);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(stdout))
            sb.Append(stdout);
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append($"[stderr] {stderr}");
        }

        if (sb.Length == 0)
            sb.Append($"(git {gitArgs} completed with exit code {process.ExitCode})");

        return sb.ToString();
    }

    /// <summary>
    /// Tokenizes a git argument string into individual arguments, respecting quoted strings.
    /// This is used instead of passing through a shell to prevent injection attacks.
    /// </summary>
    private static List<string> TokenizeGitArgs(string args)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var quoteChar = '\0';

        for (var i = 0; i < args.Length; i++)
        {
            var c = args[i];

            if (inQuotes)
            {
                if (c == quoteChar)
                    inQuotes = false;
                else
                    current.Append(c);
            }
            else if (c is '"' or '\'')
            {
                inQuotes = true;
                quoteChar = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return tokens;
    }

    private static async Task<string> ReadLimitedAsync(System.IO.StreamReader reader, int maxBytes)
    {
        var buffer = ArrayPool<char>.Shared.Rent(maxBytes);
        try
        {
            var totalRead = await reader.ReadAsync(buffer.AsMemory(0, maxBytes));
            var result = new string(buffer, 0, totalRead);

            // Check if there's more data we need to drain
            if (totalRead == maxBytes)
            {
                var drain = new char[4096];
                int drained;
                while ((drained = await reader.ReadAsync(drain.AsMemory())) > 0)
                {
                    // Drain but discard
                }
                if (drained == 0 && totalRead == maxBytes)
                    result += "\n... (output truncated)";
            }

            return result;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }
}

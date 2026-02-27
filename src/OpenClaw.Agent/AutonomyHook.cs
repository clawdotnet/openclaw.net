using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Agent.Tools;

namespace OpenClaw.Agent;

/// <summary>
/// Enforces autonomy mode and workspace/path/shell policies as a hard "deny layer" for tool execution.
/// </summary>
public sealed class AutonomyHook : IToolHook
{
    private readonly ToolingConfig _config;
    private readonly ILogger _logger;
    private readonly string? _workspaceRoot;
    public string Name => "Autonomy";

    private static readonly HashSet<string> AlwaysWriteTools = new(StringComparer.Ordinal)
    {
        "write_file",
        "shell",
        "code_exec",
        "home_assistant_write",
        "mqtt_publish",
        "inbox_zero",
        "email",
        "calendar",
        "delegate_agent"
    };

    public AutonomyHook(ToolingConfig config, ILogger logger)
    {
        _config = config;
        _logger = logger;
        _workspaceRoot = ResolveWorkspaceRoot(config);
    }

    public ValueTask<bool> BeforeExecuteAsync(string toolName, string arguments, CancellationToken ct)
    {
        var mode = (_config.AutonomyMode ?? "full").Trim().ToLowerInvariant();

        if (mode == "readonly")
        {
            if (IsWriteCapable(toolName, arguments))
            {
                _logger.LogInformation("Autonomy readonly: denied tool {Tool}", toolName);
                return ValueTask.FromResult(false);
            }
        }

        if (_config.WorkspaceOnly && _workspaceRoot is not null)
        {
            if (TryExtractPathArgument(toolName, arguments, out var path) &&
                !IsUnderWorkspace(path!, _workspaceRoot))
            {
                _logger.LogInformation("WorkspaceOnly: denied tool {Tool} path {Path}", toolName, path);
                return ValueTask.FromResult(false);
            }
        }

        if (_config.ForbiddenPathGlobs.Length > 0)
        {
            if (TryExtractPathArgument(toolName, arguments, out var path) && IsForbiddenPath(path!))
            {
                _logger.LogInformation("ForbiddenPathGlobs: denied tool {Tool} path {Path}", toolName, path);
                return ValueTask.FromResult(false);
            }

            if (toolName == "shell" && TryExtractShellCommand(arguments, out var cmd) && IsForbiddenCommand(cmd!))
            {
                _logger.LogInformation("ForbiddenPathGlobs: denied shell command");
                return ValueTask.FromResult(false);
            }
        }

        if (toolName == "shell")
        {
            if (!_config.AllowShell)
                return ValueTask.FromResult(false);

            if (TryExtractShellCommand(arguments, out var cmd))
            {
                if (!IsShellCommandAllowed(cmd!))
                {
                    _logger.LogInformation("AllowedShellCommandGlobs: denied shell command");
                    return ValueTask.FromResult(false);
                }
            }
        }

        return ValueTask.FromResult(true);
    }

    public ValueTask AfterExecuteAsync(string toolName, string arguments, string result, TimeSpan duration, bool failed, CancellationToken ct)
        => ValueTask.CompletedTask;

    private bool IsWriteCapable(string toolName, string arguments)
    {
        if (AlwaysWriteTools.Contains(toolName))
            return true;

        if (toolName == "git")
            return true; // git can mutate repo; treat as write-capable for readonly mode

        if (toolName == "database")
        {
            // DatabaseTool can be configured read-only, but treat it as write-capable for readonly mode.
            return true;
        }

        return false;
    }

    private bool IsShellCommandAllowed(string command)
    {
        var allow = _config.AllowedShellCommandGlobs;
        if (allow.Length == 0)
            return false;

        // Special-case: ["*"] means allow all
        if (allow.Length == 1 && allow[0] == "*")
            return true;

        foreach (var pat in allow)
        {
            if (string.IsNullOrWhiteSpace(pat))
                continue;
            if (GlobMatcher.IsMatch(pat.Trim(), command, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private bool IsForbiddenCommand(string command)
    {
        foreach (var patRaw in _config.ForbiddenPathGlobs)
        {
            if (string.IsNullOrWhiteSpace(patRaw))
                continue;

            var pat = patRaw.Trim();
            var envelope = pat.StartsWith('*') ? pat : "*" + pat;
            envelope = envelope.EndsWith('*') ? envelope : envelope + "*";

            if (GlobMatcher.IsMatch(envelope, command, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private bool IsForbiddenPath(string path)
    {
        var expanded = ExpandTilde(path);
        var full = ToolPathPolicy.ResolveRealPath(expanded);

        foreach (var pat in _config.ForbiddenPathGlobs)
        {
            if (string.IsNullOrWhiteSpace(pat))
                continue;

            var p = ExpandTilde(pat.Trim());
            if (GlobMatcher.IsMatch(p, expanded, StringComparison.Ordinal) ||
                GlobMatcher.IsMatch(p, full, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractShellCommand(string arguments, out string? command)
    {
        command = null;
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            if (doc.RootElement.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String)
            {
                command = c.GetString();
                return !string.IsNullOrWhiteSpace(command);
            }
        }
        catch { }
        return false;
    }

    private static bool TryExtractPathArgument(string toolName, string arguments, out string? path)
    {
        path = null;

        // Most path-based tools use "path"; git uses "cwd".
        var prop = toolName switch
        {
            "git" => "cwd",
            _ => "path"
        };

        try
        {
            using var doc = JsonDocument.Parse(arguments);
            if (doc.RootElement.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String)
            {
                path = p.GetString();
                return !string.IsNullOrWhiteSpace(path);
            }
        }
        catch { }

        return false;
    }

    private static string? ResolveWorkspaceRoot(ToolingConfig cfg)
    {
        if (!cfg.WorkspaceOnly)
            return null;

        var resolved = SecretResolver.Resolve(cfg.WorkspaceRoot);
        if (string.IsNullOrWhiteSpace(resolved))
            return null;

        var expanded = ExpandTilde(resolved);
        var full = Path.GetFullPath(expanded);
        if (!Directory.Exists(full))
            return null;

        return ToolPathPolicy.ResolveRealPath(full);
    }

    private static bool IsUnderWorkspace(string path, string workspaceRoot)
    {
        var expanded = ExpandTilde(path);
        var full = ToolPathPolicy.ResolveRealPath(expanded);

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(full, workspaceRoot, comparison))
            return true;

        var root = workspaceRoot.EndsWith(Path.DirectorySeparatorChar)
            ? workspaceRoot
            : workspaceRoot + Path.DirectorySeparatorChar;

        return full.StartsWith(root, comparison);
    }

    private static string ExpandTilde(string path)
    {
        if (path.StartsWith("~/", StringComparison.Ordinal) || path == "~")
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return path.Length == 1 ? home : Path.Combine(home, path[2..]);
        }
        return path;
    }
}


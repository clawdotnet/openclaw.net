using System.Diagnostics;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent.Execution;

internal sealed class LocalExecutionBackend : ProcessExecutionBackendBase
{
    private readonly ExecutionBackendProfileConfig _profile;
    private readonly string? _workspaceRoot;

    public LocalExecutionBackend(ExecutionBackendProfileConfig profile, string? workspaceRoot = null)
    {
        _profile = profile;
        _workspaceRoot = workspaceRoot;
    }

    public override string Name => "local";

    public override ExecutionBackendCapabilities Capabilities { get; } = new()
    {
        SupportsOneShotCommands = true,
        SupportsProcesses = true,
        SupportsPty = !OperatingSystem.IsWindows(),
        SupportsInteractiveInput = true
    };

    public override Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
        => ExecuteProcessAsync(Name, CreateProcessStartInfo(request), request.StandardInput, _profile.TimeoutSeconds, cancellationToken);

    protected override ProcessStartInfo CreateProcessStartInfo(ExecutionRequest request)
    {
        var workingDirectory = ResolveWorkingDirectory(request);
        var psi = new ProcessStartInfo
        {
            FileName = request.Command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        };

        foreach (var arg in request.Arguments)
            psi.ArgumentList.Add(arg);

        foreach (var (key, value) in _profile.Environment)
            psi.Environment[key] = value;
        foreach (var (key, value) in request.Environment)
            psi.Environment[key] = value;

        return psi;
    }

    private string ResolveWorkingDirectory(ExecutionRequest request)
    {
        var effective = request.WorkingDirectory ?? _profile.WorkingDirectory ?? _workspaceRoot ?? Environment.CurrentDirectory;

        if (!request.RequireWorkspace)
            return effective;

        var workspaceRoot = _workspaceRoot ?? _profile.WorkingDirectory ?? _profile.WorkspaceRoot;
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new InvalidOperationException("Execution backend 'local' requires a configured workspace root for this request.");

        var resolvedWorkspaceRoot = ResolveFullPath(workspaceRoot);
        var resolvedWorkingDirectory = ResolveFullPath(effective);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!string.Equals(resolvedWorkingDirectory, resolvedWorkspaceRoot, comparison))
        {
            var workspacePrefix = resolvedWorkspaceRoot.EndsWith(Path.DirectorySeparatorChar)
                ? resolvedWorkspaceRoot
                : resolvedWorkspaceRoot + Path.DirectorySeparatorChar;
            if (!resolvedWorkingDirectory.StartsWith(workspacePrefix, comparison))
            {
                throw new InvalidOperationException(
                    $"Execution backend '{Name}' denied working directory '{effective}' because it is outside the configured workspace root.");
            }
        }

        return resolvedWorkingDirectory;
    }

    private static string ResolveFullPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Environment.CurrentDirectory;

        if (value.StartsWith("~/", StringComparison.Ordinal) || string.Equals(value, "~", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            value = value.Length == 1 ? home : Path.Combine(home, value[2..]);
        }

        return Path.GetFullPath(value);
    }
}

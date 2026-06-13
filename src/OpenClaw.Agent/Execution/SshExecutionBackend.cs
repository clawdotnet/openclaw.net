using System.Diagnostics;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent.Execution;

internal sealed class SshExecutionBackend : ProcessExecutionBackendBase
{
    private readonly string _name;
    private readonly ExecutionBackendProfileConfig _profile;

    public SshExecutionBackend(string name, ExecutionBackendProfileConfig profile)
    {
        _name = name;
        _profile = profile;
    }

    public override string Name => _name;

    public override Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
        => ExecuteProcessAsync(Name, CreateProcessStartInfo(request), request.StandardInput, _profile.TimeoutSeconds, cancellationToken);

    protected override ProcessStartInfo CreateProcessStartInfo(ExecutionRequest request)
    {
        if (string.IsNullOrWhiteSpace(_profile.Host) || string.IsNullOrWhiteSpace(_profile.Username))
            throw new InvalidOperationException($"Execution backend '{Name}' requires Host and Username.");

        var psi = new ProcessStartInfo
        {
            FileName = "ssh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(_profile.Port.ToString());

        if (!string.IsNullOrWhiteSpace(_profile.PrivateKeyPath))
        {
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(_profile.PrivateKeyPath);
        }

        psi.ArgumentList.Add($"{_profile.Username}@{_profile.Host}");

        var remoteCommand = request.Command;
        if (request.Arguments.Length > 0)
            remoteCommand += " " + string.Join(" ", request.Arguments.Select(QuoteIfNeeded));

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory ?? _profile.WorkingDirectory))
            remoteCommand = $"cd {QuoteIfNeeded(request.WorkingDirectory ?? _profile.WorkingDirectory!)} && {remoteCommand}";

        foreach (var (key, value) in _profile.Environment.Concat(request.Environment))
            remoteCommand = $"{key}={QuoteIfNeeded(value)} {remoteCommand}";

        psi.ArgumentList.Add(remoteCommand);
        return psi;
    }

    private static string QuoteIfNeeded(string value)
        => value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : value;
}

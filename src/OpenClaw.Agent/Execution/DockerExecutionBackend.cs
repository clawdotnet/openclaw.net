using System.Diagnostics;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent.Execution;

internal sealed class DockerExecutionBackend : ProcessExecutionBackendBase
{
    private readonly string _name;
    private readonly ExecutionBackendProfileConfig _profile;

    public DockerExecutionBackend(string name, ExecutionBackendProfileConfig profile)
    {
        _name = name;
        _profile = profile;
    }

    public override string Name => _name;

    public override Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
        => ExecuteProcessAsync(Name, CreateProcessStartInfo(request), request.StandardInput, _profile.TimeoutSeconds, cancellationToken);

    protected override ProcessStartInfo CreateProcessStartInfo(ExecutionRequest request)
    {
        var image = request.Template ?? _profile.Image;
        if (string.IsNullOrWhiteSpace(image))
            throw new InvalidOperationException($"Execution backend '{Name}' requires an image.");

        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--rm");

        var workingDir = request.WorkingDirectory ?? _profile.WorkingDirectory;
        if (!string.IsNullOrWhiteSpace(workingDir))
        {
            psi.ArgumentList.Add("-w");
            psi.ArgumentList.Add(workingDir);
        }

        foreach (var (key, value) in _profile.Environment)
        {
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add($"{key}={value}");
        }

        foreach (var (key, value) in request.Environment)
        {
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add($"{key}={value}");
        }

        psi.ArgumentList.Add(image);
        psi.ArgumentList.Add(request.Command);
        foreach (var arg in request.Arguments)
            psi.ArgumentList.Add(arg);

        return psi;
    }
}

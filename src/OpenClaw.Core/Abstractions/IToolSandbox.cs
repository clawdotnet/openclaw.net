using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

public interface IToolSandbox
{
    Task<SandboxResult> ExecuteAsync(
        SandboxExecutionRequest request,
        CancellationToken cancellationToken = default);
}


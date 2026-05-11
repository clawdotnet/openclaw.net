using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Governance;

public sealed class NoopToolGovernanceService : IToolGovernanceService
{
    public ValueTask<GovernanceDecision> AuthorizeAsync(
        ToolGovernanceContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(GovernanceDecision.Allow("Governance disabled"));

    public ValueTask RecordResultAsync(
        ToolGovernanceContext context,
        GovernanceDecision decision,
        ToolGovernanceExecutionResult result,
        CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}

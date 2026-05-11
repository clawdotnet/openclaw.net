using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

public interface IToolGovernanceService
{
    ValueTask<GovernanceDecision> AuthorizeAsync(
        ToolGovernanceContext context,
        CancellationToken cancellationToken = default);

    ValueTask RecordResultAsync(
        ToolGovernanceContext context,
        GovernanceDecision decision,
        ToolGovernanceExecutionResult result,
        CancellationToken cancellationToken = default);
}

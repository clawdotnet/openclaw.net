using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

public sealed record PlanExecuteVerifyToolContext
{
    public required Session Session { get; init; }
    public required string CorrelationId { get; init; }
    public string? CallId { get; init; }
    public required string ToolName { get; init; }
    public required string ArgumentsJson { get; init; }
    public required ToolActionDescriptor ActionDescriptor { get; init; }
    public required ToolGovernanceDescriptor GovernanceDescriptor { get; init; }
    public bool ExistingApprovalRequired { get; init; }
    public bool IsStreaming { get; init; }
    public int ToolCallCount { get; init; } = 1;
}

public interface IPlanExecuteVerifyOrchestrator
{
    ValueTask<PlanExecuteVerifyDecision> EvaluateToolAsync(
        PlanExecuteVerifyToolContext context,
        CancellationToken cancellationToken = default);

    ValueTask RecordApprovalDecisionAsync(
        PlanExecuteVerifyRun? run,
        bool approved,
        CancellationToken cancellationToken = default);

    ValueTask<PlanExecuteVerifyRun?> CompleteToolAsync(
        PlanExecuteVerifyRun? run,
        ToolInvocation invocation,
        CancellationToken cancellationToken = default);

    ValueTask<PlanExecuteVerifyRun?> VerifyRunAsync(string runId, CancellationToken cancellationToken = default);

    PlanExecuteVerifyRun? GetRun(string id);

    IReadOnlyList<PlanExecuteVerifyRun> ListRuns(int limit = 100);
}

public sealed class NoopPlanExecuteVerifyOrchestrator : IPlanExecuteVerifyOrchestrator
{
    public static NoopPlanExecuteVerifyOrchestrator Instance { get; } = new();

    public ValueTask<PlanExecuteVerifyDecision> EvaluateToolAsync(
        PlanExecuteVerifyToolContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new PlanExecuteVerifyDecision
        {
            Decision = PlanExecuteVerifyDecisionKinds.Proceed,
            RequiresPlanExecuteVerify = false,
            RequiresApproval = false,
            RiskLevel = HarnessContractRiskLevels.Low,
            Summary = "Plan-Execute-Verify mode is disabled."
        });

    public ValueTask RecordApprovalDecisionAsync(
        PlanExecuteVerifyRun? run,
        bool approved,
        CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask<PlanExecuteVerifyRun?> CompleteToolAsync(
        PlanExecuteVerifyRun? run,
        ToolInvocation invocation,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<PlanExecuteVerifyRun?>(run);

    public ValueTask<PlanExecuteVerifyRun?> VerifyRunAsync(string runId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<PlanExecuteVerifyRun?>(null);

    public PlanExecuteVerifyRun? GetRun(string id) => null;

    public IReadOnlyList<PlanExecuteVerifyRun> ListRuns(int limit = 100) => [];
}

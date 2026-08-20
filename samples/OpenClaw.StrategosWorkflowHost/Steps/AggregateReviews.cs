using OpenClaw.StrategosWorkflowHost.Workflows;
using Strategos.Abstractions;
using Strategos.Steps;

namespace OpenClaw.StrategosWorkflowHost.Steps;

// Join target: combine the three reviewers' verdicts into one summary + average confidence.
public sealed class AggregateReviews : IWorkflowStep<ReviewState>
{
    public Task<StepResult<ReviewState>> ExecuteAsync(
        ReviewState state, StepContext context, CancellationToken cancellationToken)
    {
        var summary = string.Join(" | ", state.Reviews.Select(r => $"{r.Role}:{r.Verdict}"));
        var confidence = state.Reviews.Count > 0
            ? state.Reviews.Average(r => r.Confidence)
            : 0.0;
        return Task.FromResult(StepResult<ReviewState>.WithConfidence(
            state with
            {
                AggregatedSummary = summary,
                AggregateConfidence = confidence
            },
            confidence));
    }
}
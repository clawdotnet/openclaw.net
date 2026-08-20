using Microsoft.Extensions.AI;
using OpenClaw.StrategosWorkflowHost.Workflows;
using OpenClaw.StrategosWorkflowHost.Workflows.Models;
using Strategos.Abstractions;
using Strategos.Steps;

namespace OpenClaw.StrategosWorkflowHost.Steps;

public sealed class ArchitectureReviewer(IChatClient chat) : IWorkflowStep<ReviewState>
{
    public async Task<StepResult<ReviewState>> ExecuteAsync(
        ReviewState state, StepContext context, CancellationToken cancellationToken)
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.System, "Return ONLY JSON: {role,verdict,summary,confidence}."),
            new ChatMessage(ChatRole.User, PromptBuilders.Architecture(state.Plan, state.UserRequest)),
        };
        var response = await chat.GetResponseAsync<ReviewVerdict>(
            messages, cancellationToken: cancellationToken);
        if (!response.TryGetResult(out var verdict) || verdict is null)
            throw new InvalidOperationException("LLM did not return an architecture verdict.");
        var stamped = verdict with { Role = "architecture" };
        return StepResult<ReviewState>.WithConfidence(
            state with { Reviews = state.Reviews.Append(stamped).ToList() },
            stamped.Confidence);
    }
}
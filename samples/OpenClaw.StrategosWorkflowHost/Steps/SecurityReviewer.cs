using Microsoft.Extensions.AI;
using OpenClaw.StrategosWorkflowHost.Workflows;
using OpenClaw.StrategosWorkflowHost.Workflows.Models;
using Strategos.Abstractions;
using Strategos.Steps;

namespace OpenClaw.StrategosWorkflowHost.Steps;

// Fork branch: each reviewer is invoked concurrently and only sees the pre-fork state.
// IChatClient is injected via primary constructor; the source generator registers the step
// as transient so DI resolves IChatClient per call.
public sealed class SecurityReviewer(IChatClient chat) : IWorkflowStep<ReviewState>
{
    public async Task<StepResult<ReviewState>> ExecuteAsync(
        ReviewState state, StepContext context, CancellationToken cancellationToken)
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.System, "Return ONLY JSON: {role,verdict,summary,confidence}."),
            new ChatMessage(ChatRole.User, PromptBuilders.Security(state.Plan, state.UserRequest)),
        };
        var response = await chat.GetResponseAsync<ReviewVerdict>(
            messages, cancellationToken: cancellationToken);
        if (!response.TryGetResult(out var verdict) || verdict is null)
            throw new InvalidOperationException("LLM did not return a security verdict.");
        var stamped = verdict with { Role = "security" };
        return StepResult<ReviewState>.WithConfidence(
            state with { Reviews = state.Reviews.Append(stamped).ToList() },
            stamped.Confidence);
    }
}
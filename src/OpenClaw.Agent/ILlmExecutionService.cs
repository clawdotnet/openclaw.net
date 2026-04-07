using Microsoft.Extensions.AI;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;

namespace OpenClaw.Agent;

public sealed class LlmExecutionEstimate
{
    public long EstimatedInputTokens { get; init; }
    public required InputTokenComponentEstimate EstimatedInputTokensByComponent { get; init; }
}

public sealed class LlmExecutionResult
{
    public string? ProfileId { get; init; }
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
    public string? PolicyRuleId { get; init; }
    public string? SelectionExplanation { get; init; }
    public required ChatResponse Response { get; init; }
}

public sealed class LlmStreamingExecutionResult
{
    public string? ProfileId { get; init; }
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
    public string? PolicyRuleId { get; init; }
    public string? SelectionExplanation { get; init; }
    public required IAsyncEnumerable<ChatResponseUpdate> Updates { get; init; }
}

public interface ILlmExecutionService
{
    CircuitState DefaultCircuitState { get; }

    Task<LlmExecutionResult> GetResponseAsync(
        Session session,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions options,
        TurnContext turnContext,
        LlmExecutionEstimate estimate,
        CancellationToken ct);

    Task<LlmStreamingExecutionResult> StartStreamingAsync(
        Session session,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions options,
        TurnContext turnContext,
        LlmExecutionEstimate estimate,
        CancellationToken ct);
}

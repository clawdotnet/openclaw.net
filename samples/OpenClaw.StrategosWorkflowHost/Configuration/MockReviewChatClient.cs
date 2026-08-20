using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenClaw.StrategosWorkflowHost.Workflows.Models;

namespace OpenClaw.StrategosWorkflowHost.Configuration;

// A real IChatClient implementation for Mock mode: returns a fixed per-role verdict JSON so the
// Strategos agent-step -> IChatClient -> verdict-parse path runs end-to-end with no LLM keys.
// The three roles all return Confidence=0.8 so the workflow deterministically reaches
// AssessConfidence (0.8 < 0.85 -> OnLowConfidence -> AwaitApproval), exercising the approval gate.
public sealed class MockReviewChatClient : IChatClient
{
    public ChatClientMetadata Metadata => new("mock-review");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var role = ExtractRole(messages);
        var verdict = new ReviewVerdict(role, "review-required", "Mock review: no critical risk detected.", 0.8);
        var json = JsonSerializer.Serialize(verdict);
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, json)
        {
            AuthorName = "mock-review"
        });
        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var text = (await GetResponseAsync(messages, options, cancellationToken)).Messages[0].Text;
        yield return new ChatResponseUpdate(ChatRole.Assistant, text);
    }

    // IChatClient extends IAIService in MEAI 10.7+; mock exposes no services.
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    private static string ExtractRole(IEnumerable<ChatMessage> messages)
        => messages.Select(static m => m.Text).FirstOrDefault(static t => t is "security" or "architecture" or "cost") ?? "security";
}

using OpenClaw.Core.Models;

namespace OpenClaw.MicrosoftAgentFrameworkAdapter.A2A;

public sealed record OpenClawA2AExecutionRequest
{
    public required string SessionId { get; init; }
    public required string ChannelId { get; init; }
    public required string SenderId { get; init; }
    public required string UserText { get; init; }
    public string? MessageId { get; init; }
}

public interface IOpenClawA2AExecutionBridge
{
    Task ExecuteStreamingAsync(
        OpenClawA2AExecutionRequest request,
        Func<AgentStreamEvent, CancellationToken, ValueTask> onEvent,
        CancellationToken cancellationToken);
}

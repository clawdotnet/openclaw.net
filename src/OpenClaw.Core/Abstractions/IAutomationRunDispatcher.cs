using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

public sealed class AutomationDispatchRequest
{
    public required string AutomationId { get; init; }
    public string TriggerSource { get; init; } = AutomationRunTriggerSources.Manual;
    public string? ReplayOfRunId { get; init; }
    public int RetryAttempt { get; init; }
    public required string SessionId { get; init; }
    public required string ChannelId { get; init; }
    public required string SenderId { get; init; }
    public required string Prompt { get; init; }
    public string? Subject { get; init; }
}

public interface IAutomationRunDispatcher
{
    ValueTask<InboundMessage?> PrepareDispatchAsync(AutomationDispatchRequest request, CancellationToken ct);
}

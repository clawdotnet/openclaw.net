namespace OpenClaw.Routing.Jev;

internal sealed class JevRoutingState
{
    public required string CurrentRequest { get; init; }
    public required JevHistoryMessage[] RecentConversation { get; init; }
}

internal sealed class JevHistoryMessage
{
    public required string Role { get; init; }
    public required string Text { get; init; }
}

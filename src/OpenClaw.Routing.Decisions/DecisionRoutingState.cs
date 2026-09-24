namespace OpenClaw.Routing.Decisions;

internal sealed class DecisionRoutingState
{
    public required string CurrentRequest { get; init; }
    public required DecisionHistoryMessage[] RecentConversation { get; init; }
}

internal sealed class DecisionHistoryMessage
{
    public required string Role { get; init; }
    public required string Text { get; init; }
}

namespace OpenClaw.Agent;

public sealed record AgentTurnResult
{
    public required string Text { get; init; }
    public bool ShouldContinue { get; init; }
    public AgentTurnStopReason StopReason { get; init; } = AgentTurnStopReason.Completed;
    public string? ContinuePrompt { get; init; }

    public static AgentTurnResult Completed(string text)
        => new() { Text = text, ShouldContinue = false, StopReason = AgentTurnStopReason.Completed };
}

public enum AgentTurnStopReason
{
    Completed,
    GoalContinuationRequired,
    BatchLimitReached,
    Blocked,
    BudgetLimited,
    Failed
}

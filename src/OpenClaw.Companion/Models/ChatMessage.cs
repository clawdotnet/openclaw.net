namespace OpenClaw.Companion.Models;

public enum ChatRole : byte
{
    System = 0,
    User = 1,
    Assistant = 2
}

public sealed record ChatMessage
{
    public required ChatRole Role { get; init; }
    public required string Text { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public string RoleLabel => Role switch
    {
        ChatRole.User => "You",
        ChatRole.Assistant => "OpenClaw",
        _ => "System"
    };
}

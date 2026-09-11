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
        ChatRole.Assistant => "AgentQi",
        _ => "System"
    };

    public bool IsUser => Role == ChatRole.User;
    public bool IsAssistant => Role == ChatRole.Assistant;
    public bool IsSystem => Role == ChatRole.System && !IsToolEvent && !IsError;
    private string SafeText => Text ?? "";
    public bool IsToolEvent => Role == ChatRole.System &&
        (SafeText.StartsWith("Agent invoked tool:", StringComparison.OrdinalIgnoreCase) ||
         SafeText.Contains("tool approval", StringComparison.OrdinalIgnoreCase) ||
         SafeText.Contains("requires operator approval", StringComparison.OrdinalIgnoreCase));

    public bool IsError => Role == ChatRole.System &&
        (SafeText.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
         SafeText.Contains(" failed", StringComparison.OrdinalIgnoreCase) ||
         SafeText.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
         SafeText.Contains("blocked", StringComparison.OrdinalIgnoreCase) ||
         SafeText.Contains("denied", StringComparison.OrdinalIgnoreCase));

    public bool IsStreamingPlaceholder => Role == ChatRole.Assistant && string.IsNullOrWhiteSpace(Text);
}

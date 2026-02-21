namespace OpenClaw.Core.Contacts;

public sealed record Contact
{
    public required string PhoneE164 { get; init; }
    public string? DisplayName { get; init; }
    public bool DoNotText { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record ContactStoreState
{
    public Dictionary<string, Contact> ContactsByPhone { get; init; } = new(StringComparer.Ordinal);
}


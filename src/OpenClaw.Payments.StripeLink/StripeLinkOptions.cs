namespace OpenClaw.Payments.StripeLink;

public sealed record StripeLinkOptions
{
    public string ProviderId { get; init; } = "stripe-link";
    public string CliPath { get; init; } = "link-cli";
    public string Mode { get; init; } = "test";
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public string? WorkingDirectory { get; init; }
    public Dictionary<string, string> EnvironmentVariables { get; init; } = new(StringComparer.Ordinal);
}

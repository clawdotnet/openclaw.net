namespace OpenClaw.Core.Models;

public sealed class ConfigSourceDiagnosticItem
{
    public required string Label { get; init; }
    public required string Key { get; init; }
    public required string EffectiveValue { get; init; }
    public required string Source { get; init; }
    public bool Redacted { get; init; }
}

public sealed class ConfigSourceDiagnostics
{
    public IReadOnlyList<ConfigSourceDiagnosticItem> Items { get; init; } = [];
}

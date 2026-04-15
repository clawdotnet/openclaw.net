namespace OpenClaw.Core.Models;

public sealed class CompatibilityCatalogResponse
{
    public int Version { get; init; }
    public string Source { get; init; } = "compat/public-smoke.json";
    public IReadOnlyList<CompatibilityCatalogEntry> Items { get; init; } = [];
}

public sealed class CompatibilityCatalogEntry
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required string Kind { get; init; }
    public required string Subject { get; init; }
    public string ScenarioType { get; init; } = "positive";
    public string CompatibilityStatus { get; init; } = "unknown";
    public string InstallSurface { get; init; } = "";
    public string InstallCommand { get; init; } = "";
    public string Summary { get; init; } = "";
    public string? PackageSpec { get; init; }
    public string? PackageName { get; init; }
    public string? PluginId { get; init; }
    public string? SkillSlug { get; init; }
    public string? PackageVersion { get; init; }
    public string? ExpectedRelativePath { get; init; }
    public string? ConfigJsonExample { get; init; }
    public string[] InstallExtraPackages { get; init; } = [];
    public string[] ExpectedToolNames { get; init; } = [];
    public string[] ExpectedSkillNames { get; init; } = [];
    public string[] ExpectedDiagnosticCodes { get; init; } = [];
    public string[] Guidance { get; init; } = [];
}

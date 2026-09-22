using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Compatibility;

public static class PublicCompatibilityCatalog
{
    private const string ResourceName = "OpenClaw.Core.Compatibility.public-smoke.json";
    private static readonly Assembly CatalogAssembly = typeof(PublicCompatibilityCatalog).Assembly;
    private static readonly Lazy<CompatibilityCatalogResponse> Catalog = new(LoadCatalog);

    public static CompatibilityCatalogResponse GetCatalog(
        string? compatibilityStatus = null,
        string? kind = null,
        string? category = null)
    {
        var all = Catalog.Value;
        var items = all.Items
            .Where(item => Matches(item.CompatibilityStatus, compatibilityStatus))
            .Where(item => Matches(item.Kind, kind))
            .Where(item => Matches(item.Category, category))
            .OrderBy(static item => item.CompatibilityStatus, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Subject, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new CompatibilityCatalogResponse
        {
            Version = all.Version,
            Source = all.Source,
            Items = items
        };
    }

    private static bool Matches(string value, string? filter)
        => string.IsNullOrWhiteSpace(filter) || string.Equals(value, filter.Trim(), StringComparison.OrdinalIgnoreCase);

    private static CompatibilityCatalogResponse LoadCatalog()
    {
        using var stream = CatalogAssembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded compatibility catalog '{ResourceName}' was not found.");
        var manifest = JsonSerializer.Deserialize(stream, CompatibilityCatalogJsonContext.Default.CompatibilityCatalogManifest)
            ?? throw new InvalidOperationException("Compatibility catalog manifest could not be parsed.");

        return CreateCatalog(manifest);
    }

    internal static CompatibilityCatalogResponse CreateCatalog(CompatibilityCatalogManifest manifest)
    {
        return new CompatibilityCatalogResponse
        {
            Version = manifest.Version,
            Items = manifest.Entries.Select(MapEntry).ToArray()
        };
    }

    private static CompatibilityCatalogEntry MapEntry(CompatibilityCatalogManifestEntry entry)
    {
        var compatibilityStatus = ResolveCompatibilityStatus(entry);
        var installSurface = string.Equals(entry.Kind, "clawhub-skill", StringComparison.Ordinal)
            ? "clawhub"
            : "npm";
        var subject = entry.Slug
            ?? entry.PackageName
            ?? entry.PluginId
            ?? entry.Id;
        var scenarioType = compatibilityStatus.Equals("compatible", StringComparison.OrdinalIgnoreCase)
            ? "positive"
            : "negative";

        return new CompatibilityCatalogEntry
        {
            Id = entry.Id,
            Category = entry.Category,
            Kind = entry.Kind,
            Subject = subject,
            ScenarioType = scenarioType,
            CompatibilityStatus = compatibilityStatus,
            InstallSurface = installSurface,
            InstallCommand = BuildInstallCommand(entry),
            Summary = BuildSummary(entry, compatibilityStatus),
            PackageSpec = entry.Spec,
            PackageName = entry.PackageName,
            PluginId = entry.PluginId,
            SkillSlug = entry.Slug,
            SkillRef = entry.Ref,
            PackageVersion = entry.Version,
            ExpectedRelativePath = entry.ExpectedRelativePath,
            ConfigJsonExample = entry.ConfigJson,
            InstallExtraPackages = entry.InstallExtraPackages ?? [],
            LatestCanaryInstallExtraPackages = entry.LatestCanaryInstallExtraPackages ?? [],
            ExpectedToolNames = entry.ExpectedToolNames ?? [],
            ExpectedSkillNames = entry.ExpectedSkillNames ?? [],
            ExpectedCliCommandNames = entry.ExpectedCliCommandNames ?? [],
            ExpectedDiagnosticCodes = entry.ExpectedDiagnosticCodes ?? [],
            Guidance = BuildGuidance(entry, compatibilityStatus).ToArray()
        };
    }

    private static string BuildInstallCommand(CompatibilityCatalogManifestEntry entry)
    {
        if (string.Equals(entry.Kind, "clawhub-skill", StringComparison.Ordinal))
        {
            _ = RequireField(entry.Slug, entry, "slug");
            var skillRef = RequireField(entry.Ref, entry, "ref");
            var suffix = string.IsNullOrWhiteSpace(entry.Version)
                ? string.Empty
                : $" --version {entry.Version}";
            return $"openclaw clawhub install {skillRef}{suffix}";
        }

        var spec = RequireField(entry.Spec, entry, "spec");
        return $"openclaw plugins install {spec} --dry-run";
    }

    private static string BuildSummary(CompatibilityCatalogManifestEntry entry, string compatibilityStatus)
    {
        return entry.Category switch
        {
            "pure-skill" => "Pinned standalone skill package expected to install and parse through the upstream SKILL.md flow.",
            "js-tool-plugin" when compatibilityStatus.Equals("compatible", StringComparison.OrdinalIgnoreCase)
                => "Pinned JavaScript bridge plugin expected to load and expose its declared tools and skills.",
            "ts-jiti-plugin" when compatibilityStatus.Equals("compatible", StringComparison.OrdinalIgnoreCase)
                => "Pinned TypeScript bridge plugin expected to load when jiti is present in the plugin dependency tree.",
            "config-schema-plugin"
                => "Negative compatibility scenario proving that invalid plugin config fails fast with structured diagnostics.",
            "cli-plugin" when compatibilityStatus.Equals("compatible", StringComparison.OrdinalIgnoreCase)
                => "Pinned upstream plugin expected to load and expose its lazy root CLI command through the Node bridge.",
            "unsupported-surface-plugin"
                => "Negative compatibility scenario proving that unsupported upstream plugin surfaces fail explicitly instead of loading partially.",
            _ when compatibilityStatus.Equals("compatible", StringComparison.OrdinalIgnoreCase)
                => "Pinned compatibility scenario expected to load successfully under the documented OpenClaw.NET subset.",
            _ => "Pinned negative compatibility scenario expected to fail with explicit diagnostics."
        };
    }

    private static IEnumerable<string> BuildGuidance(CompatibilityCatalogManifestEntry entry, string compatibilityStatus)
    {
        if (string.Equals(entry.Kind, "clawhub-skill", StringComparison.Ordinal))
        {
            yield return "Remote upstream skills use the ClawHub install flow; local copies can be installed with `openclaw skills install`.";
            if (!string.IsNullOrWhiteSpace(entry.ExpectedRelativePath))
                yield return $"Expected installed file: {entry.ExpectedRelativePath}.";
            yield break;
        }

        yield return "Run the dry-run installer first so manifest validation and declared surfaces are reported before the package is copied into extensions.";

        if (entry.InstallExtraPackages is { Length: > 0 })
            yield return $"Install extra packages in the plugin dependency tree before load: {string.Join(", ", entry.InstallExtraPackages)}.";

        if (!string.IsNullOrWhiteSpace(entry.ConfigJson))
            yield return "This scenario pins a specific plugin config example; use it as a starting point when comparing your own configuration.";

        if (entry.ExpectedToolNames is { Length: > 0 })
            yield return $"Expected tool surfaces: {string.Join(", ", entry.ExpectedToolNames)}.";

        if (entry.ExpectedSkillNames is { Length: > 0 })
            yield return $"Expected bundled skills: {string.Join(", ", entry.ExpectedSkillNames)}.";

        if (entry.ExpectedCliCommandNames is { Length: > 0 })
            yield return $"Expected root CLI commands: {string.Join(", ", entry.ExpectedCliCommandNames)}.";

        if (compatibilityStatus.Equals("incompatible", StringComparison.OrdinalIgnoreCase) &&
            entry.ExpectedDiagnosticCodes is { Length: > 0 })
        {
            yield return $"Expected failure diagnostics: {string.Join(", ", entry.ExpectedDiagnosticCodes)}.";
        }

        if (entry.ExpectedDiagnosticCodes?.Any(code => string.Equals(code, "config_one_of_mismatch", StringComparison.Ordinal)) == true)
            yield return "Adjust the plugin config to the supported JSON-schema subset; this scenario intentionally demonstrates a failing shape.";
    }

    private static string ResolveCompatibilityStatus(CompatibilityCatalogManifestEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.ExpectedStatus))
            return entry.ExpectedStatus!;

        if (string.Equals(entry.Kind, "clawhub-skill", StringComparison.Ordinal))
            return "compatible";

        throw new InvalidOperationException(
            $"Compatibility catalog entry '{entry.Id}' of kind '{entry.Kind}' must declare expectedStatus.");
    }

    private static string RequireField(string? value, CompatibilityCatalogManifestEntry entry, string fieldName)
    {
        if (!string.IsNullOrWhiteSpace(value))
            return value!;

        throw new InvalidOperationException(
            $"Compatibility catalog entry '{entry.Id}' of kind '{entry.Kind}' must declare '{fieldName}'.");
    }
}

internal sealed class CompatibilityCatalogManifest
{
    public int Version { get; set; }
    public CompatibilityCatalogManifestEntry[] Entries { get; set; } = [];
}

internal sealed class CompatibilityCatalogManifestEntry
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Spec { get; set; }
    public string? PackageName { get; set; }
    public string? PluginId { get; set; }
    public string? Slug { get; set; }
    public string? Ref { get; set; }
    public string? Version { get; set; }
    public string? ExpectedStatus { get; set; }
    public string? ExpectedRelativePath { get; set; }
    public string? ConfigJson { get; set; }
    public string[]? InstallExtraPackages { get; set; }
    public string[]? LatestCanaryInstallExtraPackages { get; set; }
    public string[]? ExpectedToolNames { get; set; }
    public string[]? ExpectedSkillNames { get; set; }
    public string[]? ExpectedCliCommandNames { get; set; }
    public string[]? ExpectedDiagnosticCodes { get; set; }
}

[JsonSerializable(typeof(CompatibilityCatalogManifest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class CompatibilityCatalogJsonContext : JsonSerializerContext
{
}

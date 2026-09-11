using OpenClaw.Core.Compatibility;
using Xunit;

namespace OpenClaw.Tests;

public sealed class CompatibilityCatalogTests
{
    [Fact]
    public void GetCatalog_LoadsPinnedEntriesAndGeneratedGuidance()
    {
        var catalog = PublicCompatibilityCatalog.GetCatalog();

        Assert.True(catalog.Version >= 2);
        Assert.NotEmpty(catalog.Items);

        var tavily = Assert.Single(catalog.Items, item => item.Id == "openclaw-tavily");
        Assert.Equal("compatible", tavily.CompatibilityStatus);
        Assert.Equal("npm-plugin", tavily.Kind);
        Assert.Contains("openclaw plugins install openclaw-tavily@0.2.1 --dry-run", tavily.InstallCommand, StringComparison.Ordinal);
        Assert.Contains(tavily.InstallExtraPackages, pkg => pkg == "jiti@2.7.0");
        Assert.Contains(tavily.Guidance, note => note.Contains("jiti", StringComparison.OrdinalIgnoreCase));

        var supermemory = Assert.Single(catalog.Items, item => item.Id == "supermemory");
        Assert.Contains("openclaw@2026.1.29", supermemory.InstallExtraPackages);
        Assert.Contains("jiti@latest", supermemory.LatestCanaryInstallExtraPackages);
        Assert.Contains("openclaw@latest", supermemory.LatestCanaryInstallExtraPackages);

        var invalidConfig = Assert.Single(catalog.Items, item => item.Id == "openclaw-tavily-invalid-config");
        Assert.Equal("negative", invalidConfig.ScenarioType);
        Assert.Equal("incompatible", invalidConfig.CompatibilityStatus);
        Assert.Contains("config_one_of_mismatch", invalidConfig.ExpectedDiagnosticCodes);
    }

    [Fact]
    public void GetCatalog_AppliesFilters()
    {
        var catalog = PublicCompatibilityCatalog.GetCatalog(compatibilityStatus: "compatible", kind: "clawhub-skill", category: null);

        var item = Assert.Single(catalog.Items);
        Assert.Equal("compatible", item.CompatibilityStatus);
        Assert.Equal("clawhub-skill", item.Kind);
        Assert.Equal("peekaboo", item.SkillSlug);
        Assert.Equal("@steipete/peekaboo", item.SkillRef);
        Assert.Equal("openclaw clawhub install @steipete/peekaboo --version 1.0.0", item.InstallCommand);
    }

    [Fact]
    public void CreateCatalog_NpmPluginWithoutExpectedStatus_FailsFast()
    {
        var manifest = new CompatibilityCatalogManifest
        {
            Version = 1,
            Entries =
            [
                new CompatibilityCatalogManifestEntry
                {
                    Id = "missing-status",
                    Category = "js-tool-plugin",
                    Kind = "npm-plugin",
                    Spec = "example-plugin@1.0.0",
                    PackageName = "example-plugin",
                    PluginId = "example-plugin"
                }
            ]
        };

        var ex = Assert.Throws<InvalidOperationException>(() => PublicCompatibilityCatalog.CreateCatalog(manifest));
        Assert.Contains("expectedStatus", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateCatalog_SkillWithoutSlug_FailsFast()
    {
        var manifest = new CompatibilityCatalogManifest
        {
            Version = 1,
            Entries =
            [
                new CompatibilityCatalogManifestEntry
                {
                    Id = "missing-slug",
                    Category = "pure-skill",
                    Kind = "clawhub-skill"
                }
            ]
        };

        var ex = Assert.Throws<InvalidOperationException>(() => PublicCompatibilityCatalog.CreateCatalog(manifest));
        Assert.Contains("'slug'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateCatalog_SkillWithoutRef_FailsFast()
    {
        var manifest = new CompatibilityCatalogManifest
        {
            Version = 1,
            Entries =
            [
                new CompatibilityCatalogManifestEntry
                {
                    Id = "missing-ref",
                    Category = "pure-skill",
                    Kind = "clawhub-skill",
                    Slug = "example"
                }
            ]
        };

        var ex = Assert.Throws<InvalidOperationException>(() => PublicCompatibilityCatalog.CreateCatalog(manifest));
        Assert.Contains("'ref'", ex.Message, StringComparison.Ordinal);
    }
}

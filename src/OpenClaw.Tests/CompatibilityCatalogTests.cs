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
        Assert.Contains(tavily.InstallExtraPackages, pkg => pkg == "jiti");
        Assert.Contains(tavily.Guidance, note => note.Contains("jiti", StringComparison.OrdinalIgnoreCase));

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
    }
}

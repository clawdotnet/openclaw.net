using Xunit;

namespace OpenClaw.Tests;

public sealed class DocsConsistencyTests
{
    [Fact]
    public void OnboardingDocs_ReferenceGuidedSetupAndVerification()
    {
        var root = FindRepositoryRoot();
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        var quickstart = File.ReadAllText(Path.Combine(root, "docs", "QUICKSTART.md"));
        var userGuide = File.ReadAllText(Path.Combine(root, "docs", "USER_GUIDE.md"));
        var dockerhub = File.ReadAllText(Path.Combine(root, "docs", "DOCKERHUB.md"));

        Assert.Contains("openclaw setup", readme, StringComparison.Ordinal);
        Assert.Contains("openclaw setup launch", readme, StringComparison.Ordinal);
        Assert.Contains("openclaw setup service", readme, StringComparison.Ordinal);
        Assert.Contains("openclaw setup status", readme, StringComparison.Ordinal);
        Assert.Contains("docs/COMPATIBILITY.md", readme, StringComparison.Ordinal);
        Assert.Contains("openclaw skills inspect", readme, StringComparison.Ordinal);
        Assert.Contains("/admin/skills", readme, StringComparison.Ordinal);
        Assert.Contains("openclaw compatibility catalog", readme, StringComparison.Ordinal);
        Assert.Contains("openclaw migrate upstream", readme, StringComparison.Ordinal);
        Assert.Contains("/admin/observability/summary", readme, StringComparison.Ordinal);
        Assert.Contains("/admin/audit/export", readme, StringComparison.Ordinal);
        Assert.Contains("Breaking change", readme, StringComparison.Ordinal);
        Assert.Contains("operator account tokens", readme, StringComparison.Ordinal);

        Assert.Contains("openclaw setup", quickstart, StringComparison.Ordinal);
        Assert.Contains("openclaw setup launch", quickstart, StringComparison.Ordinal);
        Assert.Contains("openclaw setup service", quickstart, StringComparison.Ordinal);
        Assert.Contains("openclaw setup status", quickstart, StringComparison.Ordinal);
        Assert.Contains("openclaw init", quickstart, StringComparison.Ordinal);
        Assert.Contains("--doctor", quickstart, StringComparison.Ordinal);
        Assert.Contains("openclaw admin posture", quickstart, StringComparison.Ordinal);
        Assert.Contains("COMPATIBILITY.md", quickstart, StringComparison.Ordinal);
        Assert.Contains("openclaw migrate upstream", quickstart, StringComparison.Ordinal);
        Assert.Contains("Breaking change", quickstart, StringComparison.Ordinal);
        Assert.Contains("operator account tokens", quickstart, StringComparison.Ordinal);

        Assert.Contains("openclaw setup", userGuide, StringComparison.Ordinal);
        Assert.Contains("openclaw setup launch", userGuide, StringComparison.Ordinal);
        Assert.Contains("openclaw setup service", userGuide, StringComparison.Ordinal);
        Assert.Contains("openclaw setup status", userGuide, StringComparison.Ordinal);
        Assert.Contains("openclaw init", userGuide, StringComparison.Ordinal);
        Assert.Contains("Compatibility Guide", userGuide, StringComparison.Ordinal);
        Assert.Contains("openclaw skills inspect", userGuide, StringComparison.Ordinal);
        Assert.Contains("/admin/plugins/{id}/review", userGuide, StringComparison.Ordinal);
        Assert.Contains("openclaw compatibility catalog", userGuide, StringComparison.Ordinal);
        Assert.Contains("/admin/compatibility/catalog", userGuide, StringComparison.Ordinal);
        Assert.Contains("/admin/observability/summary", userGuide, StringComparison.Ordinal);
        Assert.Contains("/admin/audit/export", userGuide, StringComparison.Ordinal);
        Assert.Contains("openclaw migrate upstream", userGuide, StringComparison.Ordinal);
        Assert.Contains("Breaking Changes", userGuide, StringComparison.Ordinal);
        Assert.Contains("operator account tokens", userGuide, StringComparison.Ordinal);

        Assert.Contains("openclaw setup", dockerhub, StringComparison.Ordinal);
        Assert.Contains("openclaw setup launch", dockerhub, StringComparison.Ordinal);
        Assert.Contains("openclaw setup service", dockerhub, StringComparison.Ordinal);
        Assert.Contains("openclaw setup status", dockerhub, StringComparison.Ordinal);
        Assert.Contains("OPENCLAW_AUTH_TOKEN", dockerhub, StringComparison.Ordinal);
        Assert.Contains("bootstrap", dockerhub, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("operator account", dockerhub, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Breaking change", dockerhub, StringComparison.Ordinal);
    }

    [Fact]
    public void CompatibilityGuide_ContainsCanonicalSections()
    {
        var root = FindRepositoryRoot();
        var compatibility = File.ReadAllText(Path.Combine(root, "docs", "COMPATIBILITY.md"));

        Assert.Contains("# Compatibility Guide", compatibility, StringComparison.Ordinal);
        Assert.Contains("## Upstream Skill Compatibility", compatibility, StringComparison.Ordinal);
        Assert.Contains("## Plugin Package Compatibility", compatibility, StringComparison.Ordinal);
        Assert.Contains("## Channel Compatibility", compatibility, StringComparison.Ordinal);
        Assert.Contains("## Operator Trust Workflow", compatibility, StringComparison.Ordinal);
        Assert.Contains("## Tested Catalog", compatibility, StringComparison.Ordinal);
        Assert.Contains("## Known Limitations", compatibility, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "README.md")) &&
                Directory.Exists(Path.Combine(current.FullName, "docs")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test output directory.");
    }
}

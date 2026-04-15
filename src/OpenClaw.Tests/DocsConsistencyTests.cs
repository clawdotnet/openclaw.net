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

        Assert.Contains("openclaw setup", readme, StringComparison.Ordinal);
        Assert.Contains("docs/COMPATIBILITY.md", readme, StringComparison.Ordinal);
        Assert.Contains("openclaw skills inspect", readme, StringComparison.Ordinal);
        Assert.Contains("/admin/skills", readme, StringComparison.Ordinal);

        Assert.Contains("openclaw setup", quickstart, StringComparison.Ordinal);
        Assert.Contains("openclaw init", quickstart, StringComparison.Ordinal);
        Assert.Contains("--doctor", quickstart, StringComparison.Ordinal);
        Assert.Contains("openclaw admin posture", quickstart, StringComparison.Ordinal);
        Assert.Contains("COMPATIBILITY.md", quickstart, StringComparison.Ordinal);

        Assert.Contains("openclaw setup", userGuide, StringComparison.Ordinal);
        Assert.Contains("openclaw init", userGuide, StringComparison.Ordinal);
        Assert.Contains("Compatibility Guide", userGuide, StringComparison.Ordinal);
        Assert.Contains("openclaw skills inspect", userGuide, StringComparison.Ordinal);
        Assert.Contains("/admin/plugins/{id}/review", userGuide, StringComparison.Ordinal);
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

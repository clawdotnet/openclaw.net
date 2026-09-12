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

        // The README is the entry point; detailed operational commands belong in
        // the linked guides, whose coverage remains asserted below.
        Assert.Contains("dotnet run --project src/OpenClaw.Cli -c Release -- start", readme, StringComparison.Ordinal);
        Assert.Contains("dotnet run --project src/OpenClaw.Companion -c Release", readme, StringComparison.Ordinal);
        Assert.Contains("samples/OpenClaw.HelloAgent", readme, StringComparison.Ordinal);
        Assert.Contains("operator account tokens", readme, StringComparison.Ordinal);
        foreach (var relativePath in new[]
        {
            "docs/QUICKSTART.md", "docs/USER_GUIDE.md", "docs/COMPATIBILITY.md",
            "docs/RELEASES.md", "docs/CAPABILITY_MATRIX.md", "docs/companion-chat-configuration.md",
            "docs/images/companion/agentqi-light.png", "docs/images/companion/agentqi-dark.png"
        })
        {
            Assert.Contains(relativePath, readme, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(root, relativePath)), $"Missing README resource: {relativePath}");
        }

        Assert.Contains("openclaw setup", quickstart, StringComparison.Ordinal);
        Assert.Contains("openclaw setup launch", quickstart, StringComparison.Ordinal);
        Assert.Contains("openclaw setup service", quickstart, StringComparison.Ordinal);
        Assert.Contains("openclaw setup status", quickstart, StringComparison.Ordinal);
        Assert.Contains("openclaw models presets", quickstart, StringComparison.Ordinal);
        Assert.Contains("openclaw maintenance scan", quickstart, StringComparison.Ordinal);
        Assert.Contains("/concise on|off|auto", quickstart, StringComparison.Ordinal);
        Assert.Contains("openclaw init", quickstart, StringComparison.Ordinal);
        Assert.Contains("--doctor", quickstart, StringComparison.Ordinal);
        Assert.Contains("openclaw admin posture", quickstart, StringComparison.Ordinal);
        Assert.Contains("COMPATIBILITY.md", quickstart, StringComparison.Ordinal);
        Assert.Contains("openclaw upgrade rollback", quickstart, StringComparison.Ordinal);
        Assert.Contains("openclaw migrate upstream", quickstart, StringComparison.Ordinal);
        Assert.Contains("Breaking change", quickstart, StringComparison.Ordinal);
        Assert.Contains("operator account tokens", quickstart, StringComparison.Ordinal);

        Assert.Contains("openclaw setup", userGuide, StringComparison.Ordinal);
        Assert.Contains("openclaw setup launch", userGuide, StringComparison.Ordinal);
        Assert.Contains("openclaw setup service", userGuide, StringComparison.Ordinal);
        Assert.Contains("openclaw setup status", userGuide, StringComparison.Ordinal);
        Assert.Contains("openclaw models presets", userGuide, StringComparison.Ordinal);
        Assert.Contains("openclaw maintenance scan", userGuide, StringComparison.Ordinal);
        Assert.Contains("/concise on", userGuide, StringComparison.Ordinal);
        Assert.Contains("openclaw init", userGuide, StringComparison.Ordinal);
        Assert.Contains("Compatibility Guide", userGuide, StringComparison.Ordinal);
        Assert.Contains("openclaw skills inspect", userGuide, StringComparison.Ordinal);
        Assert.Contains("/admin/plugins/{id}/review", userGuide, StringComparison.Ordinal);
        Assert.Contains("openclaw compatibility catalog", userGuide, StringComparison.Ordinal);
        Assert.Contains("openclaw upgrade rollback", userGuide, StringComparison.Ordinal);
        Assert.Contains("/admin/compatibility/catalog", userGuide, StringComparison.Ordinal);
        Assert.Contains("/admin/maintenance", userGuide, StringComparison.Ordinal);
        Assert.Contains("/admin/observability/summary", userGuide, StringComparison.Ordinal);
        Assert.Contains("/admin/audit/export", userGuide, StringComparison.Ordinal);
        Assert.Contains("openclaw migrate upstream", userGuide, StringComparison.Ordinal);
        Assert.Contains("Breaking Changes", userGuide, StringComparison.Ordinal);
        Assert.Contains("operator account tokens", userGuide, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:11434", userGuide, StringComparison.Ordinal);

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

    [Fact]
    public void BrandingSurfaces_DefineAgentQiCompanionWithoutRenamingRuntimeIdentity()
    {
        var root = FindRepositoryRoot();
        var paths = new[]
        {
            "README.md",
            "docs/ARCHITECTURE_BOUNDARIES.md",
            "src/OpenClaw.Companion/Assets/BRANDING.md",
            "src/OpenClaw.Companion/OpenClaw.Companion.csproj",
            "src/OpenClaw.Companion/App.axaml",
            "src/OpenClaw.Companion/Views/MainWindow.axaml",
            "src/OpenClaw.Companion/Services/DesktopNotifier.cs"
        };
        var surfaces = paths.ToDictionary(
            path => path,
            path => File.ReadAllText(Path.Combine(root, path)),
            StringComparer.Ordinal);

        Assert.All(surfaces, surface =>
        {
            Assert.Contains("AgentQi Companion", surface.Value, StringComparison.Ordinal);
            Assert.DoesNotContain("AgentQi [OpenClaw.NET]", surface.Value, StringComparison.Ordinal);
        });
        Assert.Contains("OpenClaw.NET** | Repository and runtime identity", surfaces["docs/ARCHITECTURE_BOUNDARIES.md"], StringComparison.Ordinal);
        Assert.Contains("AgentQi** | Documentation and ecosystem umbrella", surfaces["docs/ARCHITECTURE_BOUNDARIES.md"], StringComparison.Ordinal);
        Assert.Contains("AgentQiX** | Reserved likely future runtime identity", surfaces["docs/ARCHITECTURE_BOUNDARIES.md"], StringComparison.Ordinal);
        Assert.Contains("GitHub release titles | **OpenClaw.NET**", surfaces["docs/ARCHITECTURE_BOUNDARIES.md"], StringComparison.Ordinal);
        Assert.Contains("Desktop window chrome", surfaces["docs/ARCHITECTURE_BOUNDARIES.md"], StringComparison.Ordinal);
        Assert.Contains("<Product>AgentQi Companion</Product>", surfaces["src/OpenClaw.Companion/OpenClaw.Companion.csproj"], StringComparison.Ordinal);
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

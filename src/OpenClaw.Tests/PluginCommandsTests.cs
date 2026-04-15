using OpenClaw.Cli;
using Xunit;

namespace OpenClaw.Tests;

public sealed class PluginCommandsTests
{
    [Fact]
    public void InspectCandidate_WithManifest_ReturnsUpstreamCompatibleSummary()
    {
        var root = CreateTempRoot();
        try
        {
            File.WriteAllText(
                Path.Combine(root, "openclaw.plugin.json"),
                """
                {
                  "id": "sample-plugin",
                  "name": "Sample Plugin",
                  "version": "1.2.3",
                  "description": "Test plugin",
                  "channels": ["telegram"],
                  "providers": ["sample-provider"],
                  "skills": ["skills"]
                }
                """);
            File.WriteAllText(Path.Combine(root, "index.js"), "export default {};");
            var skillsDir = Path.Combine(root, "skills");
            Directory.CreateDirectory(skillsDir);
            File.WriteAllText(Path.Combine(skillsDir, "SKILL.md"), "# Sample skill");

            var inspection = PluginCommands.InspectCandidate(root, "./sample-plugin", sourceIsNpm: false);

            Assert.True(inspection.Success);
            Assert.True(inspection.CanInstall);
            Assert.Equal("sample-plugin", inspection.PluginId);
            Assert.Equal("upstream-compatible", inspection.TrustLevel);
            Assert.Equal("verified", inspection.CompatibilityStatus);
            Assert.Contains("channels=telegram", inspection.DeclaredSurface, StringComparison.Ordinal);
            Assert.Contains("providers=sample-provider", inspection.DeclaredSurface, StringComparison.Ordinal);
            Assert.Contains("skills=1", inspection.DeclaredSurface, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InspectCandidate_WithStandaloneEntry_ReturnsUntrustedWarning()
    {
        var root = CreateTempRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "index.js"), "export default {};");

            var inspection = PluginCommands.InspectCandidate(root, "./standalone-plugin", sourceIsNpm: false);

            Assert.True(inspection.Success);
            Assert.True(inspection.CanInstall);
            Assert.Equal("untrusted", inspection.TrustLevel);
            Assert.Equal("entry-only", inspection.DeclaredSurface);
            Assert.Contains(inspection.Warnings, warning => warning.Contains("No openclaw.plugin.json manifest", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InspectCandidate_WithInvalidConfigSchema_BlocksInstall()
    {
        var root = CreateTempRoot();
        try
        {
            File.WriteAllText(
                Path.Combine(root, "openclaw.plugin.json"),
                """
                {
                  "id": "schema-plugin",
                  "name": "Schema Plugin",
                  "configSchema": {
                    "type": "object",
                    "$ref": "#/definitions/unsupported"
                  }
                }
                """);
            File.WriteAllText(Path.Combine(root, "index.js"), "export default {};");

            var inspection = PluginCommands.InspectCandidate(root, "./schema-plugin", sourceIsNpm: false);

            Assert.True(inspection.Success);
            Assert.False(inspection.CanInstall);
            Assert.Equal("errors", inspection.CompatibilityStatus);
            Assert.True(inspection.ErrorCount > 0);
            Assert.Contains(inspection.Diagnostics, item => item.Code == "unsupported_schema_keyword");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-plugin-command-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }
}

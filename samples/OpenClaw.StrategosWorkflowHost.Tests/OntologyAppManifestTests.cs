using System.Text.Json;
using System.Text.Json.Nodes;

using OpenClaw.StrategosWorkflowHost.Adapters;
using OpenClaw.StrategosWorkflowHost.Configuration;

using Xunit;

namespace OpenClaw.StrategosWorkflowHost.Tests;

public class OntologyAppManifestTests
{
    [Fact]
    public void Build_Returns_Manifest_With_Stable_AppId()
    {
        var manifest = OntologyAppManifest.Build(new OntologyOptions { Port = 5098 });
        Assert.Equal("strategos-ontology", manifest["id"]!.GetValue<string>());
    }

    [Fact]
    public void Build_Points_Url_At_Loopback_And_Configured_Port()
    {
        var manifest = OntologyAppManifest.Build(new OntologyOptions { Port = 5098 });
        Assert.Equal("http", manifest["transport"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:5098/mcp", manifest["url"]!.GetValue<string>());
    }

    [Fact]
    public void Build_Advertises_ProtocolVersion_2025_03_26()
    {
        var manifest = OntologyAppManifest.Build(new OntologyOptions());
        Assert.Equal("2025-03-26", manifest["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public void Write_Creates_File_In_Target_Directory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"openclaw-manifest-{Guid.NewGuid():N}");
        try
        {
            OntologyAppManifestWriter.Write(dir, OntologyAppManifest.Build(new OntologyOptions { Port = 5098 }));

            var path = Path.Combine(dir, "openclaw.mcpapp.json");
            Assert.True(File.Exists(path), $"Expected manifest at {path}");

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            Assert.Equal("strategos-ontology", root.GetProperty("id").GetString());
            Assert.Equal("http", root.GetProperty("transport").GetString());
            Assert.Equal("http://127.0.0.1:5098/mcp", root.GetProperty("url").GetString());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Write_Throws_When_Manifest_Has_No_Id()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"openclaw-manifest-noid-{Guid.NewGuid():N}");
        try
        {
            var badManifest = new JsonObject
            {
                ["transport"] = "http",
                ["url"] = "http://127.0.0.1:5098/mcp",
            };

            Assert.Throws<ArgumentException>(() => OntologyAppManifestWriter.Write(dir, badManifest));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ExpandPath_Resolves_Tilde_To_UserProfile()
    {
        var resolved = OntologyAppManifestWriter.ExpandPath("~/.openclaw/mcp-apps");
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".openclaw", "mcp-apps");

        Assert.Equal(expected, resolved);
    }
}

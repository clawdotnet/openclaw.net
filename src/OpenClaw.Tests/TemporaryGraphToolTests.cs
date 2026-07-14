using System.Text.Json;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class TemporaryGraphToolTests
{
    [Fact]
    public async Task ExecuteAsync_MarkdownJsonLdCodeBlock_ReturnsPayloadJson()
    {
        var workspace = CreateTempDir();
        var graphPath = Path.Combine(workspace, "graph.md");
        await File.WriteAllTextAsync(
            graphPath,
            """
            # temp graph

            ```jsonld
            {
              "@context": "http://openclaw.net/ontology/industrial.jsonld",
              "@type": "ex:ProductBatch",
              "ex:id": "BATCH-001"
            }
            ```
            """);

        var config = new ToolingConfig
        {
            AllowedReadRoots = [workspace]
        };
        var tool = new LoadTemporaryGraphTool(config);

        var argsJson = JsonSerializer.Serialize(new
        {
            path = graphPath,
            format = "markdown",
            code_block_language = "jsonld"
        });

        var result = await tool.ExecuteAsync(argsJson, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(result);

        Assert.Equal("ok", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("jsonld", document.RootElement.GetProperty("payload_format").GetString());
        Assert.False(document.RootElement.GetProperty("truncated").GetBoolean());
        Assert.True(document.RootElement.TryGetProperty("payload_json", out var payloadJson));
        Assert.Contains("@type", payloadJson.GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_JsonFile_ReturnsNormalizedPayloadJson()
    {
        var workspace = CreateTempDir();
        var graphPath = Path.Combine(workspace, "temp-graph.json");
        await File.WriteAllTextAsync(graphPath, "{\"nodes\":[{\"id\":\"n1\"}],\"edges\":[]}");

        var config = new ToolingConfig
        {
            AllowedReadRoots = [workspace]
        };
        var tool = new LoadTemporaryGraphTool(config);

        var argsJson = JsonSerializer.Serialize(new
        {
            path = graphPath,
            format = "json"
        });

        var result = await tool.ExecuteAsync(argsJson, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(result);

        Assert.Equal("ok", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("json", document.RootElement.GetProperty("payload_format").GetString());
        Assert.False(document.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal("{\"nodes\":[{\"id\":\"n1\"}],\"edges\":[]}", document.RootElement.GetProperty("payload_json").GetString());
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }
}

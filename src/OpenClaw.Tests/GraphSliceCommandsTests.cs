using OpenClaw.Cli;
using OpenClaw.Core.Models;
using OpenClaw.GraphSlicer;
using Xunit;

namespace OpenClaw.Tests;

public sealed class GraphSliceCommandsTests
{
    [Fact]
    public async Task ExecuteAsync_EmptySources_ReturnsError()
    {
        var engine = new GraphSlicerEngine();
        var profile = new SliceProfile
        {
            Sources = [],
            Construct = "CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o }",
            Output = new SliceOutputConfig
            {
                Path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"), "out.jsonld")
            }
        };

        var result = await engine.ExecuteAsync(profile, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("No sources configured.", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_LocalFiles_EmptyData_ReturnsError()
    {
        var workspace = CreateTempDir();
        var ttlPath = Path.Combine(workspace, "empty.ttl");
        await File.WriteAllTextAsync(ttlPath, "@prefix ex: <http://example.org/> .\n");

        var engine = new GraphSlicerEngine();
        var profile = new SliceProfile
        {
            Sources =
            [
                new SliceSourceConfig
                {
                    Kind = "local-files",
                    Paths = [ttlPath]
                }
            ],
            Construct = "CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o }",
            Output = new SliceOutputConfig { Path = Path.Combine(workspace, "out.jsonld") }
        };

        var result = await engine.ExecuteAsync(profile, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("CONSTRUCT produced an empty graph.", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_LocalFiles_WithData_ProducesOutput()
    {
        var workspace = CreateTempDir();
        var ttlPath = Path.Combine(workspace, "data.ttl");
        await File.WriteAllTextAsync(ttlPath, """
        @prefix ex: <http://example.org/> .
        ex:Alice ex:name "Alice" ; ex:age 30 .
        """);

        var outputPath = Path.Combine(workspace, "out.jsonld");
        var engine = new GraphSlicerEngine();
        var profile = new SliceProfile
        {
            Sources =
            [
                new SliceSourceConfig
                {
                    Kind = "local-files",
                    Paths = [ttlPath]
                }
            ],
            Construct = "CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o }",
            Output = new SliceOutputConfig { Path = outputPath }
        };

        var result = await engine.ExecuteAsync(profile, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(outputPath, result.OutputPath);
        Assert.Equal(2, result.TripleCount);
        Assert.True(File.Exists(outputPath));

        var content = await File.ReadAllTextAsync(outputPath);
        Assert.Contains("Alice", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_GraphSliceCommands_Help_ReturnsZero()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = await GraphSliceCommands.RunAsync(["help"], output, error);

        Assert.Equal(0, exit);
        var text = output.ToString();
        Assert.Contains("Usage:", text, StringComparison.Ordinal);
        Assert.Contains("slice", text, StringComparison.Ordinal);
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "openclaw-graph-slicer-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }
}
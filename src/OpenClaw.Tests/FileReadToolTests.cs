using OpenClaw.Agent.Tools;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class FileReadToolTests
{
    [Fact]
    public async Task ExecuteAsync_AllowsReadWhenPathExactlyMatchesAllowedRoot()
    {
        var root = CreateTempDir();
        var path = Path.Combine(root, "note.txt");
        await File.WriteAllTextAsync(path, "hello");

        var tool = new FileReadTool(new ToolingConfig
        {
            AllowedReadRoots = [path]
        });

        var output = await tool.ExecuteAsync($$"""{"path":"{{path}}"}""", CancellationToken.None);
        Assert.Equal("hello", output);
    }

    [Fact]
    public async Task ExecuteAsync_ClampsNegativeMaxLines()
    {
        var root = CreateTempDir();
        var path = Path.Combine(root, "note.txt");
        await File.WriteAllTextAsync(path, "line1\nline2\nline3");

        var tool = new FileReadTool(new ToolingConfig
        {
            AllowedReadRoots = [root]
        });

        var output = await tool.ExecuteAsync($$"""{"path":"{{path}}","max_lines":-5}""", CancellationToken.None);
        Assert.StartsWith("line1", output, StringComparison.Ordinal);
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }
}

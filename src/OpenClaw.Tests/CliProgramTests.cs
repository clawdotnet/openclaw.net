using System.IO;
using OpenClaw.Cli;
using Xunit;

namespace OpenClaw.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class CliProgramTests
{
    [Fact]
    public void ResolveAuthToken_CliToken_WarnsAndTakesPrecedence()
    {
        var previous = Environment.GetEnvironmentVariable("OPENCLAW_AUTH_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("OPENCLAW_AUTH_TOKEN", "env-token");

            var parsed = CliArgs.Parse(["--token", "cli-token"]);
            using var error = new StringWriter();

            var token = OpenClaw.Cli.Program.ResolveAuthToken(parsed, error);

            Assert.Equal("cli-token", token);
            Assert.Contains("--token is deprecated", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_AUTH_TOKEN", previous);
        }
    }

    [Fact]
    public void ResolveAuthToken_EnvToken_DoesNotWarn()
    {
        var previous = Environment.GetEnvironmentVariable("OPENCLAW_AUTH_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("OPENCLAW_AUTH_TOKEN", "env-token");

            var parsed = CliArgs.Parse([]);
            using var error = new StringWriter();

            var token = OpenClaw.Cli.Program.ResolveAuthToken(parsed, error);

            Assert.Equal("env-token", token);
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_AUTH_TOKEN", previous);
        }
    }

    [Fact]
    public void CliArgs_Parse_RepeatedAccountAndBackendOptions()
    {
        var parsed = CliArgs.Parse([
            "codex",
            "--scope", "repo",
            "--scope", "write",
            "--metadata", "team=core",
            "--metadata", "env=test",
            "--workspace", "./repo",
            "--no-stream"
        ]);

        Assert.Equal("codex", parsed.Positionals[0]);
        Assert.Equal(2, parsed.Options["--scope"].Count);
        Assert.Equal(2, parsed.Options["--metadata"].Count);
        Assert.Equal("./repo", parsed.GetOption("--workspace"));
        Assert.True(parsed.HasFlag("--no-stream"));
    }

    [Fact]
    public void CliArgs_Parse_RepeatedImageOptions()
    {
        var parsed = CliArgs.Parse(["describe", "--image", "one.png", "--image", "https://example.test/two.png"]);

        Assert.Equal(["one.png", "https://example.test/two.png"], parsed.Images);
    }

    [Fact]
    public void BuildUserContent_AddsImageMarkersForImageOptions()
    {
        var content = OpenClaw.Cli.Program.BuildUserContent(
            "Describe these.",
            files: [],
            images: ["https://example.test/cat.png"]);

        Assert.Contains("Describe these.", content, StringComparison.Ordinal);
        Assert.Contains("[IMAGE_URL:https://example.test/cat.png]", content, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildImageCommandContent_AddsImageMarkerAndDefaultPrompt()
    {
        var content = OpenClaw.Cli.Program.BuildImageCommandContent("/image https://example.test/cat.png");

        Assert.NotNull(content);
        Assert.Contains("Describe this image.", content, StringComparison.Ordinal);
        Assert.Contains("[IMAGE_URL:https://example.test/cat.png]", content, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUserContent_TreatsFileUriImagesAsImagePaths()
    {
        var tempImage = Path.Combine(Path.GetTempPath(), $"openclaw-{Guid.NewGuid():N}.png");
        var fileUri = new Uri(tempImage).AbsoluteUri;

        var content = OpenClaw.Cli.Program.BuildUserContent(
            "Describe it.",
            files: [],
            images: [fileUri]);

        Assert.Contains($"[IMAGE_PATH:{tempImage}]", content, StringComparison.Ordinal);
    }
}

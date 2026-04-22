using OpenClaw.Cli;
using Xunit;

namespace OpenClaw.Tests;

public sealed class InitCommandTests
{
    [Fact]
    public async Task Run_GeneratesBootstrapFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-init-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var exitCode = InitCommand.Run(["--output", root, "--preset", "both"]);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(root, ".env.example")));
            Assert.True(File.Exists(Path.Combine(root, "config.local.json")));
            Assert.True(File.Exists(Path.Combine(root, "config.public.json")));
            Assert.True(File.Exists(Path.Combine(root, "deploy", "Caddyfile.sample")));
            Assert.True(Directory.Exists(Path.Combine(root, "workspace")));
            Assert.True(Directory.Exists(Path.Combine(root, "memory")));

            var localConfig = await File.ReadAllTextAsync(Path.Combine(root, "config.local.json"));
            Assert.Contains(@"""sandbox"": {", localConfig, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(@"""provider"": ""None""", localConfig, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(@"""allowShell"": true", localConfig, StringComparison.OrdinalIgnoreCase);

            var publicConfig = await File.ReadAllTextAsync(Path.Combine(root, "config.public.json"));
            Assert.Contains(@"""sandbox"": {", publicConfig, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(@"""provider"": ""None""", publicConfig, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(@"""requireToolApproval"": true", publicConfig, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(@"""requireRequesterMatchForHttpToolApproval"": true", publicConfig, StringComparison.OrdinalIgnoreCase);

            var envExample = await File.ReadAllTextAsync(Path.Combine(root, ".env.example"));
            Assert.Contains("OPENCLAW_AUTH_TOKEN=replace-me", envExample, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

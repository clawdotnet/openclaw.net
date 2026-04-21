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
            Assert.Contains(@"""Sandbox"": {", localConfig, StringComparison.Ordinal);
            Assert.Contains(@"""Provider"": ""None""", localConfig, StringComparison.Ordinal);

            var publicConfig = await File.ReadAllTextAsync(Path.Combine(root, "config.public.json"));
            Assert.Contains(@"""Sandbox"": {", publicConfig, StringComparison.Ordinal);
            Assert.Contains(@"""Provider"": ""None""", publicConfig, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

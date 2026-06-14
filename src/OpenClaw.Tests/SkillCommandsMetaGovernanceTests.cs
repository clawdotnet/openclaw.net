using System.Text.Json;
using OpenClaw.Cli;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Skills;
using Xunit;

namespace OpenClaw.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class SkillCommandsMetaGovernanceTests : IDisposable
{
    private readonly string? _previousOperatorId;

    public SkillCommandsMetaGovernanceTests()
    {
        _previousOperatorId = Environment.GetEnvironmentVariable("OPENCLAW_OPERATOR_ID");
        if (string.IsNullOrWhiteSpace(_previousOperatorId))
            Environment.SetEnvironmentVariable("OPENCLAW_OPERATOR_ID", "test-operator");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("OPENCLAW_OPERATOR_ID", _previousOperatorId);
    }

    [Fact]
    public async Task RunAsync_Catalog_StableMeta_Json_PrintsBundledMetaOnly()
    {
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["catalog", "--kind", "meta", "--stable", "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            Assert.True(document.RootElement.TryGetProperty("skills", out var skills));
            foreach (var skill in skills.EnumerateArray())
            {
                Assert.Equal("meta", skill.GetProperty("kind").GetString());
                Assert.Equal(SkillSource.Bundled.ToString().ToLowerInvariant(), skill.GetProperty("source").GetString());
            }
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

    [Fact]
    public async Task RunAsync_Catalog_StableMeta_Text_PrintsStableHeader()
    {
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["catalog", "--kind", "meta", "--stable"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            Assert.Contains("Stable meta catalog", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Accept_Json_LowQuality_ReturnsQualityGateError()
    {
        var root = CreateTempRoot();
        var previousOut = Console.Out;
        var previousError = Console.Error;
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-governance-accept-low-quality",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-failed-no-evidence",
                            SkillName = "meta-flow",
                            Status = "failed"
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "accept", "sess-governance-accept-low-quality",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-failed-no-evidence:failed",
                "--json"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, output.ToString());

            using var document = JsonDocument.Parse(error.ToString());
            Assert.Equal("proposal_accept_quality_gate_failed", document.RootElement.GetProperty("errorCode").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Change_ToAccept_LowQuality_ReturnsQualityGateError()
    {
        var root = CreateTempRoot();
        var previousOut = Console.Out;
        var previousError = Console.Error;
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-governance-change-low-quality",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-failed-no-steps",
                            SkillName = "meta-flow",
                            Status = "failed"
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var dismissExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "dismiss", "sess-governance-change-low-quality",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-failed-no-steps:failed",
                "--reason", "operator-review",
                "--json"]);
            Assert.Equal(0, dismissExitCode);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var rollbackExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "rollback", "sess-governance-change-low-quality",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-failed-no-steps:failed",
                "--json"]);
            Assert.Equal(0, rollbackExitCode);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var changeExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "change", "sess-governance-change-low-quality",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-failed-no-steps:failed",
                "--to", "accept",
                "--json"]);

            Assert.Equal(1, changeExitCode);
            Assert.Equal(string.Empty, output.ToString());

            using var document = JsonDocument.Parse(error.ToString());
            Assert.Equal("proposal_accept_quality_gate_failed", document.RootElement.GetProperty("errorCode").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-skill-command-meta-governance-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }
}

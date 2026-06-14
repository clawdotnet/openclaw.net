using System.Text.Json;
using OpenClaw.Cli;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class SkillCommandsGlobalMetaRunsTests
{
    [Fact]
    public async Task RunAsync_MetaRuns_List_Json_PrintsGlobalRunPage()
    {
        var root = CreateTempRoot();
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var memoryPath = Path.Combine(root, "memory");
            await SeedSessionsAsync(memoryPath);

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "list", "--storage", memoryPath, "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            Assert.True(document.RootElement.TryGetProperty("items", out var items));
            Assert.Equal(JsonValueKind.Array, items.ValueKind);
            Assert.NotEmpty(items.EnumerateArray());
            Assert.True(document.RootElement.TryGetProperty("page", out _));
            Assert.True(document.RootElement.TryGetProperty("pageSize", out _));
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Show_Json_PrintsRequestedRunAcrossSessions()
    {
        var root = CreateTempRoot();
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var memoryPath = Path.Combine(root, "memory");
            await SeedSessionsAsync(memoryPath);

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "show", "run-failed-001", "--storage", memoryPath, "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            Assert.Equal("run-failed-001", document.RootElement.GetProperty("runId").GetString());
            Assert.Equal("sess-global-b", document.RootElement.GetProperty("sessionId").GetString());
            Assert.Equal("failed", document.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Failures_Text_PrintsOnlyFailedRuns()
    {
        var root = CreateTempRoot();
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var memoryPath = Path.Combine(root, "memory");
            await SeedSessionsAsync(memoryPath);

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "failures", "--storage", memoryPath]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            Assert.Contains("run-failed-001", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("run-ok-001", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task SeedSessionsAsync(string memoryPath)
    {
        await using var store = new FileMemoryStore(memoryPath);

        await store.SaveSessionAsync(new Session
        {
            Id = "sess-global-a",
            ChannelId = "cli",
            SenderId = "tester-a",
            MetaRunHistory =
            {
                new SessionMetaRunRecord
                {
                    RunId = "run-ok-001",
                    SkillName = "meta-flow-a",
                    Status = "completed",
                    FinalText = "completed"
                }
            }
        }, CancellationToken.None);

        await store.SaveSessionAsync(new Session
        {
            Id = "sess-global-b",
            ChannelId = "cli",
            SenderId = "tester-b",
            MetaRunHistory =
            {
                new SessionMetaRunRecord
                {
                    RunId = "run-failed-001",
                    SkillName = "meta-flow-b",
                    Status = "failed",
                    Error = "step failed",
                    ErrorCode = "meta_step_failed",
                    StepResults =
                    {
                        new SessionMetaStepResult
                        {
                            Id = "draft",
                            Kind = "llm_chat",
                            Status = "failed",
                            FailureCode = "model_error"
                        }
                    }
                }
            }
        }, CancellationToken.None);
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-skill-command-global-meta-runs-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }
}

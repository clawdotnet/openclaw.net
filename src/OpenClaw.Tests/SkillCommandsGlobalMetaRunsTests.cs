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

    [Fact]
    public async Task RunAsync_MetaRuns_Steps_Json_PrintsStepTraceForRequestedRun()
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

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "steps", "run-failed-001", "--storage", memoryPath, "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            Assert.Equal("run-failed-001", document.RootElement.GetProperty("runId").GetString());
            Assert.Equal("sess-global-b", document.RootElement.GetProperty("sessionId").GetString());
            var steps = document.RootElement.GetProperty("steps");
            Assert.Equal(1, steps.GetArrayLength());
            Assert.Equal("draft", steps[0].GetProperty("id").GetString());
            Assert.Equal("failed", steps[0].GetProperty("status").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Failures_NameFilter_ReturnsEmptySetWhenNoMatch()
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

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "failures", "--name", "meta-flow-a", "--storage", memoryPath, "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            Assert.Equal(0, document.RootElement.GetProperty("count").GetInt32());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Failures_InvalidSince_ReturnsUsageError()
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

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "failures", "--since", "bad", "--storage", memoryPath, "--json"]);

            Assert.Equal(2, exitCode);
            Assert.Equal(string.Empty, output.ToString());

            using var document = JsonDocument.Parse(error.ToString());
            Assert.Equal("invalid_since", document.RootElement.GetProperty("errorCode").GetString());
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
        }, TestContext.Current.CancellationToken);

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
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Json_IncludesCapabilityBindingTrajectory()
    {
        var root = CreateTempRoot();
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var memoryPath = Path.Combine(root, "memory");
            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-capability",
                    ChannelId = "cli",
                    SenderId = "tester-cap",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-cap-001",
                            SkillName = "meta-capability",
                            Status = "completed",
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "query",
                                    Kind = "tool_call",
                                    Status = "completed",
                                    ExecutionEvidence = new SessionMetaStepExecutionEvidence
                                    {
                                        CapabilityBinding = new CapabilityBindingTrajectory
                                        {
                                            Binding = "dynamic",
                                            IntentKey = "a1b2c3",
                                            TaskDescription = "weather city",
                                            KeyWords = "weather,city",
                                            SelectionPolicy = "first",
                                            CacheHit = false,
                                            Server = "weather-mcp",
                                            Tool = "get_weather",
                                            ElapsedMs = 12.5,
                                            Candidates = { new CapabilityBindingCandidate { Name = "weather-mcp", Rank = 1 } },
                                            Attempted = { new CapabilityBindingCandidate { Name = "weather-mcp", Rank = 1 } }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }, TestContext.Current.CancellationToken);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "sess-capability", "--storage", memoryPath, "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            var raw = output.ToString();
            using var document = JsonDocument.Parse(raw);
            var run = document.RootElement.GetProperty("runs")[0];
            // CoreJsonContext applies camelCase naming to nested run/step/evidence objects;
            // the outer envelope (sessionId/totalCount/shownCount/runs) is hand-written camelCase.
            var binding = run.GetProperty("stepResults")[0].GetProperty("executionEvidence").GetProperty("capabilityBinding");
            Assert.Equal("dynamic", binding.GetProperty("binding").GetString());
            Assert.Equal("a1b2c3", binding.GetProperty("intentKey").GetString());
            Assert.Equal("weather city", binding.GetProperty("taskDescription").GetString());
            Assert.Equal("weather,city", binding.GetProperty("keyWords").GetString());
            Assert.Equal("first", binding.GetProperty("selectionPolicy").GetString());
            Assert.False(binding.GetProperty("cacheHit").GetBoolean());
            Assert.Equal("weather-mcp", binding.GetProperty("server").GetString());
            Assert.Equal("get_weather", binding.GetProperty("tool").GetString());
            Assert.Equal(12.5, binding.GetProperty("elapsedMs").GetDouble());
            Assert.Equal(1, binding.GetProperty("candidates").GetArrayLength());
            Assert.Equal("weather-mcp", binding.GetProperty("candidates")[0].GetProperty("name").GetString());
            Assert.Equal(1, binding.GetProperty("candidates")[0].GetProperty("rank").GetInt32());
            Assert.Equal(1, binding.GetProperty("attempted").GetArrayLength());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-skill-command-global-meta-runs-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }
}

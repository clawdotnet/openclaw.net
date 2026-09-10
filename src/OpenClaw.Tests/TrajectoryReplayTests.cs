using System.Text.Json;
using Microsoft.Extensions.AI;
using NSubstitute;
using OpenClaw.Agent;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Testing;
using Xunit;

namespace OpenClaw.Tests;

public sealed class TrajectoryReplayTests
{
    private static readonly IRedactionPipeline Redaction = new RedactionPipeline([new BaselineSecretRedactor(), new TestRedactor()]);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed class TestRedactor : ISensitiveDataRedactor
    {
        public string Name => "test-sensitive-data";
        public string Redact(string? value) => (value ?? "").Replace("private-person", "[PERSON]");
    }
    private static TrajectoryExportRecord Record(string type, int index, string? content = null, string? arguments = null,
        string? result = null, string? status = null, string session = "private-session", int schema = 1)
        => new() { SchemaVersion = schema, Type = type, TurnIndex = index, SessionId = session,
            ChannelId = "private-channel", SenderId = "private-sender", Content = content,
            Role = type == "prompt" ? "user" : "assistant", ToolName = type.StartsWith("tool_") ? "lookup" : null,
            CallId = type.StartsWith("tool_") ? "private-call" : null, Arguments = arguments, Result = result, ResultStatus = status };
    private static List<TrajectoryExportRecord> Records() =>
    [
        Record("prompt", 0, "Find private-person"),
        Record("response", 1, "Checking"),
        Record("tool_call", 1, arguments: """{"query":"private-person","password":"hidden-password","nested":[{"api_key":"hidden-key"}]}"""),
        Record("tool_result", 1, result: """{"answer":"private-person","token":"hidden-token"}""", status: "completed"),
        Record("response", 2, "Found private-person")
    ];
    private static async Task<TrajectoryReplayFixture> Import(IEnumerable<TrajectoryExportRecord> records, int turn = 0)
    {
        using var reader = new StringReader(string.Join('\n', records.Select(r => JsonSerializer.Serialize(r, CoreJsonContext.Default.TrajectoryExportRecord))));
        return await TrajectoryReplayImporter.ImportAsync(reader, "private-session", turn, Redaction, Ct);
    }
    private static IAgentRuntime Runtime(IChatClient client, IReadOnlyList<ITool> tools)
        => new AgentRuntime(client, tools, Substitute.For<IMemoryStore>(), new LlmProviderConfig { Model = "offline" }, maxHistoryTurns: 20);
    private static List<ScenarioOracleDefinition> Assertions() =>
    [
        new() { Type = ScenarioOracleTypes.ToolCalled, Tool = "lookup" },
        new() { Type = ScenarioOracleTypes.FinalAnswerContains, Value = JsonSerializer.SerializeToElement("Found [PERSON]") }
    ];

    [Theory]
    [InlineData("failed")]
    [InlineData("blocked")]
    public async Task CaptureImportsAndReplaysStructuredFailures(string status)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "capture-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = new Session { Id = "private-session", SenderId = "private-person", ChannelId = "private-channel" };
            session.History.Add(new ChatTurn { Role = "user", Content = "Find private-person" });
            session.History.Add(new ChatTurn { Role = "assistant", Content = "[tool_use]", ToolCalls =
                [new ToolInvocation { CallId = "private-call", ToolName = "lookup", Arguments = "{}", Result = "provider unavailable",
                    ResultStatus = status, FailureCode = "provider_unavailable", FailureMessage = "private-person unavailable" }] });
            session.History.Add(new ChatTurn { Role = "assistant", Content = "Found private-person" });
            Assert.Equal(1, await OpenClaw.Core.Testing.RegressionCapture.CaptureAsync(session, directory, Redaction, ct: Ct));
            Assert.Equal(0, await OpenClaw.Core.Testing.RegressionCapture.CaptureAsync(session, directory, Redaction, ct: Ct));
            var captured = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(directory, "*.jsonl")), Ct);
            Assert.DoesNotContain("private-", captured);
            var fixture = await TrajectoryReplayImporter.ImportAsync(new StringReader(captured), "capture", 0, Redaction, Ct);
            Assert.Equal(status, fixture.Responses[0].ToolCalls[0].ResultStatus);
            var result = await RuntimeScenarioRunner.RunReplayAsync(fixture, Assertions(), Runtime, cancellationToken: Ct);
            Assert.True(result.Passed, result.FailureSummary);
            Assert.Contains(result.Trace.Steps, s => s.Kind == TraceStepKinds.ToolResult && s.Error == "[PERSON] unavailable");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("IMAGE_URL")]
    [InlineData("AUDIO_URL")]
    [InlineData("VIDEO_URL")]
    [InlineData("DOCUMENT_URL")]
    public async Task ReplayValidatesMultimodalPromptWithoutNetwork(string marker)
    {
        var fixture = new TrajectoryReplayFixture { Prompt = $"Describe this\n[{marker}: https://media.invalid/fixture]",
            Responses = [new ReplayResponse { Text = "A recorded answer" }] };
        var result = await RuntimeScenarioRunner.RunReplayAsync(fixture,
            [new ScenarioOracleDefinition { Type = ScenarioOracleTypes.FinalAnswerContains, Value = JsonSerializer.SerializeToElement("recorded answer") }],
            Runtime, cancellationToken: Ct);
        Assert.True(result.Passed, result.FailureSummary);
    }

    [Theory]
    [InlineData("client_secret")]
    [InlineData("private_key")]
    [InlineData("session_token")]
    [InlineData("x-api-key")]
    public async Task ImportMasksOpaqueCredentialFields(string field)
    {
        var records = Records();
        records[2] = Record("tool_call", 1, arguments: "{\"" + field + "\":\"opaque-value\"}");
        var fixture = await Import(records);
        Assert.DoesNotContain("opaque-value", fixture.Responses[0].ToolCalls[0].ArgumentsJson);
    }

    [Fact]
    public async Task UnexpectedResultIdIsRejected()
    {
        using var replay = new TrajectoryReplay(await Import(Records()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => replay.GetResponseAsync([
            new ChatMessage(ChatRole.User, "Find [PERSON]"),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("unknown", "extra")])], cancellationToken: Ct));
    }

    [Fact]
    public async Task OversizedExchangeDoesNotHideLaterValidCapture()
    {
        var directory = Path.Combine(Path.GetTempPath(), "capture-" + Guid.NewGuid().ToString("N"));
        var session = new Session { Id = "s", ChannelId = "test", SenderId = "u" };
        session.History.Add(new ChatTurn { Role = "user", Content = new string('x', 8 * 1024 * 1024 + 1) });
        session.History.Add(new ChatTurn { Role = "assistant", Content = "big" });
        session.History.Add(new ChatTurn { Role = "user", Content = "small" });
        session.History.Add(new ChatTurn { Role = "system", Content = "[goal_check]" });
        session.History.Add(new ChatTurn { Role = "assistant", Content = "done" });
        try { Assert.Equal(1, await OpenClaw.Core.Testing.RegressionCapture.CaptureAsync(session, directory, Redaction, ct: Ct)); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void ReplayRejectsLocalMediaAndDetectsMissingMedia()
    {
        Assert.Throws<InvalidDataException>(() => new TrajectoryReplay(new() { Prompt = "[IMAGE_PATH: /private/image.png]", Responses = [new() { Text = "answer" }] }));
        using var replay = new TrajectoryReplay(new() { Prompt = "Look\n[IMAGE_URL: https://media.invalid/fixture]", Responses = [new() { Text = "answer" }] });
        Assert.Throws<InvalidOperationException>(() => replay.GetResponseAsync([new ChatMessage(ChatRole.User, "Look")]).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task CaptureSkipsMissingToolResultEvenWithFinalText()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "capture-" + Guid.NewGuid().ToString("N"));
        var session = new Session { Id = "s", ChannelId = "c", SenderId = "u" };
        session.History.Add(new() { Role = "user", Content = "question" });
        session.History.Add(new() { Role = "assistant", Content = "[tool_use]", ToolCalls =
            [new ToolInvocation { ToolName = "lookup", Arguments = "{}" }] });
        session.History.Add(new() { Role = "assistant", Content = "interrupted" });
        Assert.Equal(0, await OpenClaw.Core.Testing.RegressionCapture.CaptureAsync(session, directory, Redaction, ct: Ct));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task CaptureIsBoundedAndSkipsIncompleteExchanges()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "capture-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = new Session { Id = "s", ChannelId = "c", SenderId = "u" };
            session.History.Add(new() { Role = "user", Content = "one" });
            Assert.Equal(0, await OpenClaw.Core.Testing.RegressionCapture.CaptureAsync(session, directory, Redaction, 1, Ct));
            session.History.Add(new() { Role = "assistant", Content = "answer" });
            Assert.Equal(1, await OpenClaw.Core.Testing.RegressionCapture.CaptureAsync(session, directory, Redaction, 1, Ct));
            session.History.Add(new() { Role = "user", Content = "two" }); session.History.Add(new() { Role = "assistant", Content = "another" });
            Assert.Equal(0, await OpenClaw.Core.Testing.RegressionCapture.CaptureAsync(session, directory, Redaction, 1, Ct));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Import_RedactsNestedValuesAndDropsSourceIdentity()
    {
        var fixture = await Import(Records());
        var json = JsonSerializer.Serialize(fixture, ScenarioJsonContext.Default.TrajectoryReplayFixture);
        Assert.DoesNotContain("private-", json);
        Assert.DoesNotContain("hidden-", json);
        Assert.Contains("REDACTED", json);
        Assert.Contains("[PERSON]", json);
        var restored = JsonSerializer.Deserialize(json, ScenarioJsonContext.Default.TrajectoryReplayFixture)!;
        Assert.Equal(fixture.Prompt, restored.Prompt);
        Assert.Equal(2, restored.Responses.Count);
    }

    [Fact]
    public async Task Replay_ExercisesRealRuntimeAndTools_WithIndependentAssertions()
    {
        var result = await RuntimeScenarioRunner.RunReplayAsync(await Import(Records()), Assertions(), Runtime, cancellationToken: Ct);
        Assert.True(result.Passed, result.FailureSummary);
        Assert.Contains(result.Trace.Steps, s => s.Kind == TraceStepKinds.ToolCall && s.ToolName == "lookup");
        Assert.Contains(result.Trace.Steps, s => s.Kind == TraceStepKinds.ToolResult && s.Result!.Contains("PERSON"));
        Assert.Contains(result.OracleResults, o => o.Name == "replay-consumed" && o.Passed);
    }

    [Fact]
    public async Task Replay_RequiresOutcomeAssertions()
    {
        var result = await RuntimeScenarioRunner.RunReplayAsync(await Import(Records()), [], Runtime, cancellationToken: Ct);
        Assert.False(result.Passed);
        Assert.Contains(result.OracleResults, o => o.Name == "oracles-present" && !o.Passed);
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("unknown")]
    public async Task Import_RejectsNonCompletedTools(string status)
    {
        var records = Records(); records[3] = Record("tool_result", 1, result: "outcome", status: status);
        await Assert.ThrowsAsync<InvalidDataException>(() => Import(records));
    }

    [Fact]
    public async Task Import_RejectsIncompleteExchange()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Import(Records().Take(4)));
        var records = Records(); records.RemoveAt(3);
        await Assert.ThrowsAsync<InvalidDataException>(() => Import(records));
    }

    [Fact]
    public async Task Import_RejectsMalformedArgumentsWithoutEchoingSensitiveInput()
    {
        var records = Records(); records[2] = Record("tool_call", 1, arguments: "private-person");
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Import(records));
        Assert.DoesNotContain("private-person", ex.Message);
    }

    [Fact]
    public async Task Import_RejectsWrongTurnResult()
    {
        var records = Records(); records[3] = Record("tool_result", 3, result: "wrong");
        await Assert.ThrowsAsync<InvalidDataException>(() => Import(records));
    }

    [Fact]
    public async Task Import_RejectsUnsupportedSchemaAndAmbiguousSelection()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Import([Record("prompt", 0, "hello", schema: 2)]));
        await Assert.ThrowsAsync<InvalidDataException>(() => Import(Records().Concat([Record("prompt", 0, "duplicate")])));
    }

    [Fact]
    public async Task Import_SelectsOneSessionAndStopsAtNextPrompt()
    {
        var records = Records();
        records.Insert(0, Record("prompt", 0, "other secret", session: "other-session"));
        records.Add(Record("prompt", 3, "later secret"));
        records.Add(Record("response", 4, "later answer"));
        records.Add(Record("evidence_bundle", -1));
        var fixture = await Import(records);
        Assert.Equal(2, fixture.Responses.Count);
        var later = await Import(records, 3);
        Assert.Single(later.Responses);
    }

    [Fact]
    public async Task Import_IgnoresEvidenceAndGovernancePayloads()
    {
        var fixture = await Import(Records().Concat([Record("evidence_bundle", -1), Record("governance_ledger_entry", -1)]));
        Assert.Equal(2, fixture.Responses.Count);
    }

    [Fact]
    public async Task Replay_RejectsSkippedToolsAndLatchesDivergence()
    {
        var fixture = await Import(Records());
        using var replay = new TrajectoryReplay(fixture);
        var messages = new[] { new ChatMessage(ChatRole.User, fixture.Prompt) };
        await replay.GetResponseAsync(messages, cancellationToken: Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => replay.GetResponseAsync(messages, cancellationToken: Ct));
        Assert.Throws<InvalidOperationException>(replay.VerifyComplete);
    }

    [Fact]
    public async Task Replay_RejectsChangedToolArguments()
    {
        var fixture = await Import(Records()); using var replay = new TrajectoryReplay(fixture);
        await replay.GetResponseAsync([new ChatMessage(ChatRole.User, fixture.Prompt)], cancellationToken: Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => replay.Tools[0].ExecuteAsync("{}", Ct).AsTask());
        Assert.Throws<InvalidOperationException>(replay.VerifyComplete);
    }

    [Fact]
    public async Task Replay_AcceptsReorderedJsonPropertiesAndRejectsDuplicateExecution()
    {
        var fixture = new TrajectoryReplayFixture { Prompt = "test", Responses =
            [new() { ToolCalls = [new() { ToolName = "lookup", ArgumentsJson = "{\"a\":1,\"b\":2}", Result = "ok" }] }, new() { Text = "done" }] };
        using var replay = new TrajectoryReplay(fixture);
        await replay.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], cancellationToken: Ct);
        Assert.Equal("ok", await replay.Tools[0].ExecuteAsync("{\"b\":2,\"a\":1}", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => replay.Tools[0].ExecuteAsync("{\"a\":1,\"b\":2}", Ct).AsTask());
        Assert.Throws<InvalidOperationException>(replay.VerifyComplete);
    }

    [Fact]
    public async Task Replay_RejectsChangedToolResultDeliveredToProvider()
    {
        var fixture = await Import(Records()); using var replay = new TrajectoryReplay(fixture);
        var prompt = new ChatMessage(ChatRole.User, fixture.Prompt);
        var response = await replay.GetResponseAsync([prompt], cancellationToken: Ct);
        var call = response.Messages[0].Contents.OfType<FunctionCallContent>().Single();
        await replay.Tools[0].ExecuteAsync(fixture.Responses[0].ToolCalls[0].ArgumentsJson, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => replay.GetResponseAsync(
            [prompt, new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, "wrong-result")])], cancellationToken: Ct));
    }

    [Fact]
    public async Task Replay_UsesFreshStateAcrossRuns()
    {
        var fixture = await Import(Records());
        for (var i = 0; i < 2; i++)
        {
            var result = await RuntimeScenarioRunner.RunReplayAsync(fixture, Assertions(), Runtime, cancellationToken: Ct);
            Assert.True(result.Passed, result.FailureSummary);
        }
    }

    [Fact]
    public async Task Replay_HandlesParallelCallsWithDifferentArguments()
    {
        var fixture = new TrajectoryReplayFixture
        {
            Prompt = "Look up two things", Responses =
            [
                new() { ToolCalls = [new() { ToolName = "lookup", ArgumentsJson = "{\"id\":1}", Result = "one" }, new() { ToolName = "lookup", ArgumentsJson = "{\"id\":2}", Result = "two" }] },
                new() { Text = "Both found" }
            ]
        };
        var result = await RuntimeScenarioRunner.RunReplayAsync(fixture,
            [new() { Type = ScenarioOracleTypes.FinalAnswerContains, Value = JsonSerializer.SerializeToElement("Both found") }], Runtime, cancellationToken: Ct);
        Assert.True(result.Passed, result.FailureSummary);
        Assert.Equal(2, result.Trace.Steps.Count(s => s.Kind == TraceStepKinds.ToolResult));
    }

    [Fact]
    public async Task Replay_DeniedApprovalCannotMasqueradeAsExecutedTool()
    {
        var result = await RuntimeScenarioRunner.RunReplayAsync(await Import(Records()), Assertions(),
            (provider, tools) => new AgentRuntime(provider, tools, Substitute.For<IMemoryStore>(),
                new LlmProviderConfig { Model = "offline" }, maxHistoryTurns: 20, requireToolApproval: true,
                approvalRequiredTools: ["lookup"]), cancellationToken: Ct);
        Assert.False(result.Passed);
        Assert.Contains(result.Trace.Steps, step => step.Kind == TraceStepKinds.ApprovalRequest);
        Assert.Contains(result.OracleResults, outcome => outcome.Name == "replay-consumed" && !outcome.Passed);
    }

    [Fact]
    public void Replay_RejectsAmbiguousParallelCalls()
    {
        var call = new ReplayToolCall { ToolName = "lookup", ArgumentsJson = "{}", Result = "one" };
        var fixture = new TrajectoryReplayFixture { Prompt = "prompt", Responses = [new() { ToolCalls = [call, call] }, new() { Text = "done" }] };
        Assert.Throws<InvalidDataException>(() => new TrajectoryReplay(fixture));
    }

    [Fact]
    public async Task Replay_RejectsExtraProviderResponses()
    {
        using var replay = new TrajectoryReplay(new() { Prompt = "hello", Responses = [new() { Text = "hi" }] });
        await replay.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: Ct);
        replay.VerifyComplete();
        await Assert.ThrowsAsync<InvalidOperationException>(() => replay.GetResponseAsync([], cancellationToken: Ct));
        Assert.Throws<InvalidOperationException>(replay.VerifyComplete);
    }

    [Fact]
    public async Task Import_HonorsCancellationAndSizeLimit()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TrajectoryReplayImporter.ImportAsync(new StringReader("data"), "s", 0, Redaction, canceled.Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => TrajectoryReplayImporter.ImportAsync(new StringReader(new string('x', TrajectoryReplayImporter.MaxInputCharacters + 1)), "s", 0, Redaction, Ct));
    }
}

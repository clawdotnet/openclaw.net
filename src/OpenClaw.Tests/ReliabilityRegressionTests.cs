using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenClaw.Agent;
using OpenClaw.Agent.Goal;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Models.Goal;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Services;
using OpenClaw.Dashboard.Services;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Background;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ReliabilityRegressionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openclaw-reliability-" + Guid.NewGuid().ToString("N"));
    private static Session NewSession(string id = "test") => new() { Id = id, ChannelId = "cli", SenderId = "test" };

    [Theory]
    [InlineData("disable")]
    [InlineData("delete")]
    [InlineData("demote")]
    [InlineData("password")]
    public void AccountChanges_InvalidateExistingBrowserSession(string change)
    {
        var accounts = new OperatorAccountService(_directory, NullLogger<OperatorAccountService>.Instance);
        var account = accounts.Create(new OperatorAccountCreateRequest { Username = "operator", Password = "test-password", Role = "admin" });
        Assert.True(accounts.TryAuthenticatePassword("operator", "test-password", out var identity));
        var browser = new BrowserSessionAuthService(new GatewayConfig(), accounts);
        var ticket = browser.Create(false, identity);
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"{BrowserSessionAuthService.CookieName}={ticket.SessionId}";
        context.Request.Headers[BrowserSessionAuthService.CsrfHeaderName] = ticket.CsrfToken;
        Assert.True(browser.TryAuthorize(context, true, out _));
        if (change == "delete") accounts.Delete(account.Id);
        else accounts.Update(account.Id, change switch
        {
            "disable" => new OperatorAccountUpdateRequest { Enabled = false },
            "demote" => new OperatorAccountUpdateRequest { Role = "viewer" },
            _ => new OperatorAccountUpdateRequest { Password = "replacement-password" }
        });
        Assert.False(browser.TryAuthorize(context, true, out _));
    }

    [Fact]
    public void GoalResume_ResetsTurnAndBlockerCounters_WithoutResettingUsage()
    {
        var goals = new InMemoryGoalService();
        var session = NewSession();
        goals.CreateGoal(session.Id, "Finish", 1000, 100);
        goals.UpdateTokenUsage(session.Id, 300);
        var integration = new AgentRuntimeGoalIntegration(goals);
        for (var i = 0; i <= SessionGoal.MaxContinuationsPerTurn; i++)
            integration.EvaluateGoalContinuation(session, i, 100, $"Progress {i}");
        Assert.Equal(GoalStatus.Paused, goals.GetGoal(session.Id)!.Status);
        goals.UpdateStatus(session.Id, GoalStatus.Active);
        Assert.NotNull(integration.EvaluateGoalContinuation(session, 0, 100, "New work"));
        Assert.Equal(200, goals.GetGoal(session.Id)!.TokensUsed);
        goals.RecordTurnHash(session.Id, "blocker");
        goals.RecordTurnHash(session.Id, "blocker");
        goals.RecordTurnHash(session.Id, "blocker");
        goals.UpdateModelStatus(session.Id, GoalStatus.Blocked);
        goals.UpdateStatus(session.Id, GoalStatus.Active);
        Assert.Equal(0, goals.GetGoal(session.Id)!.ConsecutiveBlockerCount);
        Assert.Throws<InvalidOperationException>(() => goals.UpdateModelStatus(session.Id, GoalStatus.Blocked));
    }

    [Fact]
    public void Goals_SurviveRestartIncludingBudgetAndStatus_AndClearDurably()
    {
        var first = new InMemoryGoalService(stateDirectory: _directory);
        first.CreateGoal("../session", "Finish work", 1000, 50);
        first.UpdateTokenUsage("../session", 250);
        first.RecordTurnHash("../session", "missing input");
        first.UpdateStatus("../session", GoalStatus.Paused);
        var restarted = new InMemoryGoalService(stateDirectory: _directory);
        var restored = restarted.GetGoal("../session")!;
        Assert.Equal(GoalStatus.Paused, restored.Status);
        Assert.Equal(200, restored.TokensUsed);
        Assert.Equal(50, restored.TokensAtStart);
        Assert.Equal(1, restored.ConsecutiveBlockerCount);
        Assert.Throws<InvalidOperationException>(() => restarted.CreateGoal("../session", "duplicate", 0, 0));
        restarted.UpdateStatus("../session", GoalStatus.Active);
        restarted.BeginTurn("../session");
        Assert.Equal(0, restarted.GetGoal("../session")!.ContinuationCount);
        restarted.ClearGoal("../session");
        Assert.Null(new InMemoryGoalService(stateDirectory: _directory).GetGoal("../session"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runtime_CanCompleteGoalAfterSuccessfulToolBatch(bool streaming)
    {
        var goals = new InMemoryGoalService();
        var session = NewSession();
        goals.CreateGoal(session.Id, "Verify then finish", 0, 0);
        var client = Substitute.For<IChatClient>();
        var verify = Substitute.For<ITool>();
        verify.Name.Returns("verify"); verify.Description.Returns("Verify work"); verify.ParameterSchema.Returns("{\"type\":\"object\"}");
        verify.ExecuteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult("passed"));
        var responses = new Queue<ChatResponse>([
            new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("a", "verify", new Dictionary<string, object?>())])),
            new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("b", "update_goal", new Dictionary<string, object?> { ["status"] = "complete" })])),
            new(new ChatMessage(ChatRole.Assistant, "Finished"))]);
        client.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(responses.Dequeue()));
        client.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => StreamResponse(responses.Dequeue()));
        var runtime = new AgentRuntime(client, [verify, new UpdateGoalTool(goals)], Substitute.For<IMemoryStore>(),
            new LlmProviderConfig { Model = "test" }, maxHistoryTurns: 20, maxIterations: 5, goalService: goals);
        if (streaming)
        {
            var events = new List<AgentStreamEvent>();
            await foreach (var item in runtime.RunStreamingAsync(session, "Do the work", TestContext.Current.CancellationToken)) events.Add(item);
            Assert.Contains(events, item => item.Type == AgentStreamEventType.Done);
            Assert.DoesNotContain(events, item => item.Type == AgentStreamEventType.Error);
            Assert.Contains(events, item => item.Content == "Finished");
        }
        else Assert.Equal("Finished", await runtime.RunAsync(session, "Do the work", TestContext.Current.CancellationToken));
        Assert.Equal(GoalStatus.Complete, goals.GetGoal(session.Id)!.Status);
        Assert.Empty(responses);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_QueuesEverySessionAcrossPages(bool sqlite)
    {
        Directory.CreateDirectory(_directory);
        IMemoryStore store = sqlite ? new SqliteMemoryStore(Path.Combine(_directory, "sessions.db"), enableFts: false) : new FileMemoryStore(_directory);
        try
        {
            for (var i = 0; i < 65; i++)
            {
                var session = NewSession($"session-{i:D3}");
                session.RunState = SessionRunState.Running;
                session.BackgroundRun = new BackgroundRunMetadata { RunId = session.Id };
                await store.SaveSessionAsync(session, TestContext.Current.CancellationToken);
            }
            await using var pipeline = new MessagePipeline();
            var config = new GatewayConfig();
            config.BackgroundExecution.Enabled = true;
            config.BackgroundExecution.AutoResumeOnStartup = true;
            config.BackgroundExecution.AutoResumeMaxConcurrent = 1;
            config.BackgroundExecution.AutoResumeStaggerSeconds = 0;
            var worker = new BackgroundSessionRecoveryWorker((IBackgroundSessionStore)store, pipeline, config, NullLogger<BackgroundSessionRecoveryWorker>.Instance);
            await worker.RecoverOnceAsync(TestContext.Current.CancellationToken);
            var ids = new List<string>();
            while (pipeline.InboundReader.TryRead(out var message)) ids.Add(message.SessionId!);
            Assert.Equal(65, ids.Count);
            Assert.Equal(65, ids.Distinct().Count());
        }
        finally { (store as IDisposable)?.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dashboard_LoginSendsGatewayContract(bool bootstrap)
    {
        var handler = new LoginHandler(bootstrap);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var auth = new AuthService(new ApiService(http));
        Assert.True(bootstrap ? await auth.LoginWithBootstrap("secret") : await auth.LoginWithToken("secret"));
        Assert.True(auth.IsAuthenticated);
    }

    private sealed class LoginHandler(bool bootstrap) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonSerializer.Deserialize(await request.Content!.ReadAsStringAsync(ct), CoreJsonContext.Default.AuthSessionRequest)!;
            Assert.Equal("/auth/session", request.RequestUri!.AbsolutePath);
            Assert.Equal("secret", bootstrap ? request.Headers.Authorization?.Parameter : body.AccountToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"role\":\"admin\",\"authMode\":\"browser-session\"}") };
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamResponse(ChatResponse response)
    {
        await Task.Yield();
        foreach (var message in response.Messages)
            yield return new ChatResponseUpdate { Role = message.Role, Contents = message.Contents };
    }

    [Fact]
    public async Task RuntimeScenarioRunner_UsesActualRuntimeRatherThanScriptedEvidence()
    {
        var client = Substitute.For<IChatClient>();
        client.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => StreamResponse(new ChatResponse(new ChatMessage(ChatRole.Assistant, "actual response"))));
        var runner = new OpenClaw.Testing.RuntimeScenarioRunner(_ => new AgentRuntime(client, [], Substitute.For<IMemoryStore>(),
            new LlmProviderConfig { Model = "test" }, maxHistoryTurns: 20));
        var result = await runner.RunAsync(new OpenClaw.Testing.AgentScenario
        {
            Id = "runtime-proof", Input = new() { UserMessage = "hello" },
            ScriptedTrace = new() { FinalAnswer = "invented response" },
            Oracles = [new() { Type = OpenClaw.Testing.ScenarioOracleTypes.FinalAnswerContains, Value = JsonSerializer.SerializeToElement("actual response") }]
        }, TestContext.Current.CancellationToken);
        Assert.True(result.Passed, result.FailureSummary);
        Assert.Equal("actual response", result.Trace.FinalAnswer);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}

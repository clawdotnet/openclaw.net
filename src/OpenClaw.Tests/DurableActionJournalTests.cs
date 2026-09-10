using OpenClaw.Agent;
using NSubstitute;
using OpenClaw.Core.Actions;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using Xunit;

namespace OpenClaw.Tests;

public sealed class DurableActionJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "actions-" + Guid.NewGuid().ToString("N"));
    private readonly Session _session = new() { Id = "s", ChannelId = "test", SenderId = "u" };
    private OpenClawToolExecutor Executor(ITool tool) => new([tool], 5, false, [], [], config: new GatewayConfig
    { Memory = new() { StoragePath = _root }, Tooling = new() { DurableActionJournal = true, RequireToolApproval = false } });
    private Task<ToolExecutionResult> Run(OpenClawToolExecutor executor, string id, TurnContext? context = null)
        => executor.ExecuteAsync("test", "{}", id, _session, context ?? new(), false, null, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(false, "{")]
    [InlineData(true, "{")]
    [InlineData(false, "[null]")]
    [InlineData(true, "[null]")]
    public async Task CorruptJournalDoesNotRetrySuccessfulSessionPersistence(bool checkpoint, string corruptJson)
    {
        var ct = TestContext.Current.CancellationToken;
        var journal = new DurableActionJournal(_root);
        using (var lease = await journal.OpenAsync(_session.Id, ct)) lease.Begin("call", "test", "{}");
        var journalPath = Assert.Single(Directory.GetFiles(Path.Combine(_root, "action-journal"), "*.json"));
        await File.WriteAllTextAsync(journalPath, corruptJson, ct);
        var memory = Substitute.For<IMemoryStore>();
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger>();
        if (checkpoint)
        {
            await new AgentCheckpointManager(memory, logger, journal).PersistToolBatchCheckpointAsync(_session, new(), 1,
                [new ToolInvocation { CallId = "call", ToolName = "test", Arguments = "{}", Result = "done" }], ct);
            Assert.NotNull(_session.ExecutionCheckpoint!.PersistedAtUtc);
        }
        else
        {
            using var manager = new OpenClaw.Core.Sessions.SessionManager(memory, new GatewayConfig
            { Memory = new() { StoragePath = _root }, Tooling = new() { DurableActionJournal = true } }, logger);
            await manager.PersistAsync(_session, ct);
        }
        await memory.Received(1).SaveSessionAsync(_session, ct);
        Assert.Contains(logger.ReceivedCalls(), call => call.GetMethodInfo().Name == "Log");
        Assert.Equal(corruptJson, await File.ReadAllTextAsync(journalPath, ct));
        var error = await Record.ExceptionAsync(() => journal.OpenAsync(_session.Id, ct));
        Assert.True(error is System.Text.Json.JsonException or InvalidDataException);
    }

    [Fact]
    public async Task CatalogReadFailureDoesNotBlockLaterMutation()
    {
        var read = Executor(new ReadFailureTool());
        await read.ExecuteAsync("memory_search", "{}", "read", _session, new(), false, null, TestContext.Current.CancellationToken);
        using (var lease = await new DurableActionJournal(_root).OpenAsync(_session.Id, TestContext.Current.CancellationToken))
            Assert.Empty(lease.Records);
        var write = new CountingTool();
        Assert.Equal("done", (await Run(Executor(write), "write")).ResultText);
        Assert.Equal(1, write.Calls);
    }
    private sealed class ReadFailureTool : ITool
    {
        public string Name => "memory_search"; public string Description => "read"; public string ParameterSchema => "{}";
        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct) => throw new IOException("Read unavailable.");
    }

    [Fact]
    public async Task MafAdapterGeneratesDistinctPersistableActionIds()
    {
        var tool = new CountingTool(); var invocations = new List<ToolInvocation>();
        using var scope = AgentExecutionContextScope.Push(new AgentExecutionContext
        { Session = _session, TurnContext = new(), SystemPromptLength = 0, SkillPromptLength = 0,
            SessionTokenBudget = 0, ToolInvocations = invocations });
        var adapter = new OpenClaw.MicrosoftAgentFrameworkAdapter.MafToolAdapter(tool, Executor(tool));
        await adapter.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(), TestContext.Current.CancellationToken);
        await adapter.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(), TestContext.Current.CancellationToken);
        Assert.Equal(2, tool.Calls);
        Assert.Equal(2, invocations.Select(c => c.CallId).Distinct().Count());
        Assert.All(invocations, call => Assert.False(string.IsNullOrWhiteSpace(call.CallId)));
    }

    [Fact]
    public async Task PreparationFailureDoesNotLeaveUnknownExternalOutcome()
    {
        var tool = new CountingTool();
        var executor = new OpenClawToolExecutor([tool], 5, false, [], [], config: new GatewayConfig
        { Memory = new() { StoragePath = _root }, Tooling = new() { DurableActionJournal = true, RequireToolApproval = false } },
            sentinelSubstitution: new FailedPreparation());
        await Run(executor, "one"); Assert.Equal(0, tool.Calls);
        using (var lease = await new DurableActionJournal(_root).OpenAsync(_session.Id, TestContext.Current.CancellationToken))
            Assert.Equal("not_executed", Assert.Single(lease.Records).State);
        await Run(Executor(tool), "two"); Assert.Equal(1, tool.Calls);
    }

    private sealed class FailedPreparation : OpenClaw.Core.Security.ISentinelSubstitutionService
    {
        public ValueTask<OpenClaw.Core.Security.SentinelSubstitutionResult> SubstituteAsync(OpenClaw.Core.Security.SentinelSubstitutionContext context, CancellationToken ct)
            => throw new InvalidOperationException("Unavailable secret reference.");
    }

    [Fact]
    public async Task ReusedIdentityWithDifferentArgumentsBlocksInsteadOfThrowing()
    {
        var executor = Executor(new CountingTool());
        await Run(executor, "one");
        var result = await executor.ExecuteAsync("test", "{\"changed\":true}", "one", _session, new(), false, null, TestContext.Current.CancellationToken);
        Assert.Equal("action_identity_conflict", result.FailureCode);
    }

    [Fact]
    public async Task JournalWaitHonorsCallerCancellation()
    {
        var journal = new DurableActionJournal(_root);
        using var lease = await journal.OpenAsync(_session.Id, TestContext.Current.CancellationToken);
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => journal.OpenAsync(_session.Id, cancelled.Token));
    }

    [Fact]
    public async Task ReconciliationErrorsRemainBlockedWithoutAnotherDispatch()
    {
        var tool = new ProviderTool { FailReconciliation = true };
        await Run(Executor(tool), "one");
        Assert.Equal("action_reconciliation_required", (await Run(Executor(tool), "one")).FailureCode);
        Assert.Single(tool.Keys);
    }
    [Fact]
    public async Task ReplayUsesFinalInterceptedResult()
    {
        var tool = new CountingTool();
        var executor = new OpenClawToolExecutor([tool], 5, false, [], [], config: new GatewayConfig
        { Memory = new() { StoragePath = _root }, Tooling = new() { DurableActionJournal = true, RequireToolApproval = false } },
            interceptors: [new ReducedResult()]);
        Assert.Equal("reduced", (await Run(executor, "one")).ResultText);
        Assert.Equal("reduced", (await Run(Executor(tool), "one")).ResultText);
        Assert.Equal(1, tool.Calls);
    }
    private sealed class ReducedResult : IToolResultInterceptor
    {
        public int Order => 0; public string Name => "test";
        public ValueTask<string> InterceptAsync(ReductionContext context, CancellationToken ct) => ValueTask.FromResult("reduced");
    }

    [Fact]
    public async Task PersistedResultsDoNotBlockAfterHistoryCompaction()
    {
        var tool = new CountingTool();
        await Run(Executor(tool), "one");
        _session.History.Add(new ChatTurn { Role = "assistant", Content = "[tool_use]", ToolCalls =
            [new ToolInvocation { CallId = "one", ToolName = "test", Arguments = "{}", Result = "done" }] });
        var memory = NSubstitute.Substitute.For<IMemoryStore>();
        using var manager = new OpenClaw.Core.Sessions.SessionManager(memory, new GatewayConfig
        { Memory = new() { StoragePath = _root }, Tooling = new() { DurableActionJournal = true } });
        await manager.PersistAsync(_session, TestContext.Current.CancellationToken);
        _session.History.Clear();
        Assert.Equal("done", (await Run(Executor(tool), "two")).ResultText);
        Assert.Equal(2, tool.Calls);
    }

    [Fact]
    public async Task CompletedDispatchReplaysWithoutCallingProviderAfterRestart()
    {
        var tool = new CountingTool();
        Assert.Equal("done", (await Run(Executor(tool), "one")).ResultText);
        Assert.Equal("done", (await Run(Executor(tool), "one")).ResultText);
        Assert.Equal(1, tool.Calls);
        Assert.Equal("action_reconciliation_required", (await Run(Executor(tool), "changed-id")).FailureCode);
        Assert.Equal(1, tool.Calls);
    }
    [Fact]
    public async Task ParallelBatchCanFinishButFreshRuntimeCannotBypassUncheckpointedResult()
    {
        var tool = new CountingTool(); var executor = Executor(tool); var context = new TurnContext();
        var results = await Task.WhenAll(Run(executor, "one", context), Run(executor, "two", context));
        Assert.All(results, r => Assert.Equal("done", r.ResultText)); Assert.Equal(2, tool.Calls);
        Assert.Equal("action_reconciliation_required", (await Run(Executor(tool), "three")).FailureCode);
    }
    [Fact]
    public async Task TimeoutLeavesUnknownOutcomeAndBlocksChangedCallId()
    {
        var tool = new CountingTool { Fail = true };
        Assert.Equal("action_reconciliation_required", (await Run(Executor(tool), "one")).FailureCode);
        Assert.Equal("action_reconciliation_required", (await Run(Executor(tool), "two")).FailureCode);
        Assert.Equal(1, tool.Calls);
    }
    [Fact]
    public async Task ProviderConfirmedNonExecutionRetriesUsingSameKey()
    {
        var tool = new ProviderTool();
        Assert.Equal("action_reconciliation_required", (await Run(Executor(tool), "one")).FailureCode);
        tool.Fail = false;
        Assert.Equal("done", (await Run(Executor(tool), "one")).ResultText);
        Assert.Equal(2, tool.Keys.Count); Assert.Equal(tool.Keys[0], tool.Keys[1]);
        Assert.Equal(1, tool.Reconciliations);
    }
    [Fact]
    public async Task ReconciliationRequiresEvidenceAndCurrentRevision()
    {
        using var lease = await new DurableActionJournal(_root).OpenAsync("../session", TestContext.Current.CancellationToken);
        var record = lease.Begin("call", "test", "secret-argument");
        Assert.Throws<ArgumentException>(() => lease.Resolve(record, record.Revision, "not_executed", ""));
        Assert.Throws<InvalidOperationException>(() => lease.Resolve(record, 0, "not_executed", "receipt"));
        Assert.Throws<InvalidOperationException>(() => lease.Begin("call", "test", "changed"));
        Assert.DoesNotContain("secret-argument", File.ReadAllText(Directory.GetFiles(Path.Combine(_root, "action-journal"), "*.json").Single()));
    }
    [Fact]
    public async Task ConcurrentLeaseWaitIsCancellable()
    {
        var journal = new DurableActionJournal(_root);
        using var lease = await journal.OpenAsync("s", TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => journal.OpenAsync("s", cancellation.Token));
    }
    private class CountingTool : ITool
    {
        public string Name => "test"; public string Description => "test"; public string ParameterSchema => "{\"type\":\"object\"}";
        public int Calls; public bool Fail;
        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        { Calls++; if (Fail) throw new OperationCanceledException(); return ValueTask.FromResult("done"); }
    }
    private sealed class ProviderTool : CountingTool, IReconcilableTool
    {
        public readonly List<string> Keys = []; public int Reconciliations; public bool FailReconciliation;
        public ProviderTool() { Fail = true; }
        public ValueTask<ActionOutcome> ReconcileAsync(string key, CancellationToken ct)
        { Reconciliations++; if (FailReconciliation) throw new IOException("provider offline"); return ValueTask.FromResult(new ActionOutcome("not_executed")); }
        public ValueTask<string> ExecuteWithIdempotencyAsync(string args, string key, ToolExecutionContext context, CancellationToken ct)
        { Assert.Equal(key, context.IdempotencyKey); Keys.Add(key); return ExecuteAsync(args, ct); }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}

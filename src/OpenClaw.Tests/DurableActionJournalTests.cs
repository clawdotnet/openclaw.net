using OpenClaw.Agent;
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

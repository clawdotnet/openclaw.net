using NSubstitute;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Actions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Models.Goal;
using OpenClaw.Core.Recovery;
using OpenClaw.Core.Services;
using Xunit;

namespace OpenClaw.Tests;

public sealed class GuidedRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "recovery-" + Guid.NewGuid().ToString("N"));
    private readonly Session _session = new() { Id = "session", SenderId = "user", ChannelId = "test" };
    private readonly InMemoryGoalService _goals = new();
    private readonly IMemoryStore _memory = Substitute.For<IMemoryStore>();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task ResumeRejectsActiveExecutionApprovalsAndExhaustedBudget(bool running, bool approval, bool exhausted)
    {
        var goal = _goals.CreateGoal(_session.Id, "work", 10, 0);
        _goals.UpdateStatus(_session.Id, GoalStatus.Paused);
        if (exhausted) _goals.UpdateTokenUsage(_session.Id, 10);
        var state = GuidedRecovery.Describe(_session, _goals.GetGoal(_session.Id), [], true, running, approval);
        Assert.False(state.CanResume);
        await Assert.ThrowsAsync<InvalidOperationException>(() => GuidedRecovery.ApplyAsync(_session, _goals, _memory, null,
            new() { Command = "resume", Revision = state.Revision }, running, approval, Ct));
    }
    [Fact]
    public void ResumeRespectsSessionBudgetAndCurrentSessionUsage()
    {
        _goals.CreateGoal(_session.Id, "work", 100, 0); _goals.UpdateStatus(_session.Id, GoalStatus.Paused);
        _session.TotalInputTokens = 50;
        Assert.False(GuidedRecovery.Describe(_session, _goals.GetGoal(_session.Id), [], true, false, false, 50).CanResume);
        _session.TotalInputTokens = 100;
        Assert.False(GuidedRecovery.Describe(_session, _goals.GetGoal(_session.Id), [], true, false, false).CanResume);
    }

    [Fact]
    public async Task ResumeUsesCurrentRevisionAndPreservesBudget()
    {
        _goals.CreateGoal(_session.Id, "work", 100, 0); _goals.UpdateStatus(_session.Id, GoalStatus.Paused);
        var state = GuidedRecovery.Describe(_session, _goals.GetGoal(_session.Id), [], true, false, false);
        await GuidedRecovery.ApplyAsync(_session, _goals, _memory, null, new() { Command = "resume", Revision = state.Revision }, false, false, Ct);
        Assert.Equal(GoalStatus.Active, _goals.GetGoal(_session.Id)!.Status); Assert.Equal(100, _goals.GetGoal(_session.Id)!.TokenBudget);
        await Assert.ThrowsAsync<InvalidOperationException>(() => GuidedRecovery.ApplyAsync(_session, _goals, _memory, null,
            new() { Command = "resume", Revision = state.Revision }, false, false, Ct));
    }
    [Fact]
    public async Task CompletedResolutionPersistsHistoryBeforeReleasingBlock()
    {
        using var lease = await new DurableActionJournal(_root).OpenAsync(_session.Id, Ct);
        var action = lease.Begin("call", "email", "{}");
        var state = GuidedRecovery.Describe(_session, null, lease.Records, true, false, false);
        await GuidedRecovery.ApplyAsync(_session, null, _memory, lease,
            new() { Command = "completed", Revision = state.Revision, ActionId = action.Id, ActionRevision = action.Revision,
                Evidence = "Provider receipt 123", Result = "Sent once" }, false, false, Ct);
        Assert.Equal("completed", action.State);
        Assert.Equal("Sent once", Assert.Single(Assert.Single(_session.History).ToolCalls!).Result);
        Assert.Empty(GuidedRecovery.Describe(_session, null, lease.Records, true, false, false).Actions);
        await _memory.Received(1).SaveSessionAsync(_session, Ct);
    }
    [Fact]
    public async Task PersistenceFailureKeepsUnknownOutcomeAndRollsBackLiveHistory()
    {
        using var lease = await new DurableActionJournal(_root).OpenAsync(_session.Id, Ct);
        var action = lease.Begin("call", "email", "{}");
        _memory.SaveSessionAsync(_session, Ct).Returns(_ => throw new IOException("disk full"));
        var state = GuidedRecovery.Describe(_session, null, lease.Records, true, false, false);
        await Assert.ThrowsAsync<IOException>(() => GuidedRecovery.ApplyAsync(_session, null, _memory, lease,
            new() { Command = "completed", Revision = state.Revision, ActionId = action.Id, ActionRevision = action.Revision, Evidence = "receipt", Result = "sent" }, false, false, Ct));
        Assert.Empty(_session.History); Assert.Equal("started", action.State);
    }
    [Fact]
    public async Task CannotDeclareRecordedCompletionNotExecuted()
    {
        using var lease = await new DurableActionJournal(_root).OpenAsync(_session.Id, Ct);
        var action = lease.Begin("call", "email", "{}"); lease.Complete(action, "sent");
        var state = GuidedRecovery.Describe(_session, null, lease.Records, true, false, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => GuidedRecovery.ApplyAsync(_session, null, _memory, lease,
            new() { Command = "not_executed", Revision = state.Revision, ActionId = action.Id, ActionRevision = action.Revision, Evidence = "guess" }, false, false, Ct));
        Assert.Equal("completed", action.State);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}

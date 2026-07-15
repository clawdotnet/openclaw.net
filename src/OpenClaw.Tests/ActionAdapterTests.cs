using OpenClaw.Agent.Actions;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ActionAdapterTests
{
    [Fact]
    public async Task ExecuteAsync_PreCheckThenExecution_Succeeds()
    {
        var connector = new FakeConnector(new Dictionary<string, ActionAdapterStepResult>(StringComparer.Ordinal)
        {
            ["crm.precheck"] = ActionAdapterStepResult.Succeeded(),
            ["crm.exec.1"] = ActionAdapterStepResult.Succeeded(),
            ["crm.exec.2"] = ActionAdapterStepResult.Succeeded(),
            ["crm.rollback.1"] = ActionAdapterStepResult.Succeeded()
        });
        var adapter = new ActionAdapter(connector, new InMemoryActionIdempotencyRegistry());

        var result = await adapter.ExecuteAsync(BuildProposal(), TestContext.Current.CancellationToken);

        Assert.Equal("succeeded", result.Status);
        Assert.False(result.RollbackTriggered);
        Assert.Equal(["crm.precheck", "crm.exec.1", "crm.exec.2"], connector.InvocationOrder);
    }

    [Fact]
    public async Task ExecuteAsync_ExecutionStepFails_TriggersRollbackChain()
    {
        var connector = new FakeConnector(new Dictionary<string, ActionAdapterStepResult>(StringComparer.Ordinal)
        {
            ["crm.precheck"] = ActionAdapterStepResult.Succeeded(),
            ["crm.exec.1"] = ActionAdapterStepResult.Succeeded(),
            ["crm.exec.2"] = ActionAdapterStepResult.Failure("connector_error"),
            ["crm.rollback.1"] = ActionAdapterStepResult.Succeeded(),
            ["crm.rollback.2"] = ActionAdapterStepResult.Succeeded()
        });
        var adapter = new ActionAdapter(connector, new InMemoryActionIdempotencyRegistry());

        var proposal = BuildProposal(rollbackCalls: ["crm.rollback.1", "crm.rollback.2"]);
        var result = await adapter.ExecuteAsync(proposal, TestContext.Current.CancellationToken);

        Assert.Equal("rolled_back", result.Status);
        Assert.True(result.RollbackTriggered);
        Assert.Equal("connector_error", result.ResultCode);
        Assert.Equal(["crm.precheck", "crm.exec.1", "crm.exec.2", "crm.rollback.1", "crm.rollback.2"], connector.InvocationOrder);
    }

    [Fact]
    public async Task ExecuteAsync_RollbackFails_ReturnsRollbackFailed()
    {
        var connector = new FakeConnector(new Dictionary<string, ActionAdapterStepResult>(StringComparer.Ordinal)
        {
            ["crm.precheck"] = ActionAdapterStepResult.Succeeded(),
            ["crm.exec.1"] = ActionAdapterStepResult.Failure("execution_failed"),
            ["crm.rollback.1"] = ActionAdapterStepResult.Failure("rollback_failed")
        });
        var adapter = new ActionAdapter(connector, new InMemoryActionIdempotencyRegistry());

        var result = await adapter.ExecuteAsync(BuildProposal(rollbackCalls: ["crm.rollback.1"]), TestContext.Current.CancellationToken);

        Assert.Equal("rollback_failed", result.Status);
        Assert.True(result.RollbackTriggered);
        Assert.Equal("rollback_failed", result.ResultCode);
    }

    [Fact]
    public async Task ExecuteAsync_PreCheckFails_DoesNotRunExecutionOrRollback()
    {
        var connector = new FakeConnector(new Dictionary<string, ActionAdapterStepResult>(StringComparer.Ordinal)
        {
            ["crm.precheck"] = ActionAdapterStepResult.Failure("precheck_failed"),
            ["crm.exec.1"] = ActionAdapterStepResult.Succeeded(),
            ["crm.rollback.1"] = ActionAdapterStepResult.Succeeded()
        });
        var adapter = new ActionAdapter(connector, new InMemoryActionIdempotencyRegistry());

        var result = await adapter.ExecuteAsync(BuildProposal(), TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Status);
        Assert.False(result.RollbackTriggered);
        Assert.Equal("precheck_failed", result.ResultCode);
        Assert.Equal(["crm.precheck"], connector.InvocationOrder);
    }

    [Fact]
    public async Task ExecuteAsync_IdempotencyConflict_ReturnsConflictCode()
    {
        var connector = new FakeConnector(new Dictionary<string, ActionAdapterStepResult>(StringComparer.Ordinal)
        {
            ["crm.precheck"] = ActionAdapterStepResult.Succeeded(),
            ["crm.exec.1"] = ActionAdapterStepResult.Succeeded(),
            ["crm.exec.2"] = ActionAdapterStepResult.Succeeded()
        });
        var registry = new InMemoryActionIdempotencyRegistry();
        var adapter = new ActionAdapter(connector, registry);
        var proposal = BuildProposal(idempotencyKey: "proposal-C123-20260715-99");

        var first = await adapter.ExecuteAsync(proposal, TestContext.Current.CancellationToken);
        var second = await adapter.ExecuteAsync(proposal, TestContext.Current.CancellationToken);

        Assert.Equal("succeeded", first.Status);
        Assert.Equal("failed", second.Status);
        Assert.Equal("idempotency_conflict", second.ResultCode);
        Assert.Equal(3, connector.InvocationOrder.Count);
    }

    private static ActionProposal BuildProposal(string idempotencyKey = "proposal-C123-20260715-01", IReadOnlyList<string>? rollbackCalls = null)
    {
        var rollbacks = rollbackCalls ?? ["crm.rollback.1"];
        return new ActionProposal
        {
            ActionName = "sync_customer_tier",
            Source = new ActionProposalSource { MetaSkill = "customer-risk-assistant", RunId = "run_1", StepId = "step_1" },
            Trigger = new ActionProposalTrigger { Condition = "riskLevel == medium", EvidenceRefs = ["ev_001"] },
            Target = new ActionProposalTarget { System = "crm", Operation = "updateCustomerTier" },
            PreChecks = [new ActionCall { Call = "crm.precheck" }],
            Execution = [new ActionCall { Call = "crm.exec.1" }, new ActionCall { Call = "crm.exec.2" }],
            Rollback = rollbacks.Select(call => new ActionCall { Call = call }).ToArray(),
            IdempotencyKey = idempotencyKey,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["env"] = "test"
            }
        };
    }

    private sealed class FakeConnector : IActionAdapterConnector
    {
        private readonly IReadOnlyDictionary<string, ActionAdapterStepResult> _results;

        public FakeConnector(IReadOnlyDictionary<string, ActionAdapterStepResult> results)
        {
            _results = results;
        }

        public List<string> InvocationOrder { get; } = [];

        public ValueTask<ActionAdapterStepResult> InvokeAsync(ActionCall step, CancellationToken cancellationToken)
        {
            InvocationOrder.Add(step.Call);
            if (_results.TryGetValue(step.Call, out var result))
                return ValueTask.FromResult(result);

            return ValueTask.FromResult(ActionAdapterStepResult.Succeeded());
        }
    }
}

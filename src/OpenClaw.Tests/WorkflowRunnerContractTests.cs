using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class WorkflowRunnerContractTests
{
    [Fact]
    public async Task LegacyRunner_WhenContextOmittedOrNull_ShouldPreserveOldCalls()
    {
        IAgentWorkflowRunner runner = new LegacyRunner();
        var request = new AgentWorkflowRequest();
        var response = new AgentWorkflowResponse { PortId = "approval" };
        Assert.Equal("run", (await runner.RunAsync(request, default)).RunId);
        Assert.Equal("run", (await runner.GetAsync("run", default)).RunId);
        Assert.Equal("run", (await runner.RespondAsync("run", response, default)).RunId);
        await foreach (var evt in runner.StreamAsync("run", default))
            Assert.Equal("event", evt.Id);
        Assert.Equal("run", (await runner.RunAsync(request, null, default)).RunId);
        Assert.Equal("run", (await runner.GetAsync("run", null, default)).RunId);
        Assert.Equal("run", (await runner.RespondAsync("run", response, null, default)).RunId);
        await foreach (var evt in runner.StreamAsync("run", null, default))
            Assert.Equal("event", evt.Id);
    }

    [Fact]
    public async Task LegacyRunner_WhenCallerProvided_ShouldRejectEveryOperation()
    {
        IAgentWorkflowRunner runner = new LegacyRunner();
        var caller = new WorkflowExecutionContext("review", "issuer", "user", DateTimeOffset.UtcNow.AddMinutes(5), "private-token");
        await Assert.ThrowsAsync<NotSupportedException>(() => runner.RunAsync(new(), caller, default));
        await Assert.ThrowsAsync<NotSupportedException>(() => runner.GetAsync("run", caller, default));
        await Assert.ThrowsAsync<NotSupportedException>(() => runner.RespondAsync("run", new() { PortId = "approval" }, caller, default));
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in runner.StreamAsync("run", caller, default)) { }
        });
    }

    [Fact]
    public void ExecutionContext_WhenFormattedOrSerialized_ShouldNotExposeData()
    {
        var caller = new WorkflowExecutionContext("review", "private-issuer", "private-user", DateTimeOffset.UtcNow, "private-token");
        Assert.DoesNotContain("private-token", caller.ToString());
        Assert.Equal("{}", JsonSerializer.Serialize(caller));
    }

    private sealed class LegacyRunner : IAgentWorkflowRunner
    {
        public string BackendId => "review";
        public string WorkflowId => "workflow";
        public AgentWorkflowBackendSummary GetSummary() => new() { Id = BackendId, Kind = "legacy", WorkflowName = WorkflowId };
        public Task<AgentWorkflowRunResult> RunAsync(AgentWorkflowRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentWorkflowRunResult { WorkflowId = WorkflowId, RunId = "run", Status = "running" });
        public Task<AgentWorkflowRunSnapshot> GetAsync(string runId, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentWorkflowRunSnapshot { WorkflowId = WorkflowId, RunId = runId, Status = "running" });
        public Task<AgentWorkflowRunSnapshot> RespondAsync(string runId, AgentWorkflowResponse response, CancellationToken cancellationToken = default)
            => GetAsync(runId, cancellationToken);
        public async IAsyncEnumerable<AgentWorkflowEvent> StreamAsync(string runId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new AgentWorkflowEvent { Id = "event", Type = "status" };
        }
    }
}

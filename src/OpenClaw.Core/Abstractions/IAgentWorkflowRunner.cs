using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

public interface IAgentWorkflowRunner
{
    string BackendId { get; }
    string WorkflowId { get; }

    AgentWorkflowBackendSummary GetSummary();

    Task<AgentWorkflowRunResult> RunAsync(
        AgentWorkflowRequest request,
        CancellationToken cancellationToken = default);

    Task<AgentWorkflowRunSnapshot> GetAsync(
        string runId,
        CancellationToken cancellationToken = default);

    Task<AgentWorkflowRunSnapshot> RespondAsync(
        string runId,
        AgentWorkflowResponse response,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<AgentWorkflowEvent> StreamAsync(
        string runId,
        CancellationToken cancellationToken = default);
}

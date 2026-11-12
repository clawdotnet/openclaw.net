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

    Task<AgentWorkflowRunResult> RunAsync(
        AgentWorkflowRequest request,
        WorkflowExecutionContext? executionContext,
        CancellationToken cancellationToken)
        => executionContext is null
            ? RunAsync(request, cancellationToken)
            : throw new NotSupportedException("This workflow runner does not support user delegation.");

    Task<AgentWorkflowRunSnapshot> GetAsync(
        string runId,
        WorkflowExecutionContext? executionContext,
        CancellationToken cancellationToken)
        => executionContext is null
            ? GetAsync(runId, cancellationToken)
            : throw new NotSupportedException("This workflow runner does not support user delegation.");

    Task<AgentWorkflowRunSnapshot> RespondAsync(
        string runId,
        AgentWorkflowResponse response,
        WorkflowExecutionContext? executionContext,
        CancellationToken cancellationToken)
        => executionContext is null
            ? RespondAsync(runId, response, cancellationToken)
            : throw new NotSupportedException("This workflow runner does not support user delegation.");

    IAsyncEnumerable<AgentWorkflowEvent> StreamAsync(
        string runId,
        WorkflowExecutionContext? executionContext,
        CancellationToken cancellationToken)
        => executionContext is null
            ? StreamAsync(runId, cancellationToken)
            : throw new NotSupportedException("This workflow runner does not support user delegation.");
}

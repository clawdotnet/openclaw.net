using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Workflows;

// Alias runner for Kind="strategos-http". Composes MafDurableHttpWorkflowRunner
// because the on-the-wire contract is identical (maf-durable-http); this type
// exists only to tag the backend in summary, events, and runtime metadata as
// Strategos-backed so observability + UIs can distinguish it from generic
// maf-durable-http backends. Composition (has-a) over inheritance because
// MafDurableHttpWorkflowRunner is internal sealed.
internal sealed class StrategosHttpWorkflowRunner : IAgentWorkflowRunner, IDisposable
{
    private readonly MafDurableHttpWorkflowRunner _inner;

    public StrategosHttpWorkflowRunner(
        string backendId,
        WorkflowBackendConfig config,
        RuntimeEventStore events,
        RuntimeEventWebhook? webhook,
        ILogger<StrategosHttpWorkflowRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(config, nameof(config));
        ArgumentNullException.ThrowIfNull(events, nameof(events));
        ArgumentNullException.ThrowIfNull(logger, nameof(logger));

        _inner = new MafDurableHttpWorkflowRunner(
            backendId,
            config,
            events,
            webhook,
            NullLogger<MafDurableHttpWorkflowRunner>.Instance);

        BackendId = _inner.BackendId;
        WorkflowId = _inner.WorkflowId;
    }

    public string BackendId { get; }

    public string WorkflowId { get; }

    public AgentWorkflowBackendSummary GetSummary()
    {
        // Override Kind: inner returns MafDurableHttp. We re-emit with StrategosHttp
        // so observers see the configured kind, not the implementation kind.
        var innerSummary = _inner.GetSummary();
        return new AgentWorkflowBackendSummary
        {
            Id = innerSummary.Id,
            Kind = AgentWorkflowBackendKinds.StrategosHttp,
            WorkflowName = innerSummary.WorkflowName,
            DisplayName = innerSummary.DisplayName,
            Enabled = innerSummary.Enabled,
        };
    }

    public Task<AgentWorkflowRunResult> RunAsync(AgentWorkflowRequest request, CancellationToken cancellationToken = default)
        => _inner.RunAsync(request, cancellationToken);

    public Task<AgentWorkflowRunSnapshot> GetAsync(string runId, CancellationToken cancellationToken = default)
        => _inner.GetAsync(runId, cancellationToken);

    public Task<AgentWorkflowRunSnapshot> RespondAsync(string runId, AgentWorkflowResponse response, CancellationToken cancellationToken = default)
        => _inner.RespondAsync(runId, response, cancellationToken);

    public IAsyncEnumerable<AgentWorkflowEvent> StreamAsync(string runId, CancellationToken cancellationToken = default)
        => _inner.StreamAsync(runId, cancellationToken);

    public void Dispose() => _inner.Dispose();
}
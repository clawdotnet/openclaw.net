using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using System.Text.Json;

namespace OpenClaw.Gateway.Workflows;

internal sealed class AgentWorkflowRegistry : IDisposable
{
    private readonly Dictionary<string, IAgentWorkflowRunner> _runners;
    private bool _disposed;

    public AgentWorkflowRegistry(
        GatewayConfig config,
        RuntimeEventStore events,
        ILoggerFactory loggerFactory)
        : this(config, events, loggerFactory, null, null, TimeProvider.System)
    {
    }

    public AgentWorkflowRegistry(
        GatewayConfig config,
        RuntimeEventStore events,
        ILoggerFactory loggerFactory,
        IWorkflowAccessTokenProvider? tokenProvider,
        IHttpClientFactory? httpClientFactory,
        TimeProvider timeProvider)
    {
        _runners = new Dictionary<string, IAgentWorkflowRunner>(StringComparer.OrdinalIgnoreCase);
        if (!config.Workflows.Enabled)
            return;

        foreach (var (backendId, backendConfig) in config.Workflows.Backends)
        {
            if (string.IsNullOrWhiteSpace(backendId) || !backendConfig.Enabled)
                continue;

            var kind = string.IsNullOrWhiteSpace(backendConfig.Kind)
                ? AgentWorkflowBackendKinds.MafDurableHttp
                : backendConfig.Kind.Trim();

            if (!string.Equals(kind, AgentWorkflowBackendKinds.MafDurableHttp, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Unsupported workflow backend kind '{kind}' for backend '{backendId}'.");

            var normalizedBackendId = backendId.Trim();
            if (_runners.ContainsKey(normalizedBackendId))
                throw new InvalidOperationException($"Duplicate workflow backend id '{normalizedBackendId}' after trimming whitespace.");

            var frozen = JsonSerializer.Deserialize(
                JsonSerializer.Serialize(backendConfig, CoreJsonContext.Default.WorkflowBackendConfig),
                CoreJsonContext.Default.WorkflowBackendConfig)
                ?? throw new InvalidOperationException("Workflow backend configuration could not be copied.");
            _runners[normalizedBackendId] = httpClientFactory is null
                ? new MafDurableHttpWorkflowRunner(normalizedBackendId, frozen, events, loggerFactory.CreateLogger<MafDurableHttpWorkflowRunner>())
                : new MafDurableHttpWorkflowRunner(normalizedBackendId, frozen, events, loggerFactory.CreateLogger<MafDurableHttpWorkflowRunner>(),
                    tokenProvider, httpClientFactory.CreateClient("workflow-backend"), timeProvider);
        }
    }

    public IReadOnlyList<AgentWorkflowBackendSummary> List()
        => _runners.Values
            .OrderBy(static runner => runner.BackendId, StringComparer.OrdinalIgnoreCase)
            .Select(static runner => runner.GetSummary())
            .ToArray();

    public Task<AgentWorkflowRunResult> RunAsync(
        string backendId,
        AgentWorkflowRequest request,
        CancellationToken cancellationToken)
        => GetRunner(backendId).RunAsync(request, cancellationToken);

    public Task<AgentWorkflowRunSnapshot> GetAsync(
        string backendId,
        string runId,
        CancellationToken cancellationToken)
        => GetRunner(backendId).GetAsync(runId, cancellationToken);

    public Task<AgentWorkflowRunSnapshot> RespondAsync(
        string backendId,
        string runId,
        AgentWorkflowResponse response,
        CancellationToken cancellationToken)
        => GetRunner(backendId).RespondAsync(runId, response, cancellationToken);

    private IAgentWorkflowRunner GetRunner(string backendId)
    {
        if (string.IsNullOrWhiteSpace(backendId))
            throw new KeyNotFoundException("Workflow backend id is required.");

        if (_runners.TryGetValue(backendId.Trim(), out var runner))
            return runner;

        throw new KeyNotFoundException($"Workflow backend '{backendId}' was not found.");
    }

    public Task<AgentWorkflowRunResult> RunAsync(string backendId, AgentWorkflowRequest request,
        WorkflowExecutionContext? executionContext, CancellationToken cancellationToken)
        => GetRunner(backendId).RunAsync(request, executionContext, cancellationToken);

    public Task<AgentWorkflowRunSnapshot> GetAsync(string backendId, string runId,
        WorkflowExecutionContext? executionContext, CancellationToken cancellationToken)
        => GetRunner(backendId).GetAsync(runId, executionContext, cancellationToken);

    public Task<AgentWorkflowRunSnapshot> RespondAsync(string backendId, string runId, AgentWorkflowResponse response,
        WorkflowExecutionContext? executionContext, CancellationToken cancellationToken)
        => GetRunner(backendId).RespondAsync(runId, response, executionContext, cancellationToken);

    public IAsyncEnumerable<AgentWorkflowEvent> StreamAsync(string backendId, string runId,
        WorkflowExecutionContext? executionContext, CancellationToken cancellationToken)
        => GetRunner(backendId).StreamAsync(runId, executionContext, cancellationToken);

    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var disposable in _runners.Values.OfType<IDisposable>())
            disposable.Dispose();

        _disposed = true;
    }
}

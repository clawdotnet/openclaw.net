using Microsoft.Extensions.AI;

using OpenClaw.StrategosWorkflowHost.Configuration;

using Strategos.Abstractions;
using Strategos.Selection;

namespace OpenClaw.StrategosWorkflowHost.Adapters;

/// <summary>
/// IChatClient decorator that picks an agent via Thompson Sampling on every
/// call and routes the request to the matching inner client. The selected
/// agent id is recorded in <see cref="RunIdAgentSelectionCache"/> so the later
/// outcome event (delivered over /runtime-events) can be attributed back to
/// the agent that produced it.
///
/// Failure modes all fall back to <c>defaultClient</c> rather than throwing:
/// <list type="bullet">
///   <item>selector returns <c>Result.Failure</c></item>
///   <item>selected agentId has no entry in <see cref="SelectorOptions.InnerClients"/></item>
///   <item>ChatOptions does not carry runId/stepName (we still pick, but skip
///   caching so the outcome cannot be correlated back)</item>
/// </list>
/// </summary>
public sealed class SelectorBackedChatClient : IChatClient
{
    private readonly IAgentSelector _selector;
    private readonly RunIdAgentSelectionCache _cache;
    private readonly IChatClient _defaultClient;
    private readonly IReadOnlyDictionary<string, IChatClient> _innerClients;
    private readonly SelectorOptions _options;
    private readonly ILogger<SelectorBackedChatClient> _logger;

    public SelectorBackedChatClient(
        IAgentSelector selector,
        RunIdAgentSelectionCache cache,
        IChatClient defaultClient,
        IReadOnlyDictionary<string, IChatClient> innerClients,
        SelectorOptions options,
        ILogger<SelectorBackedChatClient> logger)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(defaultClient);
        ArgumentNullException.ThrowIfNull(innerClients);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _selector = selector;
        _cache = cache;
        _defaultClient = defaultClient;
        _innerClients = innerClients;
        _options = options;
        _logger = logger;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var inner = await ResolveInnerClientAsync(messages, options, cancellationToken).ConfigureAwait(false);
        return await inner.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var inner = await ResolveInnerClientAsync(messages, options, cancellationToken).ConfigureAwait(false);
        await foreach (var update in inner.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => _defaultClient.GetService(serviceType, serviceKey);

    public void Dispose() => _defaultClient.Dispose();

    private async Task<IChatClient> ResolveInnerClientAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        var context = BuildContext(messages);
        var selectionResult = await _selector.SelectAgentAsync(context, cancellationToken).ConfigureAwait(false);

        if (!selectionResult.IsSuccess || selectionResult.Value is null)
        {
            _logger.LogWarning(
                "Selector returned failure: {Error}. Falling back to default client.",
                selectionResult.Error);
            return _defaultClient;
        }

        var selected = selectionResult.Value.SelectedAgentId;
        if (!_innerClients.TryGetValue(selected, out var inner))
        {
            _logger.LogWarning(
                "Selected agent {AgentId} has no InnerClient registered. Falling back to default client.",
                selected);
            return _defaultClient;
        }

        // Only record when runId/stepName are present — otherwise the outcome
        // event can never correlate back, so caching is wasted.
        if (TryGetCorrelationKey(options, out var runId, out var stepName))
        {
            _cache.Set(runId, stepName, selected, _options.TaskCategory);
        }

        return inner;
    }

    private AgentSelectionContext BuildContext(IEnumerable<ChatMessage> messages)
    {
        var taskDescription = ExtractFirstUserText(messages);
        return new AgentSelectionContext
        {
            WorkflowId = Guid.Empty, // sidecar's workflow correlation is via runId+stepName, not WorkflowId
            StepName = "StrategosChat",
            TaskDescription = taskDescription,
            AvailableAgents = _options.AvailableAgents,
        };
    }

    private static string ExtractFirstUserText(IEnumerable<ChatMessage> messages)
    {
        foreach (var m in messages)
        {
            if (m.Role == ChatRole.User)
                return m.Text ?? string.Empty;
        }
        return string.Empty;
    }

    private static bool TryGetCorrelationKey(ChatOptions? options, out string runId, out string stepName)
    {
        runId = string.Empty;
        stepName = string.Empty;
        if (options?.AdditionalProperties is null) return false;

        if (!options.AdditionalProperties.TryGetValue("runId", out var ridObj) || ridObj is not string rid || string.IsNullOrWhiteSpace(rid))
            return false;
        if (!options.AdditionalProperties.TryGetValue("stepName", out var snObj) || snObj is not string sn || string.IsNullOrWhiteSpace(sn))
            return false;

        runId = rid;
        stepName = sn;
        return true;
    }
}

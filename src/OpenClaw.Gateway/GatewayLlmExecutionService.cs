using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenClaw.Agent;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;

namespace OpenClaw.Gateway;

internal sealed class GatewayLlmExecutionService : ILlmExecutionService
{
    private sealed class RouteState
    {
        public required CircuitBreaker CircuitBreaker { get; init; }
        public long Requests;
        public long Retries;
        public long Errors;
        public string? LastError;
        public DateTimeOffset? LastErrorAtUtc;
    }

    private readonly GatewayConfig _config;
    private readonly LlmProviderRegistry _registry;
    private readonly ProviderPolicyService _policyService;
    private readonly RuntimeEventStore _eventStore;
    private readonly RuntimeMetrics _runtimeMetrics;
    private readonly ProviderUsageTracker _providerUsage;
    private readonly ILogger<GatewayLlmExecutionService> _logger;
    private readonly ConcurrentDictionary<string, RouteState> _routes = new(StringComparer.OrdinalIgnoreCase);

    public GatewayLlmExecutionService(
        GatewayConfig config,
        LlmProviderRegistry registry,
        ProviderPolicyService policyService,
        RuntimeEventStore eventStore,
        RuntimeMetrics runtimeMetrics,
        ProviderUsageTracker providerUsage,
        ILogger<GatewayLlmExecutionService> logger)
    {
        _config = config;
        _registry = registry;
        _policyService = policyService;
        _eventStore = eventStore;
        _runtimeMetrics = runtimeMetrics;
        _providerUsage = providerUsage;
        _logger = logger;
    }

    public CircuitState DefaultCircuitState
        => GetRouteState(_config.Llm.Provider, _config.Llm.Model).CircuitBreaker.State;

    public IReadOnlyList<ProviderRouteHealthSnapshot> SnapshotRoutes()
        => _registry.Snapshot()
            .SelectMany(registration =>
            {
                var models = registration.Models.Length > 0 ? registration.Models : [_config.Llm.Model];
                return models.Distinct(StringComparer.OrdinalIgnoreCase).Select(modelId =>
                {
                    var state = GetRouteState(registration.ProviderId, modelId);
                    return new ProviderRouteHealthSnapshot
                    {
                        ProviderId = registration.ProviderId,
                        ModelId = modelId,
                        IsDefaultRoute = registration.IsDefault && string.Equals(modelId, _config.Llm.Model, StringComparison.OrdinalIgnoreCase),
                        IsDynamic = registration.IsDynamic,
                        OwnerId = registration.OwnerId,
                        CircuitState = state.CircuitBreaker.State.ToString(),
                        Requests = Interlocked.Read(ref state.Requests),
                        Retries = Interlocked.Read(ref state.Retries),
                        Errors = Interlocked.Read(ref state.Errors),
                        LastError = state.LastError,
                        LastErrorAtUtc = state.LastErrorAtUtc
                    };
                });
            })
            .OrderBy(static item => item.ProviderId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.ModelId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public void ResetProvider(string providerId)
    {
        foreach (var key in _routes.Keys.Where(key => key.StartsWith(providerId + ":", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            if (_routes.TryRemove(key, out var state))
                state.CircuitBreaker.Reset();
        }
    }

    public async Task<LlmExecutionResult> GetResponseAsync(
        Session session,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions options,
        TurnContext turnContext,
        LlmExecutionEstimate estimate,
        CancellationToken ct)
    {
        var resolved = _policyService.Resolve(session, _config.Llm);
        var effectiveOptions = CreateEffectiveOptions(options, resolved, estimate);
        var modelsToTry = new[] { resolved.ModelId }
            .Concat(resolved.FallbackModels.Where(static item => !string.IsNullOrWhiteSpace(item)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        RecordEvent(session, turnContext, "llm", "route_selected", "info", $"Selected provider route {resolved.ProviderId}/{resolved.ModelId}", new()
        {
            ["providerId"] = resolved.ProviderId,
            ["modelId"] = resolved.ModelId,
            ["policyRuleId"] = resolved.RuleId ?? ""
        });

        Exception? lastError = null;
        for (var modelIndex = 0; modelIndex < modelsToTry.Length; modelIndex++)
        {
            var modelId = modelsToTry[modelIndex];
            var routeState = GetRouteState(resolved.ProviderId, modelId);
            var chatClient = GetClient(resolved.ProviderId);

            for (var attempt = 0; attempt <= _config.Llm.RetryCount; attempt++)
            {
                Interlocked.Increment(ref routeState.Requests);
                _providerUsage.RecordRequest(resolved.ProviderId, modelId);

                if (attempt > 0 || modelIndex > 0)
                {
                    Interlocked.Increment(ref routeState.Retries);
                    turnContext.RecordRetry();
                    _runtimeMetrics.IncrementLlmRetries();
                    _providerUsage.RecordRetry(resolved.ProviderId, modelId);
                    var delayMs = Math.Min(4_000, (int)Math.Pow(2, attempt + modelIndex) * 500);
                    await Task.Delay(delayMs, ct);
                }

                try
                {
                    RecordEvent(session, turnContext, "llm", "request_started", "info", $"LLM request started for {resolved.ProviderId}/{modelId}", new()
                    {
                        ["providerId"] = resolved.ProviderId,
                        ["modelId"] = modelId
                    });

                    effectiveOptions.ModelId = modelId;
                    var response = await routeState.CircuitBreaker.ExecuteAsync(async innerCt =>
                    {
                        if (_config.Llm.TimeoutSeconds > 0)
                        {
                            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(innerCt);
                            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_config.Llm.TimeoutSeconds));
                            return await chatClient.GetResponseAsync(messages, effectiveOptions, timeoutCts.Token);
                        }

                        return await chatClient.GetResponseAsync(messages, effectiveOptions, innerCt);
                    }, ct);

                    RecordEvent(session, turnContext, "llm", "request_completed", "info", $"LLM request completed for {resolved.ProviderId}/{modelId}", new()
                    {
                        ["providerId"] = resolved.ProviderId,
                        ["modelId"] = modelId
                    });

                    return new LlmExecutionResult
                    {
                        ProviderId = resolved.ProviderId,
                        ModelId = modelId,
                        PolicyRuleId = resolved.RuleId,
                        Response = response
                    };
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    Interlocked.Increment(ref routeState.Errors);
                    routeState.LastError = ex.Message;
                    routeState.LastErrorAtUtc = DateTimeOffset.UtcNow;
                    _runtimeMetrics.IncrementLlmErrors();
                    _providerUsage.RecordError(resolved.ProviderId, modelId);
                    RecordEvent(session, turnContext, "llm", "request_failed", "error", ex.Message, new()
                    {
                        ["providerId"] = resolved.ProviderId,
                        ["modelId"] = modelId,
                        ["exceptionType"] = ex.GetType().Name
                    });

                    if (!IsTransient(ex))
                        break;
                }
            }
        }

        throw lastError ?? new InvalidOperationException("LLM route execution failed.");
    }

    public Task<LlmStreamingExecutionResult> StartStreamingAsync(
        Session session,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions options,
        TurnContext turnContext,
        LlmExecutionEstimate estimate,
        CancellationToken ct)
    {
        var resolved = _policyService.Resolve(session, _config.Llm);
        var effectiveOptions = CreateEffectiveOptions(options, resolved, estimate);
        var routeState = GetRouteState(resolved.ProviderId, resolved.ModelId);
        var chatClient = GetClient(resolved.ProviderId);

        Interlocked.Increment(ref routeState.Requests);
        _providerUsage.RecordRequest(resolved.ProviderId, resolved.ModelId);
        RecordEvent(session, turnContext, "llm", "route_selected", "info", $"Selected provider route {resolved.ProviderId}/{resolved.ModelId}", new()
        {
            ["providerId"] = resolved.ProviderId,
            ["modelId"] = resolved.ModelId,
            ["policyRuleId"] = resolved.RuleId ?? ""
        });
        RecordEvent(session, turnContext, "llm", "stream_started", "info", $"LLM stream started for {resolved.ProviderId}/{resolved.ModelId}", new()
        {
            ["providerId"] = resolved.ProviderId,
            ["modelId"] = resolved.ModelId,
            ["policyRuleId"] = resolved.RuleId ?? ""
        });

        effectiveOptions.ModelId = resolved.ModelId;
        IAsyncEnumerable<ChatResponseUpdate> updates = StreamWithCircuitAsync(
            session,
            turnContext,
            chatClient,
            routeState,
            resolved.ProviderId,
            resolved.ModelId,
            messages,
            effectiveOptions,
            ct);

        return Task.FromResult(new LlmStreamingExecutionResult
        {
            ProviderId = resolved.ProviderId,
            ModelId = resolved.ModelId,
            PolicyRuleId = resolved.RuleId,
            Updates = updates
        });
    }

    private async IAsyncEnumerable<ChatResponseUpdate> StreamWithCircuitAsync(
        Session session,
        TurnContext turnContext,
        IChatClient chatClient,
        RouteState routeState,
        string providerId,
        string modelId,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        routeState.CircuitBreaker.ThrowIfOpen();
        CancellationToken activeToken = ct;
        CancellationTokenSource? timeoutCts = null;
        if (_config.Llm.TimeoutSeconds > 0)
        {
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_config.Llm.TimeoutSeconds));
            activeToken = timeoutCts.Token;
        }

        try
        {
            await using var enumerator = chatClient
                .GetStreamingResponseAsync(messages, options, activeToken)
                .GetAsyncEnumerator(activeToken);

            while (true)
            {
                ChatResponseUpdate current;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                        break;

                    current = enumerator.Current;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    routeState.CircuitBreaker.RecordFailure();
                    Interlocked.Increment(ref routeState.Errors);
                    routeState.LastError = ex.Message;
                    routeState.LastErrorAtUtc = DateTimeOffset.UtcNow;
                    _runtimeMetrics.IncrementLlmErrors();
                    _providerUsage.RecordError(providerId, modelId);
                    RecordEvent(session, turnContext, "llm", "stream_failed", "error", ex.Message, new()
                    {
                        ["providerId"] = providerId,
                        ["modelId"] = modelId,
                        ["exceptionType"] = ex.GetType().Name
                    });
                    throw;
                }

                yield return current;
            }

            routeState.CircuitBreaker.RecordSuccess();
            RecordEvent(session, turnContext, "llm", "stream_completed", "info", $"LLM stream completed for {providerId}/{modelId}", new()
            {
                ["providerId"] = providerId,
                ["modelId"] = modelId
            });
        }
        finally
        {
            timeoutCts?.Dispose();
        }
    }

    private IChatClient GetClient(string providerId)
    {
        if (!_registry.TryGet(providerId, out var registration) || registration is null)
        {
            throw new InvalidOperationException(
                $"Provider '{providerId}' is not registered. " +
                $"Available providers: {string.Join(", ", _registry.Snapshot().Select(static item => item.ProviderId))}");
        }

        return registration.Client;
    }

    private RouteState GetRouteState(string providerId, string modelId)
        => _routes.GetOrAdd(
            $"{providerId}:{modelId}",
            _ => new RouteState
            {
                CircuitBreaker = new CircuitBreaker(
                    _config.Llm.CircuitBreakerThreshold,
                    TimeSpan.FromSeconds(_config.Llm.CircuitBreakerCooldownSeconds),
                    _logger)
            });

    private ChatOptions CreateEffectiveOptions(ChatOptions source, ResolvedProviderRoute resolved, LlmExecutionEstimate estimate)
    {
        var maxOutputTokens = source.MaxOutputTokens;
        if (resolved.MaxOutputTokens > 0)
            maxOutputTokens = maxOutputTokens is > 0 ? Math.Min(maxOutputTokens.Value, resolved.MaxOutputTokens) : resolved.MaxOutputTokens;

        if (resolved.MaxInputTokens > 0 && estimate.EstimatedInputTokens > resolved.MaxInputTokens)
        {
            throw new InvalidOperationException(
                $"Provider policy blocked this request because estimated input tokens ({estimate.EstimatedInputTokens}) exceed maxInputTokens ({resolved.MaxInputTokens}).");
        }

        if (resolved.MaxTotalTokens > 0)
        {
            var configuredOutput = maxOutputTokens ?? _config.Llm.MaxTokens;
            var remaining = resolved.MaxTotalTokens - estimate.EstimatedInputTokens;
            if (remaining <= 0)
            {
                throw new InvalidOperationException(
                    $"Provider policy blocked this request because estimated total tokens would exceed maxTotalTokens ({resolved.MaxTotalTokens}).");
            }

            maxOutputTokens = Math.Min(configuredOutput, (int)remaining);
        }

        return new ChatOptions
        {
            ModelId = resolved.ModelId,
            MaxOutputTokens = maxOutputTokens,
            Temperature = source.Temperature,
            Tools = source.Tools,
            ResponseFormat = source.ResponseFormat
        };
    }

    private void RecordEvent(
        Session session,
        TurnContext turnContext,
        string component,
        string action,
        string severity,
        string summary,
        Dictionary<string, string>? metadata = null)
    {
        _eventStore.Append(new RuntimeEventEntry
        {
            Id = $"evt_{Guid.NewGuid():N}"[..20],
            SessionId = session.Id,
            ChannelId = session.ChannelId,
            SenderId = session.SenderId,
            CorrelationId = turnContext.CorrelationId,
            Component = component,
            Action = action,
            Severity = severity,
            Summary = summary,
            Metadata = metadata
        });
    }

    private static bool IsTransient(Exception ex)
        => ex is HttpRequestException
            || ex is TimeoutException
            || ex is TaskCanceledException
            || ex is CircuitOpenException;
}

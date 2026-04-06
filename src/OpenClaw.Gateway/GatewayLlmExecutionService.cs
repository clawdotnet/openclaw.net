using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Gateway.Models;

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
    private readonly ConfiguredModelProfileRegistry _modelProfiles;
    private readonly IModelSelectionPolicy _selectionPolicy;
    private readonly ProviderPolicyService _policyService;
    private readonly RuntimeEventStore _eventStore;
    private readonly RuntimeMetrics _runtimeMetrics;
    private readonly ProviderUsageTracker _providerUsage;
    private readonly ILogger<GatewayLlmExecutionService> _logger;
    private readonly ConcurrentDictionary<string, RouteState> _routes = new(StringComparer.OrdinalIgnoreCase);

    public GatewayLlmExecutionService(
        GatewayConfig config,
        ConfiguredModelProfileRegistry modelProfiles,
        IModelSelectionPolicy selectionPolicy,
        ProviderPolicyService policyService,
        RuntimeEventStore eventStore,
        RuntimeMetrics runtimeMetrics,
        ProviderUsageTracker providerUsage,
        ILogger<GatewayLlmExecutionService> logger)
    {
        _config = config;
        _modelProfiles = modelProfiles;
        _selectionPolicy = selectionPolicy;
        _policyService = policyService;
        _eventStore = eventStore;
        _runtimeMetrics = runtimeMetrics;
        _providerUsage = providerUsage;
        _logger = logger;
    }

    public GatewayLlmExecutionService(
        GatewayConfig config,
        LlmProviderRegistry registry,
        ProviderPolicyService policyService,
        RuntimeEventStore eventStore,
        RuntimeMetrics runtimeMetrics,
        ProviderUsageTracker providerUsage,
        ILogger<GatewayLlmExecutionService> logger)
        : this(
            config,
            new ConfiguredModelProfileRegistry(config, NullLogger<ConfiguredModelProfileRegistry>.Instance),
            new DefaultModelSelectionPolicy(new ConfiguredModelProfileRegistry(config, NullLogger<ConfiguredModelProfileRegistry>.Instance)),
            policyService,
            eventStore,
            runtimeMetrics,
            providerUsage,
            logger)
    {
    }

    public CircuitState DefaultCircuitState
        => GetRouteState(
            _modelProfiles.DefaultProfileId ?? "default",
            _config.Llm.Provider,
            _config.Llm.Model).CircuitBreaker.State;

    public IReadOnlyList<ProviderRouteHealthSnapshot> SnapshotRoutes()
        => _modelProfiles.ListStatuses()
            .Select(profile =>
            {
                var state = GetRouteState(profile.Id, profile.ProviderId, profile.ModelId);
                return new ProviderRouteHealthSnapshot
                {
                    ProfileId = profile.Id,
                    ProviderId = profile.ProviderId,
                    ModelId = profile.ModelId,
                    IsDefaultRoute = profile.IsDefault,
                    CircuitState = state.CircuitBreaker.State.ToString(),
                    Requests = Interlocked.Read(ref state.Requests),
                    Retries = Interlocked.Read(ref state.Retries),
                    Errors = Interlocked.Read(ref state.Errors),
                    LastError = state.LastError,
                    LastErrorAtUtc = state.LastErrorAtUtc,
                    Tags = profile.Tags,
                    ValidationIssues = profile.ValidationIssues
                };
            })
            .OrderBy(static item => item.ProfileId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public void ResetProvider(string providerId)
    {
        foreach (var key in _routes.Keys.Where(key => key.Contains($":{providerId}:", StringComparison.OrdinalIgnoreCase)).ToArray())
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
        var selection = ResolveSelection(session, messages, options, streaming: false);
        var legacyPolicy = _policyService.Resolve(session, _config.Llm);

        RecordEvent(session, turnContext, "llm", "route_selected", "info", $"Selected provider route {selection.ProviderId}/{selection.ModelId}", new()
        {
            ["providerId"] = selection.ProviderId,
            ["modelId"] = selection.ModelId,
            ["profileId"] = selection.SelectedProfileId ?? "",
            ["policyRuleId"] = legacyPolicy.RuleId ?? ""
        });
        if (!string.IsNullOrWhiteSpace(selection.Explanation))
            _logger.LogInformation("{Explanation}", selection.Explanation);

        Exception? lastError = null;
        foreach (var candidate in selection.Candidates)
        {
            if (!_modelProfiles.TryGetRegistration(candidate.Profile.Id, out var registration) || registration?.Client is null)
                continue;

            var modelsToTry = new[] { ResolveRequestedModelId(session, candidate.Profile) }
                .Concat(candidate.FallbackModels.Where(static item => !string.IsNullOrWhiteSpace(item)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            for (var modelIndex = 0; modelIndex < modelsToTry.Length; modelIndex++)
            {
                var modelId = modelsToTry[modelIndex];
                var routeState = GetRouteState(candidate.Profile.Id, candidate.Profile.ProviderId, modelId);
                var chatClient = registration.Client;
                var effectiveOptions = CreateEffectiveOptions(options, candidate.Profile, registration.ProviderConfig, legacyPolicy, estimate);

                for (var attempt = 0; attempt <= registration.ProviderConfig.RetryCount; attempt++)
                {
                    Interlocked.Increment(ref routeState.Requests);
                    _providerUsage.RecordRequest(candidate.Profile.ProviderId, modelId);

                    if (attempt > 0 || modelIndex > 0)
                    {
                        Interlocked.Increment(ref routeState.Retries);
                        turnContext.RecordRetry();
                        _runtimeMetrics.IncrementLlmRetries();
                        _providerUsage.RecordRetry(candidate.Profile.ProviderId, modelId);
                        var delayMs = Math.Min(4_000, (int)Math.Pow(2, attempt + modelIndex) * 500);
                        await Task.Delay(delayMs, ct);
                    }

                    try
                    {
                        RecordEvent(session, turnContext, "llm", "request_started", "info", $"LLM request started for {candidate.Profile.ProviderId}/{modelId}", new()
                        {
                            ["providerId"] = candidate.Profile.ProviderId,
                            ["modelId"] = modelId,
                            ["profileId"] = candidate.Profile.Id
                        });

                        effectiveOptions.ModelId = modelId;
                        var response = await routeState.CircuitBreaker.ExecuteAsync(async innerCt =>
                        {
                            if (registration.ProviderConfig.TimeoutSeconds > 0)
                            {
                                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(innerCt);
                                timeoutCts.CancelAfter(TimeSpan.FromSeconds(registration.ProviderConfig.TimeoutSeconds));
                                return await chatClient.GetResponseAsync(messages, effectiveOptions, timeoutCts.Token);
                            }

                            return await chatClient.GetResponseAsync(messages, effectiveOptions, innerCt);
                        }, ct);

                        RecordEvent(session, turnContext, "llm", "request_completed", "info", $"LLM request completed for {candidate.Profile.ProviderId}/{modelId}", new()
                        {
                            ["providerId"] = candidate.Profile.ProviderId,
                            ["modelId"] = modelId,
                            ["profileId"] = candidate.Profile.Id
                        });

                        return new LlmExecutionResult
                        {
                            ProfileId = candidate.Profile.Id,
                            ProviderId = candidate.Profile.ProviderId,
                            ModelId = modelId,
                            PolicyRuleId = legacyPolicy.RuleId,
                            SelectionExplanation = selection.Explanation,
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
                        _providerUsage.RecordError(candidate.Profile.ProviderId, modelId);
                        RecordEvent(session, turnContext, "llm", "request_failed", "error", ex.Message, new()
                        {
                            ["providerId"] = candidate.Profile.ProviderId,
                            ["modelId"] = modelId,
                            ["profileId"] = candidate.Profile.Id,
                            ["exceptionType"] = ex.GetType().Name
                        });

                        if (!IsTransient(ex))
                            break;
                    }
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
        var selection = ResolveSelection(session, messages, options, streaming: true);
        var legacyPolicy = _policyService.Resolve(session, _config.Llm);
        var candidate = selection.Candidates.FirstOrDefault()
            ?? throw new InvalidOperationException("No model profile candidate is available for streaming.");
        if (!_modelProfiles.TryGetRegistration(candidate.Profile.Id, out var registration) || registration?.Client is null)
            throw new ModelSelectionException($"Selected model profile '{candidate.Profile.Id}' is not available.");

        var effectiveOptions = CreateEffectiveOptions(options, candidate.Profile, registration.ProviderConfig, legacyPolicy, estimate);
        var selectedModelId = ResolveRequestedModelId(session, candidate.Profile);
        var routeState = GetRouteState(candidate.Profile.Id, candidate.Profile.ProviderId, selectedModelId);
        var chatClient = registration.Client;

        Interlocked.Increment(ref routeState.Requests);
        _providerUsage.RecordRequest(candidate.Profile.ProviderId, selectedModelId);
        RecordEvent(session, turnContext, "llm", "route_selected", "info", $"Selected provider route {candidate.Profile.ProviderId}/{selectedModelId}", new()
        {
            ["providerId"] = candidate.Profile.ProviderId,
            ["modelId"] = selectedModelId,
            ["profileId"] = candidate.Profile.Id,
            ["policyRuleId"] = legacyPolicy.RuleId ?? ""
        });
        RecordEvent(session, turnContext, "llm", "stream_started", "info", $"LLM stream started for {candidate.Profile.ProviderId}/{selectedModelId}", new()
        {
            ["providerId"] = candidate.Profile.ProviderId,
            ["modelId"] = selectedModelId,
            ["profileId"] = candidate.Profile.Id,
            ["policyRuleId"] = legacyPolicy.RuleId ?? ""
        });

        effectiveOptions.ModelId = selectedModelId;
        IAsyncEnumerable<ChatResponseUpdate> updates = StreamWithCircuitAsync(
            session,
            turnContext,
            chatClient,
            routeState,
            candidate.Profile.ProviderId,
            selectedModelId,
            messages,
            effectiveOptions,
            registration.ProviderConfig.TimeoutSeconds,
            candidate.Profile.Id,
            ct);

        return Task.FromResult(new LlmStreamingExecutionResult
        {
            ProfileId = candidate.Profile.Id,
            ProviderId = candidate.Profile.ProviderId,
            ModelId = selectedModelId,
            PolicyRuleId = legacyPolicy.RuleId,
            SelectionExplanation = selection.Explanation,
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
        int timeoutSeconds,
        string profileId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        routeState.CircuitBreaker.ThrowIfOpen();
        CancellationToken activeToken = ct;
        CancellationTokenSource? timeoutCts = null;
        if (timeoutSeconds > 0)
        {
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
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
                        ["profileId"] = profileId,
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
                ["modelId"] = modelId,
                ["profileId"] = profileId
            });
        }
        finally
        {
            timeoutCts?.Dispose();
        }
    }

    private RouteState GetRouteState(string profileId, string providerId, string modelId)
        => _routes.GetOrAdd(
            $"{profileId}:{providerId}:{modelId}",
            _ => new RouteState
            {
                CircuitBreaker = new CircuitBreaker(
                    _config.Llm.CircuitBreakerThreshold,
                    TimeSpan.FromSeconds(_config.Llm.CircuitBreakerCooldownSeconds),
                    _logger)
            });

    private ModelSelectionResult ResolveSelection(
        Session session,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions options,
        bool streaming)
    {
        var explicitProfileId = !string.IsNullOrWhiteSpace(session.ModelProfileId)
            ? session.ModelProfileId
            : (!string.IsNullOrWhiteSpace(session.ModelOverride) && _modelProfiles.TryGet(session.ModelOverride!, out _)
                ? session.ModelOverride
                : null);
        return _selectionPolicy.Resolve(new ModelSelectionRequest
        {
            ExplicitProfileId = explicitProfileId,
            Session = session,
            Messages = messages,
            Options = options,
            Streaming = streaming
        });
    }

    private ChatOptions CreateEffectiveOptions(
        ChatOptions source,
        ModelProfile profile,
        LlmProviderConfig providerConfig,
        ResolvedProviderRoute legacyPolicy,
        LlmExecutionEstimate estimate)
    {
        var maxOutputTokens = source.MaxOutputTokens;
        if (profile.Capabilities.MaxOutputTokens > 0)
            maxOutputTokens = maxOutputTokens is > 0 ? Math.Min(maxOutputTokens.Value, profile.Capabilities.MaxOutputTokens) : profile.Capabilities.MaxOutputTokens;
        if (legacyPolicy.MaxOutputTokens > 0)
            maxOutputTokens = maxOutputTokens is > 0 ? Math.Min(maxOutputTokens.Value, legacyPolicy.MaxOutputTokens) : legacyPolicy.MaxOutputTokens;

        if (profile.Capabilities.MaxContextTokens > 0 && estimate.EstimatedInputTokens > profile.Capabilities.MaxContextTokens)
        {
            throw new ModelSelectionException(
                $"Selected model profile '{profile.Id}' cannot satisfy this request because estimated input tokens ({estimate.EstimatedInputTokens}) exceed MaxContextTokens ({profile.Capabilities.MaxContextTokens}).");
        }

        if (legacyPolicy.MaxInputTokens > 0 && estimate.EstimatedInputTokens > legacyPolicy.MaxInputTokens)
        {
            throw new InvalidOperationException(
                $"Provider policy blocked this request because estimated input tokens ({estimate.EstimatedInputTokens}) exceed maxInputTokens ({legacyPolicy.MaxInputTokens}).");
        }

        if (legacyPolicy.MaxTotalTokens > 0)
        {
            var configuredOutput = maxOutputTokens ?? providerConfig.MaxTokens;
            var remaining = legacyPolicy.MaxTotalTokens - estimate.EstimatedInputTokens;
            if (remaining <= 0)
            {
                throw new InvalidOperationException(
                    $"Provider policy blocked this request because estimated total tokens would exceed maxTotalTokens ({legacyPolicy.MaxTotalTokens}).");
            }

            maxOutputTokens = Math.Min(configuredOutput, (int)remaining);
        }

        return new ChatOptions
        {
            ModelId = profile.ModelId,
            MaxOutputTokens = maxOutputTokens,
            Temperature = source.Temperature,
            Tools = source.Tools,
            ResponseFormat = source.ResponseFormat
        };
    }

    private string ResolveRequestedModelId(Session session, ModelProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(session.ModelOverride) && !_modelProfiles.TryGet(session.ModelOverride!, out _))
            return session.ModelOverride!.Trim();

        return profile.ModelId;
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

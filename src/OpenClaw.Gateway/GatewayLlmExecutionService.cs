using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Gateway.Models;
using OpenClaw.Gateway.PromptCaching;

namespace OpenClaw.Gateway;

internal sealed class GatewayLlmExecutionService : ILlmExecutionService
{
    internal sealed record ProviderRuntimeFailureClassification(string Code, string UserMessage, string OperatorMessage);

    private sealed class CompatibilityServices
    {
        public required ConfiguredModelProfileRegistry Registry { get; init; }
        public required IModelSelectionPolicy SelectionPolicy { get; init; }
    }

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
    private readonly PromptCacheCoordinator _promptCacheCoordinator;
    private readonly PromptCacheWarmRegistry _promptCacheWarmRegistry;
    private readonly ILogger<GatewayLlmExecutionService> _logger;
    private readonly ConcurrentDictionary<string, RouteState> _routes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _reportedProviderGuidance = new(StringComparer.OrdinalIgnoreCase);

    public GatewayLlmExecutionService(
        GatewayConfig config,
        ConfiguredModelProfileRegistry modelProfiles,
        IModelSelectionPolicy selectionPolicy,
        ProviderPolicyService policyService,
        RuntimeEventStore eventStore,
        RuntimeMetrics runtimeMetrics,
        ProviderUsageTracker providerUsage,
        ILogger<GatewayLlmExecutionService> logger)
        : this(
            config,
            modelProfiles,
            selectionPolicy,
            policyService,
            eventStore,
            runtimeMetrics,
            providerUsage,
            new PromptCacheCoordinator(config, new PromptCacheTraceWriter(config)),
            new PromptCacheWarmRegistry(),
            logger)
    {
    }

    public GatewayLlmExecutionService(
        GatewayConfig config,
        ConfiguredModelProfileRegistry modelProfiles,
        IModelSelectionPolicy selectionPolicy,
        ProviderPolicyService policyService,
        RuntimeEventStore eventStore,
        RuntimeMetrics runtimeMetrics,
        ProviderUsageTracker providerUsage,
        PromptCacheCoordinator promptCacheCoordinator,
        PromptCacheWarmRegistry promptCacheWarmRegistry,
        ILogger<GatewayLlmExecutionService> logger)
    {
        _config = config;
        _modelProfiles = modelProfiles;
        _selectionPolicy = selectionPolicy;
        _policyService = policyService;
        _eventStore = eventStore;
        _runtimeMetrics = runtimeMetrics;
        _providerUsage = providerUsage;
        _promptCacheCoordinator = promptCacheCoordinator;
        _promptCacheWarmRegistry = promptCacheWarmRegistry;
        _logger = logger;
    }

    internal GatewayLlmExecutionService(
        GatewayConfig config,
        LlmProviderRegistry registry,
        ProviderPolicyService policyService,
        RuntimeEventStore eventStore,
        RuntimeMetrics runtimeMetrics,
        ProviderUsageTracker providerUsage,
        ILogger<GatewayLlmExecutionService> logger)
        : this(
            config,
            registry,
            policyService,
            eventStore,
            runtimeMetrics,
            providerUsage,
            new PromptCacheCoordinator(config, new PromptCacheTraceWriter(config)),
            new PromptCacheWarmRegistry(),
            logger)
    {
    }

    internal GatewayLlmExecutionService(
        GatewayConfig config,
        LlmProviderRegistry registry,
        ProviderPolicyService policyService,
        RuntimeEventStore eventStore,
        RuntimeMetrics runtimeMetrics,
        ProviderUsageTracker providerUsage,
        PromptCacheCoordinator promptCacheCoordinator,
        PromptCacheWarmRegistry promptCacheWarmRegistry,
        ILogger<GatewayLlmExecutionService> logger)
        : this(
            config,
            CreateCompatibilityServices(config, registry),
            policyService,
            eventStore,
            runtimeMetrics,
            providerUsage,
            promptCacheCoordinator,
            promptCacheWarmRegistry,
            logger)
    {
    }

    private GatewayLlmExecutionService(
        GatewayConfig config,
        CompatibilityServices compatibility,
        ProviderPolicyService policyService,
        RuntimeEventStore eventStore,
        RuntimeMetrics runtimeMetrics,
        ProviderUsageTracker providerUsage,
        PromptCacheCoordinator promptCacheCoordinator,
        PromptCacheWarmRegistry promptCacheWarmRegistry,
        ILogger<GatewayLlmExecutionService> logger)
        : this(
            config,
            compatibility.Registry,
            compatibility.SelectionPolicy,
            policyService,
            eventStore,
            runtimeMetrics,
            providerUsage,
            promptCacheCoordinator,
            promptCacheWarmRegistry,
            logger)
    {
    }

    public CircuitState DefaultCircuitState
    {
        get
        {
            if (_modelProfiles.DefaultProfileId is not null &&
                _modelProfiles.TryGetRegistration(_modelProfiles.DefaultProfileId, out var registration) &&
                registration is not null)
            {
                return GetRouteStateSnapshot(registration.Profile.Id, registration.Profile.ProviderId, registration.Profile.ModelId).CircuitBreaker.State;
            }

            return GetRouteStateSnapshot("default", _config.Llm.Provider, _config.Llm.Model).CircuitBreaker.State;
        }
    }

    public IReadOnlyList<ProviderRouteHealthSnapshot> SnapshotRoutes()
        => BuildRouteDescriptors()
            .Select(route =>
            {
                var state = GetRouteStateSnapshot(route.ProfileId ?? "default", route.ProviderId, route.ModelId);
                return new ProviderRouteHealthSnapshot
                {
                    ProfileId = route.ProfileId,
                    ProviderId = route.ProviderId,
                    ModelId = route.ModelId,
                    IsDefaultRoute = route.IsDefaultRoute,
                    CircuitState = state.CircuitBreaker.State.ToString(),
                    Requests = Interlocked.Read(ref state.Requests),
                    Retries = Interlocked.Read(ref state.Retries),
                    Errors = Interlocked.Read(ref state.Errors),
                    LastError = state.LastError,
                    LastErrorAtUtc = state.LastErrorAtUtc,
                    Tags = route.Tags,
                    ValidationIssues = route.ValidationIssues
                };
            })
            .OrderBy(static item => item.ProfileId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public void ResetProvider(string providerId)
    {
        foreach (var key in _routes.Keys.ToArray())
        {
            if (!TryParseRouteKey(key, out _, out var routeProviderId, out _) ||
                !string.Equals(routeProviderId, providerId, StringComparison.OrdinalIgnoreCase))
                continue;

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
        var selection = ResolveSelection(session, messages, options, estimate, streaming: false);
        var legacyPolicy = _policyService.Resolve(session, _config.Llm);
        if (!string.IsNullOrWhiteSpace(selection.Explanation))
            _logger.LogInformation("{Explanation}", selection.Explanation);

        Exception? lastError = null;
        var routeSelectedRecorded = false;
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
                var chatClient = registration.Client;
                if (!TryCreateEffectiveOptions(options, candidate.Profile, registration.ProviderConfig, legacyPolicy, estimate, out var effectiveOptions, out var profileLimitError))
                {
                    lastError = new ModelSelectionException(profileLimitError);
                    continue;
                }

                effectiveOptions.ModelId = modelId;
                var prepared = _promptCacheCoordinator.Prepare(session, candidate.Profile, modelId, messages, effectiveOptions);
                _promptCacheWarmRegistry.Record(prepared);

                var routeState = GetOrAddRouteState(candidate.Profile.Id, candidate.Profile.ProviderId, modelId);

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
                        if (!routeSelectedRecorded)
                        {
                            routeSelectedRecorded = true;
                            RecordEvent(session, turnContext, "llm", "route_selected", "info", $"Selected provider route {candidate.Profile.ProviderId}/{modelId}", new()
                            {
                                ["providerId"] = candidate.Profile.ProviderId,
                                ["modelId"] = modelId,
                                ["profileId"] = candidate.Profile.Id,
                                ["policyRuleId"] = legacyPolicy.RuleId ?? ""
                            });
                        }

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
                                return await chatClient.GetResponseAsync(prepared.Messages, prepared.Options, timeoutCts.Token);
                            }

                            return await chatClient.GetResponseAsync(prepared.Messages, prepared.Options, innerCt);
                        }, ct);
                        NormalizePromptCacheUsage(response);
                        var cacheUsage = PromptCacheUsageExtractor.FromUsage(response.Usage);
                        _promptCacheCoordinator.RecordResponse(prepared.Descriptor, cacheUsage.CacheReadTokens, cacheUsage.CacheWriteTokens);

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
                        var surfacedError = SurfaceProviderFailure(ex, candidate.Profile.ProviderId);
                        lastError = surfacedError;
                        Interlocked.Increment(ref routeState.Errors);
                        routeState.LastError = surfacedError.Message;
                        routeState.LastErrorAtUtc = DateTimeOffset.UtcNow;
                        _runtimeMetrics.IncrementLlmErrors();
                        _providerUsage.RecordError(candidate.Profile.ProviderId, modelId);
                        RecordEvent(session, turnContext, "llm", "request_failed", "error", surfacedError.Message, new()
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
        var selection = ResolveSelection(session, messages, options, estimate, streaming: true);
        var legacyPolicy = _policyService.Resolve(session, _config.Llm);
        Exception? lastError = null;
        foreach (var candidate in selection.Candidates)
        {
            if (!_modelProfiles.TryGetRegistration(candidate.Profile.Id, out var registration) || registration?.Client is null)
                continue;

            if (!TryCreateEffectiveOptions(options, candidate.Profile, registration.ProviderConfig, legacyPolicy, estimate, out var effectiveOptions, out var profileLimitError))
            {
                lastError = new ModelSelectionException(profileLimitError);
                continue;
            }

            var selectedModelId = ResolveRequestedModelId(session, candidate.Profile);
            var prepared = _promptCacheCoordinator.Prepare(session, candidate.Profile, selectedModelId, messages, effectiveOptions);
            _promptCacheWarmRegistry.Record(prepared);
            var routeState = GetOrAddRouteState(candidate.Profile.Id, candidate.Profile.ProviderId, selectedModelId);
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

            IAsyncEnumerable<ChatResponseUpdate> updates = StreamWithCircuitAsync(
                session,
                turnContext,
                chatClient,
                routeState,
                candidate.Profile.ProviderId,
                selectedModelId,
                prepared.Messages,
                prepared.Options,
                registration.ProviderConfig.TimeoutSeconds,
                candidate.Profile.Id,
                prepared.Descriptor,
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

        throw lastError ?? new InvalidOperationException("No model profile candidate is available for streaming.");
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
        PromptCacheDescriptor descriptor,
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
                    var surfacedError = SurfaceProviderFailure(ex, providerId);
                    routeState.CircuitBreaker.RecordFailure();
                    Interlocked.Increment(ref routeState.Errors);
                    routeState.LastError = surfacedError.Message;
                    routeState.LastErrorAtUtc = DateTimeOffset.UtcNow;
                    _runtimeMetrics.IncrementLlmErrors();
                    _providerUsage.RecordError(providerId, modelId);
                    _promptCacheCoordinator.RecordResponse(descriptor, 0, 0);
                    RecordEvent(session, turnContext, "llm", "stream_failed", "error", surfacedError.Message, new()
                    {
                        ["providerId"] = providerId,
                        ["modelId"] = modelId,
                        ["profileId"] = profileId,
                        ["exceptionType"] = ex.GetType().Name
                    });
                    throw surfacedError;
                }

                foreach (var usage in current.Contents.OfType<UsageContent>())
                {
                    var cacheUsage = PromptCacheUsageExtractor.FromUsage(usage.Details);
                    if (cacheUsage != PromptCacheUsage.Empty)
                        _promptCacheCoordinator.RecordResponse(descriptor, cacheUsage.CacheReadTokens, cacheUsage.CacheWriteTokens);
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

    private RouteState GetOrAddRouteState(string profileId, string providerId, string modelId)
        => _routes.GetOrAdd(
            BuildRouteKey(profileId, providerId, modelId),
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
        LlmExecutionEstimate estimate,
        bool streaming)
    {
        var explicitProfileId = !string.IsNullOrWhiteSpace(session.ModelProfileId)
            ? session.ModelProfileId
            : (!string.IsNullOrWhiteSpace(session.ModelOverride) && _modelProfiles.TryGet(session.ModelOverride!, out _)
                ? session.ModelOverride
                : null);

        var reservedOutputTokens = options.MaxOutputTokens
            ?? session.ModelRequirements.MinOutputTokens
            ?? _config.Llm.MaxTokens;

        return _selectionPolicy.Resolve(new ModelSelectionRequest
        {
            ExplicitProfileId = explicitProfileId,
            Session = session,
            Messages = messages,
            Options = options,
            Streaming = streaming,
            EstimatedInputTokens = estimate.EstimatedInputTokens,
            ReservedOutputTokens = reservedOutputTokens > 0 ? reservedOutputTokens : null
        });
    }

    private bool TryCreateEffectiveOptions(
        ChatOptions source,
        ModelProfile profile,
        LlmProviderConfig providerConfig,
        ResolvedProviderRoute legacyPolicy,
        LlmExecutionEstimate estimate,
        out ChatOptions effectiveOptions,
        out string profileLimitError)
    {
        var maxOutputTokens = source.MaxOutputTokens;
        if (profile.Capabilities.MaxOutputTokens > 0)
            maxOutputTokens = maxOutputTokens is > 0 ? Math.Min(maxOutputTokens.Value, profile.Capabilities.MaxOutputTokens) : profile.Capabilities.MaxOutputTokens;
        if (legacyPolicy.MaxOutputTokens > 0)
            maxOutputTokens = maxOutputTokens is > 0 ? Math.Min(maxOutputTokens.Value, legacyPolicy.MaxOutputTokens) : legacyPolicy.MaxOutputTokens;

        var estimatedTotalTokens = estimate.EstimatedInputTokens + (maxOutputTokens ?? 0);
        if (profile.Capabilities.MaxContextTokens > 0 && estimatedTotalTokens > profile.Capabilities.MaxContextTokens)
        {
            profileLimitError =
                $"Selected model profile '{profile.Id}' cannot satisfy this request because estimated prompt plus reserved output tokens ({estimatedTotalTokens}) exceed MaxContextTokens ({profile.Capabilities.MaxContextTokens}).";
            effectiveOptions = source;
            return false;
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

        profileLimitError = string.Empty;
        effectiveOptions = new ChatOptions
        {
            ModelId = profile.ModelId,
            MaxOutputTokens = maxOutputTokens,
            Temperature = source.Temperature,
            Tools = source.Tools,
            ResponseFormat = source.ResponseFormat,
            ConversationId = source.ConversationId,
            Instructions = source.Instructions,
            TopP = source.TopP,
            TopK = source.TopK,
            FrequencyPenalty = source.FrequencyPenalty,
            PresencePenalty = source.PresencePenalty,
            Seed = source.Seed,
            Reasoning = source.Reasoning,
            StopSequences = source.StopSequences?.ToList(),
            AllowMultipleToolCalls = source.AllowMultipleToolCalls,
            ToolMode = source.ToolMode,
            AdditionalProperties = source.AdditionalProperties?.Clone()
        };
        return true;
    }

    private static void NormalizePromptCacheUsage(ChatResponse response)
    {
        if (response.Usage is null)
            response.Usage = new UsageDetails();

        if (response.AdditionalProperties is null)
            return;

        if (response.Usage.CachedInputTokenCount is null &&
            TryReadLong(response.AdditionalProperties, "cache_read_tokens", out var cacheRead))
        {
            response.Usage.CachedInputTokenCount = cacheRead;
        }

        if (TryReadLong(response.AdditionalProperties, "cache_write_tokens", out var cacheWrite) ||
            TryReadLong(response.AdditionalProperties, "cache_creation_input_tokens", out cacheWrite))
        {
            response.Usage.AdditionalCounts ??= new AdditionalPropertiesDictionary<long>();
            response.Usage.AdditionalCounts["cache_write_tokens"] = cacheWrite;
        }
    }

    private static bool TryReadLong(IReadOnlyDictionary<string, object?> properties, string key, out long value)
    {
        value = 0;
        if (!properties.TryGetValue(key, out var raw) || raw is null)
            return false;

        return raw switch
        {
            long longValue => (value = longValue) >= 0,
            int intValue => (value = intValue) >= 0,
            string text when long.TryParse(text, out var parsed) => (value = parsed) >= 0,
            _ => false
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

    private Exception SurfaceProviderFailure(Exception ex, string providerId)
    {
        var classification = ClassifyProviderFailure(ex, providerId);
        if (classification is null)
            return ex;

        if (_reportedProviderGuidance.TryAdd($"{providerId}:{classification.Code}", 0))
            _logger.LogWarning("Provider guidance: {Guidance}", classification.OperatorMessage);

        return new InvalidOperationException(classification.UserMessage, ex);
    }

    internal static ProviderRuntimeFailureClassification? ClassifyProviderFailure(Exception ex, string providerId)
    {
        var message = ex.GetBaseException().Message;
        var providerLabel = FormatProviderLabel(providerId);
        var apiKeyHint = ResolveApiKeyHint(providerId);

        if (Contains(message, "MODEL_PROVIDER_KEY must be set"))
        {
            return new ProviderRuntimeFailureClassification(
                Code: "missing-key",
                UserMessage: $"{providerLabel} credentials are missing. Set {apiKeyHint} and retry.",
                OperatorMessage: $"{providerLabel} requests cannot run because the API key is missing. Set {apiKeyHint} before retrying.");
        }

        if (Contains(message, "MODEL_PROVIDER_ENDPOINT must be set") ||
            Contains(message, "Endpoint must be set for provider"))
        {
            return new ProviderRuntimeFailureClassification(
                Code: "missing-endpoint",
                UserMessage: $"{providerLabel} endpoint is missing. Set MODEL_PROVIDER_ENDPOINT or OpenClaw:Llm:Endpoint and retry.",
                OperatorMessage: $"{providerLabel} requests cannot run because the provider endpoint is missing. Set MODEL_PROVIDER_ENDPOINT or OpenClaw:Llm:Endpoint before retrying.");
        }

        if (Contains(message, "invalid_api_key") ||
            Contains(message, "Incorrect API key provided") ||
            Contains(message, "invalid api key") ||
            Contains(message, "invalid api-key"))
        {
            return new ProviderRuntimeFailureClassification(
                Code: "invalid-key",
                UserMessage: $"{providerLabel} credentials were rejected. Update {apiKeyHint} and retry.",
                OperatorMessage: $"{providerLabel} requests are failing because the configured API key was rejected. Update {apiKeyHint} and retry.");
        }

        if ((Contains(message, "401") || Contains(message, "403")) &&
            (Contains(message, "auth") || Contains(message, "unauthorized") || Contains(message, "forbidden")))
        {
            return new ProviderRuntimeFailureClassification(
                Code: "auth-rejected",
                UserMessage: $"{providerLabel} credentials were rejected. Update {apiKeyHint} and retry.",
                OperatorMessage: $"{providerLabel} requests are returning authorization failures. Verify {apiKeyHint} and retry.");
        }

        if (Contains(message, "Unsupported LLM provider") ||
            Contains(message, "Configured provider '"))
        {
            return new ProviderRuntimeFailureClassification(
                Code: "unsupported-provider",
                UserMessage: $"{providerLabel} is not available in the current runtime. Update OpenClaw:Llm:Provider or enable the required plugin.",
                OperatorMessage: $"{providerLabel} is not available in the current runtime. Update OpenClaw:Llm:Provider or enable the required provider plugin before retrying.");
        }

        return null;
    }

    private static bool IsTransient(Exception ex)
        => ex is HttpRequestException
            || ex is TimeoutException
            || ex is TaskCanceledException
            || ex is CircuitOpenException;

    private static string ResolveApiKeyHint(string providerId)
        => providerId.Trim().ToLowerInvariant() switch
        {
            "openai" => "MODEL_PROVIDER_KEY or OPENAI_API_KEY",
            _ => "MODEL_PROVIDER_KEY"
        };

    private static string FormatProviderLabel(string providerId)
        => providerId.Trim().ToLowerInvariant() switch
        {
            "openai" => "OpenAI",
            "azure-openai" => "Azure OpenAI",
            "anthropic" or "claude" => "Anthropic",
            "gemini" or "google" => "Gemini",
            { Length: > 0 } => $"Provider '{providerId}'",
            _ => "The configured provider"
        };

    private static bool Contains(string value, string fragment)
        => value.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    private RouteState GetRouteStateSnapshot(string profileId, string providerId, string modelId)
        => _routes.TryGetValue(BuildRouteKey(profileId, providerId, modelId), out var state)
            ? state
            : new RouteState
            {
                CircuitBreaker = new CircuitBreaker(
                    _config.Llm.CircuitBreakerThreshold,
                    TimeSpan.FromSeconds(_config.Llm.CircuitBreakerCooldownSeconds),
                    _logger)
            };

    private IReadOnlyList<(string? ProfileId, string ProviderId, string ModelId, bool IsDefaultRoute, string[] Tags, string[] ValidationIssues)> BuildRouteDescriptors()
    {
        var descriptors = new Dictionary<string, (string? ProfileId, string ProviderId, string ModelId, bool IsDefaultRoute, string[] Tags, string[] ValidationIssues)>(StringComparer.Ordinal);
        var statuses = _modelProfiles.ListStatuses().ToDictionary(status => status.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var status in statuses.Values)
        {
            foreach (var modelId in status.FallbackModels.Prepend(status.ModelId).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var key = BuildRouteKey(status.Id, status.ProviderId, modelId);
                descriptors[key] = (status.Id, status.ProviderId, modelId, status.IsDefault, status.Tags, status.ValidationIssues);
            }
        }

        foreach (var key in _routes.Keys)
        {
            if (!TryParseRouteKey(key, out var profileId, out var providerId, out var modelId) || descriptors.ContainsKey(key))
                continue;

            if (statuses.TryGetValue(profileId, out var status))
            {
                descriptors[key] = (profileId, providerId, modelId, status.IsDefault, status.Tags, status.ValidationIssues);
                continue;
            }

            descriptors[key] = (profileId, providerId, modelId, false, [], []);
        }

        return descriptors.Values.ToArray();
    }

    private static string BuildRouteKey(string profileId, string providerId, string modelId)
        => string.Join(':', EncodeRouteSegment(profileId), EncodeRouteSegment(providerId), EncodeRouteSegment(modelId));

    private static bool TryParseRouteKey(string key, out string profileId, out string providerId, out string modelId)
    {
        profileId = string.Empty;
        providerId = string.Empty;
        modelId = string.Empty;

        var parts = key.Split(':');
        if (parts.Length != 3)
            return false;

        try
        {
            profileId = DecodeRouteSegment(parts[0]);
            providerId = DecodeRouteSegment(parts[1]);
            modelId = DecodeRouteSegment(parts[2]);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string EncodeRouteSegment(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));

    private static string DecodeRouteSegment(string value)
        => Encoding.UTF8.GetString(Convert.FromBase64String(value));

    private static CompatibilityServices CreateCompatibilityServices(GatewayConfig config, LlmProviderRegistry registry)
    {
        var modelProfiles = new ConfiguredModelProfileRegistry(config, NullLogger<ConfiguredModelProfileRegistry>.Instance, registry);
        return new CompatibilityServices
        {
            Registry = modelProfiles,
            SelectionPolicy = new DefaultModelSelectionPolicy(modelProfiles)
        };
    }
}

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;

namespace OpenClaw.Agent;

internal sealed class AgentTurnAccounting
{
    private readonly RuntimeMetrics? _metrics;
    private readonly ProviderUsageTracker? _providerUsage;
    private readonly LlmProviderConfig _config;
    private readonly long _sessionTokenBudget;
    private readonly bool _estimateTokenBudgetAdmission;
    private readonly ITurnTokenUsageObserver? _turnTokenUsageObserver;
    private readonly Func<CircuitState> _circuitState;
    private readonly Func<Session, bool>? _isContractTokenBudgetExceeded;
    private readonly Func<Session, bool>? _isContractRuntimeBudgetExceeded;
    private readonly Action<Session, string, string, long, long>? _recordContractTurnUsage;
    private readonly Action<Session, string>? _appendContractSnapshot;
    private readonly ILogger? _logger;

    public AgentTurnAccounting(
        RuntimeMetrics? metrics,
        ProviderUsageTracker? providerUsage,
        LlmProviderConfig config,
        long sessionTokenBudget,
        bool estimateTokenBudgetAdmission,
        ITurnTokenUsageObserver? turnTokenUsageObserver,
        Func<CircuitState> circuitState,
        Func<Session, bool>? isContractTokenBudgetExceeded,
        Func<Session, bool>? isContractRuntimeBudgetExceeded,
        Action<Session, string, string, long, long>? recordContractTurnUsage,
        Action<Session, string>? appendContractSnapshot,
        ILogger? logger)
    {
        _metrics = metrics;
        _providerUsage = providerUsage;
        _config = config;
        _sessionTokenBudget = sessionTokenBudget;
        _estimateTokenBudgetAdmission = estimateTokenBudgetAdmission;
        _turnTokenUsageObserver = turnTokenUsageObserver;
        _circuitState = circuitState;
        _isContractTokenBudgetExceeded = isContractTokenBudgetExceeded;
        _isContractRuntimeBudgetExceeded = isContractRuntimeBudgetExceeded;
        _recordContractTurnUsage = recordContractTurnUsage;
        _appendContractSnapshot = appendContractSnapshot;
        _logger = logger;
    }

    public void IncrementRequests() => _metrics?.IncrementRequests();

    public void IncrementLlmErrors() => _metrics?.IncrementLlmErrors();

    public bool TryRejectSessionTokenBudget(Session session, TurnContext turnCtx, out string message)
    {
        message = string.Empty;
        if (_sessionTokenBudget <= 0 || session.GetTotalTokens() < _sessionTokenBudget)
            return false;

        _logger?.LogInformation("[{CorrelationId}] Session token budget exceeded mid-turn ({Used}/{Budget})",
            turnCtx.CorrelationId, session.GetTotalTokens(), _sessionTokenBudget);
        message = "You've reached the token limit for this session. Please start a new conversation.";
        return true;
    }

    public bool TryRejectEstimatedBudget(Session session, LlmExecutionEstimate estimate, out string message)
    {
        message = string.Empty;
        if (!_estimateTokenBudgetAdmission || _sessionTokenBudget <= 0)
            return false;

        var remaining = _sessionTokenBudget - session.GetTotalTokens();
        if (remaining > 0 && estimate.EstimatedInputTokens < remaining)
            return false;

        message =
            $"This session is close to its token budget. Estimated prompt tokens ({estimate.EstimatedInputTokens:N0}) " +
            $"meet or exceed the remaining budget ({remaining:N0}). Please start a new conversation.";
        _metrics?.IncrementEstimatedTokenAdmissionRejects();
        _logger?.LogInformation(
            "Estimated token admission control rejected session {SessionId} ({EstimatedInputTokens}/{RemainingBudget})",
            session.Id,
            estimate.EstimatedInputTokens,
            remaining);
        return true;
    }

    public bool TryRejectContractBudget(Session session, out string message)
    {
        message = string.Empty;
        if (session.ContractPolicy is null)
            return false;

        if (_isContractRuntimeBudgetExceeded?.Invoke(session) == true)
        {
            message = "This contract has expired and can no longer execute new work.";
            return true;
        }

        if (_isContractTokenBudgetExceeded?.Invoke(session) == true)
        {
            message = "This contract has reached its token budget and cannot continue.";
            return true;
        }

        return false;
    }

    public void RecordLlmResultUsage(
        Session session,
        TurnContext turnCtx,
        TimeSpan elapsed,
        IReadOnlyList<ChatMessage> messages,
        LlmExecutionResult executionResult,
        int skillPromptLength)
    {
        var response = executionResult.Response;
        var inputTokens = response.Usage?.InputTokenCount ?? 0;
        var outputTokens = response.Usage?.OutputTokenCount ?? 0;
        var cacheUsage = PromptCacheUsageExtractor.FromUsage(response.Usage);
        turnCtx.RecordLlmCall(elapsed, inputTokens, outputTokens);
        _metrics?.IncrementLlmCalls();
        _metrics?.AddInputTokens(inputTokens);
        _metrics?.AddOutputTokens(outputTokens);
        _metrics?.AddPromptCacheReads(cacheUsage.CacheReadTokens);
        _metrics?.AddPromptCacheWrites(cacheUsage.CacheWriteTokens);
        _providerUsage?.AddTokens(executionResult.ProviderId, executionResult.ModelId, inputTokens, outputTokens);
        _providerUsage?.AddCacheTokens(executionResult.ProviderId, executionResult.ModelId, cacheUsage.CacheReadTokens, cacheUsage.CacheWriteTokens);

        session.AddTokenUsage(inputTokens, outputTokens);
        session.AddCacheUsage(cacheUsage.CacheReadTokens, cacheUsage.CacheWriteTokens);
        _recordContractTurnUsage?.Invoke(session, executionResult.ProviderId, executionResult.ModelId, inputTokens, outputTokens);
        var isUsageEstimated = executionResult.Response.Usage is null;
        RecordTurnUsage(
            session,
            executionResult.ProviderId,
            executionResult.ModelId,
            inputTokens,
            outputTokens,
            cacheUsage.CacheReadTokens,
            cacheUsage.CacheWriteTokens,
            isUsageEstimated
                ? LlmExecutionEstimateBuilder.BuildInputTokenEstimate(messages, inputTokens, skillPromptLength)
                : new InputTokenComponentEstimate(),
            isEstimated: isUsageEstimated);
    }

    public void RecordStreamingTurnUsage(
        Session session,
        TurnContext turnCtx,
        IReadOnlyList<ChatMessage> messages,
        AgentStreamCollectResult streamResult,
        int skillPromptLength)
    {
        var providerId = string.IsNullOrWhiteSpace(streamResult.ProviderId)
            ? _config.Provider
            : streamResult.ProviderId!;
        var modelId = string.IsNullOrWhiteSpace(streamResult.ModelId)
            ? _config.Model
            : streamResult.ModelId!;

        turnCtx.RecordLlmCall(streamResult.Elapsed, streamResult.InputTokens, streamResult.OutputTokens);
        _metrics?.IncrementLlmCalls();
        _metrics?.AddInputTokens(streamResult.InputTokens);
        _metrics?.AddOutputTokens(streamResult.OutputTokens);
        _metrics?.AddPromptCacheReads(streamResult.CacheReadTokens);
        _metrics?.AddPromptCacheWrites(streamResult.CacheWriteTokens);
        _providerUsage?.AddTokens(providerId, modelId, streamResult.InputTokens, streamResult.OutputTokens);
        _providerUsage?.AddCacheTokens(providerId, modelId, streamResult.CacheReadTokens, streamResult.CacheWriteTokens);
        session.AddTokenUsage(streamResult.InputTokens, streamResult.OutputTokens);
        session.AddCacheUsage(streamResult.CacheReadTokens, streamResult.CacheWriteTokens);
        _recordContractTurnUsage?.Invoke(session, providerId, modelId, streamResult.InputTokens, streamResult.OutputTokens);
        var isUsageEstimated = streamResult.IsUsageEstimated;
        RecordTurnUsage(
            session,
            providerId,
            modelId,
            streamResult.InputTokens,
            streamResult.OutputTokens,
            streamResult.CacheReadTokens,
            streamResult.CacheWriteTokens,
            isUsageEstimated
                ? LlmExecutionEstimateBuilder.BuildInputTokenEstimate(messages, streamResult.InputTokens, skillPromptLength)
                : new InputTokenComponentEstimate(),
            isEstimated: isUsageEstimated);
    }

    public void RecordCompactionUsage(
        Session session,
        TurnContext turnCtx,
        TimeSpan elapsed,
        IReadOnlyList<ChatMessage> messages,
        LlmExecutionResult executionResult,
        long inputTokens,
        long outputTokens,
        int skillPromptLength)
    {
        var cacheUsage = PromptCacheUsageExtractor.FromUsage(executionResult.Response.Usage);

        session.AddTokenUsage(inputTokens, outputTokens);
        session.AddCacheUsage(cacheUsage.CacheReadTokens, cacheUsage.CacheWriteTokens);
        _recordContractTurnUsage?.Invoke(session, executionResult.ProviderId, executionResult.ModelId, inputTokens, outputTokens);
        turnCtx.RecordLlmCall(elapsed, inputTokens, outputTokens);
        _metrics?.IncrementLlmCalls();
        _metrics?.AddInputTokens(inputTokens);
        _metrics?.AddOutputTokens(outputTokens);
        _metrics?.AddPromptCacheReads(cacheUsage.CacheReadTokens);
        _metrics?.AddPromptCacheWrites(cacheUsage.CacheWriteTokens);
        _providerUsage?.AddTokens(executionResult.ProviderId, executionResult.ModelId, inputTokens, outputTokens);
        _providerUsage?.AddCacheTokens(executionResult.ProviderId, executionResult.ModelId, cacheUsage.CacheReadTokens, cacheUsage.CacheWriteTokens);
        var isUsageEstimated = executionResult.Response.Usage is null;
        RecordTurnUsage(
            session,
            executionResult.ProviderId,
            executionResult.ModelId,
            inputTokens,
            outputTokens,
            cacheUsage.CacheReadTokens,
            cacheUsage.CacheWriteTokens,
            isUsageEstimated
                ? LlmExecutionEstimateBuilder.BuildInputTokenEstimate(messages, inputTokens, skillPromptLength)
                : new InputTokenComponentEstimate(),
            isEstimated: isUsageEstimated);
    }

    private void RecordTurnUsage(
        Session session,
        string providerId,
        string modelId,
        long inputTokens,
        long outputTokens,
        long cacheReadTokens,
        long cacheWriteTokens,
        InputTokenComponentEstimate estimatedInputTokensByComponent,
        bool isEstimated)
    {
        var record = new TurnTokenUsageRecord
        {
            SessionId = session.Id,
            ChannelId = session.ChannelId,
            ProviderId = providerId,
            ModelId = modelId,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CacheReadTokens = cacheReadTokens,
            CacheWriteTokens = cacheWriteTokens,
            EstimatedInputTokensByComponent = estimatedInputTokensByComponent,
            IsEstimated = isEstimated
        };

        if (_turnTokenUsageObserver is not null)
        {
            _turnTokenUsageObserver.RecordTurn(record);
            return;
        }

        _providerUsage?.RecordTurn(
            record.SessionId,
            record.ChannelId,
            record.ProviderId,
            record.ModelId,
            record.InputTokens,
            record.OutputTokens,
            record.CacheReadTokens,
            record.CacheWriteTokens,
            record.EstimatedInputTokensByComponent);
    }

    public void IncrementMemoryCompactions() => _metrics?.IncrementMemoryCompactions();

    public void AppendContractSnapshot(Session session, string status)
    {
        if (session.ContractPolicy is null)
            return;

        _appendContractSnapshot?.Invoke(session, status);
    }

    public void LogTurnComplete(TurnContext turnCtx)
    {
        _metrics?.SetCircuitBreakerState((int)_circuitState());
        _logger?.LogInformation("[{CorrelationId}] Turn complete: {Summary}", turnCtx.CorrelationId, turnCtx.ToString());
    }

    public void RecordProviderRequest(string providerId, string modelId) => _providerUsage?.RecordRequest(providerId, modelId);

    public void RecordProviderRetry(string providerId, string modelId) => _providerUsage?.RecordRetry(providerId, modelId);

    public void RecordProviderError(string providerId, string modelId) => _providerUsage?.RecordError(providerId, modelId);

    public void IncrementLlmRetries() => _metrics?.IncrementLlmRetries();
}

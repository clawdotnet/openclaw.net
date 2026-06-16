using System.Diagnostics;
using System.Net.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;

namespace OpenClaw.Agent;

internal sealed class AgentModelExecutor
{
    private readonly IChatClient _chatClient;
    private readonly LlmProviderConfig _config;
    private readonly CircuitBreaker _circuitBreaker;
    private readonly ILlmExecutionService? _llmExecutionService;
    private readonly AgentTurnAccounting _accounting;
    private readonly ILogger? _logger;
    private readonly int _llmTimeoutSeconds;
    private readonly int _retryCount;

    public AgentModelExecutor(
        IChatClient chatClient,
        LlmProviderConfig config,
        CircuitBreaker circuitBreaker,
        ILlmExecutionService? llmExecutionService,
        AgentTurnAccounting accounting,
        ILogger? logger)
    {
        _chatClient = chatClient;
        _config = config;
        _circuitBreaker = circuitBreaker;
        _llmExecutionService = llmExecutionService;
        _accounting = accounting;
        _logger = logger;
        _llmTimeoutSeconds = config.TimeoutSeconds;
        _retryCount = config.RetryCount;
    }

    public CircuitState CircuitBreakerState => _llmExecutionService?.DefaultCircuitState ?? _circuitBreaker.State;

    public async Task<LlmExecutionResult> CallLlmWithResilienceAsync(
        Session session,
        List<ChatMessage> messages,
        ChatOptions options,
        TurnContext turnCtx,
        int skillPromptLength,
        CancellationToken ct)
    {
        using var activity = Telemetry.ActivitySource.StartActivity("Agent.CallLlm");
        activity?.SetTag("llm.messages_count", messages.Count);

        var estimate = LlmExecutionEstimateBuilder.Create(messages, skillPromptLength);
        if (_accounting.TryRejectEstimatedBudget(session, estimate, out var admissionMessage))
            throw new EstimatedBudgetAdmissionException(admissionMessage);

        if (_llmExecutionService is not null)
            return await _llmExecutionService.GetResponseAsync(
                session,
                messages,
                options,
                turnCtx,
                estimate,
                ct);

        var lastException = default(Exception);

        for (var attempt = 0; attempt <= _retryCount; attempt++)
        {
            var providerId = _config.Provider;
            var modelId = options.ModelId ?? _config.Model;
            _accounting.RecordProviderRequest(providerId, modelId);
            if (attempt > 0)
            {
                var delayMs = (int)Math.Pow(2, attempt - 1) * 1000; // 1s, 2s, 4s ...
                turnCtx.RecordRetry();
                _accounting.IncrementLlmRetries();
                _accounting.RecordProviderRetry(providerId, modelId);
                _logger?.LogInformation("[{CorrelationId}] LLM retry {Attempt}/{Max} after {Delay}ms",
                    turnCtx.CorrelationId, attempt, _retryCount, delayMs);
                await Task.Delay(delayMs, ct);
            }

            try
            {
                var response = await _circuitBreaker.ExecuteAsync(async innerCt =>
                {
                    if (_llmTimeoutSeconds > 0)
                    {
                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(innerCt);
                        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_llmTimeoutSeconds));
                        return await _chatClient.GetResponseAsync(messages, options, timeoutCts.Token);
                    }

                    return await _chatClient.GetResponseAsync(messages, options, innerCt);
                }, ct);

                return new LlmExecutionResult
                {
                    ProviderId = providerId,
                    ModelId = modelId,
                    Response = response
                };
            }
            catch (CircuitOpenException)
            {
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException httpEx) when (IsTransient(httpEx))
            {
                lastException = httpEx;
                _accounting.RecordProviderError(providerId, modelId);
                _logger?.LogWarning(httpEx, "Transient LLM error on attempt {Attempt}", attempt + 1);
            }
            catch (OperationCanceledException timeoutEx) when (!ct.IsCancellationRequested)
            {
                lastException = timeoutEx;
                _accounting.RecordProviderError(providerId, modelId);
                _logger?.LogWarning("LLM call timed out on attempt {Attempt} (timeout {Timeout}s)", attempt + 1, _llmTimeoutSeconds);
            }
            catch (Exception ex) when (attempt < _retryCount && IsTransient(ex))
            {
                lastException = ex;
                _accounting.RecordProviderError(providerId, modelId);
                _logger?.LogWarning(ex, "Transient LLM error on attempt {Attempt}", attempt + 1);
            }
        }

        throw lastException ?? new InvalidOperationException("LLM call failed with no captured exception.");
    }

    public async Task<AgentStreamCollectResult> StreamLlmCollectAsync(
        Session session,
        List<ChatMessage> messages,
        ChatOptions options,
        TurnContext turnCtx,
        int skillPromptLength,
        CancellationToken ct)
    {
        var result = new AgentStreamCollectResult();
        var llmSw = Stopwatch.StartNew();
        var estimate = LlmExecutionEstimateBuilder.Create(messages, skillPromptLength);
        if (_accounting.TryRejectEstimatedBudget(session, estimate, out var admissionMessage))
        {
            result.Error = admissionMessage;
            return result;
        }

        if (_llmExecutionService is not null)
        {
            try
            {
                var streamExecution = await _llmExecutionService.StartStreamingAsync(session, messages, options, turnCtx, estimate, ct);
                result.ProviderId = streamExecution.ProviderId;
                result.ModelId = streamExecution.ModelId;

                await foreach (var update in streamExecution.Updates.WithCancellation(ct))
                {
                    CollectStreamingUpdate(update, result);
                }
            }
            catch (CircuitOpenException coe)
            {
                result.Error = coe.Message;
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (ModelSelectionException ex)
            {
                _logger?.LogWarning("[{CorrelationId}] Streaming model selection failed: {Message}", turnCtx.CorrelationId, ex.Message);
                result.Error = ex.Message;
                return result;
            }
            catch (Exception ex) when (IsExpectedLlmFailure(ex))
            {
                _accounting.IncrementLlmErrors();
                _logger?.LogError(ex, "[{CorrelationId}] Streaming LLM call failed after all retries and fallbacks", turnCtx.CorrelationId);
                result.Error = "Sorry, I'm having trouble reaching my AI provider right now. Please try again shortly.";
                return result;
            }

            llmSw.Stop();
            if (result.InputTokens == 0)
            {
                result.InputTokens = LlmExecutionEstimateBuilder.EstimateInputTokens(messages);
                result.IsUsageEstimated = true;
            }
            if (result.OutputTokens == 0)
            {
                result.OutputTokens = LlmExecutionEstimateBuilder.EstimateTokenCount(result.FullText.Length);
                result.IsUsageEstimated = true;
            }

            result.ProviderId ??= _config.Provider;
            result.ModelId ??= options.ModelId ?? _config.Model;
            result.Elapsed = llmSw.Elapsed;
            return result;
        }

        var currentModel = options.ModelId ?? _config.Model;
        var modelsToTry = new List<string> { currentModel };
        if (_config.FallbackModels is { Length: > 0 })
        {
            foreach (var fallback in _config.FallbackModels.Where(fallback =>
                !string.Equals(fallback, currentModel, StringComparison.OrdinalIgnoreCase)))
                modelsToTry.Add(fallback);
        }

        Exception? lastException = null;

        foreach (var model in modelsToTry)
        {
            _accounting.RecordProviderRequest(_config.Provider, model);
            using var timeoutCts = _llmTimeoutSeconds > 0
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;
            timeoutCts?.CancelAfter(TimeSpan.FromSeconds(_llmTimeoutSeconds));
            var effectiveCt = timeoutCts?.Token ?? ct;

            if (model != currentModel)
            {
                options.ModelId = model;
                _accounting.RecordProviderRetry(_config.Provider, model);
                _logger?.LogWarning("[{CorrelationId}] Retrying streaming with fallback model '{Fallback}'", turnCtx.CorrelationId, model);
            }

            try
            {
                IAsyncEnumerable<ChatResponseUpdate> stream = StreamLlmAsync(messages, options, effectiveCt);

                await foreach (var update in stream.WithCancellation(effectiveCt))
                {
                    CollectStreamingUpdate(update, result);
                }

                lastException = null;
                break;
            }
            catch (CircuitOpenException coe)
            {
                result.Error = coe.Message;
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsExpectedLlmFailure(ex))
            {
                lastException = ex;
                _accounting.RecordProviderError(_config.Provider, model);
                _logger?.LogWarning(ex, "[{CorrelationId}] Streaming LLM call failed for model '{Model}'", turnCtx.CorrelationId, model);
                result.TextDeltas.Clear();
                result.ToolCalls.Clear();
                result.InputTokens = 0;
                result.OutputTokens = 0;
                result.CacheReadTokens = 0;
                result.CacheWriteTokens = 0;
                result.IsUsageEstimated = false;
            }
        }

        if (lastException is not null)
        {
            _accounting.IncrementLlmErrors();
            _logger?.LogError(lastException, "[{CorrelationId}] Streaming LLM call failed after all retries and fallbacks", turnCtx.CorrelationId);
            result.Error = "Sorry, I'm having trouble reaching my AI provider right now. Please try again shortly.";
            return result;
        }

        llmSw.Stop();

        if (result.InputTokens == 0)
        {
            result.InputTokens = LlmExecutionEstimateBuilder.EstimateInputTokens(messages);
            result.IsUsageEstimated = true;
        }
        if (result.OutputTokens == 0)
        {
            result.OutputTokens = LlmExecutionEstimateBuilder.EstimateTokenCount(result.FullText.Length);
            result.IsUsageEstimated = true;
        }

        result.ProviderId = _config.Provider;
        result.ModelId = options.ModelId ?? _config.Model;
        result.Elapsed = llmSw.Elapsed;

        return result;
    }

    private IAsyncEnumerable<ChatResponseUpdate> StreamLlmAsync(
        List<ChatMessage> messages,
        ChatOptions options,
        CancellationToken ct)
    {
        _circuitBreaker.ThrowIfOpen();
        return _chatClient.GetStreamingResponseAsync(messages, options, ct);
    }

    private static void CollectStreamingUpdate(ChatResponseUpdate update, AgentStreamCollectResult result)
    {
        if (!string.IsNullOrEmpty(update.Text))
            result.TextDeltas.Add(update.Text);

        foreach (var content in update.Contents)
        {
            if (content is FunctionCallContent fc)
                result.ToolCalls.Add(fc);

            if (content is UsageContent usage)
            {
                if (usage.Details.InputTokenCount is > 0)
                    result.InputTokens = (int)usage.Details.InputTokenCount.Value;
                if (usage.Details.OutputTokenCount is > 0)
                    result.OutputTokens = (int)usage.Details.OutputTokenCount.Value;
                var cacheUsage = PromptCacheUsageExtractor.FromUsage(usage.Details);
                if (cacheUsage.CacheReadTokens > 0)
                    result.CacheReadTokens = (int)cacheUsage.CacheReadTokens;
                if (cacheUsage.CacheWriteTokens > 0)
                    result.CacheWriteTokens = (int)cacheUsage.CacheWriteTokens;
            }
        }
    }

    private static bool IsTransient(Exception ex)
    {
        if (ex is HttpRequestException { StatusCode: null })
            return true;

        if (ex is HttpRequestException httpEx && httpEx.StatusCode.HasValue)
        {
            var code = (int)httpEx.StatusCode.Value;
            return code is 429 or (>= 500 and <= 599);
        }

        return ex is IOException or System.Net.Sockets.SocketException;
    }

    private static bool IsExpectedLlmFailure(Exception ex)
        => ex is HttpRequestException
            or TimeoutException
            or OperationCanceledException
            or IOException
            or System.Net.Sockets.SocketException
            || ex is InvalidOperationException invalidOperation && IsExpectedLlmInvalidOperation(invalidOperation);

    private static bool IsExpectedLlmInvalidOperation(InvalidOperationException ex)
    {
        if (ex.InnerException is not null && IsExpectedLlmFailure(ex.InnerException))
            return true;

        var message = ex.Message;
        return message.Contains("LLM", StringComparison.OrdinalIgnoreCase)
            || message.Contains("provider", StringComparison.OrdinalIgnoreCase)
            || message.Contains("model", StringComparison.OrdinalIgnoreCase)
            || message.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || message.Contains("API key", StringComparison.OrdinalIgnoreCase)
            || message.Contains("endpoint", StringComparison.OrdinalIgnoreCase)
            || message.Contains("token budget", StringComparison.OrdinalIgnoreCase);
    }
}

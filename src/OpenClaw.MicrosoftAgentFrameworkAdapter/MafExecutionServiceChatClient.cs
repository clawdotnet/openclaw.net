using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenClaw.Agent;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;

namespace OpenClaw.MicrosoftAgentFrameworkAdapter;

internal sealed class MafExecutionServiceChatClient : IChatClient
{
    private readonly ILlmExecutionService _llmExecutionService;
    private readonly RuntimeMetrics _metrics;
    private readonly ProviderUsageTracker _providerUsage;
    private readonly MafTelemetryAdapter _telemetry;
    private readonly ILogger? _logger;

    public MafExecutionServiceChatClient(
        ILlmExecutionService llmExecutionService,
        RuntimeMetrics metrics,
        ProviderUsageTracker providerUsage,
        MafTelemetryAdapter telemetry,
        ILogger? logger = null)
    {
        _llmExecutionService = llmExecutionService;
        _metrics = metrics;
        _providerUsage = providerUsage;
        _telemetry = telemetry;
        _logger = logger;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var executionContext = MafExecutionContextScope.Current;
        var messageList = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        options ??= new ChatOptions();
        var estimate = LlmExecutionEstimateBuilder.Create(
            messageList,
            executionContext.SkillPromptLength);
        var sw = Stopwatch.StartNew();
        var result = await _llmExecutionService.GetResponseAsync(
            executionContext.Session,
            messageList,
            options,
            executionContext.TurnContext,
            estimate,
            cancellationToken);
        sw.Stop();

        RecordUsage(
            executionContext,
            messageList,
            sw.Elapsed,
            result.ProviderId,
            result.ModelId,
            result.Response.Usage?.InputTokenCount,
            result.Response.Usage?.OutputTokenCount,
            PromptCacheUsageExtractor.FromUsage(result.Response.Usage));

        _telemetry.TagProvider(Activity.Current, result.ProviderId, result.ModelId);
        return result.Response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var executionContext = MafExecutionContextScope.Current;
        var messageList = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        options ??= new ChatOptions();
        var estimate = LlmExecutionEstimateBuilder.Create(
            messageList,
            executionContext.SkillPromptLength);
        var sw = Stopwatch.StartNew();
        var result = await _llmExecutionService.StartStreamingAsync(
            executionContext.Session,
            messageList,
            options,
            executionContext.TurnContext,
            estimate,
            cancellationToken);

        long? inputTokens = null;
        long? outputTokens = null;
        var cacheUsage = PromptCacheUsage.Empty;
        var streamedText = new StringBuilder();

        await foreach (var update in result.Updates.WithCancellation(cancellationToken))
        {
            if (!string.IsNullOrEmpty(update.Text))
                streamedText.Append(update.Text);

            foreach (var content in update.Contents)
            {
                if (content is not UsageContent usage)
                    continue;

                if (usage.Details.InputTokenCount is > 0)
                    inputTokens = usage.Details.InputTokenCount.Value;
                if (usage.Details.OutputTokenCount is > 0)
                    outputTokens = usage.Details.OutputTokenCount.Value;
                cacheUsage = PromptCacheUsageExtractor.FromUsage(usage.Details);
            }

            yield return update;
        }

        sw.Stop();
        RecordUsage(
            executionContext,
            messageList,
            sw.Elapsed,
            result.ProviderId,
            result.ModelId,
            inputTokens,
            outputTokens,
            cacheUsage,
            fallbackOutputLength: streamedText.Length);

        _telemetry.TagProvider(Activity.Current, result.ProviderId, result.ModelId);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(_llmExecutionService) ? _llmExecutionService : null;

    public void Dispose()
    {
    }

    private void RecordUsage(
        MafExecutionContext executionContext,
        IReadOnlyList<ChatMessage> messages,
        TimeSpan elapsed,
        string providerId,
        string modelId,
        long? inputTokens,
        long? outputTokens,
        PromptCacheUsage cacheUsage,
        int fallbackOutputLength = 0)
    {
        var resolvedInputTokens = inputTokens is > 0
            ? inputTokens.Value
            : LlmExecutionEstimateBuilder.EstimateInputTokens(messages);
        var resolvedOutputTokens = outputTokens is > 0
            ? outputTokens.Value
            : fallbackOutputLength > 0
                ? LlmExecutionEstimateBuilder.EstimateTokenCount(fallbackOutputLength)
                : 0;

        executionContext.TurnContext.RecordLlmCall(elapsed, resolvedInputTokens, resolvedOutputTokens);
        executionContext.Session.AddTokenUsage(resolvedInputTokens, resolvedOutputTokens);
        executionContext.Session.AddCacheUsage(cacheUsage.CacheReadTokens, cacheUsage.CacheWriteTokens);
        executionContext.RecordContractTurnUsage?.Invoke(executionContext.Session, providerId, modelId, resolvedInputTokens, resolvedOutputTokens);
        _metrics.IncrementLlmCalls();
        _metrics.AddInputTokens(resolvedInputTokens);
        _metrics.AddOutputTokens(resolvedOutputTokens);
        _metrics.AddPromptCacheReads(cacheUsage.CacheReadTokens);
        _metrics.AddPromptCacheWrites(cacheUsage.CacheWriteTokens);
        _providerUsage.AddTokens(providerId, modelId, resolvedInputTokens, resolvedOutputTokens);
        _providerUsage.AddCacheTokens(providerId, modelId, cacheUsage.CacheReadTokens, cacheUsage.CacheWriteTokens);
        var record = new OpenClaw.Core.Models.TurnTokenUsageRecord
        {
            SessionId = executionContext.Session.Id,
            ChannelId = executionContext.Session.ChannelId,
            ProviderId = providerId,
            ModelId = modelId,
            InputTokens = resolvedInputTokens,
            OutputTokens = resolvedOutputTokens,
            CacheReadTokens = cacheUsage.CacheReadTokens,
            CacheWriteTokens = cacheUsage.CacheWriteTokens,
            EstimatedInputTokensByComponent = LlmExecutionEstimateBuilder.BuildInputTokenEstimate(
                messages,
                resolvedInputTokens,
                executionContext.SkillPromptLength),
            IsEstimated = inputTokens is null || outputTokens is null
        };

        if (executionContext.TurnTokenUsageObserver is not null)
        {
            executionContext.TurnTokenUsageObserver.RecordTurn(record);
            return;
        }

        _providerUsage.RecordTurn(
            record.SessionId,
            record.ChannelId,
            record.ProviderId,
            record.ModelId,
            record.InputTokens,
            record.OutputTokens,
            record.CacheReadTokens,
            record.CacheWriteTokens,
            record.EstimatedInputTokensByComponent);

        _logger?.LogDebug(
            "[{CorrelationId}] MAF chat client completed provider={ProviderId} model={ModelId} input={InputTokens} output={OutputTokens}",
            executionContext.TurnContext.CorrelationId,
            providerId,
            modelId,
            resolvedInputTokens,
            resolvedOutputTokens);
    }
}

using System.Collections.Concurrent;
using System.Threading;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Observability;

public sealed class ProviderUsageTracker
{
    private readonly ConcurrentDictionary<(string ProviderId, string ModelId), UsageCounter> _usage = new();
    private readonly ConcurrentQueue<ProviderTurnUsageEntry> _recentTurns = new();
    private const int MaxRecentTurns = 256;

    public void RecordRequest(string providerId, string modelId)
        => GetCounter(providerId, modelId).IncrementRequests();

    public void RecordRetry(string providerId, string modelId)
        => GetCounter(providerId, modelId).IncrementRetries();

    public void RecordError(string providerId, string modelId)
        => GetCounter(providerId, modelId).IncrementErrors();

    public void AddTokens(string providerId, string modelId, long inputTokens, long outputTokens)
    {
        var counter = GetCounter(providerId, modelId);
        if (inputTokens > 0)
            counter.AddInputTokens(inputTokens);
        if (outputTokens > 0)
            counter.AddOutputTokens(outputTokens);
    }

    public IReadOnlyList<ProviderUsageSnapshot> Snapshot()
        => _usage
            .Select(static kvp => kvp.Value.Snapshot(kvp.Key.ProviderId, kvp.Key.ModelId))
            .OrderBy(static item => item.ProviderId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.ModelId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public void RecordTurn(
        string sessionId,
        string channelId,
        string providerId,
        string modelId,
        long inputTokens,
        long outputTokens,
        InputTokenComponentEstimate estimatedInputTokensByComponent)
    {
        _recentTurns.Enqueue(new ProviderTurnUsageEntry
        {
            SessionId = string.IsNullOrWhiteSpace(sessionId) ? "unknown" : sessionId,
            ChannelId = string.IsNullOrWhiteSpace(channelId) ? "unknown" : channelId,
            ProviderId = string.IsNullOrWhiteSpace(providerId) ? "unknown" : providerId,
            ModelId = string.IsNullOrWhiteSpace(modelId) ? "default" : modelId,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            EstimatedInputTokensByComponent = estimatedInputTokensByComponent
        });

        while (_recentTurns.Count > MaxRecentTurns && _recentTurns.TryDequeue(out _))
        {
        }
    }

    public IReadOnlyList<ProviderTurnUsageEntry> RecentTurns(string? sessionId = null, int limit = 50)
    {
        var normalizedLimit = Math.Clamp(limit, 1, MaxRecentTurns);
        var items = _recentTurns.ToArray()
            .Where(item => string.IsNullOrWhiteSpace(sessionId)
                || string.Equals(item.SessionId, sessionId, StringComparison.Ordinal))
            .OrderByDescending(static item => item.TimestampUtc)
            .Take(normalizedLimit)
            .ToArray();

        return items;
    }

    private UsageCounter GetCounter(string providerId, string modelId)
    {
        var normalizedProviderId = string.IsNullOrWhiteSpace(providerId) ? "unknown" : providerId.Trim();
        var normalizedModelId = string.IsNullOrWhiteSpace(modelId) ? "default" : modelId.Trim();
        return _usage.GetOrAdd((normalizedProviderId, normalizedModelId), static _ => new UsageCounter());
    }

    private sealed class UsageCounter
    {
        private long _requests;
        private long _retries;
        private long _errors;
        private long _inputTokens;
        private long _outputTokens;

        public void IncrementRequests() => Interlocked.Increment(ref _requests);
        public void IncrementRetries() => Interlocked.Increment(ref _retries);
        public void IncrementErrors() => Interlocked.Increment(ref _errors);
        public void AddInputTokens(long value) => Interlocked.Add(ref _inputTokens, value);
        public void AddOutputTokens(long value) => Interlocked.Add(ref _outputTokens, value);

        public ProviderUsageSnapshot Snapshot(string providerId, string modelId)
            => new()
            {
                ProviderId = providerId,
                ModelId = modelId,
                Requests = Interlocked.Read(ref _requests),
                Retries = Interlocked.Read(ref _retries),
                Errors = Interlocked.Read(ref _errors),
                InputTokens = Interlocked.Read(ref _inputTokens),
                OutputTokens = Interlocked.Read(ref _outputTokens)
            };
    }
}

public sealed class ProviderUsageSnapshot
{
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
    public long Requests { get; init; }
    public long Retries { get; init; }
    public long Errors { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
}

using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Observability;

public sealed class ProviderUsageTurnTokenUsageObserver : ITurnTokenUsageObserver
{
    private readonly ProviderUsageTracker _providerUsage;

    public ProviderUsageTurnTokenUsageObserver(ProviderUsageTracker providerUsage)
    {
        _providerUsage = providerUsage;
    }

    public void RecordTurn(TurnTokenUsageRecord record)
        => _providerUsage.RecordTurn(
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
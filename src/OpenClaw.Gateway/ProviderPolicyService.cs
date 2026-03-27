using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal sealed class ProviderPolicyService
{
    private const string DirectoryName = "admin";
    private const string FileName = "provider-policies.json";

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly ILogger<ProviderPolicyService> _logger;
    private List<ProviderPolicyRule>? _cached;

    public ProviderPolicyService(string storagePath, ILogger<ProviderPolicyService> logger)
    {
        var rootedStoragePath = Path.IsPathRooted(storagePath)
            ? storagePath
            : Path.GetFullPath(storagePath);
        _path = Path.Combine(rootedStoragePath, DirectoryName, FileName);
        _logger = logger;
    }

    public IReadOnlyList<ProviderPolicyRule> List()
    {
        lock (_gate)
        {
            return LoadUnsafe().OrderByDescending(static item => item.Priority)
                .ThenBy(static item => item.CreatedAtUtc)
                .ToArray();
        }
    }

    public ProviderPolicyRule AddOrUpdate(ProviderPolicyRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.ProviderId))
            throw new InvalidOperationException("providerId is required.");
        if (string.IsNullOrWhiteSpace(rule.ModelId))
            throw new InvalidOperationException("modelId is required.");

        var normalized = new ProviderPolicyRule
        {
            Id = string.IsNullOrWhiteSpace(rule.Id) ? $"pp_{Guid.NewGuid():N}"[..20] : rule.Id.Trim(),
            Priority = rule.Priority,
            Enabled = rule.Enabled,
            ChannelId = Normalize(rule.ChannelId),
            SenderId = Normalize(rule.SenderId),
            SessionId = Normalize(rule.SessionId),
            ProviderId = rule.ProviderId.Trim(),
            ModelId = rule.ModelId.Trim(),
            FallbackModels = rule.FallbackModels
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Select(static item => item.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            MaxInputTokens = Math.Max(0, rule.MaxInputTokens),
            MaxOutputTokens = Math.Max(0, rule.MaxOutputTokens),
            MaxTotalTokens = Math.Max(0, rule.MaxTotalTokens),
            CreatedAtUtc = rule.CreatedAtUtc == default ? DateTimeOffset.UtcNow : rule.CreatedAtUtc
        };

        lock (_gate)
        {
            var items = LoadUnsafe();
            items.RemoveAll(item => string.Equals(item.Id, normalized.Id, StringComparison.Ordinal));
            items.Add(normalized);
            SaveUnsafe(items);
            return normalized;
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            var items = LoadUnsafe();
            var removed = items.RemoveAll(item => string.Equals(item.Id, id, StringComparison.Ordinal)) > 0;
            if (removed)
                SaveUnsafe(items);
            return removed;
        }
    }

    public ResolvedProviderRoute Resolve(Session session, LlmProviderConfig defaultConfig)
    {
        lock (_gate)
        {
            var items = LoadUnsafe();
            var match = items
                .Where(static item => item.Enabled)
                .OrderByDescending(static item => item.Priority)
                .ThenBy(static item => item.CreatedAtUtc)
                .FirstOrDefault(item =>
                    Matches(item.ChannelId, session.ChannelId) &&
                    Matches(item.SenderId, session.SenderId) &&
                    Matches(item.SessionId, session.Id));

            if (match is null)
            {
                return new ResolvedProviderRoute(
                    RuleId: null,
                    ProviderId: defaultConfig.Provider,
                    ModelId: session.ModelOverride ?? defaultConfig.Model,
                    FallbackModels: defaultConfig.FallbackModels,
                    MaxInputTokens: 0,
                    MaxOutputTokens: 0,
                    MaxTotalTokens: 0);
            }

            return new ResolvedProviderRoute(
                RuleId: match.Id,
                ProviderId: match.ProviderId,
                ModelId: string.IsNullOrWhiteSpace(session.ModelOverride) ? match.ModelId : session.ModelOverride!,
                FallbackModels: match.FallbackModels,
                MaxInputTokens: match.MaxInputTokens,
                MaxOutputTokens: match.MaxOutputTokens,
                MaxTotalTokens: match.MaxTotalTokens);
        }
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool Matches(string? expected, string actual)
        => string.IsNullOrWhiteSpace(expected) || string.Equals(expected, actual, StringComparison.Ordinal);

    private List<ProviderPolicyRule> LoadUnsafe()
    {
        if (_cached is not null)
            return _cached;

        if (AtomicJsonFileStore.TryLoad(_path, CoreJsonContext.Default.ListProviderPolicyRule, out List<ProviderPolicyRule>? items, out var error))
        {
            _cached = items ?? [];
            return _cached;
        }

        _logger.LogWarning("Failed to load provider policies from {Path}: {Error}", _path, error);
        _cached = [];

        return _cached;
    }

    private void SaveUnsafe(List<ProviderPolicyRule> items)
    {
        if (!AtomicJsonFileStore.TryWriteAtomic(_path, items, CoreJsonContext.Default.ListProviderPolicyRule, out var error))
        {
            _logger.LogWarning("Failed to save provider policies to {Path}: {Error}", _path, error);
            throw new InvalidOperationException($"Failed to persist provider policies: {error}");
        }

        _cached = items;
    }
}

internal sealed record ResolvedProviderRoute(
    string? RuleId,
    string ProviderId,
    string ModelId,
    IReadOnlyList<string> FallbackModels,
    int MaxInputTokens,
    int MaxOutputTokens,
    int MaxTotalTokens);

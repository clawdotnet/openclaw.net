using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal sealed class ActorRateLimitService
{
    private const string DirectoryName = "admin";
    private const string FileName = "rate-limit-policies.json";

    private sealed class WindowState
    {
        public long BurstWindowSeconds;
        public int BurstCount;
        public long SustainedWindowSeconds;
        public int SustainedCount;
    }

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, WindowState> _windows = new(StringComparer.Ordinal);
    private readonly ILogger<ActorRateLimitService> _logger;
    private List<ActorRateLimitPolicy>? _cached;

    public ActorRateLimitService(string storagePath, ILogger<ActorRateLimitService> logger)
    {
        var rootedStoragePath = Path.IsPathRooted(storagePath)
            ? storagePath
            : Path.GetFullPath(storagePath);
        _path = Path.Combine(rootedStoragePath, DirectoryName, FileName);
        _logger = logger;
    }

    public IReadOnlyList<ActorRateLimitPolicy> ListPolicies()
    {
        lock (_gate)
        {
            return LoadUnsafe().ToArray();
        }
    }

    public ActorRateLimitPolicy AddOrUpdate(ActorRateLimitPolicy policy)
    {
        var normalized = new ActorRateLimitPolicy
        {
            Id = string.IsNullOrWhiteSpace(policy.Id) ? $"rl_{Guid.NewGuid():N}"[..20] : policy.Id.Trim(),
            ActorType = policy.ActorType.Trim(),
            EndpointScope = policy.EndpointScope.Trim(),
            MatchValue = string.IsNullOrWhiteSpace(policy.MatchValue) ? null : policy.MatchValue.Trim(),
            BurstLimit = Math.Max(1, policy.BurstLimit),
            BurstWindowSeconds = Math.Max(1, policy.BurstWindowSeconds),
            SustainedLimit = Math.Max(1, policy.SustainedLimit),
            SustainedWindowSeconds = Math.Max(policy.BurstWindowSeconds, policy.SustainedWindowSeconds),
            Enabled = policy.Enabled,
            CreatedAtUtc = policy.CreatedAtUtc == default ? DateTimeOffset.UtcNow : policy.CreatedAtUtc
        };

        lock (_gate)
        {
            var items = LoadUnsafe();
            items.RemoveAll(item => string.Equals(item.Id, normalized.Id, StringComparison.Ordinal));
            items.Add(normalized);
            SaveUnsafe(items);
        }

        return normalized;
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

    public bool TryConsume(string actorType, string actorKey, string endpointScope, out string? blockedByPolicyId)
    {
        blockedByPolicyId = null;
        if (string.IsNullOrWhiteSpace(actorType) || string.IsNullOrWhiteSpace(actorKey) || string.IsNullOrWhiteSpace(endpointScope))
            return true;

        var policies = ListPolicies()
            .Where(item => item.Enabled)
            .Where(item => string.Equals(item.ActorType, actorType, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.Equals(item.EndpointScope, endpointScope, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.EndpointScope, "*", StringComparison.Ordinal))
            .Where(item => string.IsNullOrWhiteSpace(item.MatchValue)
                || string.Equals(item.MatchValue, actorKey, StringComparison.Ordinal))
            .OrderByDescending(static item => item.BurstLimit)
            .ToArray();

        if (policies.Length == 0)
            return true;

        foreach (var policy in policies)
        {
            var window = _windows.GetOrAdd($"{policy.Id}:{actorKey}", static _ => new WindowState());
            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            lock (window)
            {
                if (window.BurstWindowSeconds == 0 || nowUnix - window.BurstWindowSeconds >= policy.BurstWindowSeconds)
                {
                    window.BurstWindowSeconds = nowUnix;
                    window.BurstCount = 0;
                }

                if (window.SustainedWindowSeconds == 0 || nowUnix - window.SustainedWindowSeconds >= policy.SustainedWindowSeconds)
                {
                    window.SustainedWindowSeconds = nowUnix;
                    window.SustainedCount = 0;
                }

                if (window.BurstCount >= policy.BurstLimit || window.SustainedCount >= policy.SustainedLimit)
                {
                    blockedByPolicyId = policy.Id;
                    return false;
                }

                window.BurstCount++;
                window.SustainedCount++;
            }
        }

        return true;
    }

    public IReadOnlyList<ActorRateLimitStatus> SnapshotActive()
    {
        var policies = ListPolicies().ToDictionary(static item => item.Id, StringComparer.Ordinal);
        var results = new List<ActorRateLimitStatus>();
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var entry in _windows)
        {
            var separator = entry.Key.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
                continue;

            var policyId = entry.Key[..separator];
            var actorKey = entry.Key[(separator + 1)..];
            if (!policies.TryGetValue(policyId, out var policy))
                continue;

            lock (entry.Value)
            {
                if (nowUnix - entry.Value.SustainedWindowSeconds > policy.SustainedWindowSeconds &&
                    nowUnix - entry.Value.BurstWindowSeconds > policy.BurstWindowSeconds)
                {
                    continue;
                }

                results.Add(new ActorRateLimitStatus
                {
                    ActorType = policy.ActorType,
                    EndpointScope = policy.EndpointScope,
                    ActorKey = actorKey,
                    BurstCount = entry.Value.BurstCount,
                    SustainedCount = entry.Value.SustainedCount,
                    BurstWindowStartedAtUtc = DateTimeOffset.FromUnixTimeSeconds(entry.Value.BurstWindowSeconds),
                    SustainedWindowStartedAtUtc = DateTimeOffset.FromUnixTimeSeconds(entry.Value.SustainedWindowSeconds)
                });
            }
        }

        return results
            .OrderByDescending(static item => item.SustainedWindowStartedAtUtc)
            .Take(200)
            .ToArray();
    }

    private List<ActorRateLimitPolicy> LoadUnsafe()
    {
        if (_cached is not null)
            return _cached;

        try
        {
            if (!File.Exists(_path))
            {
                _cached = [];
                return _cached;
            }

            var json = File.ReadAllText(_path);
            _cached = JsonSerializer.Deserialize(json, CoreJsonContext.Default.ListActorRateLimitPolicy) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load actor rate-limit policies from {Path}", _path);
            _cached = [];
        }

        return _cached;
    }

    private void SaveUnsafe(List<ActorRateLimitPolicy> items)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(items, CoreJsonContext.Default.ListActorRateLimitPolicy);
            File.WriteAllText(_path, json);
            _cached = items;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save actor rate-limit policies to {Path}", _path);
        }
    }
}

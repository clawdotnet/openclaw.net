using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal sealed class ActorRateLimitService
{
    private const string DirectoryName = "admin";
    private const string FileName = "rate-limit-policies.json";
    private const int PruneInterval = 128;

    private sealed class WindowState
    {
        public long BurstWindowSeconds;
        public int BurstCount;
        public long SustainedWindowSeconds;
        public int SustainedCount;
        public long LastTouchedUnixSeconds;
    }

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, WindowState> _windows = new(StringComparer.Ordinal);
    private readonly ILogger<ActorRateLimitService> _logger;
    private long _pruneCounter;
    private List<ActorRateLimitPolicy>? _cached;

    internal int ActiveWindowCount => _windows.Count;

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
            {
                SaveUnsafe(items);
                RemoveWindowsForPolicy(id);
            }
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

        MaybePruneWindows(policies);

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
                window.LastTouchedUnixSeconds = nowUnix;
            }
        }

        return true;
    }

    public IReadOnlyList<ActorRateLimitStatus> SnapshotActive()
    {
        var policies = ListPolicies().ToDictionary(static item => item.Id, StringComparer.Ordinal);
        MaybePruneWindows(policies.Values);
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

    private void MaybePruneWindows(IEnumerable<ActorRateLimitPolicy> policies)
    {
        if (Interlocked.Increment(ref _pruneCounter) % PruneInterval != 0)
            return;

        PruneStaleWindows(policies, nowUnix: null);
    }

    internal void PruneStaleWindows()
        => PruneStaleWindows(ListPolicies(), nowUnix: null);

    internal void PruneStaleWindows(long nowUnix)
        => PruneStaleWindows(ListPolicies(), nowUnix);

    private void PruneStaleWindows(IEnumerable<ActorRateLimitPolicy> policies, long? nowUnix)
    {
        var policiesById = policies.ToDictionary(static item => item.Id, StringComparer.Ordinal);
        var currentUnix = nowUnix ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var entry in _windows)
        {
            var separator = entry.Key.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                _windows.TryRemove(entry.Key, out _);
                continue;
            }

            var policyId = entry.Key[..separator];
            if (!policiesById.TryGetValue(policyId, out var policy))
            {
                _windows.TryRemove(entry.Key, out _);
                continue;
            }

            var remove = false;
            lock (entry.Value)
            {
                var lastTouchedUnix = Math.Max(
                    entry.Value.LastTouchedUnixSeconds,
                    Math.Max(entry.Value.BurstWindowSeconds, entry.Value.SustainedWindowSeconds));
                var staleAfterSeconds = Math.Max(policy.BurstWindowSeconds, policy.SustainedWindowSeconds) * 2L;
                remove = lastTouchedUnix == 0 || currentUnix - lastTouchedUnix > staleAfterSeconds;
            }

            if (remove)
                _windows.TryRemove(entry.Key, out _);
        }
    }

    private void RemoveWindowsForPolicy(string policyId)
    {
        var prefix = policyId + ":";
        foreach (var entry in _windows)
        {
            if (entry.Key.StartsWith(prefix, StringComparison.Ordinal))
                _windows.TryRemove(entry.Key, out _);
        }
    }

    private List<ActorRateLimitPolicy> LoadUnsafe()
    {
        if (_cached is not null)
            return _cached;

        if (AtomicJsonFileStore.TryLoad(_path, CoreJsonContext.Default.ListActorRateLimitPolicy, out List<ActorRateLimitPolicy>? items, out var error))
        {
            _cached = items ?? [];
            return _cached;
        }

        _logger.LogWarning("Failed to load actor rate-limit policies from {Path}: {Error}", _path, error);
        _cached = [];

        return _cached;
    }

    private void SaveUnsafe(List<ActorRateLimitPolicy> items)
    {
        if (!AtomicJsonFileStore.TryWriteAtomic(_path, items, CoreJsonContext.Default.ListActorRateLimitPolicy, out var error))
        {
            _logger.LogWarning("Failed to save actor rate-limit policies to {Path}: {Error}", _path, error);
            throw new InvalidOperationException($"Failed to persist actor rate-limit policies: {error}");
        }

        _cached = items;
    }
}

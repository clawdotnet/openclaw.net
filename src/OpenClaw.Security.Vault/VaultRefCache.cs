using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Security;

namespace OpenClaw.Security.Vault;

/// <summary>
/// TTL cache for vault secret values. Backed by <see cref="IMemoryCache"/>.
/// Single-flight per key (SemaphoreSlim), refresh-ahead on TTL expiry,
/// stale-on-failure fallback.
/// </summary>
public sealed class VaultRefCache
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<VaultRefCache> _logger;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _clock;
    private readonly CancellationToken _lifetimeToken;
    private readonly Dictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly object _locksLock = new();
    private readonly Dictionary<string, long> _versions = new(StringComparer.Ordinal);

    private sealed record Entry(string Value, DateTimeOffset FetchedAt, bool Refreshing);

    public VaultRefCache(
        IMemoryCache cache,
        ILogger<VaultRefCache> logger,
        TimeSpan ttl,
        CancellationToken lifetimeToken = default, TimeProvider? clock = null)
    {
        _cache = cache;
        _logger = logger;
        _ttl = ttl;
        _clock = clock ?? TimeProvider.System;
        _lifetimeToken = lifetimeToken;
    }

    public bool TryGet(VaultRef key, out string value)
    {
        var ck = CacheKey(key);
        if (_cache.TryGetValue<Entry>(ck, out var entry) && entry is not null && _clock.GetUtcNow() - entry.FetchedAt < _ttl * 2)
        {
            value = entry.Value;
            return true;
        }
        value = string.Empty;
        return false;
    }

    public async Task<string> GetOrFetchAsync(VaultRef key, Func<CancellationToken, Task<string>> fetch, CancellationToken ct)
    {
        var ck = CacheKey(key);
        var sem = GetLock(ck);

        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long version;
            lock (_locksLock) version = _versions.GetValueOrDefault(ck);
            if (_cache.TryGetValue<Entry>(ck, out var existing) && existing is not null && _clock.GetUtcNow() - existing.FetchedAt < _ttl * 2)
            {
                var age = _clock.GetUtcNow() - existing.FetchedAt;
                if (age < _ttl)
                    return existing.Value;

                // Stale: kick off refresh-ahead, return stale value now.
                // Mark Refreshing synchronously (under the key lock) so concurrent
                // readers do not each start their own background refresh.
                if (!existing.Refreshing)
                {
                    var captured = key;
                    lock (_locksLock)
                    {
                        if (_versions.GetValueOrDefault(ck) != version) return existing.Value;
                        _cache.Set(ck, existing with { Refreshing = true }, StaleEntryOptions);
                    }
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Refresh-ahead outlives the request that observed the stale
                            // entry, so it must not inherit that caller's cancellation.
                            var v = await fetch(_lifetimeToken).ConfigureAwait(false);
                            Set(captured, v, version);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("Vault refresh-ahead failed for key {Key} ({Kind}); keeping stale value until its deadline.", ck, ex.GetType().Name);
                            lock (_locksLock)
                                if (_versions.GetValueOrDefault(ck) == version)
                                    _cache.Set(ck, existing with { Refreshing = false }, StaleEntryOptions);
                        }
                    }, CancellationToken.None);
                }

                return existing.Value;
            }

            // Miss: fetch synchronously under lock.
            try
            {
                var v = await fetch(ct).ConfigureAwait(false);
                Set(key, v, version);
                return v;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Typed resolution errors (path/key not found, auth) propagate as-is;
                // unknown failures are wrapped as transient unavailability.
                if (ex is SecretResolutionException)
                    throw;
                _logger.LogError("Vault fetch failed for key {Key} ({Kind}).", ck, ex.GetType().Name);
                throw new VaultUnavailableException("Vault fetch failed.", retryable: true);
            }
        }
        finally
        {
            sem.Release();
        }
    }

    public void Invalidate(VaultRef key)
    {
        lock (_locksLock)
        {
            var ck = CacheKey(key);
            _versions[ck] = _versions.GetValueOrDefault(ck) + 1;
            _cache.Remove(ck);
        }
    }

    private MemoryCacheEntryOptions StaleEntryOptions => new()
    {
        AbsoluteExpirationRelativeToNow = _ttl * 2  // keep stale value beyond TTL for fallback
    };

    private void Set(VaultRef key, string value, long version)
    {
        lock (_locksLock)
        {
            var ck = CacheKey(key);
            if (_versions.GetValueOrDefault(ck) != version) return;
            _versions[ck] = version + 1;
            var entry = new Entry(value, _clock.GetUtcNow(), Refreshing: false);
            _cache.Set(ck, entry, StaleEntryOptions);
        }
    }

    private SemaphoreSlim GetLock(string cacheKey)
    {
        lock (_locksLock)
        {
            if (!_locks.TryGetValue(cacheKey, out var sem))
            {
                sem = new SemaphoreSlim(1, 1);
                _locks[cacheKey] = sem;
            }
            return sem;
        }
    }

    private static string CacheKey(VaultRef key) =>
        $"vault:{key.Mount.Length}:{key.Mount}{key.Path.Length}:{key.Path}{key.Key.Length}:{key.Key}";
}

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
    private readonly CancellationToken _lifetimeToken;
    private readonly Dictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly object _locksLock = new();

    private sealed record Entry(string Value, DateTimeOffset FetchedAt, bool Refreshing);

    public VaultRefCache(
        IMemoryCache cache,
        ILogger<VaultRefCache> logger,
        TimeSpan ttl,
        CancellationToken lifetimeToken = default)
    {
        _cache = cache;
        _logger = logger;
        _ttl = ttl;
        _lifetimeToken = lifetimeToken;
    }

    public bool TryGet(VaultRef key, out string value)
    {
        var ck = CacheKey(key);
        if (_cache.TryGetValue<Entry>(ck, out var entry) && entry is not null)
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
            if (_cache.TryGetValue<Entry>(ck, out var existing) && existing is not null)
            {
                var age = DateTimeOffset.UtcNow - existing.FetchedAt;
                if (age < _ttl)
                    return existing.Value;

                // Stale: kick off refresh-ahead, return stale value now.
                // Mark Refreshing synchronously (under the key lock) so concurrent
                // readers do not each start their own background refresh.
                if (!existing.Refreshing)
                {
                    var captured = key;
                    _cache.Set(ck, existing with { Refreshing = true }, StaleEntryOptions);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Refresh-ahead outlives the request that observed the stale
                            // entry, so it must not inherit that caller's cancellation.
                            var v = await fetch(_lifetimeToken).ConfigureAwait(false);
                            Set(captured, v);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Vault refresh-ahead failed for key {Key}; keeping stale value.", ck);
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
                Set(key, v);
                return v;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Typed resolution errors (path/key not found, auth) propagate as-is;
                // unknown failures are wrapped as transient unavailability.
                if (ex is SecretResolutionException)
                    throw;
                _logger.LogError(ex, "Vault fetch failed for key {Key}.", ck);
                throw new VaultUnavailableException($"Vault fetch failed: {ex.Message}", retryable: true);
            }
        }
        finally
        {
            sem.Release();
        }
    }

    public void Invalidate(VaultRef key)
    {
        _cache.Remove(CacheKey(key));
    }

    private MemoryCacheEntryOptions StaleEntryOptions => new()
    {
        AbsoluteExpirationRelativeToNow = _ttl * 2  // keep stale value beyond TTL for fallback
    };

    private void Set(VaultRef key, string value)
    {
        var entry = new Entry(value, DateTimeOffset.UtcNow, Refreshing: false);
        _cache.Set(CacheKey(key), entry, StaleEntryOptions);
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
        $"vault:{key.Mount}:{key.Path}#{key.Key}";
}

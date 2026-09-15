using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Security.Vault;
using Xunit;

namespace OpenClaw.Tests.Security;

public sealed class VaultRefCacheTests
{
    private static VaultRefCache NewCache(TimeSpan? ttl = null)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        return new VaultRefCache(cache, NullLogger<VaultRefCache>.Instance, ttl ?? TimeSpan.FromMinutes(5));
    }

    private static VaultRef Key(string path = "x", string key = "k")
        => new(path, key, KvVersion: 2, Mount: "secret");

    [Fact]
    public async Task GetOrFetchAsync_CacheMiss_CallsFetchOnce_ReturnsAndCaches()
    {
        var cache = NewCache();
        var key = Key();
        var calls = 0;
        Task<string> Fetch(CancellationToken _)
        {
            calls++;
            return Task.FromResult("v");
        }

        var v = await cache.GetOrFetchAsync(key, Fetch, CancellationToken.None);
        Assert.Equal("v", v);
        Assert.Equal(1, calls);

        var v2 = await cache.GetOrFetchAsync(key, Fetch, CancellationToken.None);
        Assert.Equal("v", v2);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetOrFetchAsync_ConcurrentMisses_SingleFlight_OneFetch()
    {
        var cache = NewCache();
        var key = Key();
        var calls = 0;
        async Task<string> Fetch(CancellationToken _)
        {
            await Task.Delay(50);
            Interlocked.Increment(ref calls);
            return "v";
        }

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => cache.GetOrFetchAsync(key, Fetch, CancellationToken.None))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal("v", r));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetOrFetchAsync_StaleEntry_RefreshAhead_ReturnsStale()
    {
        // Entries are kept for 2×TTL so stale reads can fall back. The stale window is
        // (TTL, 2×TTL): age must exceed TTL (150ms) but stay below 2×TTL (300ms).
        var cache = NewCache(TimeSpan.FromMilliseconds(150));
        var key = Key();
        var calls = 0;
        async Task<string> Fetch(CancellationToken _)
        {
            await Task.Yield();
            return Interlocked.Increment(ref calls) switch { 1 => "v1", _ => "v2" };
        }

        var first = await cache.GetOrFetchAsync(key, Fetch, CancellationToken.None);
        Assert.Equal("v1", first);
        await Task.Delay(200);
        var second = await cache.GetOrFetchAsync(key, Fetch, CancellationToken.None);
        Assert.Equal("v1", second); // stale returned
        // Give refresh-ahead a moment to land
        await Task.Delay(100);
        var third = await cache.GetOrFetchAsync(key, Fetch, CancellationToken.None);
        Assert.Equal("v2", third);
        Assert.True(calls >= 2);
    }

    [Fact]
    public async Task GetOrFetchAsync_FetchFailsWithStale_ReturnsStale()
    {
        // TTL=100ms, stale window (100ms, 200ms): wait 150ms so the entry is stale but still present.
        var cache = NewCache(TimeSpan.FromMilliseconds(100));
        var key = Key();

        await cache.GetOrFetchAsync(key, _ => Task.FromResult("v1"), CancellationToken.None);
        await Task.Delay(150);

        var result = await cache.GetOrFetchAsync(key, _ => throw new InvalidOperationException("boom"), CancellationToken.None);
        Assert.Equal("v1", result);
    }

    [Fact]
    public async Task GetOrFetchAsync_FetchFailsNoStale_Throws()
    {
        var cache = NewCache();
        var key = Key();
        await Assert.ThrowsAsync<VaultUnavailableException>(() =>
            cache.GetOrFetchAsync(key, _ => throw new InvalidOperationException("boom"), CancellationToken.None));
    }

    [Fact]
    public void TryGet_NoEntry_False()
    {
        var cache = NewCache();
        Assert.False(cache.TryGet(Key(), out _));
    }

    [Fact]
    public async Task TryGet_AfterFetch_TrueAndReturnsValue()
    {
        var cache = NewCache();
        var key = Key();
        await cache.GetOrFetchAsync(key, _ => Task.FromResult("v"), CancellationToken.None);
        Assert.True(cache.TryGet(key, out var v));
        Assert.Equal("v", v);
    }

    [Fact]
    public async Task Invalidate_RemovesEntry_NextFetchHitsVault()
    {
        var cache = NewCache();
        var key = Key();
        await cache.GetOrFetchAsync(key, _ => Task.FromResult("v1"), CancellationToken.None);
        cache.Invalidate(key);
        var result = await cache.GetOrFetchAsync(key, _ => Task.FromResult("v2"), CancellationToken.None);
        Assert.Equal("v2", result);
    }
}

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Security.Vault;
using Xunit;

namespace OpenClaw.Tests.Security;

public sealed class VaultRefCacheTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static VaultRefCache NewCache(TimeSpan? ttl = null, TimeProvider? clock = null)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        return new VaultRefCache(cache, NullLogger<VaultRefCache>.Instance, ttl ?? TimeSpan.FromMinutes(5), clock: clock);
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
        // Advance logical time without depending on the scheduler or real cache eviction.
        var clock = new Clock();
        var cache = NewCache(TimeSpan.FromMinutes(5), clock);
        var key = Key();
        var calls = 0;
        async Task<string> Fetch(CancellationToken _)
        {
            await Task.Yield();
            return Interlocked.Increment(ref calls) switch { 1 => "v1", _ => "v2" };
        }

        var first = await cache.GetOrFetchAsync(key, Fetch, CancellationToken.None);
        Assert.Equal("v1", first);

        clock.Now += TimeSpan.FromMinutes(6);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        string observed = "v1";
        while (observed != "v2" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
            observed = await cache.GetOrFetchAsync(key, Fetch, CancellationToken.None);
        }
        Assert.Equal("v2", observed); // stale value served until refresh-ahead landed v2
        Assert.True(calls >= 2);
    }

    [Fact]
    public async Task GetOrFetchAsync_FetchFailsWithStale_ReturnsStale()
    {
        // Enter the stale window deterministically, then observe the background refresh.
        var clock = new Clock();
        var cache = NewCache(TimeSpan.FromMinutes(5), clock);
        var key = Key();

        await cache.GetOrFetchAsync(key, _ => Task.FromResult("v1"), CancellationToken.None);

        var calls = 0;
        Task<string> FailingFetch(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            return Task.FromException<string>(new InvalidOperationException("boom"));
        }

        clock.Now += TimeSpan.FromMinutes(6);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref calls) == 0 && DateTime.UtcNow < deadline)
        {
            // Fresh: served without fetching. Stale: refresh-ahead fires (and fails).
            Assert.Equal("v1", await cache.GetOrFetchAsync(key, FailingFetch, CancellationToken.None));
            await Task.Delay(25);
        }
        Assert.True(Volatile.Read(ref calls) > 0, "entry never went stale");

        var result = await cache.GetOrFetchAsync(key, FailingFetch, CancellationToken.None);
        Assert.Equal("v1", result); // stale value returned, fetch failure swallowed
    }

    [Fact]
    public async Task GetOrFetchAsync_RefreshAhead_DoesNotInheritCallerCancellation()
    {
        var clock = new Clock();
        var cache = NewCache(TimeSpan.FromMinutes(5), clock);
        var key = Key();
        await cache.GetOrFetchAsync(key, _ => Task.FromResult("v1"), CancellationToken.None);
        clock.Now += TimeSpan.FromMinutes(6);

        using var callerCts = new CancellationTokenSource();
        var refreshStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string> Refresh(CancellationToken token)
        {
            refreshStarted.TrySetResult(token);
            await releaseRefresh.Task.WaitAsync(token);
            return "v2";
        }

        var resolution = cache.GetOrFetchAsync(key, Refresh, callerCts.Token);
        Assert.Equal("v1", await resolution.WaitAsync(TimeSpan.FromSeconds(2)));
        var refreshToken = await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        callerCts.Cancel();
        Assert.False(refreshToken.IsCancellationRequested);

        releaseRefresh.SetResult();
        var deadline = DateTime.UtcNow.AddSeconds(2);
        string observed = "v1";
        while (observed != "v2" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
            Assert.True(cache.TryGet(key, out observed));
        }
        Assert.Equal("v2", observed);
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

    [Fact]
    public async Task StaleDeadline_IsNotExtendedByFailedRefresh()
    {
        var clock = new Clock();
        var cache = NewCache(TimeSpan.FromMinutes(5), clock);
        var key = Key();
        await cache.GetOrFetchAsync(key, _ => Task.FromResult("old"), CancellationToken.None);
        clock.Now += TimeSpan.FromMinutes(6);
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> Fail(CancellationToken _)
        {
            attempted.TrySetResult();
            throw new InvalidOperationException("sensitive-response-body");
        }
        Assert.Equal("old", await cache.GetOrFetchAsync(key, Fail, CancellationToken.None));
        await attempted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        clock.Now += TimeSpan.FromMinutes(5);
        Assert.False(cache.TryGet(key, out _));
        var exception = await Assert.ThrowsAsync<VaultUnavailableException>(() => cache.GetOrFetchAsync(key, Fail, CancellationToken.None));
        Assert.DoesNotContain("sensitive-response-body", exception.ToString());
    }

    [Fact]
    public async Task Invalidation_DuringFetch_DoesNotRepopulateCache()
    {
        var cache = NewCache();
        var key = Key();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = cache.GetOrFetchAsync(key, _ => release.Task, CancellationToken.None);
        cache.Invalidate(key);
        release.SetResult("old");
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(cache.TryGet(key, out _));
        Assert.Equal("new", await cache.GetOrFetchAsync(key, _ => Task.FromResult("new"), CancellationToken.None));
    }

}

using OpenClaw.Agent.Tools;
using Xunit;

namespace OpenClaw.Tests;

public sealed class CapabilityBindingCacheTests
{
    private const string IntentKey = "key-1";

    [Fact]
    public void Set_TryGet_SameSessionAndKey_HitsAcrossCalls()
    {
        var cache = new CapabilityBindingCache();
        cache.Set("sess-1", IntentKey, "weather-mcp", "get_weather");

        Assert.True(cache.TryGet("sess-1", IntentKey, out var server, out var tool));
        Assert.Equal("weather-mcp", server);
        Assert.Equal("get_weather", tool);
        Assert.True(cache.TryGet("sess-1", IntentKey, out _, out _));
    }

    [Fact]
    public void TryGet_UnknownSessionOrKey_Misses()
    {
        var cache = new CapabilityBindingCache();
        cache.Set("sess-1", IntentKey, "weather-mcp", "get_weather");

        Assert.False(cache.TryGet("sess-2", IntentKey, out _, out _));
        Assert.False(cache.TryGet("sess-1", "other-key", out _, out _));
    }

    [Fact]
    public async Task TryGet_ExpiredEntry_Misses()
    {
        var cache = new CapabilityBindingCache(TimeSpan.FromMilliseconds(50));
        cache.Set("sess-1", IntentKey, "weather-mcp", "get_weather");

        await Task.Delay(200);

        Assert.False(cache.TryGet("sess-1", IntentKey, out _, out _));
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        var cache = new CapabilityBindingCache();
        cache.Set("sess-1", IntentKey, "weather-mcp", "get_weather");
        cache.Set("sess-2", "key-2", "amap-mcp-server", "geocode");

        cache.Clear();

        Assert.False(cache.TryGet("sess-1", IntentKey, out _, out _));
        Assert.False(cache.TryGet("sess-2", "key-2", out _, out _));
    }

    [Theory]
    [InlineData("weather city", "weather,city", "First")]
    [InlineData("weather city", null, "First")]
    [InlineData("ghost-city", "weather", "ExactName")]
    public void ComputeIntentKey_IsDeterministicAndSensitive(string task, string? keywords, string policy)
    {
        var a = CapabilityBindingCache.ComputeIntentKey(task, keywords, policy);
        var b = CapabilityBindingCache.ComputeIntentKey(task, keywords, policy);

        Assert.Equal(64, a.Length);
        Assert.Equal(a, b);
        Assert.NotEqual(a, CapabilityBindingCache.ComputeIntentKey(task + "x", keywords, policy));
        Assert.NotEqual(a, CapabilityBindingCache.ComputeIntentKey(task, keywords, policy + "x"));
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ActorRateLimitServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ActorRateLimitService _service;

    public ActorRateLimitServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_tempDir);
        _service = new ActorRateLimitService(_tempDir, NullLogger<ActorRateLimitService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void TryConsume_BurstLimit_BlocksAfterExhausted()
    {
        _service.AddOrUpdate(new ActorRateLimitPolicy
        {
            Id = "burst-test",
            ActorType = "ip",
            EndpointScope = "chat",
            BurstLimit = 3,
            BurstWindowSeconds = 60,
            SustainedLimit = 100,
            SustainedWindowSeconds = 300,
            Enabled = true
        });

        for (var i = 0; i < 3; i++)
        {
            Assert.True(_service.TryConsume("ip", "10.0.0.1", "chat", out _));
        }

        Assert.False(_service.TryConsume("ip", "10.0.0.1", "chat", out var blockedBy));
        Assert.Equal("burst-test", blockedBy);
    }

    [Fact]
    public void TryConsume_SustainedLimit_BlocksAfterExhausted()
    {
        _service.AddOrUpdate(new ActorRateLimitPolicy
        {
            Id = "sustained-test",
            ActorType = "ip",
            EndpointScope = "chat",
            BurstLimit = 100,
            BurstWindowSeconds = 10,
            SustainedLimit = 5,
            SustainedWindowSeconds = 300,
            Enabled = true
        });

        for (var i = 0; i < 5; i++)
        {
            Assert.True(_service.TryConsume("ip", "10.0.0.1", "chat", out _));
        }

        Assert.False(_service.TryConsume("ip", "10.0.0.1", "chat", out var blockedBy));
        Assert.Equal("sustained-test", blockedBy);
    }

    [Fact]
    public void TryConsume_PerActorIsolation_IndependentLimits()
    {
        _service.AddOrUpdate(new ActorRateLimitPolicy
        {
            Id = "isolation-test",
            ActorType = "ip",
            EndpointScope = "chat",
            BurstLimit = 2,
            BurstWindowSeconds = 60,
            SustainedLimit = 100,
            SustainedWindowSeconds = 300,
            Enabled = true
        });

        // Actor A uses 2 requests
        Assert.True(_service.TryConsume("ip", "actor-a", "chat", out _));
        Assert.True(_service.TryConsume("ip", "actor-a", "chat", out _));
        Assert.False(_service.TryConsume("ip", "actor-a", "chat", out _));

        // Actor B is unaffected
        Assert.True(_service.TryConsume("ip", "actor-b", "chat", out _));
        Assert.True(_service.TryConsume("ip", "actor-b", "chat", out _));
    }

    [Fact]
    public void TryConsume_DisabledPolicy_DoesNotBlock()
    {
        _service.AddOrUpdate(new ActorRateLimitPolicy
        {
            Id = "disabled-test",
            ActorType = "ip",
            EndpointScope = "chat",
            BurstLimit = 1,
            BurstWindowSeconds = 60,
            SustainedLimit = 1,
            SustainedWindowSeconds = 300,
            Enabled = false
        });

        Assert.True(_service.TryConsume("ip", "10.0.0.1", "chat", out _));
        Assert.True(_service.TryConsume("ip", "10.0.0.1", "chat", out _));
    }

    [Fact]
    public void PruneStaleWindows_RemovesExpiredWindows()
    {
        _service.AddOrUpdate(new ActorRateLimitPolicy
        {
            Id = "prune-test",
            ActorType = "ip",
            EndpointScope = "chat",
            BurstLimit = 100,
            BurstWindowSeconds = 10,
            SustainedLimit = 100,
            SustainedWindowSeconds = 30,
            Enabled = true
        });

        _service.TryConsume("ip", "10.0.0.1", "chat", out _);
        Assert.True(_service.ActiveWindowCount > 0);

        // Prune with a far-future timestamp (2x sustained window = 60s, so 120s in the future is safe)
        var farFuture = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 120;
        _service.PruneStaleWindows(farFuture);

        Assert.Equal(0, _service.ActiveWindowCount);
    }
}

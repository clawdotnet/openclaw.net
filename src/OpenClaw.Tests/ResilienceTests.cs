using OpenClaw.Agent;
using OpenClaw.Core.Http;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// Tests for Phase 2 resilience features: CircuitBreaker, AgentRuntime retry/timeout, HttpClientFactory.
/// </summary>
public sealed class ResilienceTests
{
    // ── CircuitBreaker ────────────────────────────────────────────────────

    [Fact]
    public async Task CircuitBreaker_StartsInClosedState()
    {
        var cb = new CircuitBreaker(failureThreshold: 3, cooldown: TimeSpan.FromSeconds(10));
        Assert.Equal(CircuitState.Closed, cb.State);

        var result = await cb.ExecuteAsync(_ => Task.FromResult(42), CancellationToken.None);
        Assert.Equal(42, result);
        Assert.Equal(CircuitState.Closed, cb.State);
    }

    [Fact]
    public async Task CircuitBreaker_OpensAfterThresholdFailures()
    {
        var cb = new CircuitBreaker(failureThreshold: 2, cooldown: TimeSpan.FromMinutes(5));

        // Failure 1 — stays closed
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cb.ExecuteAsync<int>(_ => throw new InvalidOperationException("fail"), CancellationToken.None));
        Assert.Equal(CircuitState.Closed, cb.State);

        // Failure 2 — opens
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cb.ExecuteAsync<int>(_ => throw new InvalidOperationException("fail"), CancellationToken.None));
        Assert.Equal(CircuitState.Open, cb.State);
    }

    [Fact]
    public async Task CircuitBreaker_OpenState_ThrowsCircuitOpenException()
    {
        var cb = new CircuitBreaker(failureThreshold: 1, cooldown: TimeSpan.FromMinutes(5));

        // Trip the breaker
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cb.ExecuteAsync<int>(_ => throw new InvalidOperationException("fail"), CancellationToken.None));
        Assert.Equal(CircuitState.Open, cb.State);

        // Subsequent calls are short-circuited
        var ex = await Assert.ThrowsAsync<CircuitOpenException>(() =>
            cb.ExecuteAsync(_ => Task.FromResult(1), CancellationToken.None));
        Assert.True(ex.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public async Task CircuitBreaker_TransitionsToHalfOpen_AfterCooldown()
    {
        var cb = new CircuitBreaker(failureThreshold: 1, cooldown: TimeSpan.FromMilliseconds(50));

        // Trip the breaker
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cb.ExecuteAsync<int>(_ => throw new InvalidOperationException("fail"), CancellationToken.None));
        Assert.Equal(CircuitState.Open, cb.State);

        // Wait for cooldown
        await Task.Delay(100);

        // Next call should succeed (probe) and close the circuit
        var result = await cb.ExecuteAsync(_ => Task.FromResult(99), CancellationToken.None);
        Assert.Equal(99, result);
        Assert.Equal(CircuitState.Closed, cb.State);
    }

    [Fact]
    public async Task CircuitBreaker_HalfOpen_FailureReopens()
    {
        var cb = new CircuitBreaker(failureThreshold: 1, cooldown: TimeSpan.FromMilliseconds(50));

        // Trip the breaker
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cb.ExecuteAsync<int>(_ => throw new InvalidOperationException("fail"), CancellationToken.None));

        // Wait for cooldown
        await Task.Delay(100);

        // Probe fails → re-opens
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cb.ExecuteAsync<int>(_ => throw new InvalidOperationException("still broken"), CancellationToken.None));
        Assert.Equal(CircuitState.Open, cb.State);
    }

    [Fact]
    public async Task CircuitBreaker_SuccessResetsFailureCount()
    {
        var cb = new CircuitBreaker(failureThreshold: 3, cooldown: TimeSpan.FromMinutes(5));

        // 2 failures (under threshold)
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cb.ExecuteAsync<int>(_ => throw new InvalidOperationException("f1"), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cb.ExecuteAsync<int>(_ => throw new InvalidOperationException("f2"), CancellationToken.None));
        Assert.Equal(CircuitState.Closed, cb.State);

        // 1 success — resets counter
        await cb.ExecuteAsync(_ => Task.FromResult(1), CancellationToken.None);

        // 2 more failures — still under threshold (counter was reset)
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cb.ExecuteAsync<int>(_ => throw new InvalidOperationException("f3"), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cb.ExecuteAsync<int>(_ => throw new InvalidOperationException("f4"), CancellationToken.None));
        Assert.Equal(CircuitState.Closed, cb.State);
    }

    [Fact]
    public async Task CircuitBreaker_CancellationDoesNotCountAsFailure()
    {
        var cb = new CircuitBreaker(failureThreshold: 1, cooldown: TimeSpan.FromMinutes(5));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            cb.ExecuteAsync<int>(_ => throw new OperationCanceledException(), cts.Token));

        // Should still be closed — cancellation is not a service failure
        Assert.Equal(CircuitState.Closed, cb.State);
    }

    [Fact]
    public async Task CircuitBreaker_ThreadSafety_ConcurrentCalls()
    {
        var cb = new CircuitBreaker(failureThreshold: 10, cooldown: TimeSpan.FromMinutes(5));
        var tasks = new Task<int>[50];

        for (var i = 0; i < tasks.Length; i++)
        {
            var val = i;
            tasks[i] = cb.ExecuteAsync(_ => Task.FromResult(val), CancellationToken.None);
        }

        var results = await Task.WhenAll(tasks);
        Assert.Equal(50, results.Length);
        Assert.Equal(CircuitState.Closed, cb.State);
    }

    // ── HttpClientFactory ─────────────────────────────────────────────────

    [Fact]
    public void HttpClientFactory_Create_ReturnsHttpClient()
    {
        using var client = HttpClientFactory.Create();
        Assert.NotNull(client);
    }

    [Fact]
    public void HttpClientFactory_Create_WithCustomLifetime()
    {
        using var client = HttpClientFactory.Create(TimeSpan.FromMinutes(5));
        Assert.NotNull(client);
    }

    // ── AgentRuntime config integration ───────────────────────────────────

    [Fact]
    public void LlmProviderConfig_DefaultResilienceValues()
    {
        var config = new LlmProviderConfig();
        Assert.Equal(120, config.TimeoutSeconds);
        Assert.Equal(3, config.RetryCount);
        Assert.Equal(5, config.CircuitBreakerThreshold);
        Assert.Equal(30, config.CircuitBreakerCooldownSeconds);
    }

    [Fact]
    public void ToolingConfig_DefaultToolTimeoutSeconds()
    {
        var config = new ToolingConfig();
        Assert.Equal(30, config.ToolTimeoutSeconds);
    }
}

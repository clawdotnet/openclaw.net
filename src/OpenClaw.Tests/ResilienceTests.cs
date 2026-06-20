using OpenClaw.Agent;
using OpenClaw.Core.Http;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
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

        var result = await cb.ExecuteAsync(_ => Task.FromResult(42), TestContext.Current.CancellationToken);
        Assert.Equal(42, result);
        Assert.Equal(CircuitState.Closed, cb.State);
    }

    [Fact]
    public async Task CircuitBreaker_OpensAfterThresholdFailures()
    {
        var cb = new CircuitBreaker(failureThreshold: 2, cooldown: TimeSpan.FromMinutes(5));

        // Failure 1 — stays closed
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cb.ExecuteAsync<int>(_ => throw new InvalidOperationException("fail"), TestContext.Current.CancellationToken));
        Assert.Equal(CircuitState.Closed, cb.State);

        // Failure 2 — opens
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cb.ExecuteAsync<int>(_ => throw new InvalidOperationException("fail"), TestContext.Current.CancellationToken));
        Assert.Equal(CircuitState.Open, cb.State);
    }

    [Fact]
    public async Task CircuitBreaker_OpenState_ThrowsCircuitOpenException()
    {
        var cb = new CircuitBreaker(failureThreshold: 1, cooldown: TimeSpan.FromMinutes(5));

        // Trip the breaker
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cb.ExecuteAsync<int>(_ => throw new InvalidOperationException("fail"), TestContext.Current.CancellationToken));
        Assert.Equal(CircuitState.Open, cb.State);

        // Subsequent calls are short-circuited
        var ex = await Assert.ThrowsAsync<CircuitOpenException>(() =>
            cb.ExecuteAsync(_ => Task.FromResult(1), TestContext.Current.CancellationToken));
        Assert.True(ex.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public async Task CircuitBreaker_TransitionsToHalfOpen_AfterCooldown()
    {
        var cb = new CircuitBreaker(failureThreshold: 1, cooldown: TimeSpan.FromMilliseconds(50));

        // Trip the breaker
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cb.ExecuteAsync<int>(_ => throw new InvalidOperationException("fail"), TestContext.Current.CancellationToken));
        Assert.Equal(CircuitState.Open, cb.State);

        // Wait for cooldown
        await Task.Delay(100);

        // Next call should succeed (probe) and close the circuit
        var result = await cb.ExecuteAsync(_ => Task.FromResult(99), TestContext.Current.CancellationToken);
        Assert.Equal(99, result);
        Assert.Equal(CircuitState.Closed, cb.State);
    }

    [Fact]
    public async Task CircuitBreaker_HalfOpen_FailureReopens()
    {
        var cb = new CircuitBreaker(failureThreshold: 1, cooldown: TimeSpan.FromMilliseconds(50));

        // Trip the breaker
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cb.ExecuteAsync<int>(_ => throw new InvalidOperationException("fail"), TestContext.Current.CancellationToken));

        // Wait for cooldown
        await Task.Delay(100);

        // Probe fails → re-opens
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cb.ExecuteAsync<int>(_ => throw new InvalidOperationException("still broken"), TestContext.Current.CancellationToken));
        Assert.Equal(CircuitState.Open, cb.State);

        var ex = await Assert.ThrowsAsync<CircuitOpenException>(() =>
            cb.ExecuteAsync(_ => Task.FromResult(1), TestContext.Current.CancellationToken));
        Assert.True(ex.RetryAfter > TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public async Task CircuitBreaker_SuccessResetsFailureCount()
    {
        var cb = new CircuitBreaker(failureThreshold: 3, cooldown: TimeSpan.FromMinutes(5));

        // 2 failures (under threshold)
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cb.ExecuteAsync<int>(_ => throw new InvalidOperationException("f1"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cb.ExecuteAsync<int>(_ => throw new InvalidOperationException("f2"), TestContext.Current.CancellationToken));
        Assert.Equal(CircuitState.Closed, cb.State);

        // 1 success — resets counter
        await cb.ExecuteAsync(_ => Task.FromResult(1), TestContext.Current.CancellationToken);

        // 2 more failures — still under threshold (counter was reset)
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cb.ExecuteAsync<int>(_ => throw new InvalidOperationException("f3"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cb.ExecuteAsync<int>(_ => throw new InvalidOperationException("f4"), TestContext.Current.CancellationToken));
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

    [Theory]
    [InlineData("@hourly", 0, 15, 1, 1, true)]
    [InlineData("@daily", 0, 0, 1, 1, true)]
    [InlineData("@weekly", 0, 0, 4, 1, true)]
    [InlineData("@monthly", 0, 0, 1, 1, true)]
    [InlineData("1-5/2 * * * *", 3, 9, 1, 1, true)]
    [InlineData("1-5/2 * * * *", 4, 9, 1, 1, false)]
    public void CronScheduler_IsTime_SupportsAliasesAndRangeSteps(string expression, int minute, int hour, int day, int month, bool expected)
    {
        var time = new DateTimeOffset(2026, month, day, hour, minute, 0, TimeSpan.Zero);
        Assert.Equal(expected, CronScheduler.IsTime(expression, time));
    }

    [Fact]
    public void CronScheduler_IsTime_SupportsLastDayOfMonth()
    {
        var matching = new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero);
        var notMatching = new DateTimeOffset(2026, 2, 27, 0, 0, 0, TimeSpan.Zero);

        Assert.True(CronScheduler.IsTime("0 0 L * *", matching));
        Assert.False(CronScheduler.IsTime("0 0 L * *", notMatching));
    }

    [Theory]
    [InlineData("0,15,30,45 * * * *", 15, true)]
    [InlineData("0,15,30,45 * * * *", 14, false)]
    [InlineData("*/10 * * * *", 0, true)]
    [InlineData("*/10 * * * *", 20, true)]
    [InlineData("*/10 * * * *", 5, false)]
    public void CronScheduler_IsTime_SupportsListsAndStepWildcards(string expression, int minute, bool expected)
    {
        var time = new DateTimeOffset(2026, 1, 1, 12, minute, 0, TimeSpan.Zero);
        Assert.Equal(expected, CronScheduler.IsTime(expression, time));
    }

    [Fact]
    public void CronScheduler_IsTime_IgnoresSubSecondPrecision()
    {
        var time = new DateTimeOffset(2026, 1, 1, 12, 30, 0, 250, TimeSpan.Zero);
        Assert.True(CronScheduler.IsTime("30 12 * * *", time));
    }

    [Theory]
    [InlineData("0 9-17 * * 1-5", 0, 10, DayOfWeek.Monday, true)]
    [InlineData("0 9-17 * * 1-5", 0, 8, DayOfWeek.Monday, false)]
    [InlineData("0 9-17 * * 1-5", 0, 10, DayOfWeek.Saturday, false)]
    public void CronScheduler_IsTime_SupportsDayOfWeekRanges(string expression, int minute, int hour, DayOfWeek dow, bool expected)
    {
        // Find a date in 2026 matching the given day of week
        var baseDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        while (baseDate.DayOfWeek != dow)
            baseDate = baseDate.AddDays(1);
        var time = new DateTimeOffset(baseDate.Year, baseDate.Month, baseDate.Day, hour, minute, 0, TimeSpan.Zero);
        Assert.Equal(expected, CronScheduler.IsTime(expression, time));
    }

    [Fact]
    public void CronScheduler_IsTime_EmptyExpression_ReturnsFalse()
    {
        var time = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.False(CronScheduler.IsTime("", time));
        Assert.False(CronScheduler.IsTime("   ", time));
    }

    [Fact]
    public void CronScheduler_IsTime_InvalidExpression_ReturnsFalse()
    {
        var time = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.False(CronScheduler.IsTime("not a cron", time));
        Assert.False(CronScheduler.IsTime("60 * * * *", time)); // minute out of range
    }

    [Fact]
    public async Task CircuitBreaker_ThreadSafety_ConcurrentCalls()
    {
        var cb = new CircuitBreaker(failureThreshold: 10, cooldown: TimeSpan.FromMinutes(5));
        var tasks = new Task<int>[50];

        for (var i = 0; i < tasks.Length; i++)
        {
            var val = i;
            tasks[i] = cb.ExecuteAsync(_ => Task.FromResult(val), TestContext.Current.CancellationToken);
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

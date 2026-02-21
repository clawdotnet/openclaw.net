using OpenClaw.Core.Observability;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// Tests for Phase 3 observability: TurnContext, RuntimeMetrics.
/// </summary>
public sealed class ObservabilityTests
{
    // ── TurnContext ───────────────────────────────────────────────────────

    [Fact]
    public void TurnContext_CorrelationId_IsNonEmpty()
    {
        var ctx = new TurnContext { SessionId = "s1", ChannelId = "ws" };
        Assert.False(string.IsNullOrEmpty(ctx.CorrelationId));
    }

    [Fact]
    public void TurnContext_CorrelationId_StableWithinInstance()
    {
        var ctx = new TurnContext();
        var id1 = ctx.CorrelationId;
        var id2 = ctx.CorrelationId;
        Assert.Equal(id1, id2);
    }

    [Fact]
    public void TurnContext_CorrelationId_UniqueAcrossInstances()
    {
        var ctx1 = new TurnContext();
        var ctx2 = new TurnContext();
        Assert.NotEqual(ctx1.CorrelationId, ctx2.CorrelationId);
    }

    [Fact]
    public void TurnContext_RecordLlmCall_AccumulatesMetrics()
    {
        var ctx = new TurnContext();
        ctx.RecordLlmCall(TimeSpan.FromMilliseconds(100), 50, 20);
        ctx.RecordLlmCall(TimeSpan.FromMilliseconds(200), 30, 10);

        Assert.Equal(2, ctx.LlmCallCount);
        Assert.Equal(80, ctx.TotalInputTokens);
        Assert.Equal(30, ctx.TotalOutputTokens);
        Assert.Equal(TimeSpan.FromMilliseconds(300), ctx.TotalLlmLatency);
    }

    [Fact]
    public void TurnContext_RecordRetry_IncrementCounter()
    {
        var ctx = new TurnContext();
        ctx.RecordRetry();
        ctx.RecordRetry();
        Assert.Equal(2, ctx.RetryCount);
    }

    [Fact]
    public void TurnContext_RecordToolCall_TracksFailuresAndTimeouts()
    {
        var ctx = new TurnContext();
        ctx.RecordToolCall(TimeSpan.FromMilliseconds(50), failed: false, timedOut: false);
        ctx.RecordToolCall(TimeSpan.FromMilliseconds(100), failed: true, timedOut: false);
        ctx.RecordToolCall(TimeSpan.FromMilliseconds(30), failed: true, timedOut: true);

        Assert.Equal(3, ctx.ToolCallCount);
        Assert.Equal(2, ctx.ToolFailureCount);
        Assert.Equal(1, ctx.ToolTimeoutCount);
        Assert.Equal(TimeSpan.FromMilliseconds(180), ctx.TotalToolDuration);
    }

    [Fact]
    public void TurnContext_ToString_ContainsAllFields()
    {
        var ctx = new TurnContext { SessionId = "test-session", ChannelId = "ws" };
        ctx.RecordLlmCall(TimeSpan.FromMilliseconds(100), 50, 20);
        ctx.RecordToolCall(TimeSpan.FromMilliseconds(30), failed: false, timedOut: false);

        var summary = ctx.ToString();
        Assert.Contains("session=test-session", summary);
        Assert.Contains("llm=1", summary);
        Assert.Contains("tokens=50in/20out", summary);
        Assert.Contains("tools=1", summary);
    }

    [Fact]
    public void TurnContext_DefaultValues_AreZero()
    {
        var ctx = new TurnContext();
        Assert.Equal(0, ctx.LlmCallCount);
        Assert.Equal(0, ctx.TotalInputTokens);
        Assert.Equal(0, ctx.TotalOutputTokens);
        Assert.Equal(0, ctx.RetryCount);
        Assert.Equal(0, ctx.ToolCallCount);
        Assert.Equal(0, ctx.ToolFailureCount);
        Assert.Equal(0, ctx.ToolTimeoutCount);
        Assert.Equal(TimeSpan.Zero, ctx.TotalLlmLatency);
        Assert.Equal(TimeSpan.Zero, ctx.TotalToolDuration);
    }

    // ── RuntimeMetrics ────────────────────────────────────────────────────

    [Fact]
    public void RuntimeMetrics_DefaultValues_AreZero()
    {
        var m = new RuntimeMetrics();
        Assert.Equal(0, m.TotalRequests);
        Assert.Equal(0, m.TotalLlmCalls);
        Assert.Equal(0, m.TotalInputTokens);
        Assert.Equal(0, m.TotalOutputTokens);
        Assert.Equal(0, m.TotalToolCalls);
        Assert.Equal(0, m.TotalToolFailures);
        Assert.Equal(0, m.TotalToolTimeouts);
        Assert.Equal(0, m.TotalLlmRetries);
        Assert.Equal(0, m.TotalLlmErrors);
        Assert.Equal(0, m.ActiveSessions);
        Assert.Equal(0, m.CircuitBreakerState);
    }

    [Fact]
    public void RuntimeMetrics_IncrementCounters()
    {
        var m = new RuntimeMetrics();
        m.IncrementRequests();
        m.IncrementRequests();
        m.IncrementLlmCalls();
        m.AddInputTokens(100);
        m.AddOutputTokens(50);
        m.IncrementToolCalls();
        m.IncrementToolFailures();
        m.IncrementToolTimeouts();
        m.IncrementLlmRetries();
        m.IncrementLlmErrors();

        Assert.Equal(2, m.TotalRequests);
        Assert.Equal(1, m.TotalLlmCalls);
        Assert.Equal(100, m.TotalInputTokens);
        Assert.Equal(50, m.TotalOutputTokens);
        Assert.Equal(1, m.TotalToolCalls);
        Assert.Equal(1, m.TotalToolFailures);
        Assert.Equal(1, m.TotalToolTimeouts);
        Assert.Equal(1, m.TotalLlmRetries);
        Assert.Equal(1, m.TotalLlmErrors);
    }

    [Fact]
    public void RuntimeMetrics_Gauges_CanBeSet()
    {
        var m = new RuntimeMetrics();
        m.SetActiveSessions(42);
        m.SetCircuitBreakerState(1);

        Assert.Equal(42, m.ActiveSessions);
        Assert.Equal(1, m.CircuitBreakerState);
    }

    [Fact]
    public void RuntimeMetrics_Snapshot_ReflectsCurrentValues()
    {
        var m = new RuntimeMetrics();
        m.IncrementRequests();
        m.AddInputTokens(200);
        m.SetActiveSessions(5);

        var snap = m.Snapshot();
        Assert.Equal(1, snap.TotalRequests);
        Assert.Equal(200, snap.TotalInputTokens);
        Assert.Equal(5, snap.ActiveSessions);
    }

    [Fact]
    public async Task RuntimeMetrics_ThreadSafety_ConcurrentIncrements()
    {
        var m = new RuntimeMetrics();
        const int iterations = 1000;
        var tasks = new Task[4];

        for (var t = 0; t < tasks.Length; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (var i = 0; i < iterations; i++)
                {
                    m.IncrementRequests();
                    m.IncrementToolCalls();
                    m.AddInputTokens(1);
                }
            });
        }

        await Task.WhenAll(tasks);

        Assert.Equal(4 * iterations, m.TotalRequests);
        Assert.Equal(4 * iterations, m.TotalToolCalls);
        Assert.Equal(4 * iterations, m.TotalInputTokens);
    }
}

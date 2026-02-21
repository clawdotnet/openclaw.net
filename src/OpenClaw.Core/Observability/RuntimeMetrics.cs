using System.Threading;

namespace OpenClaw.Core.Observability;

/// <summary>
/// Lightweight runtime metrics counters.
/// All operations are lock-free using <see cref="Interlocked"/>.
/// Exposed via the health endpoint for monitoring/alerting.
/// </summary>
public sealed class RuntimeMetrics
{
    // ── Counters (monotonically increasing) ───────────────────────────────
    private long _totalRequests;
    private long _totalLlmCalls;
    private long _totalInputTokens;
    private long _totalOutputTokens;
    private long _totalToolCalls;
    private long _totalToolFailures;
    private long _totalToolTimeouts;
    private long _totalLlmRetries;
    private long _totalLlmErrors;

    // ── Gauges ────────────────────────────────────────────────────────────
    private int _activeSessions;
    private int _circuitBreakerState; // 0=Closed, 1=Open, 2=HalfOpen

    public long TotalRequests => Interlocked.Read(ref _totalRequests);
    public long TotalLlmCalls => Interlocked.Read(ref _totalLlmCalls);
    public long TotalInputTokens => Interlocked.Read(ref _totalInputTokens);
    public long TotalOutputTokens => Interlocked.Read(ref _totalOutputTokens);
    public long TotalToolCalls => Interlocked.Read(ref _totalToolCalls);
    public long TotalToolFailures => Interlocked.Read(ref _totalToolFailures);
    public long TotalToolTimeouts => Interlocked.Read(ref _totalToolTimeouts);
    public long TotalLlmRetries => Interlocked.Read(ref _totalLlmRetries);
    public long TotalLlmErrors => Interlocked.Read(ref _totalLlmErrors);
    public int ActiveSessions => Volatile.Read(ref _activeSessions);
    public int CircuitBreakerState => Volatile.Read(ref _circuitBreakerState);

    public void IncrementRequests() => Interlocked.Increment(ref _totalRequests);
    public void IncrementLlmCalls() => Interlocked.Increment(ref _totalLlmCalls);
    public void AddInputTokens(long n) => Interlocked.Add(ref _totalInputTokens, n);
    public void AddOutputTokens(long n) => Interlocked.Add(ref _totalOutputTokens, n);
    public void IncrementToolCalls() => Interlocked.Increment(ref _totalToolCalls);
    public void IncrementToolFailures() => Interlocked.Increment(ref _totalToolFailures);
    public void IncrementToolTimeouts() => Interlocked.Increment(ref _totalToolTimeouts);
    public void IncrementLlmRetries() => Interlocked.Increment(ref _totalLlmRetries);
    public void IncrementLlmErrors() => Interlocked.Increment(ref _totalLlmErrors);
    public void SetActiveSessions(int count) => Volatile.Write(ref _activeSessions, count);
    public void SetCircuitBreakerState(int state) => Volatile.Write(ref _circuitBreakerState, state);

    /// <summary>
    /// Snapshot for JSON serialization. Uses a struct to avoid allocations in the AOT path.
    /// </summary>
    public MetricsSnapshot Snapshot() => new()
    {
        TotalRequests = TotalRequests,
        TotalLlmCalls = TotalLlmCalls,
        TotalInputTokens = TotalInputTokens,
        TotalOutputTokens = TotalOutputTokens,
        TotalToolCalls = TotalToolCalls,
        TotalToolFailures = TotalToolFailures,
        TotalToolTimeouts = TotalToolTimeouts,
        TotalLlmRetries = TotalLlmRetries,
        TotalLlmErrors = TotalLlmErrors,
        ActiveSessions = ActiveSessions,
        CircuitBreakerState = CircuitBreakerState
    };
}

public struct MetricsSnapshot
{
    public long TotalRequests { get; set; }
    public long TotalLlmCalls { get; set; }
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public long TotalToolCalls { get; set; }
    public long TotalToolFailures { get; set; }
    public long TotalToolTimeouts { get; set; }
    public long TotalLlmRetries { get; set; }
    public long TotalLlmErrors { get; set; }
    public int ActiveSessions { get; set; }
    public int CircuitBreakerState { get; set; }
}

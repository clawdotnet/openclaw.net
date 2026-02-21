using System.Diagnostics;

namespace OpenClaw.Core.Observability;

/// <summary>
/// Lightweight per-request context for correlation IDs and turn-level metrics.
/// No external dependency — uses <see cref="Activity"/> when available,
/// falls back to a simple GUID-based correlation ID.
/// Thread-safe: all mutable counters use <see cref="Interlocked"/> for safe concurrent updates
/// during parallel tool execution.
/// </summary>
public sealed class TurnContext
{
    /// <summary>
    /// Correlation ID that ties all log entries for a single user turn together.
    /// Consumers can set <see cref="Activity.Current"/> upstream for distributed tracing
    /// and this will pick up the trace ID automatically.
    /// </summary>
    public string CorrelationId { get; } = Activity.Current?.TraceId.ToString()
                                           ?? Guid.NewGuid().ToString("N")[..16];

    public string SessionId { get; init; } = "";
    public string ChannelId { get; init; } = "";

    // ── LLM metrics (single-threaded: only called from the sequential agent loop) ──
    private int _llmCallCount;
    private long _totalInputTokens;
    private long _totalOutputTokens;
    private long _totalLlmLatencyTicks;
    private int _retryCount;

    public int LlmCallCount => Volatile.Read(ref _llmCallCount);
    public long TotalInputTokens => Interlocked.Read(ref _totalInputTokens);
    public long TotalOutputTokens => Interlocked.Read(ref _totalOutputTokens);
    public TimeSpan TotalLlmLatency => new(Interlocked.Read(ref _totalLlmLatencyTicks));
    public int RetryCount => Volatile.Read(ref _retryCount);

    // ── Tool metrics (concurrent: parallel tool execution writes simultaneously) ──
    private int _toolCallCount;
    private long _totalToolDurationTicks;
    private int _toolFailureCount;
    private int _toolTimeoutCount;

    public int ToolCallCount => Volatile.Read(ref _toolCallCount);
    public TimeSpan TotalToolDuration => new(Interlocked.Read(ref _totalToolDurationTicks));
    public int ToolFailureCount => Volatile.Read(ref _toolFailureCount);
    public int ToolTimeoutCount => Volatile.Read(ref _toolTimeoutCount);

    public void RecordLlmCall(TimeSpan latency, long inputTokens, long outputTokens)
    {
        Interlocked.Increment(ref _llmCallCount);
        Interlocked.Add(ref _totalLlmLatencyTicks, latency.Ticks);
        Interlocked.Add(ref _totalInputTokens, inputTokens);
        Interlocked.Add(ref _totalOutputTokens, outputTokens);
    }

    public void RecordRetry() => Interlocked.Increment(ref _retryCount);

    public void RecordToolCall(TimeSpan duration, bool failed, bool timedOut)
    {
        Interlocked.Increment(ref _toolCallCount);
        Interlocked.Add(ref _totalToolDurationTicks, duration.Ticks);
        if (failed) Interlocked.Increment(ref _toolFailureCount);
        if (timedOut) Interlocked.Increment(ref _toolTimeoutCount);
    }

    /// <summary>
    /// Returns a summary suitable for structured logging.
    /// </summary>
    public override string ToString() =>
        $"Turn[{CorrelationId}] session={SessionId} llm={LlmCallCount} retries={RetryCount} " +
        $"tokens={TotalInputTokens}in/{TotalOutputTokens}out " +
        $"tools={ToolCallCount} toolFails={ToolFailureCount} toolTimeouts={ToolTimeoutCount} " +
        $"llmLatency={TotalLlmLatency.TotalMilliseconds:F0}ms toolDuration={TotalToolDuration.TotalMilliseconds:F0}ms";
}

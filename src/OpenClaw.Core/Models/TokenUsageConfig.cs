namespace OpenClaw.Core.Models;

/// <summary>
/// Gateway-side token usage export. The gateway never talks to Kafka directly: it only batches
/// events over HTTP to an out-of-sandbox collector (TokenHub.Collector), which owns the
/// Kafka producer and credentials. This keeps the short-lived sandbox image free of the Kafka
/// client, its SASL secrets, and a network hole to the internal broker cluster.
/// <para>Bound from the <c>OpenClaw:TokenUsage</c> configuration section.</para>
/// </summary>
public sealed class TokenUsageConfig
{
    /// <summary>Sink selection: "none" (no-op, hot path pays nothing) or "http" (batch POST to collector).</summary>
    public string Sink { get; set; } = "none";

    /// <summary>
    /// Fixed digital-employee id for all events from this gateway instance.
    /// Null/empty = use each session's SenderId (one channel identity = one digital employee).
    /// </summary>
    public string? AgentId { get; set; }

    public TokenUsageHttpConfig Http { get; set; } = new();

    /// <summary>True when the HTTP thin client should be wired up.</summary>
    public bool IsHttpSinkEnabled
        => string.Equals(Sink, "http", StringComparison.OrdinalIgnoreCase);
}

/// <summary>HTTP thin-client settings for shipping token usage events to the collector.</summary>
public sealed class TokenUsageHttpConfig
{
    /// <summary>Collector ingest endpoint. The sandbox network policy only needs to allow this one address.</summary>
    public string CollectorUrl { get; set; } = "http://localhost:8088/ingest/token-usage";

    /// <summary>Bearer token ref resolved via SecretResolver (env:VAR / raw:literal); never plaintext in config.</summary>
    public string? AuthTokenRef { get; set; }

    /// <summary>In-memory queue capacity; oldest events are dropped when full to protect the chat flow.</summary>
    public int QueueCapacity { get; set; } = 4096;

    /// <summary>Max events batched into a single POST.</summary>
    public int BatchSize { get; set; } = 64;

    /// <summary>Max time a partial batch waits before being flushed.</summary>
    public int FlushIntervalMs { get; set; } = 1000;

    /// <summary>Per-request HTTP timeout.</summary>
    public int TimeoutSeconds { get; set; } = 10;
}

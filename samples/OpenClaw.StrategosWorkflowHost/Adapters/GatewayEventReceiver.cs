using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using OpenClaw.Core.Models;

using Strategos.Abstractions;

namespace OpenClaw.StrategosWorkflowHost.Adapters;

/// <summary>
/// HTTP-side counterpart to <see cref="OpenClaw.Gateway.Webhooks.RuntimeEventWebhook"/> on the gateway.
/// Receives <see cref="RuntimeEventEntry"/> POSTs, validates the bearer token,
/// deduplicates by entry id, and feeds the entry through
/// <see cref="AgentOutcomeMapper"/> into
/// <see cref="IAgentSelector.RecordOutcomeAsync"/>.
///
/// The receiver is a plain class (not an <c>IHostedService</c>) because the
/// sidecar's runtime-events endpoint is wired via minimal-API routing; the
/// hosting surface is just the POST route mapping.
/// </summary>
public sealed class GatewayEventReceiver
{
    private const int DedupCapacity = 10_000;

    private readonly AgentOutcomeMapper _mapper;
    private readonly IAgentSelector _selector;
    private readonly string? _expectedBearerToken;
    private readonly ILogger<GatewayEventReceiver> _logger;
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions DeserializeOptions = new(JsonSerializerDefaults.Web);

    public GatewayEventReceiver(
        AgentOutcomeMapper mapper,
        IAgentSelector selector,
        string? expectedBearerToken,
        ILogger<GatewayEventReceiver> logger)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(logger);
        _mapper = mapper;
        _selector = selector;
        _expectedBearerToken = string.IsNullOrWhiteSpace(expectedBearerToken) ? null : expectedBearerToken;
        _logger = logger;
    }

    /// <summary>
    /// Records an event id as seen (e.g. for warmup or replay scenarios).
    /// Exposed for callers that want to seed the dedup set from outside the
    /// HTTP loop. The HTTP handler also updates the set internally.
    /// </summary>
    public void RecordSeen(string eventId)
    {
        if (string.IsNullOrEmpty(eventId)) return;
        _seen.TryAdd(eventId, 0);
        TrimSeenIfOverCapacity();
    }

    /// <summary>
    /// Returns true if <paramref name="eventId"/> is currently in the dedup set.
    /// </summary>
    public bool IsSeen(string eventId)
        => !string.IsNullOrEmpty(eventId) && _seen.ContainsKey(eventId);

    /// <summary>
    /// Processes a POST to /runtime-events. Always returns 200 for accepted
    /// (or filtered) entries — the gateway's webhook treats anything other
    /// than 5xx as success. Returns 401 only when the bearer token does not
    /// match.
    /// </summary>
    public async Task<IResult> HandleAsync(HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_expectedBearerToken is not null)
        {
            var auth = context.Request.Headers.Authorization.ToString();
            if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                || !FixedTimeEquals(auth.Substring("Bearer ".Length).Trim(), _expectedBearerToken))
            {
                _logger.LogWarning("Rejecting /runtime-events: bearer token mismatch.");
                return Results.Unauthorized();
            }
        }

        RuntimeEventEntry? entry;
        try
        {
            entry = await JsonSerializer.DeserializeAsync<RuntimeEventEntry>(
                context.Request.Body,
                DeserializeOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Rejecting /runtime-events: malformed JSON.");
            return Results.BadRequest();
        }

        if (entry is null)
        {
            return Results.BadRequest();
        }

        if (!_seen.TryAdd(entry.Id, 0))
        {
            _logger.LogDebug("Skipping duplicate runtime event {EventId}.", entry.Id);
            return Results.Ok();
        }

        TrimSeenIfOverCapacity();

        var mapped = _mapper.Map(entry, cancellationToken);
        if (mapped is null)
        {
            return Results.Ok(); // filtered out (non-workflow, neutral action, cache miss)
        }

        try
        {
            var outcome = await _selector.RecordOutcomeAsync(
                mapped.Value.AgentId,
                mapped.Value.TaskCategory,
                mapped.Value.Outcome,
                cancellationToken).ConfigureAwait(false);

            if (!outcome.IsSuccess)
            {
                _logger.LogWarning(
                    "RecordOutcomeAsync returned failure for agent {AgentId}: {Error}",
                    mapped.Value.AgentId,
                    outcome.Error);
            }
        }
        catch (Exception ex)
        {
            // Log and swallow — a throwing selector must not interrupt the HTTP loop.
            _logger.LogWarning(ex,
                "RecordOutcomeAsync threw for agent {AgentId}; dropping outcome.",
                mapped.Value.AgentId);
        }

        return Results.Ok();
    }

    private void TrimSeenIfOverCapacity()
    {
        if (_seen.Count <= DedupCapacity) return;

        // Crude FIFO: drop the oldest half. Order isn't guaranteed by
        // ConcurrentDictionary, but for dedup purposes we just need *some*
        // entries to fall out — perfect LRU semantics aren't required.
        var keys = _seen.Keys.Take(_seen.Count - DedupCapacity / 2).ToList();
        foreach (var k in keys)
        {
            _seen.TryRemove(k, out _);
        }
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}

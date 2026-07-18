using System.Collections.Concurrent;

namespace OpenClaw.Core.Pipeline;

/// <summary>
/// Tracks active agent-execution sessions by session key.
/// Allows an external caller (e.g. an admin endpoint) to signal cancellation for
/// a session that is currently processing an LLM/tool call.
/// Thread-safe; designed for use from multiple inbound worker threads.
/// </summary>
public sealed class SessionAbortRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a new <see cref="CancellationTokenSource"/> for the given session,
    /// linked to <paramref name="parentToken"/> so that app-shutdown or request
    /// cancellation also cancels the returned source.
    /// Call <see cref="Unregister"/> in the finally block of the worker.
    /// </summary>
    public CancellationTokenSource Register(string sessionId, CancellationToken parentToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        // Per-session locking in GatewayWorkers ensures at most one concurrent entry per session,
        // but defensively dispose any replaced entry to prevent CTS leaks.
        if (_active.TryRemove(sessionId, out var existing))
            existing.Dispose();
        _active[sessionId] = cts;
        return cts;
    }

    /// <summary>
    /// Removes the abort source for the given session and disposes it.
    /// Safe to call even if the session was never registered.
    /// </summary>
    public void Unregister(string sessionId)
    {
        if (sessionId is null)
            return;
        if (_active.TryRemove(sessionId, out var cts))
            cts.Dispose();
    }

    /// <summary>
    /// Signals cancellation for the named session's current in-flight execution.
    /// Returns <c>true</c> if an active session was found and cancellation was requested;
    /// <c>false</c> if no session with that id is currently being processed.
    /// </summary>
    public bool TryAbort(string sessionId)
    {
        if (_active.TryGetValue(sessionId, out var cts))
        {
            cts.Cancel();
            return true;
        }
        return false;
    }

    /// <summary>Session IDs that are currently being actively processed.</summary>
    public IReadOnlyCollection<string> ActiveSessionIds => _active.Keys.ToArray();
}

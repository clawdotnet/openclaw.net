using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Sessions;

/// <summary>
/// Manages active sessions with automatic expiry. Thread-safe, allocation-light.
/// </summary>
public sealed class SessionManager
{
    private readonly ConcurrentDictionary<string, Session> _active = new();
    private readonly IMemoryStore _store;
    private readonly ILogger? _logger;
    private readonly TimeSpan _timeout;
    private readonly int _maxSessions;
    private int _activeCount;

    public SessionManager(IMemoryStore store, GatewayConfig config, ILogger? logger = null)
    {
        _store = store;
        _logger = logger;
        _timeout = TimeSpan.FromMinutes(config.SessionTimeoutMinutes);
        _maxSessions = config.MaxConcurrentSessions;
    }

    /// <summary>
    /// Get or create a session for the given channel+sender pair.
    /// Session key is deterministic: channelId:senderId
    /// </summary>
    public async ValueTask<Session> GetOrCreateAsync(string channelId, string senderId, CancellationToken ct)
    {
        var key = string.Concat(channelId, ":", senderId);
        var now = DateTimeOffset.UtcNow;

        if (_active.TryGetValue(key, out var session))
        {
            session.LastActiveAt = now;
            return session;
        }

        // Try loading from disk
        session = await _store.GetSessionAsync(key, ct);
        if (session is not null && session.State == SessionState.Active)
        {
            session.LastActiveAt = now;
            if (_active.TryAdd(key, session))
            {
                Interlocked.Increment(ref _activeCount);
                return session;
            }

            if (_active.TryGetValue(key, out var canonical))
            {
                canonical.LastActiveAt = now;
                return canonical;
            }

            // Extremely unlikely race: fall back to returning loaded session.
            return session;
        }

        // Evict expired sessions if at capacity
        if (Volatile.Read(ref _activeCount) >= _maxSessions)
            EvictExpired();

        if (Volatile.Read(ref _activeCount) >= _maxSessions)
            EvictLeastRecentlyActive();

        var created = new Session
        {
            Id = key,
            ChannelId = channelId,
            SenderId = senderId
        };

        if (_active.TryAdd(key, created))
        {
            created.LastActiveAt = now;
            Interlocked.Increment(ref _activeCount);
            return created;
        }

        // If another thread won the race, return the canonical session.
        if (_active.TryGetValue(key, out var activeSession))
        {
            activeSession.LastActiveAt = now;
            return activeSession;
        }

        // Extremely unlikely race: fall back to returning created session.
        created.LastActiveAt = now;
        return created;
    }

    public async ValueTask PersistAsync(Session session, CancellationToken ct)
    {
        const int MaxRetries = 3;
        var delay = TimeSpan.FromMilliseconds(100);

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                await _store.SaveSessionAsync(session, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaxRetries)
            {
                _logger?.LogWarning(ex, "Session persistence failed (attempt {Attempt}/{MaxRetries}) for {SessionId}", 
                    attempt, MaxRetries, session.Id);
                await Task.Delay(delay, ct);
                delay *= 2; // Exponential backoff
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Session persistence failed after {MaxRetries} attempts for {SessionId}", 
                    MaxRetries, session.Id);
                throw;
            }
        }
    }

    // ── Conversation Branching ─────────────────────────────────────────

    /// <summary>
    /// Create a named branch snapshot of the current session history.
    /// Returns the branch ID which can be used to restore later.
    /// </summary>
    public async ValueTask<string> BranchAsync(Session session, string branchName, CancellationToken ct)
    {
        var branchId = $"{session.Id}:branch:{branchName}:{DateTimeOffset.UtcNow.Ticks}";
        var branch = new SessionBranch
        {
            BranchId = branchId,
            SessionId = session.Id,
            Name = branchName,
            History = session.History.ToList() // Deep copy of history
        };
        await _store.SaveBranchAsync(branch, ct);
        _logger?.LogInformation("Created branch '{Branch}' for session {SessionId} with {Turns} turns",
            branchName, session.Id, session.History.Count);
        return branchId;
    }

    /// <summary>
    /// Restore a session's history from a previously saved branch.
    /// </summary>
    public async ValueTask<bool> RestoreBranchAsync(Session session, string branchId, CancellationToken ct)
    {
        var branch = await _store.LoadBranchAsync(branchId, ct);
        if (branch is null || branch.SessionId != session.Id)
            return false;

        session.History.Clear();
        session.History.AddRange(branch.History);
        session.LastActiveAt = DateTimeOffset.UtcNow;

        _logger?.LogInformation("Restored branch '{Branch}' for session {SessionId} ({Turns} turns)",
            branch.Name, session.Id, branch.History.Count);
        return true;
    }

    /// <summary>
    /// List all branches for a session.
    /// </summary>
    public ValueTask<IReadOnlyList<SessionBranch>> ListBranchesAsync(string sessionId, CancellationToken ct)
        => _store.ListBranchesAsync(sessionId, ct);


    /// <summary>
    /// Returns true if the given session key is currently in the active sessions dictionary.
    /// </summary>
    public bool IsActive(string sessionKey) => _active.ContainsKey(sessionKey);

    /// <summary>
    /// Number of currently active sessions (for metrics).
    /// </summary>
    public int ActiveCount => Volatile.Read(ref _activeCount);

    private void EvictExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - _timeout;
        foreach (var kvp in _active)
        {
            if (kvp.Value.LastActiveAt < cutoff)
            {
                kvp.Value.State = SessionState.Expired;
                if (_active.TryRemove(kvp.Key, out var removed))
                {
                    Interlocked.Decrement(ref _activeCount);
                    _logger?.LogInformation("Session {SessionId} expired and evicted", kvp.Key);
                    _ = PersistBestEffortAsync(removed);
                }
            }
        }
    }

    private void EvictLeastRecentlyActive()
    {
        // Safety bound to prevent spin-looping under heavy concurrent access
        var maxAttempts = _maxSessions + 1;
        var attempts = 0;
        while (Volatile.Read(ref _activeCount) >= _maxSessions)
        {
            if (++attempts > maxAttempts)
                return;

            string? oldestKey = null;
            var oldestAt = DateTimeOffset.MaxValue;

            foreach (var kvp in _active)
            {
                if (kvp.Value.LastActiveAt < oldestAt)
                {
                    oldestAt = kvp.Value.LastActiveAt;
                    oldestKey = kvp.Key;
                }
            }

            if (oldestKey is null)
                return;

            if (_active.TryRemove(oldestKey, out var removed))
            {
                removed.State = SessionState.Expired;
                Interlocked.Decrement(ref _activeCount);
                _ = PersistBestEffortAsync(removed);
            }
            else
            {
                return;
            }
        }
    }

    private async Task PersistBestEffortAsync(Session session)
    {
        try
        {
            await _store.SaveSessionAsync(session, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Best-effort persistence failed for session {SessionId}", session.Id);
        }
    }
}

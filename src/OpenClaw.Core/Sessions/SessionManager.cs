using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;

namespace OpenClaw.Core.Sessions;

/// <summary>
/// Manages active sessions with automatic expiry. Thread-safe, allocation-light.
/// </summary>
public sealed class SessionManager
{
    private readonly ConcurrentDictionary<string, Session> _active = new();
    private readonly IMemoryStore _store;
    private readonly ILogger? _logger;
    private readonly RuntimeMetrics? _metrics;
    private readonly TimeSpan _timeout;
    private readonly int _maxSessions;
    private readonly SemaphoreSlim _admissionGate = new(1, 1);
    private int _activeCount;

    public SessionManager(IMemoryStore store, GatewayConfig config, ILogger? logger = null, RuntimeMetrics? metrics = null)
    {
        _store = store;
        _logger = logger;
        _metrics = metrics;
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
        return await GetOrCreateByIdAsync(key, channelId, senderId, ct);
    }

    /// <summary>
    /// Get or create a session for an explicit session id. Useful for cron jobs and webhooks
    /// that want stable, named sessions independent of channel+sender.
    /// </summary>
    public async ValueTask<Session> GetOrCreateByIdAsync(string sessionId, string channelId, string senderId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("sessionId must be set.", nameof(sessionId));

        var key = sessionId;
        var now = DateTimeOffset.UtcNow;

        if (_active.TryGetValue(key, out var session))
        {
            session.LastActiveAt = now;
            return session;
        }

        await _admissionGate.WaitAsync(ct);
        try
        {
            if (_active.TryGetValue(key, out var activeSession))
            {
                activeSession.LastActiveAt = now;
                return activeSession;
            }

            session = await _store.GetSessionAsync(key, ct);
            if (session is not null && session.State == SessionState.Active)
            {
                session.LastActiveAt = now;
                EnsureCapacityForAdmission();
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

                return session;
            }

            EnsureCapacityForAdmission();

            var created = new Session
            {
                Id = key,
                ChannelId = channelId,
                SenderId = senderId,
                LastActiveAt = now
            };

            if (_active.TryAdd(key, created))
            {
                Interlocked.Increment(ref _activeCount);
                return created;
            }

            if (_active.TryGetValue(key, out activeSession))
            {
                activeSession.LastActiveAt = now;
                return activeSession;
            }

            return created;
        }
        finally
        {
            _admissionGate.Release();
        }
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

    public async ValueTask<SessionDiffResponse?> BuildBranchDiffAsync(
        Session session,
        string branchId,
        SessionMetadataSnapshot? metadata,
        CancellationToken ct)
    {
        var branch = await _store.LoadBranchAsync(branchId, ct);
        if (branch is null || !string.Equals(branch.SessionId, session.Id, StringComparison.Ordinal))
            return null;

        var sharedPrefix = 0;
        var maxPrefix = Math.Min(session.History.Count, branch.History.Count);
        while (sharedPrefix < maxPrefix && TurnsEqual(session.History[sharedPrefix], branch.History[sharedPrefix]))
            sharedPrefix++;

        return new SessionDiffResponse
        {
            SessionId = session.Id,
            BranchId = branch.BranchId,
            BranchName = branch.Name,
            SharedPrefixTurns = sharedPrefix,
            CurrentTurnCount = session.History.Count,
            BranchTurnCount = branch.History.Count,
            CurrentOnlyTurnSummaries = session.History.Skip(sharedPrefix).Select(SummarizeTurn).ToArray(),
            BranchOnlyTurnSummaries = branch.History.Skip(sharedPrefix).Select(SummarizeTurn).ToArray(),
            Metadata = metadata
        };
    }

    /// <summary>
    /// Returns a list of all currently active sessions in memory.
    /// </summary>
    public Task<List<Session>> ListActiveAsync(CancellationToken ct)
    {
        return Task.FromResult(_active.Values.ToList());
    }

    /// <summary>
    /// Tries to get an active in-memory session by channel+sender pair. Returns null if not found in memory.
    /// This is a synchronous, lock-free check against the active session cache only (no disk I/O).
    /// </summary>
    public Session? TryGetActive(string channelId, string senderId)
    {
        var key = string.Concat(channelId, ":", senderId);
        return _active.TryGetValue(key, out var session) ? session : null;
    }

    /// <summary>
    /// Tries to find an active in-memory session by its session ID. O(n) scan of active sessions.
    /// </summary>
    public Session? TryGetActiveById(string sessionId)
    {
        foreach (var session in _active.Values)
        {
            if (string.Equals(session.Id, sessionId, StringComparison.Ordinal))
                return session;
        }
        return null;
    }

    /// <summary>
    /// Loads a specific session from memory or disk by its ID.
    /// </summary>
    public async ValueTask<Session?> LoadAsync(string sessionId, CancellationToken ct)
    {
        if (_active.TryGetValue(sessionId, out var session))
            return session;

        return await _store.GetSessionAsync(sessionId, ct);
    }

    /// <summary>
    /// Removes an active in-memory session by id.
    /// Useful for explicitly ephemeral request-scoped sessions.
    /// </summary>
    public bool RemoveActive(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return false;

        if (_active.TryRemove(sessionId, out _))
        {
            Interlocked.Decrement(ref _activeCount);
            return true;
        }

        return false;
    }


    /// <summary>
    /// Returns true if the given session key is currently in the active sessions dictionary.
    /// </summary>
    public bool IsActive(string sessionKey) => _active.ContainsKey(sessionKey);

    /// <summary>
    /// Number of currently active sessions (for metrics).
    /// </summary>
    public int ActiveCount => Volatile.Read(ref _activeCount);

    /// <summary>
    /// Proactively evict expired sessions from the active in-memory dictionary.
    /// Returns the number of evicted sessions.
    /// </summary>
    public int SweepExpiredActiveSessions()
    {
        var cutoff = DateTimeOffset.UtcNow - _timeout;
        var removedCount = 0;
        foreach (var kvp in _active)
        {
            if (kvp.Value.LastActiveAt < cutoff)
            {
                kvp.Value.State = SessionState.Expired;
                if (_active.TryRemove(kvp.Key, out var removed))
                {
                    Interlocked.Decrement(ref _activeCount);
                    removedCount++;
                    _metrics?.IncrementSessionEvictions();
                    _logger?.LogInformation("Session {SessionId} expired and evicted", kvp.Key);
                    _ = PersistBestEffortAsync(removed);
                }
            }
        }

        return removedCount;
    }

    private static bool TurnsEqual(ChatTurn left, ChatTurn right)
    {
        if (!string.Equals(left.Role, right.Role, StringComparison.Ordinal) ||
            !string.Equals(left.Content, right.Content, StringComparison.Ordinal))
            return false;

        var leftCalls = left.ToolCalls;
        var rightCalls = right.ToolCalls;
        if (ReferenceEquals(leftCalls, rightCalls))
            return true;
        if (leftCalls is null || rightCalls is null || leftCalls.Count != rightCalls.Count)
            return false;

        for (var i = 0; i < leftCalls.Count; i++)
        {
            var l = leftCalls[i];
            var r = rightCalls[i];
            if (!string.Equals(l.ToolName, r.ToolName, StringComparison.Ordinal) ||
                !string.Equals(l.Arguments, r.Arguments, StringComparison.Ordinal) ||
                !string.Equals(l.Result, r.Result, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string SummarizeTurn(ChatTurn turn)
    {
        var content = string.IsNullOrWhiteSpace(turn.Content)
            ? turn.Role
            : turn.Content.Trim();
        if (content.Length > 180)
            content = content[..180] + "…";
        return $"{turn.Role}: {content}";
    }

    private void EvictLeastRecentlyActive()
    {
        if (_maxSessions <= 0)
            return;

        // TODO: This is an O(n) scan over all active sessions. If MaxConcurrentSessions grows
        // beyond hundreds, consider replacing with a PriorityQueue<string, DateTimeOffset> for O(log n) eviction.
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
                _metrics?.IncrementSessionEvictions();
                _ = PersistBestEffortAsync(removed);
            }
            else
            {
                return;
            }
        }
    }

    private void EnsureCapacityForAdmission()
    {
        if (_maxSessions <= 0)
            return;

        if (Volatile.Read(ref _activeCount) >= _maxSessions)
            SweepExpiredActiveSessions();

        if (Volatile.Read(ref _activeCount) >= _maxSessions)
            EvictLeastRecentlyActive();

        if (Volatile.Read(ref _activeCount) >= _maxSessions)
        {
            _metrics?.IncrementSessionCapacityRejects();
            throw new InvalidOperationException(
                $"Maximum concurrent sessions limit ({_maxSessions}) has been reached.");
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

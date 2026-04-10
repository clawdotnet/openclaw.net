using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Sessions;

namespace OpenClaw.Gateway.Backends;

internal sealed class CodingAgentBackendRegistry : ICodingAgentBackendRegistry
{
    private readonly IReadOnlyList<BackendDefinition> _definitions;
    private readonly IReadOnlyDictionary<string, ICodingAgentBackend> _backends;

    public CodingAgentBackendRegistry(IEnumerable<ICodingAgentBackend> backends)
    {
        var map = new Dictionary<string, ICodingAgentBackend>(StringComparer.OrdinalIgnoreCase);
        foreach (var backend in backends)
            map[backend.Definition.BackendId] = backend;

        _backends = map;
        _definitions = map.Values
            .Select(static item => item.Definition)
            .OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<BackendDefinition> List()
        => _definitions;

    public bool TryGet(string backendId, out ICodingAgentBackend? backend)
        => _backends.TryGetValue(backendId, out backend);
}

internal sealed class BackendSessionCoordinator
{
    private readonly ICodingAgentBackendRegistry _registry;
    private readonly IBackendSessionStore _store;
    private readonly BackendSessionEventStreamStore _liveEvents;
    private readonly SessionManager _sessions;

    public BackendSessionCoordinator(
        ICodingAgentBackendRegistry registry,
        IBackendSessionStore store,
        BackendSessionEventStreamStore liveEvents,
        SessionManager sessions)
    {
        _registry = registry;
        _store = store;
        _liveEvents = liveEvents;
        _sessions = sessions;
    }

    public IReadOnlyList<BackendDefinition> ListBackends()
        => _registry.List();

    public BackendDefinition? GetBackend(string backendId)
        => _registry.TryGet(backendId, out var backend) ? backend?.Definition : null;

    public ValueTask<IReadOnlyList<BackendSessionRecord>> ListSessionsAsync(string? backendId, CancellationToken ct)
        => _store.ListBackendSessionsAsync(backendId, ct);

    public ValueTask<BackendSessionRecord?> GetSessionAsync(string sessionId, CancellationToken ct)
        => _store.GetBackendSessionAsync(sessionId, ct);

    public ValueTask<IReadOnlyList<BackendEvent>> ListEventsAsync(string sessionId, long afterSequence, int limit, CancellationToken ct)
        => _store.ListBackendEventsAsync(sessionId, afterSequence, limit, ct);

    public async Task<BackendProbeResult> ProbeAsync(string backendId, BackendProbeRequest request, CancellationToken ct)
    {
        if (!_registry.TryGet(backendId, out var backend) || backend is null)
            throw new InvalidOperationException($"Backend '{backendId}' is not registered.");

        return await backend.ProbeAsync(request, ct);
    }

    public async Task<BackendSessionRecord> StartSessionAsync(StartBackendSessionRequest request, CancellationToken ct)
    {
        if (!_registry.TryGet(request.BackendId, out var backend) || backend is null)
            throw new InvalidOperationException($"Backend '{request.BackendId}' is not registered.");

        var session = new BackendSessionRecord
        {
            SessionId = $"bks_{Guid.NewGuid():N}"[..20],
            BackendId = backend.Definition.BackendId,
            Provider = backend.Definition.Provider,
            State = BackendSessionState.Pending,
            OwnerSessionId = request.OwnerSessionId,
            WorkspacePath = request.WorkspacePath,
            Model = request.Model ?? backend.Definition.DefaultModel,
            ReadOnly = request.ReadOnly ?? backend.Definition.AccessPolicy.ReadOnlyByDefault,
            DisplayName = backend.Definition.DisplayName,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            StartedAtUtc = null,
            CompletedAtUtc = null,
            LastEventSequence = 0
        };

        await _store.SaveBackendSessionAsync(session, ct);
        var runtime = new BackendSessionRuntime(session, _store, _liveEvents, _sessions);

        try
        {
            if (!string.IsNullOrWhiteSpace(request.Prompt))
            {
                await runtime.AppendOwnerHistoryTurnAsync(new ChatTurn
                {
                    Role = "user",
                    Content = $"[backend:{session.BackendId} prompt] {request.Prompt}",
                    Timestamp = DateTimeOffset.UtcNow
                }, ct);
            }

            var handle = await backend.StartSessionAsync(
                request with { },
                runtime,
                ct);

            var started = runtime.Session with
            {
                State = string.Equals(runtime.Session.State, BackendSessionState.Pending, StringComparison.OrdinalIgnoreCase)
                    ? BackendSessionState.Running
                    : runtime.Session.State,
                StartedAtUtc = runtime.Session.StartedAtUtc ?? handle.CreatedAtUtc
            };
            await runtime.UpdateSessionAsync(started, ct);
            return runtime.Session;
        }
        catch (Exception ex)
        {
            var failed = session with
            {
                State = BackendSessionState.Failed,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                LastError = ex.Message
            };
            await runtime.UpdateSessionAsync(failed, ct);
            await runtime.AppendEventAsync(new BackendErrorEvent
            {
                SessionId = session.SessionId,
                Message = ex.Message
            }, ct);
            await runtime.AppendEventAsync(new BackendSessionCompletedEvent
            {
                SessionId = session.SessionId,
                Reason = "start_failed"
            }, ct);
            throw;
        }
    }

    public async Task SendInputAsync(string backendId, string sessionId, BackendInput input, CancellationToken ct)
    {
        if (!_registry.TryGet(backendId, out var backend) || backend is null)
            throw new InvalidOperationException($"Backend '{backendId}' is not registered.");

        await backend.SendInputAsync(sessionId, input, ct);
    }

    public async Task StopSessionAsync(string backendId, string sessionId, CancellationToken ct)
    {
        if (!_registry.TryGet(backendId, out var backend) || backend is null)
            throw new InvalidOperationException($"Backend '{backendId}' is not registered.");

        await backend.StopSessionAsync(sessionId, ct);
    }

    private sealed class BackendSessionRuntime : IBackendSessionRuntime
    {
        private readonly IBackendSessionStore _store;
        private readonly BackendSessionEventStreamStore _liveEvents;
        private readonly SessionManager _sessions;
        private readonly Lock _gate = new();
        private long _lastSequence;

        public BackendSessionRuntime(
            BackendSessionRecord session,
            IBackendSessionStore store,
            BackendSessionEventStreamStore liveEvents,
            SessionManager sessions)
        {
            Session = session;
            _store = store;
            _liveEvents = liveEvents;
            _sessions = sessions;
            _lastSequence = session.LastEventSequence;
        }

        public BackendSessionRecord Session { get; private set; }

        public async ValueTask AppendEventAsync(BackendEvent evt, CancellationToken ct)
        {
            BackendEvent stamped;
            BackendSessionRecord nextSession;
            lock (_gate)
            {
                var sequence = ++_lastSequence;
                stamped = evt with
                {
                    SessionId = Session.SessionId,
                    Sequence = sequence,
                    TimestampUtc = evt.TimestampUtc == default ? DateTimeOffset.UtcNow : evt.TimestampUtc
                };

                nextSession = Session with { LastEventSequence = sequence };
                Session = nextSession;
            }

            await _store.AppendBackendEventAsync(stamped, ct);
            await _store.SaveBackendSessionAsync(nextSession, ct);
            _liveEvents.Record(stamped);
            await SyncOwnerSessionAsync(stamped, ct);
        }

        public async ValueTask UpdateSessionAsync(BackendSessionRecord session, CancellationToken ct)
        {
            lock (_gate)
            {
                var sequence = Math.Max(_lastSequence, session.LastEventSequence);
                _lastSequence = sequence;
                Session = session with { LastEventSequence = sequence };
            }

            await _store.SaveBackendSessionAsync(Session, ct);
        }

        internal async ValueTask AppendOwnerHistoryTurnAsync(ChatTurn turn, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(Session.OwnerSessionId))
                return;

            var ownerId = Session.OwnerSessionId;
            var ownerSession = await _sessions.LoadAsync(ownerId, ct);
            if (ownerSession is null)
                return;

            await using var lease = await _sessions.AcquireSessionLockAsync(ownerId, ct);
            ownerSession = await _sessions.LoadAsync(ownerId, ct) ?? ownerSession;
            ownerSession.History.Add(turn);
            ownerSession.LastActiveAt = DateTimeOffset.UtcNow;
            await _sessions.PersistAsync(ownerSession, ct, sessionLockHeld: true);
        }

        private async ValueTask SyncOwnerSessionAsync(BackendEvent evt, CancellationToken ct)
        {
            var turn = MapOwnerHistoryTurn(evt);
            if (turn is null)
                return;

            await AppendOwnerHistoryTurnAsync(turn, ct);
        }

        private ChatTurn? MapOwnerHistoryTurn(BackendEvent evt)
        {
            return evt switch
            {
                BackendAssistantMessageEvent assistant => new ChatTurn
                {
                    Role = "assistant",
                    Content = $"[backend:{Session.BackendId}] {assistant.Text}",
                    Timestamp = evt.TimestampUtc
                },
                BackendStdoutOutputEvent stdout => new ChatTurn
                {
                    Role = "assistant",
                    Content = $"[backend:{Session.BackendId} stdout] {stdout.Text}",
                    Timestamp = evt.TimestampUtc
                },
                BackendErrorEvent error => new ChatTurn
                {
                    Role = "system",
                    Content = $"[backend:{Session.BackendId} error] {error.Message}",
                    Timestamp = evt.TimestampUtc
                },
                BackendStderrOutputEvent stderr => new ChatTurn
                {
                    Role = "system",
                    Content = $"[backend:{Session.BackendId} stderr] {stderr.Text}",
                    Timestamp = evt.TimestampUtc
                },
                BackendToolCallRequestedEvent tool => new ChatTurn
                {
                    Role = "system",
                    Content = $"[backend:{Session.BackendId} tool] {tool.ToolName}",
                    Timestamp = evt.TimestampUtc
                },
                BackendShellCommandProposedEvent shell => new ChatTurn
                {
                    Role = "system",
                    Content = $"[backend:{Session.BackendId} shell proposed] {shell.Command}",
                    Timestamp = evt.TimestampUtc
                },
                BackendShellCommandExecutedEvent shell => new ChatTurn
                {
                    Role = "system",
                    Content = $"[backend:{Session.BackendId} shell executed] {shell.Command}" +
                              (shell.ExitCode is int exitCode ? $" (exit {exitCode})" : string.Empty),
                    Timestamp = evt.TimestampUtc
                },
                BackendPatchProposedEvent patch => new ChatTurn
                {
                    Role = "system",
                    Content = $"[backend:{Session.BackendId} patch proposed] {(string.IsNullOrWhiteSpace(patch.Path) ? "patch" : patch.Path)}",
                    Timestamp = evt.TimestampUtc
                },
                BackendPatchAppliedEvent patch => new ChatTurn
                {
                    Role = "system",
                    Content = $"[backend:{Session.BackendId} patch applied] {patch.Summary ?? patch.Path ?? "patch"}",
                    Timestamp = evt.TimestampUtc
                },
                BackendFileReadEvent file => new ChatTurn
                {
                    Role = "system",
                    Content = $"[backend:{Session.BackendId} file read] {file.Path}",
                    Timestamp = evt.TimestampUtc
                },
                BackendFileWriteEvent file => new ChatTurn
                {
                    Role = "system",
                    Content = $"[backend:{Session.BackendId} file write] {file.Path}",
                    Timestamp = evt.TimestampUtc
                },
                BackendSessionCompletedEvent completed => new ChatTurn
                {
                    Role = "system",
                    Content = $"[backend:{Session.BackendId} completed] " +
                              $"reason={completed.Reason ?? "completed"}" +
                              (completed.ExitCode is int exitCode ? $" exit={exitCode}" : string.Empty),
                    Timestamp = evt.TimestampUtc
                },
                _ => null
            };
        }
    }
}

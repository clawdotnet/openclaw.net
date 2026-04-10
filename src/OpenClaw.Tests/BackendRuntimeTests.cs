using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Features;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Sessions;
using OpenClaw.Gateway.Backends;
using Xunit;

namespace OpenClaw.Tests;

public sealed class BackendRuntimeTests
{
    [Fact]
    public async Task Coordinator_WithFakeBackend_StoresLifecycle()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-backend-runtime-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new FileFeatureStore(root);
            var registry = new CodingAgentBackendRegistry([new FakeCodingAgentBackend()]);
            var sessionStore = new FileMemoryStore(root, maxCachedSessions: 8);
            var sessionManager = new SessionManager(sessionStore, new GatewayConfig(), NullLogger.Instance);
            var coordinator = new BackendSessionCoordinator(registry, store, new BackendSessionEventStreamStore(), sessionManager);

            var session = await coordinator.StartSessionAsync(new StartBackendSessionRequest
            {
                BackendId = "fake-backend",
                Prompt = "hello"
            }, CancellationToken.None);

            Assert.Equal(BackendSessionState.Running, session.State);

            await coordinator.SendInputAsync("fake-backend", session.SessionId, new BackendInput { Text = "ping" }, CancellationToken.None);
            await coordinator.StopSessionAsync("fake-backend", session.SessionId, CancellationToken.None);

            var events = await coordinator.ListEventsAsync(session.SessionId, 0, 20, CancellationToken.None);
            Assert.Contains(events, item => item is BackendAssistantMessageEvent assistant && assistant.Text.Contains("hello", StringComparison.Ordinal));
            Assert.Contains(events, item => item is BackendSessionCompletedEvent);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Coordinator_WithOwnerSession_SyncsBackendEventsIntoSessionHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-backend-runtime-sync-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var backendStore = new FileFeatureStore(root);
            var memoryStore = new FileMemoryStore(root, maxCachedSessions: 8);
            var sessionManager = new SessionManager(memoryStore, new GatewayConfig(), NullLogger.Instance);
            var owner = await sessionManager.GetOrCreateByIdAsync("sess_owner", "api", "owner", CancellationToken.None);
            owner.History.Add(new ChatTurn { Role = "user", Content = "original" });
            await sessionManager.PersistAsync(owner, CancellationToken.None);

            var registry = new CodingAgentBackendRegistry([new FakeCodingAgentBackend()]);
            var coordinator = new BackendSessionCoordinator(registry, backendStore, new BackendSessionEventStreamStore(), sessionManager);
            var backendSession = await coordinator.StartSessionAsync(new StartBackendSessionRequest
            {
                BackendId = "fake-backend",
                OwnerSessionId = owner.Id,
                Prompt = "delegate work"
            }, CancellationToken.None);

            await coordinator.SendInputAsync("fake-backend", backendSession.SessionId, new BackendInput { Text = "continue" }, CancellationToken.None);
            await coordinator.StopSessionAsync("fake-backend", backendSession.SessionId, CancellationToken.None);

            var updated = await sessionManager.LoadAsync(owner.Id, CancellationToken.None);
            Assert.NotNull(updated);
            Assert.Contains(updated!.History, turn => turn.Role == "user" && turn.Content.Contains("[backend:fake-backend prompt] delegate work", StringComparison.Ordinal));
            Assert.Contains(updated.History, turn => turn.Role == "assistant" && turn.Content.Contains("fake received: delegate work", StringComparison.Ordinal));
            Assert.Contains(updated.History, turn => turn.Role == "assistant" && turn.Content.Contains("fake echo: continue", StringComparison.Ordinal));
            Assert.Contains(updated.History, turn => turn.Role == "system" && turn.Content.Contains("completed", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ProcessHost_ExecuteAndInteractiveSession_Work()
    {
        if (!File.Exists("/bin/sh"))
            return;

        var host = new CodingBackendProcessHost(NullLogger<CodingBackendProcessHost>.Instance);
        var execute = await host.ExecuteAsync(new CodingBackendProcessSpec
        {
            SessionId = "probe",
            BackendId = "shell",
            Command = "/bin/sh",
            Arguments = ["-lc", "printf hello && printf err >&2"],
            TimeoutSeconds = 5
        }, CancellationToken.None);

        Assert.Equal(0, execute.ExitCode);
        Assert.Equal("hello", execute.Stdout);
        Assert.Equal("err", execute.Stderr);

        if (!File.Exists("/bin/cat"))
            return;

        var runtime = new TestRuntime(new BackendSessionRecord
        {
            SessionId = "bks_process",
            BackendId = "shell",
            Provider = "shell"
        }, new FileFeatureStore(Path.Combine(Path.GetTempPath(), "openclaw-backend-process-store", Guid.NewGuid().ToString("N"))));

        await host.StartAsync(
            new CodingBackendProcessSpec
            {
                SessionId = runtime.Session.SessionId,
                BackendId = "shell",
                Command = "/bin/cat",
                TimeoutSeconds = 5
            },
            line => [new BackendAssistantMessageEvent { SessionId = runtime.Session.SessionId, Text = line }],
            line => [new BackendStderrOutputEvent { SessionId = runtime.Session.SessionId, Text = line }],
            runtime,
            CancellationToken.None);

        await host.WriteInputAsync(runtime.Session.SessionId, new BackendInput { Text = "echo-me", CloseInput = true }, CancellationToken.None);
        await Task.Delay(500);

        Assert.Contains(runtime.Events, item => item is BackendAssistantMessageEvent assistant && assistant.Text == "echo-me");
    }

    [Fact]
    public async Task ProcessHost_StopAsync_CompletesSessionAsCancelled()
    {
        if (!File.Exists("/bin/cat"))
            return;

        var host = new CodingBackendProcessHost(NullLogger<CodingBackendProcessHost>.Instance);
        var runtime = new TestRuntime(new BackendSessionRecord
        {
            SessionId = "bks_stop",
            BackendId = "shell",
            Provider = "shell"
        }, new FileFeatureStore(Path.Combine(Path.GetTempPath(), "openclaw-backend-process-stop-store", Guid.NewGuid().ToString("N"))));

        await host.StartAsync(
            new CodingBackendProcessSpec
            {
                SessionId = runtime.Session.SessionId,
                BackendId = "shell",
                Command = "/bin/cat",
                TimeoutSeconds = 30
            },
            _ => [],
            _ => [],
            runtime,
            CancellationToken.None);

        await host.StopAsync(runtime.Session.SessionId, CancellationToken.None);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline && runtime.Session.CompletedAtUtc is null)
            await Task.Delay(10);

        Assert.Equal(BackendSessionState.Cancelled, runtime.Session.State);
        Assert.NotNull(runtime.Session.CompletedAtUtc);
        Assert.Contains(runtime.Events, item => item is BackendSessionCompletedEvent completed && completed.Reason == "process_stopped");
    }

    private sealed class TestRuntime : IBackendSessionRuntime
    {
        private readonly IBackendSessionStore _store;
        private long _sequence;

        public TestRuntime(BackendSessionRecord session, IBackendSessionStore store)
        {
            Session = session;
            _store = store;
        }

        public BackendSessionRecord Session { get; private set; }
        public List<BackendEvent> Events { get; } = [];

        public async ValueTask AppendEventAsync(BackendEvent evt, CancellationToken ct)
        {
            var stamped = evt with { Sequence = ++_sequence, SessionId = Session.SessionId };
            Events.Add(stamped);
            Session = Session with { LastEventSequence = _sequence };
            await _store.AppendBackendEventAsync(stamped, ct);
            await _store.SaveBackendSessionAsync(Session, ct);
        }

        public async ValueTask UpdateSessionAsync(BackendSessionRecord session, CancellationToken ct)
        {
            Session = session with { LastEventSequence = Math.Max(session.LastEventSequence, _sequence) };
            await _store.SaveBackendSessionAsync(Session, ct);
        }
    }
}

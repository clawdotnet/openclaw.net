using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Features;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class CodingBackendStoreTests
{
    [Theory]
    [InlineData("file")]
    [InlineData("sqlite")]
    public async Task ConnectedAccountStore_RoundTrips(string provider)
    {
        using var fixture = CreateStore(provider);
        var store = fixture.AccountStore;

        var account = new ConnectedAccount
        {
            Id = "acct_test",
            Provider = "codex",
            DisplayName = "Test Account",
            SecretKind = ConnectedAccountSecretKind.SecretRef,
            SecretRef = "env:TEST_CODEX_KEY",
            IsActive = true
        };

        await store.SaveAccountAsync(account, CancellationToken.None);

        var loaded = await store.GetAccountAsync(account.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(account.Provider, loaded.Provider);

        var listed = await store.ListAccountsAsync(CancellationToken.None);
        Assert.Contains(listed, item => item.Id == account.Id);

        await store.DeleteAccountAsync(account.Id, CancellationToken.None);
        Assert.Null(await store.GetAccountAsync(account.Id, CancellationToken.None));
    }

    [Theory]
    [InlineData("file")]
    [InlineData("sqlite")]
    public async Task BackendSessionStore_RoundTripsEvents(string provider)
    {
        using var fixture = CreateStore(provider);
        var store = fixture.SessionStore;

        var session = new BackendSessionRecord
        {
            SessionId = "bks_test",
            BackendId = "fake-backend",
            Provider = "fake",
            State = BackendSessionState.Running
        };

        await store.SaveBackendSessionAsync(session, CancellationToken.None);
        await store.AppendBackendEventAsync(new BackendAssistantMessageEvent
        {
            SessionId = session.SessionId,
            Sequence = 1,
            Text = "hello"
        }, CancellationToken.None);
        await store.AppendBackendEventAsync(new BackendSessionCompletedEvent
        {
            SessionId = session.SessionId,
            Sequence = 2,
            ExitCode = 0,
            Reason = "done"
        }, CancellationToken.None);

        var loaded = await store.GetBackendSessionAsync(session.SessionId, CancellationToken.None);
        Assert.NotNull(loaded);

        var events = await store.ListBackendEventsAsync(session.SessionId, 0, 10, CancellationToken.None);
        Assert.Equal(2, events.Count);
        Assert.IsType<BackendAssistantMessageEvent>(events[0]);
        Assert.IsType<BackendSessionCompletedEvent>(events[1]);

        await store.DeleteBackendSessionAsync(session.SessionId, CancellationToken.None);
        Assert.Null(await store.GetBackendSessionAsync(session.SessionId, CancellationToken.None));
    }

    private static StoreFixture CreateStore(string provider)
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-backend-store-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return provider switch
        {
            "sqlite" => new StoreFixture(
                new SqliteFeatureStore(Path.Combine(root, "openclaw.db")),
                root),
            _ => new StoreFixture(
                new FileFeatureStore(root),
                root)
        };
    }

    private sealed class StoreFixture : IDisposable
    {
        private readonly IDisposable? _disposable;

        public StoreFixture(object store, string path)
        {
            AccountStore = (IConnectedAccountStore)store;
            SessionStore = (IBackendSessionStore)store;
            _disposable = store as IDisposable;
            Path = path;
        }

        public string Path { get; }
        public IConnectedAccountStore AccountStore { get; }
        public IBackendSessionStore SessionStore { get; }

        public void Dispose()
        {
            _disposable?.Dispose();
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}

using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

public interface IConnectedAccountStore
{
    ValueTask<IReadOnlyList<ConnectedAccount>> ListAccountsAsync(CancellationToken ct);
    ValueTask<ConnectedAccount?> GetAccountAsync(string accountId, CancellationToken ct);
    ValueTask SaveAccountAsync(ConnectedAccount account, CancellationToken ct);
    ValueTask DeleteAccountAsync(string accountId, CancellationToken ct);
}

public interface IBackendSessionStore
{
    ValueTask<IReadOnlyList<BackendSessionRecord>> ListBackendSessionsAsync(string? backendId, CancellationToken ct);
    ValueTask<BackendSessionRecord?> GetBackendSessionAsync(string sessionId, CancellationToken ct);
    ValueTask SaveBackendSessionAsync(BackendSessionRecord session, CancellationToken ct);
    ValueTask DeleteBackendSessionAsync(string sessionId, CancellationToken ct);
    ValueTask AppendBackendEventAsync(BackendEvent evt, CancellationToken ct);
    ValueTask<IReadOnlyList<BackendEvent>> ListBackendEventsAsync(string sessionId, long afterSequence, int limit, CancellationToken ct);
}

public interface IBackendCredentialResolver
{
    ValueTask<ResolvedBackendCredential?> ResolveAsync(string provider, BackendCredentialSourceConfig? source, CancellationToken ct);
    ValueTask<ResolvedBackendCredential?> ResolveAsync(string provider, ConnectedAccountSecretRef? source, CancellationToken ct);
}

public interface IBackendSessionRuntime
{
    BackendSessionRecord Session { get; }
    ValueTask AppendEventAsync(BackendEvent evt, CancellationToken ct);
    ValueTask UpdateSessionAsync(BackendSessionRecord session, CancellationToken ct);
}

public interface ICodingAgentBackend
{
    BackendDefinition Definition { get; }
    Task<BackendProbeResult> ProbeAsync(BackendProbeRequest request, CancellationToken ct);
    Task<BackendSessionHandle> StartSessionAsync(StartBackendSessionRequest request, IBackendSessionRuntime runtime, CancellationToken ct);
    Task SendInputAsync(string sessionId, BackendInput input, CancellationToken ct);
    Task StopSessionAsync(string sessionId, CancellationToken ct);
}

public interface ICodingAgentBackendRegistry
{
    IReadOnlyList<BackendDefinition> List();
    bool TryGet(string backendId, out ICodingAgentBackend? backend);
}

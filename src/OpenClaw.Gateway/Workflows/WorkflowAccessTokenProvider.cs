using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Workflows;

internal interface IWorkflowAccessTokenProvider
{
    Task<WorkflowAccessToken> GetAsync(string backendId, WorkflowAuthenticationConfig authentication,
        WorkflowExecutionContext executionContext, CancellationToken cancellationToken);
}

internal sealed class WorkflowAccessToken(string accessToken, DateTimeOffset expiresAtUtc, DateTimeOffset refreshAtUtc)
{
    [JsonIgnore]
    public string AccessToken { get; } = accessToken;
    public DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;
    public DateTimeOffset RefreshAtUtc { get; } = refreshAtUtc;
    public override string ToString() => "WorkflowAccessToken { [REDACTED] }";
}

internal sealed class WorkflowAccessTokenProvider(
    WorkflowTokenExchangeClient exchangeClient, TimeProvider timeProvider,
    IHostApplicationLifetime applicationLifetime, ILogger<WorkflowAccessTokenProvider> logger) : IWorkflowAccessTokenProvider
{
    private const int Capacity = 1024;
    private readonly object _gate = new();
    private readonly Dictionary<CacheKey, Entry> _entries = [];
    private int _waiters;

    public async Task<WorkflowAccessToken> GetAsync(string backendId, WorkflowAuthenticationConfig authentication,
        WorkflowExecutionContext executionContext, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateCaller(backendId, executionContext);
        var key = CreateKey(backendId, authentication, executionContext);
        TaskCompletionSource<bool>? start = null;
        Task<WorkflowAccessToken> task;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var cached) && cached.Token is { } token &&
                timeProvider.GetUtcNow() < token.RefreshAtUtc)
                return token;
            if (_waiters >= Capacity)
                throw Failure(backendId);

            if (_entries.TryGetValue(key, out var entry) && entry.Pending is { } pending)
                task = pending;
            else
            {
                if (!_entries.ContainsKey(key) && _entries.Count >= Capacity)
                {
                    var candidate = _entries.Where(pair => pair.Value.Pending is null)
                        .OrderBy(pair => pair.Value.Token?.ExpiresAtUtc).FirstOrDefault();
                    if (candidate.Value is null)
                        throw Failure(backendId);
                    _entries.Remove(candidate.Key);
                }
                entry = new Entry();
                start = new(TaskCreationOptions.RunContinuationsAsynchronously);
                task = AcquireAsync(key, entry, backendId, authentication, executionContext, start.Task);
                entry.Pending = task;
                _entries[key] = entry;
            }
            _waiters++;
        }
        start?.TrySetResult(true);
        try
        {
            var token = await task.WaitAsync(cancellationToken);
            ValidateCaller(backendId, executionContext);
            if (timeProvider.GetUtcNow() >= token.ExpiresAtUtc)
                throw Failure(backendId);
            return token;
        }
        finally
        {
            lock (_gate)
                _waiters--;
        }
    }

    private async Task<WorkflowAccessToken> AcquireAsync(CacheKey key, Entry entry, string backendId,
        WorkflowAuthenticationConfig authentication, WorkflowExecutionContext context, Task start)
    {
        var succeeded = false;
        await start;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30), timeProvider);
        using var shared = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, applicationLifetime.ApplicationStopping);
        try
        {
            var result = await exchangeClient.ExchangeAsync(backendId, authentication, context, shared.Token);
            ValidateCaller(backendId, context);
            var expires = result.ExchangeStartedAt.AddSeconds(result.ExpiresInSeconds);
            if (context.ExpiresAtUtc < expires)
                expires = context.ExpiresAtUtc;
            var remaining = expires - result.AcquiredAt;
            if (remaining <= TimeSpan.Zero || expires <= timeProvider.GetUtcNow())
                throw Failure(backendId);
            var margin = TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(60).Ticks, remaining.Ticks / 2));
            var token = new WorkflowAccessToken(result.AccessToken, expires, expires - margin);
            lock (_gate)
            {
                entry.Token = token;
                entry.Pending = null;
            }
            succeeded = true;
            return token;
        }
        catch (OperationCanceledException) when (!applicationLifetime.ApplicationStopping.IsCancellationRequested)
        {
            throw Failure(backendId);
        }
        finally
        {
            if (!succeeded)
            {
                lock (_gate)
                {
                    if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                        _entries.Remove(key);
                }
            }
        }
    }

    private void ValidateCaller(string backendId, WorkflowExecutionContext context)
    {
        if (!string.Equals(backendId, context.BackendId, StringComparison.OrdinalIgnoreCase) ||
            context.ExpiresAtUtc <= timeProvider.GetUtcNow())
        {
            logger.LogWarning("Workflow backend {BackendId} rejected caller with {ErrorCode}.", backendId, "workflow_user_auth_required");
            throw new WorkflowDelegationException("workflow_user_auth_required", StatusCodes.Status401Unauthorized);
        }
    }

    private WorkflowDelegationException Failure(string backendId)
    {
        logger.LogWarning("Workflow backend {BackendId} token provider failed with {ErrorCode}.", backendId, "workflow_token_exchange_failed");
        return new("workflow_token_exchange_failed", StatusCodes.Status502BadGateway);
    }

    private static CacheKey CreateKey(string backendId, WorkflowAuthenticationConfig auth, WorkflowExecutionContext context)
    {
        var scopes = auth.Scopes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var encodedScopes = string.Concat(scopes.Select(scope => scope.Length + ":" + scope));
        return new(context.Issuer, context.Subject, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context.Assertion))),
            backendId.ToUpperInvariant(), auth.Mode.ToLowerInvariant(), auth.ClientId, auth.TokenEndpoint,
            encodedScopes, auth.Audience, auth.Resource);
    }

    private sealed record CacheKey(string Issuer, string Subject, string AssertionFingerprint, string BackendId,
        string Mode, string? ClientId, string? Endpoint, string Scopes, string? Audience, string? Resource);

    private sealed class Entry
    {
        public WorkflowAccessToken? Token { get; set; }
        public Task<WorkflowAccessToken>? Pending { get; set; }
    }
}

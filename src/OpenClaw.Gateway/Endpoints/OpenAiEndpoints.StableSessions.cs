using System.Text;
using OpenClaw.Core.Models;
using OpenClaw.Core.Sessions;

namespace OpenClaw.Gateway.Endpoints;

internal static partial class OpenAiEndpoints
{
    private static bool ShouldHydrateRequestHistory(string? stableSessionId, Session session)
        => string.IsNullOrWhiteSpace(stableSessionId) || session.History.Count == 0;

    private static async Task PersistStableSessionAsync(SessionManager sessionManager, Session session, bool sessionLockHeld)
    {
        try
        {
            using var persistCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await sessionManager.PersistAsync(session, persistCts.Token, sessionLockHeld);
        }
        catch
        {
            // Stable-session persistence is best-effort after the response has already been produced.
        }
    }

    private static async Task FinalizeOpenAiSessionAsync(
        SessionManager sessionManager,
        Session session,
        bool isStableSession,
        bool persistStableSession,
        IAsyncDisposable? stableSessionLock)
    {
        try
        {
            if (!isStableSession)
            {
                sessionManager.RemoveActive(session.Id);
                return;
            }

            if (persistStableSession)
                await PersistStableSessionAsync(sessionManager, session, sessionLockHeld: stableSessionLock is not null);
            else if (stableSessionLock is null && session.StableSessionBinding is null && session.History.Count == 0)
                sessionManager.RemoveActive(session.Id);
        }
        finally
        {
            if (stableSessionLock is not null)
                await stableSessionLock.DisposeAsync();
        }
    }

    private static bool TryGetOptionalStableSessionId(HttpContext ctx, out string? stableSessionId, out string? error)
    {
        stableSessionId = null;
        error = null;
        if (!ctx.Request.Headers.TryGetValue(StableSessionHeader, out var values))
            return true;

        var value = values.ToString().Trim();
        if (string.IsNullOrWhiteSpace(value))
            return true;

        if (!IsSafeStableSessionId(value))
        {
            error = $"Header '{StableSessionHeader}' contains an unsafe stable session id.";
            return false;
        }

        stableSessionId = value;
        return true;
    }

    private static StableSessionBindingInfo CreateStableSessionBinding(string stableSessionId, string requesterKey)
    {
        var namespaceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(requesterKey)).AsSpan(0, 8))
            .ToLowerInvariant();
        return new StableSessionBindingInfo
        {
            ExternalSessionId = stableSessionId,
            Namespace = namespaceHash,
            OwnerKey = requesterKey,
            BoundAtUtc = DateTimeOffset.UtcNow
        };
    }

    private static string BuildScopedStableSessionId(StableSessionBindingInfo binding)
        => $"openai-stable:{binding.Namespace}:{binding.ExternalSessionId}";

    private static bool TryEnsureStableSessionBinding(
        Session session,
        StableSessionBindingInfo expectedBinding,
        out string? error)
    {
        error = null;
        if (!string.Equals(session.SenderId, expectedBinding.OwnerKey, StringComparison.Ordinal))
        {
            error = "Stable session belongs to another requester scope and cannot be reused with this token or IP.";
            return false;
        }

        if (session.StableSessionBinding is null)
        {
            session.StableSessionBinding = new StableSessionBindingInfo
            {
                ExternalSessionId = expectedBinding.ExternalSessionId,
                Namespace = expectedBinding.Namespace,
                OwnerKey = expectedBinding.OwnerKey,
                BoundAtUtc = expectedBinding.BoundAtUtc
            };
            return true;
        }

        if (!string.Equals(session.StableSessionBinding.ExternalSessionId, expectedBinding.ExternalSessionId, StringComparison.Ordinal) ||
            !string.Equals(session.StableSessionBinding.Namespace, expectedBinding.Namespace, StringComparison.Ordinal) ||
            !string.Equals(session.StableSessionBinding.OwnerKey, expectedBinding.OwnerKey, StringComparison.Ordinal))
        {
            error = "Stable session binding does not match the current requester scope. Check the stable session namespace shown in admin diagnostics.";
            return false;
        }

        return true;
    }

    private static bool IsSafeStableSessionId(string value)
    {
        if (value.Length is 0 or > 128)
            return false;

        if (value.Contains("..", StringComparison.Ordinal) ||
            value.Contains('/', StringComparison.Ordinal) ||
            value.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
                continue;

            if (ch is '-' or '_' or '.' or ':')
                continue;

            return false;
        }

        return true;
    }
}

using OpenClaw.Core.Models;

namespace OpenClaw.Core.Sessions;

/// <summary>
/// Write access to a session. Owned sessions accept turns from the owner or an admin. Unowned sessions (created
/// by channels, cron, or before ownership existed) stay open, and writing to one never claims it. Callers without
/// an account are admin-equivalent credentials (bootstrap, open loopback) or channel and system flows, which
/// address sessions by their own keys.
/// </summary>
public static class SessionAccess
{
    public const string DeniedMessage = "This conversation belongs to another account.";

    public static bool CanWrite(Session session, string? accountId, bool isAdmin)
        => string.IsNullOrWhiteSpace(session.OwnerAccountId)
           || string.IsNullOrWhiteSpace(accountId)
           || isAdmin
           || string.Equals(session.OwnerAccountId, accountId, StringComparison.Ordinal);
}

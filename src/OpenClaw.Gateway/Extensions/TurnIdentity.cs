using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Extensions;

/// <summary>
/// Decides whose identity a pipeline turn runs with. <see cref="Session.AuthenticatedUserId"/> scopes per-user
/// capability bindings and is passed to MCP servers as the userId, so it must describe the caller of the current
/// turn rather than whoever last wrote to the session.
/// </summary>
internal static class TurnIdentity
{
    /// <summary>Returns true when the session's identity changed and should be persisted.</summary>
    public static bool Apply(Session session, InboundMessage message)
    {
        string? identity;
        if (!string.IsNullOrWhiteSpace(message.AuthenticatedUserId))
            identity = message.AuthenticatedUserId;
        else if (ActsOnSessionsBehalf(message))
            return false;
        else
            identity = null; // an external sender without an account must not inherit a previous writer's account

        if (string.Equals(session.AuthenticatedUserId, identity, StringComparison.Ordinal))
            return false;

        session.AuthenticatedUserId = identity;
        return true;
    }

    private static bool ActsOnSessionsBehalf(InboundMessage message)
        => message.IsSystem
           || !string.IsNullOrWhiteSpace(message.CronJobName)
           || !string.IsNullOrWhiteSpace(message.AutomationRunId)
           || !string.IsNullOrWhiteSpace(message.BackgroundRunId);
}

using OpenClaw.Core.Security;

namespace OpenClaw.Gateway.A2A;

/// <summary>
/// The signed-in caller behind the current A2A request. The A2A middleware sets it before the SDK runs the
/// handler; unlike the HTTP context, an async-local value still flows into work the SDK detaches from the request.
/// </summary>
internal static class A2ACallerContext
{
    private static readonly AsyncLocal<string?> CurrentAccountId = new();
    private static readonly AsyncLocal<bool> CurrentIsAdmin = new();
    private static readonly AsyncLocal<McpCallerCredentialContext?> CurrentMcpCallerCredentialContext = new();

    public static string? AccountId
    {
        get => CurrentAccountId.Value;
        set => CurrentAccountId.Value = value;
    }

    /// <summary>Whether the caller holds the admin role, which may write to sessions other accounts own.</summary>
    public static bool IsAdmin
    {
        get => CurrentIsAdmin.Value;
        set => CurrentIsAdmin.Value = value;
    }

    public static McpCallerCredentialContext? McpCallerCredentialContext
    {
        get => CurrentMcpCallerCredentialContext.Value;
        set => CurrentMcpCallerCredentialContext.Value = value;
    }
}

using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;
using OpenClaw.Core.Models;
using OpenClaw.Core.Sessions;

namespace OpenClaw.Gateway.Endpoints;

internal static partial class OpenAiEndpoints
{
    private const int MaxChatCompletionRequestBytes = 1024 * 1024;
    private const string StableSessionHeader = "X-OpenClaw-Session-Id";
    private const string NoImplicitToolsAllowed = "__openclaw_openai_no_implicit_tools__";

    // OpenAI-shaped so SDK clients surface the message instead of a bare status.
    private const string OperatorRoleRequiredErrorJson =
        $$$"""{"error":{"message":"{{{EndpointHelpers.OperatorRoleRequiredMessage}}}","type":"permission_error","code":"insufficient_role"}}""";

    private const string SessionForbiddenErrorJson =
        $$$"""{"error":{"message":"{{{SessionAccess.DeniedMessage}}}","type":"permission_error","code":"session_forbidden"}}""";

    public static void MapOpenClawOpenAiEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        MapChatCompletionsEndpoint(app, startup, runtime);
        MapResponsesEndpoint(app, startup, runtime);
    }

    private static async Task<bool> TryRejectBelowOperatorAsync(HttpContext ctx, GatewayStartupContext startup)
    {
        if (EndpointHelpers.CanExecuteAgent(ctx, startup))
            return false;

        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(OperatorRoleRequiredErrorJson, ctx.RequestAborted);
        return true;
    }

    // A stable session is keyed by the bearer token's hash or, for browser sessions, the client address, so two
    // signed-in accounts behind one address can derive the same session. The owner check keeps them apart.
    private static async Task<bool> TryRejectOtherAccountsSessionAsync(HttpContext ctx, Session session, EndpointHelpers.CallerAccount caller)
    {
        if (SessionAccess.CanWrite(session, caller.AccountId, caller.IsAdmin))
            return false;

        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(SessionForbiddenErrorJson, ctx.RequestAborted);
        return true;
    }

    private static void ApplyImplicitToolPolicy(Session session, GatewayAppRuntime runtime, string? presetId)
    {
        ClearImplicitToolSuppression(session);

        if (HasExplicitPreset(session, runtime, presetId))
            return;

        if (!TryResolveSelectedProfile(session, runtime, out var profile) ||
            profile.Capabilities.SupportsTools)
        {
            return;
        }

        session.RouteAllowedTools = [NoImplicitToolsAllowed];
    }

    private static void ClearImplicitToolSuppression(Session session)
    {
        if (session.RouteAllowedTools.Length == 1 &&
            string.Equals(session.RouteAllowedTools[0], NoImplicitToolsAllowed, StringComparison.Ordinal))
        {
            session.RouteAllowedTools = [];
        }
    }

    private static bool TryResolveSelectedProfile(
        Session session,
        GatewayAppRuntime runtime,
        out ModelProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(session.ModelProfileId) &&
            runtime.Operations.ModelProfiles.TryGet(session.ModelProfileId, out var explicitProfile) &&
            explicitProfile is not null)
        {
            profile = explicitProfile;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(session.ModelOverride))
        {
            profile = null!;
            return false;
        }

        var defaultProfileId = runtime.Operations.ModelProfiles.DefaultProfileId;
        if (!string.IsNullOrWhiteSpace(defaultProfileId) &&
            runtime.Operations.ModelProfiles.TryGet(defaultProfileId, out var defaultProfile) &&
            defaultProfile is not null)
        {
            profile = defaultProfile;
            return true;
        }

        profile = null!;
        return false;
    }

    private static bool HasExplicitPreset(Session session, GatewayAppRuntime runtime, string? presetId)
    {
        if (!string.IsNullOrWhiteSpace(presetId) || !string.IsNullOrWhiteSpace(session.RoutePresetId))
            return true;

        var metadata = runtime.Operations.SessionMetadata.Get(session.Id);
        return !string.IsNullOrWhiteSpace(metadata.ActivePresetId);
    }
}

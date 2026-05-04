using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Endpoints;

internal static partial class OpenAiEndpoints
{
    private const int MaxChatCompletionRequestBytes = 1024 * 1024;
    private const string StableSessionHeader = "X-OpenClaw-Session-Id";
    private const string NoImplicitToolsAllowed = "__openclaw_openai_no_implicit_tools__";

    public static void MapOpenClawOpenAiEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        MapChatCompletionsEndpoint(app, startup, runtime);
        MapResponsesEndpoint(app, startup, runtime);
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

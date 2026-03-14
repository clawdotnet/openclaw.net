using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Endpoints;

internal static class EndpointMappingsExtensions
{
    public static void MapOpenClawEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        app.MapOpenClawDiagnosticsEndpoints(startup, runtime);
        app.MapOpenClawOpenAiEndpoints(startup, runtime);
        app.MapOpenClawWebUiEndpoints(startup, runtime);
        app.MapOpenClawAdminEndpoints(startup, runtime);
        app.MapOpenClawControlEndpoints(startup, runtime);
        app.MapOpenClawWebSocketEndpoints(startup, runtime);
        app.MapOpenClawWebhookEndpoints(startup, runtime);
    }
}

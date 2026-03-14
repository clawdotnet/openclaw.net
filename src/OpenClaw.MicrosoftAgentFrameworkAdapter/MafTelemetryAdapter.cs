using System.Diagnostics;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;

namespace OpenClaw.MicrosoftAgentFrameworkAdapter;

public sealed class MafTelemetryAdapter
{
    public Activity? StartRunActivity(string operationName, Session session, GatewayRuntimeState runtimeState)
    {
        var activity = Telemetry.ActivitySource.StartActivity(operationName);
        activity?.SetTag("orchestrator.id", MafCapabilities.OrchestratorId);
        activity?.SetTag("runtime.mode", runtimeState.EffectiveModeName);
        activity?.SetTag("session.id", session.Id);
        activity?.SetTag("channel.id", session.ChannelId);
        return activity;
    }

    public void TagProvider(Activity? activity, string providerId, string modelId)
    {
        activity?.SetTag("llm.provider", providerId);
        activity?.SetTag("llm.model", modelId);
    }
}

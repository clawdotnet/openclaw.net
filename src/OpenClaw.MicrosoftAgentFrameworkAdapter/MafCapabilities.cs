using OpenClaw.Core.Models;

namespace OpenClaw.MicrosoftAgentFrameworkAdapter;

public static class MafCapabilities
{
    public const string OrchestratorId = RuntimeOrchestrator.Maf;

    public static bool SupportsMode(GatewayRuntimeState runtimeState)
        => runtimeState.EffectiveMode is GatewayRuntimeMode.Jit or GatewayRuntimeMode.Aot;

    public static void EnsureSupported(GatewayRuntimeState runtimeState)
    {
        _ = runtimeState;
    }
}

using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Gateway;

internal readonly record struct BrowserToolAvailability(
    bool ConfiguredEnabled,
    bool LocalExecutionSupported,
    bool ExecutionBackendConfigured,
    bool Registered,
    string Reason);

internal static class BrowserToolSupport
{
    public static BrowserToolAvailability Evaluate(GatewayConfig config, GatewayRuntimeState runtimeState)
    {
        var summary = BrowserToolCapabilityEvaluator.Evaluate(config, runtimeState);
        return new BrowserToolAvailability(
            summary.ConfiguredEnabled,
            summary.LocalExecutionSupported,
            summary.ExecutionBackendConfigured,
            summary.Registered,
            summary.Reason);
    }
}

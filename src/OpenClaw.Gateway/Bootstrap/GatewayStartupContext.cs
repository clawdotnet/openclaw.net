using OpenClaw.Agent.Plugins;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Bootstrap;

internal sealed class GatewayStartupContext
{
    public required GatewayConfig Config { get; init; }
    public required GatewayRuntimeState RuntimeState { get; init; }
    public required bool IsNonLoopbackBind { get; init; }
    public ConfigSourceDiagnostics? ConfigSources { get; init; }
    public string? WorkspacePath { get; init; }
    public NativeDynamicPluginHost? NativeDynamicPluginHost { get; set; }

    /// <summary>TokenHub thin-client config, bound from the <c>OpenClaw:TokenUsage</c> section.</summary>
    public TokenUsageConfig TokenUsage { get; init; } = new();
}

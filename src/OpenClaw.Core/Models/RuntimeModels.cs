using System.Runtime.CompilerServices;

namespace OpenClaw.Core.Models;

public enum GatewayRuntimeMode
{
    Aot,
    Jit
}

public sealed class RuntimeConfig
{
    /// <summary>"auto" (default), "aot", or "jit".</summary>
    public string Mode { get; set; } = "auto";

    /// <summary>"native" (default) or "maf".</summary>
    public string Orchestrator { get; set; } = RuntimeOrchestrator.Native;
}

public sealed class GatewayRuntimeState
{
    public required string RequestedMode { get; init; }
    public required GatewayRuntimeMode EffectiveMode { get; init; }
    public required bool DynamicCodeSupported { get; init; }

    public string EffectiveModeName => EffectiveMode == GatewayRuntimeMode.Aot ? "aot" : "jit";
}

public static class RuntimeModeResolver
{
    public static GatewayRuntimeState Resolve(RuntimeConfig? config, bool? dynamicCodeSupported = null)
    {
        var requestedMode = Normalize(config?.Mode);
        var supportsDynamicCode = dynamicCodeSupported ?? RuntimeFeature.IsDynamicCodeSupported;

        if (requestedMode == "jit" && !supportsDynamicCode)
        {
            throw new InvalidOperationException(
                "OpenClaw:Runtime:Mode='jit' requires a JIT-capable build, but dynamic code is not supported in this artifact.");
        }

        var effectiveMode = requestedMode switch
        {
            "aot" => GatewayRuntimeMode.Aot,
            "jit" => GatewayRuntimeMode.Jit,
            _ => supportsDynamicCode ? GatewayRuntimeMode.Jit : GatewayRuntimeMode.Aot
        };

        return new GatewayRuntimeState
        {
            RequestedMode = requestedMode,
            EffectiveMode = effectiveMode,
            DynamicCodeSupported = supportsDynamicCode
        };
    }

    public static string Normalize(string? mode)
        => (mode ?? "auto").Trim().ToLowerInvariant();
}

public static class RuntimeOrchestrator
{
    public const string Native = "native";
    public const string Maf = "maf";

    public static string Normalize(string? orchestrator)
        => (orchestrator ?? Native).Trim().ToLowerInvariant();
}

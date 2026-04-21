using OpenClaw.Core.Models;

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
        var configuredEnabled = config.Tooling.EnableBrowserTool;
        var localExecutionSupported = runtimeState.DynamicCodeSupported;
        var executionBackendConfigured = HasExecutionBackend(config) || HasLegacySandboxRoute(config);
        var registered = configuredEnabled && (localExecutionSupported || executionBackendConfigured);

        var reason = !configuredEnabled
            ? "disabled"
            : registered
                ? executionBackendConfigured && !localExecutionSupported
                    ? "backend_only"
                    : "available"
                : "local_execution_unavailable_without_backend";

        return new BrowserToolAvailability(
            configuredEnabled,
            localExecutionSupported,
            executionBackendConfigured,
            registered,
            reason);
    }

    private static bool HasExecutionBackend(GatewayConfig config)
    {
        if (!config.Execution.Enabled || !config.Execution.Tools.TryGetValue("browser", out var route))
            return false;

        return IsNonLocalBackendAvailable(config, route.Backend)
            || IsNonLocalBackendAvailable(config, route.FallbackBackend);
    }

    private static bool HasLegacySandboxRoute(GatewayConfig config)
        => ToolSandboxPolicy.IsOpenSandboxProviderConfigured(config)
           && ToolSandboxPolicy.ResolveMode(config, "browser", ToolSandboxMode.Prefer) is not ToolSandboxMode.None;

    private static bool IsNonLocalBackendAvailable(GatewayConfig config, string? backendName)
    {
        if (string.IsNullOrWhiteSpace(backendName) ||
            backendName.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (backendName.Equals("opensandbox", StringComparison.OrdinalIgnoreCase))
            return ToolSandboxPolicy.IsOpenSandboxProviderConfigured(config);

        return config.Execution.Profiles.TryGetValue(backendName, out var profile)
               && profile.Enabled
               && !profile.Type.Equals(ExecutionBackendType.Local, StringComparison.OrdinalIgnoreCase);
    }
}

namespace OpenClaw.Core.Models;

public sealed class SandboxConfig
{
    public string Provider { get; set; } = SandboxProviderNames.None;
    public string? Endpoint { get; set; }

    /// <summary>
    /// Optional API key for OpenSandbox. Supports env:NAME and raw:VALUE secret refs.
    /// </summary>
    public string? ApiKey { get; set; }

    public int DefaultTTL { get; set; } = 300;
    public Dictionary<string, SandboxToolConfig> Tools { get; set; } = new(StringComparer.Ordinal);
}

public sealed class SandboxToolConfig
{
    public string? Mode { get; set; }

    /// <summary>
    /// For the current OpenSandbox integration this maps directly to the image URI used to create the sandbox.
    /// </summary>
    public string? Template { get; set; }

    public int? TTL { get; set; }
}

public static class SandboxProviderNames
{
    public const string None = "None";
    public const string OpenSandbox = "OpenSandbox";

    public static string Normalize(string? provider)
        => string.IsNullOrWhiteSpace(provider)
            ? None
            : provider.Trim();
}

public static class ToolSandboxPolicy
{
    public static bool IsOpenSandboxProviderConfigured(GatewayConfig config)
        => string.Equals(
            SandboxProviderNames.Normalize(config.Sandbox.Provider),
            SandboxProviderNames.OpenSandbox,
            StringComparison.OrdinalIgnoreCase);

    public static bool TryParseMode(string? value, out ToolSandboxMode mode)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            mode = ToolSandboxMode.None;
            return false;
        }

        if (string.Equals(value, nameof(ToolSandboxMode.None), StringComparison.OrdinalIgnoreCase))
        {
            mode = ToolSandboxMode.None;
            return true;
        }

        if (string.Equals(value, nameof(ToolSandboxMode.Prefer), StringComparison.OrdinalIgnoreCase))
        {
            mode = ToolSandboxMode.Prefer;
            return true;
        }

        if (string.Equals(value, nameof(ToolSandboxMode.Require), StringComparison.OrdinalIgnoreCase))
        {
            mode = ToolSandboxMode.Require;
            return true;
        }

        mode = ToolSandboxMode.None;
        return false;
    }

    public static ToolSandboxMode ResolveMode(
        GatewayConfig config,
        string toolName,
        ToolSandboxMode defaultMode)
    {
        if (config.Sandbox.Tools.TryGetValue(toolName, out var toolConfig) &&
            TryParseMode(toolConfig.Mode, out var configuredMode))
        {
            return configuredMode;
        }

        return defaultMode;
    }

    public static string? ResolveTemplate(GatewayConfig config, string toolName)
        => config.Sandbox.Tools.TryGetValue(toolName, out var toolConfig)
            ? toolConfig.Template
            : null;

    public static int ResolveTimeToLiveSeconds(
        GatewayConfig config,
        string toolName,
        int? requestedTimeToLiveSeconds = null)
    {
        if (requestedTimeToLiveSeconds is > 0)
            return requestedTimeToLiveSeconds.Value;

        if (config.Sandbox.Tools.TryGetValue(toolName, out var toolConfig) &&
            toolConfig.TTL is > 0)
        {
            return toolConfig.TTL.Value;
        }

        return config.Sandbox.DefaultTTL;
    }

    public static bool IsRequireSandboxed(
        GatewayConfig config,
        string toolName,
        ToolSandboxMode defaultMode)
        => IsOpenSandboxProviderConfigured(config) &&
           ResolveMode(config, toolName, defaultMode) == ToolSandboxMode.Require;

    public static IEnumerable<(string ToolName, ToolSandboxMode DefaultMode)> EnumerateBuiltInCandidates(GatewayConfig config)
    {
        if (!config.Tooling.ReadOnlyMode && config.Tooling.AllowShell)
            yield return ("shell", ToolSandboxMode.Prefer);

        if (!config.Tooling.ReadOnlyMode && config.Plugins.Native.CodeExec.Enabled)
            yield return ("code_exec", ToolSandboxMode.Prefer);

        if (config.Tooling.EnableBrowserTool)
            yield return ("browser", ToolSandboxMode.Prefer);
    }
}

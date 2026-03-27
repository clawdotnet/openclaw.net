using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway;

internal static class SecurityPostureBuilder
{
    public static SecurityPostureResponse Build(GatewayStartupContext startup, GatewayAppRuntime runtime)
    {
        var config = startup.Config;
        var publicBind = startup.IsNonLoopbackBind;
        var autonomyMode = (config.Tooling.AutonomyMode ?? "full").Trim().ToLowerInvariant();
        var pluginBridgeEnabled = runtime.PluginReports.Any(static report => string.Equals(report.Origin, "bridge", StringComparison.Ordinal));
        var transportMode = NormalizeTransportMode(config.Plugins.Transport.Mode);
        var securityMode = transportMode == "stdio" ? "stdio" : "hardened_local_ipc";
        var browserSessionCookieSecureEffective = !publicBind || config.Security.TrustForwardedHeaders;
        var sandboxConfigured = ToolSandboxPolicy.IsOpenSandboxProviderConfigured(config);
        var riskFlags = new List<string>();
        var recommendations = new List<string>();

        if (publicBind && string.IsNullOrWhiteSpace(config.AuthToken))
        {
            riskFlags.Add("public_bind_missing_auth_token");
            recommendations.Add("Configure OpenClaw:AuthToken before exposing a non-loopback bind.");
        }

        if (publicBind && !config.Security.RequireRequesterMatchForHttpToolApproval)
        {
            riskFlags.Add("public_bind_admin_override_tool_approval");
            recommendations.Add("Set OpenClaw:Security:RequireRequesterMatchForHttpToolApproval=true on public binds unless operator override is explicitly intended.");
        }

        if (publicBind && !browserSessionCookieSecureEffective)
        {
            riskFlags.Add("browser_session_cookie_may_be_insecure");
            recommendations.Add("Enable TrustForwardedHeaders and configure KnownProxies when browser admin sessions run behind TLS termination.");
        }

        if (publicBind && pluginBridgeEnabled)
        {
            riskFlags.Add("public_bind_plugin_bridge_enabled");
            recommendations.Add("Disable third-party bridge plugins on public binds unless the deployment explicitly accepts that risk.");
        }

        if (publicBind && HasRawSecretRefs(config))
        {
            riskFlags.Add("public_bind_raw_secret_refs");
            recommendations.Add("Replace raw: secret refs with env: references before Internet-facing deployment.");
        }

        if (publicBind && config.Tooling.AllowShell && !ToolSandboxPolicy.IsRequireSandboxed(config, "shell", ToolSandboxMode.Prefer))
        {
            riskFlags.Add("public_bind_unsandboxed_shell");
            recommendations.Add("Require sandboxing for shell on public binds or disable shell tooling entirely.");
        }

        if (runtime.EffectiveRequireToolApproval && runtime.EffectiveApprovalRequiredTools.Count == 0)
        {
            riskFlags.Add("approval_mode_without_tool_list");
            recommendations.Add("Populate Tooling:ApprovalRequiredTools or use supervised autonomy defaults.");
        }

        if (!sandboxConfigured)
            recommendations.Add("Configure OpenSandbox for stronger isolation of shell, code_exec, and browser tools.");

        return new SecurityPostureResponse
        {
            PublicBind = publicBind,
            AuthTokenConfigured = !string.IsNullOrWhiteSpace(config.AuthToken),
            BrowserSessionCookieSecureEffective = browserSessionCookieSecureEffective,
            BrowserSessionsEnabled = publicBind,
            TrustForwardedHeaders = config.Security.TrustForwardedHeaders,
            RequireRequesterMatchForHttpToolApproval = config.Security.RequireRequesterMatchForHttpToolApproval,
            ToolApprovalRequired = runtime.EffectiveRequireToolApproval,
            AutonomyMode = autonomyMode,
            PluginBridgeEnabled = pluginBridgeEnabled,
            PluginBridgeTransportMode = transportMode,
            PluginBridgeSecurityMode = securityMode,
            SandboxConfigured = sandboxConfigured,
            AllowsRawSecretRefsOnPublicBind = config.Security.AllowRawSecretRefsOnPublicBind,
            RiskFlags = riskFlags,
            Recommendations = recommendations
        };
    }

    private static bool HasRawSecretRefs(GatewayConfig config)
    {
        return SecretResolver.IsRawRef(config.Llm.ApiKey)
            || SecretResolver.IsRawRef(config.Sandbox.ApiKey)
            || SecretResolver.IsRawRef(config.Channels.Sms.Twilio.AuthTokenRef)
            || SecretResolver.IsRawRef(config.Channels.Telegram.BotTokenRef)
            || SecretResolver.IsRawRef(config.Channels.Telegram.WebhookSecretTokenRef)
            || SecretResolver.IsRawRef(config.Channels.WhatsApp.WebhookAppSecretRef)
            || SecretResolver.IsRawRef(config.Channels.WhatsApp.CloudApiTokenRef)
            || SecretResolver.IsRawRef(config.Channels.WhatsApp.BridgeTokenRef)
            || SecretResolver.IsRawRef(config.Channels.Teams.AppIdRef)
            || SecretResolver.IsRawRef(config.Channels.Teams.AppPasswordRef)
            || SecretResolver.IsRawRef(config.Channels.Teams.TenantIdRef);
    }

    private static string NormalizeTransportMode(string? mode)
        => string.IsNullOrWhiteSpace(mode) ? "stdio" : mode.Trim().ToLowerInvariant();
}

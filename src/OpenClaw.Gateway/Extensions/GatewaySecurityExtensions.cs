using System;
using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Gateway;

namespace OpenClaw.Gateway.Extensions;

public static class GatewaySecurityExtensions
{
    public static void ApplyStrictPublicBindProfile(GatewayConfig config, bool isNonLoopbackBind)
    {
        if (!isNonLoopbackBind || !config.Security.StrictPublicBindProfile)
            return;

        config.Security.RequireRequesterMatchForHttpToolApproval = true;
        config.Security.AllowUnsafeToolingOnPublicBind = false;
        config.Security.AllowPluginBridgeOnPublicBind = false;
        config.Security.AllowRawSecretRefsOnPublicBind = false;
        if (!config.Canvas.AllowOnPublicBind)
            config.Canvas.Enabled = false;
        config.Tooling.RequireToolApproval = true;
        config.Tooling.ApprovalRequiredTools = config.Tooling.ApprovalRequiredTools
            .Concat(["shell", "process", "write_file", "code_exec", "git"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static void EnforcePublicBindHardening(GatewayConfig config, bool isNonLoopbackBind)
    {
        if (!isNonLoopbackBind)
            return;

        if (config.Canvas.Enabled && !config.Canvas.AllowOnPublicBind)
        {
            throw new InvalidOperationException(
                "Refusing to start with Canvas command forwarding enabled on a non-loopback bind. " +
                "Disable OpenClaw:Canvas:Enabled or explicitly opt in via OpenClaw:Canvas:AllowOnPublicBind=true.");
        }

        var localShellEnabled = IsUnsafeLocalToolExecutionExposed(config, "shell");
        var localProcessEnabled = IsUnsafeLocalToolExecutionExposed(config, "process");
        var toolingUnsafe =
            localShellEnabled ||
            localProcessEnabled ||
            config.Tooling.AllowedReadRoots.Contains("*", StringComparer.Ordinal) ||
            config.Tooling.AllowedWriteRoots.Contains("*", StringComparer.Ordinal);

        if (toolingUnsafe && !config.Security.AllowUnsafeToolingOnPublicBind)
        {
            throw new InvalidOperationException(
                "Refusing to start with unsafe tooling settings on a non-loopback bind. " +
                "Disable or isolate shell/process execution and restrict AllowedReadRoots/AllowedWriteRoots, " +
                "or explicitly opt in via OpenClaw:Security:AllowUnsafeToolingOnPublicBind=true.");
        }

        if ((config.Plugins.Enabled || config.Plugins.DynamicNative.Enabled || config.Plugins.Mcp.Enabled) && !config.Security.AllowPluginBridgeOnPublicBind)
        {
            throw new InvalidOperationException(
                "Refusing to start with third-party plugin execution enabled on a non-loopback bind. " +
                "Disable OpenClaw:Plugins:Enabled / OpenClaw:Plugins:DynamicNative:Enabled, or explicitly opt in via OpenClaw:Security:AllowPluginBridgeOnPublicBind=true.");
        }

        if (config.Channels.WhatsApp.Enabled)
        {
            if (string.Equals(config.Channels.WhatsApp.Type, "official", StringComparison.OrdinalIgnoreCase))
            {
                if (!config.Channels.WhatsApp.ValidateSignature)
                {
                    throw new InvalidOperationException(
                        "Refusing to start with WhatsApp official webhooks on a non-loopback bind without signature validation. " +
                        "Set OpenClaw:Channels:WhatsApp:ValidateSignature=true and configure WebhookAppSecretRef.");
                }

                var appSecret = SecretResolver.Resolve(config.Channels.WhatsApp.WebhookAppSecretRef)
                    ?? config.Channels.WhatsApp.WebhookAppSecret;
                if (string.IsNullOrWhiteSpace(appSecret))
                {
                    throw new InvalidOperationException(
                        "Refusing to start with WhatsApp official webhooks on a non-loopback bind without a webhook app secret. " +
                        "Set OpenClaw:Channels:WhatsApp:WebhookAppSecretRef (recommended) or WebhookAppSecret.");
                }
            }
            else if (string.Equals(config.Channels.WhatsApp.Type, "bridge", StringComparison.OrdinalIgnoreCase))
            {
                var bridgeToken = SecretResolver.Resolve(config.Channels.WhatsApp.BridgeTokenRef)
                    ?? config.Channels.WhatsApp.BridgeToken;
                if (string.IsNullOrWhiteSpace(bridgeToken))
                {
                    throw new InvalidOperationException(
                        "Refusing to start with WhatsApp bridge webhooks on a non-loopback bind without inbound authentication. " +
                        "Set OpenClaw:Channels:WhatsApp:BridgeTokenRef (recommended) or BridgeToken.");
                }
            }
            else if (string.Equals(config.Channels.WhatsApp.Type, "first_party_worker", StringComparison.OrdinalIgnoreCase) &&
                config.Channels.WhatsApp.FirstPartyWorker.Accounts.Count == 0)
            {
                throw new InvalidOperationException(
                    "Refusing to start with first-party WhatsApp worker enabled without any configured accounts. " +
                    "Set OpenClaw:Channels:WhatsApp:FirstPartyWorker:Accounts.");
            }
        }

        if (config.Channels.Sms.Twilio.Enabled)
        {
            if (!config.Channels.Sms.Twilio.ValidateSignature)
            {
                throw new InvalidOperationException(
                    "Refusing to start with Twilio SMS webhooks on a non-loopback bind without signature validation. " +
                    "Set OpenClaw:Channels:Sms:Twilio:ValidateSignature=true.");
            }

            if (string.IsNullOrWhiteSpace(config.Channels.Sms.Twilio.WebhookPublicBaseUrl))
            {
                throw new InvalidOperationException(
                    "Refusing to start with Twilio SMS webhooks on a non-loopback bind without WebhookPublicBaseUrl. " +
                    "Set OpenClaw:Channels:Sms:Twilio:WebhookPublicBaseUrl so signatures can be verified.");
            }

            if (string.IsNullOrWhiteSpace(SecretResolver.Resolve(config.Channels.Sms.Twilio.AuthTokenRef)))
            {
                throw new InvalidOperationException(
                    "Refusing to start with Twilio SMS webhooks on a non-loopback bind without AuthTokenRef. " +
                    "Set OpenClaw:Channels:Sms:Twilio:AuthTokenRef.");
            }
        }

        if (config.Channels.Telegram.Enabled)
        {
            if (!config.Channels.Telegram.ValidateSignature)
            {
                throw new InvalidOperationException(
                    "Refusing to start with Telegram webhooks on a non-loopback bind without webhook secret validation. " +
                    "Set OpenClaw:Channels:Telegram:ValidateSignature=true.");
            }

            var secretToken = SecretResolver.Resolve(config.Channels.Telegram.WebhookSecretTokenRef)
                ?? config.Channels.Telegram.WebhookSecretToken;
            if (string.IsNullOrWhiteSpace(secretToken))
            {
                throw new InvalidOperationException(
                    "Refusing to start with Telegram webhooks on a non-loopback bind without a webhook secret token. " +
                    "Set OpenClaw:Channels:Telegram:WebhookSecretTokenRef or WebhookSecretToken.");
            }
        }

        if (config.Channels.Teams.Enabled)
        {
            if (!config.Channels.Teams.ValidateToken)
            {
                throw new InvalidOperationException(
                    "Refusing to start with Teams inbound webhooks on a non-loopback bind without JWT validation. " +
                    "Set OpenClaw:Channels:Teams:ValidateToken=true.");
            }

            var appId = SecretResolver.Resolve(config.Channels.Teams.AppIdRef) ?? config.Channels.Teams.AppId;
            var appPassword = SecretResolver.Resolve(config.Channels.Teams.AppPasswordRef) ?? config.Channels.Teams.AppPassword;
            var tenantId = SecretResolver.Resolve(config.Channels.Teams.TenantIdRef) ?? config.Channels.Teams.TenantId;
            if (string.IsNullOrWhiteSpace(appId) ||
                string.IsNullOrWhiteSpace(appPassword) ||
                string.IsNullOrWhiteSpace(tenantId))
            {
                throw new InvalidOperationException(
                    "Refusing to start with Teams inbound webhooks on a non-loopback bind without full token-validation credentials. " +
                    "Set AppId/AppIdRef, AppPassword/AppPasswordRef, and TenantId/TenantIdRef.");
            }
        }

        if (config.Channels.Slack.Enabled)
        {
            if (!config.Channels.Slack.ValidateSignature)
            {
                throw new InvalidOperationException(
                    "Refusing to start with Slack webhooks on a non-loopback bind without signature validation. " +
                    "Set OpenClaw:Channels:Slack:ValidateSignature=true.");
            }

            var signingSecret = SecretResolver.Resolve(config.Channels.Slack.SigningSecretRef) ?? config.Channels.Slack.SigningSecret;
            if (string.IsNullOrWhiteSpace(signingSecret))
            {
                throw new InvalidOperationException(
                    "Refusing to start with Slack webhooks on a non-loopback bind without a signing secret. " +
                    "Set OpenClaw:Channels:Slack:SigningSecretRef or SigningSecret.");
            }
        }

        if (config.Channels.Discord.Enabled)
        {
            if (!config.Channels.Discord.ValidateSignature)
            {
                throw new InvalidOperationException(
                    "Refusing to start with Discord interactions on a non-loopback bind without signature validation. " +
                    "Set OpenClaw:Channels:Discord:ValidateSignature=true.");
            }

            var publicKey = SecretResolver.Resolve(config.Channels.Discord.PublicKeyRef) ?? config.Channels.Discord.PublicKey;
            if (string.IsNullOrWhiteSpace(publicKey))
            {
                throw new InvalidOperationException(
                    "Refusing to start with Discord interactions on a non-loopback bind without a public key. " +
                    "Set OpenClaw:Channels:Discord:PublicKeyRef or PublicKey.");
            }
        }

        if (!config.Security.AllowRawSecretRefsOnPublicBind)
        {
            var rawSecretPaths = FindRawSecretRefs(config);
            if (rawSecretPaths.Count > 0)
            {
                var sample = string.Join(", ", rawSecretPaths.Take(3));
                var suffix = rawSecretPaths.Count > 3 ? ", ..." : "";
                throw new InvalidOperationException(
                    "Refusing to start with a raw: secret ref on a non-loopback bind. " +
                    $"Detected in: {sample}{suffix}. " +
                    "Use env:... / OS keychain storage, or explicitly opt in via OpenClaw:Security:AllowRawSecretRefsOnPublicBind=true.");
            }
        }
    }

    public static bool IsUnsafeLocalToolExecutionExposed(GatewayConfig config, string toolName)
    {
        if (!config.Tooling.AllowShell)
            return false;

        if (TryResolveExecutionBackend(config, toolName, out var backendName))
            return string.Equals(backendName, "local", StringComparison.OrdinalIgnoreCase);

        return toolName switch
        {
            "shell" => !ToolSandboxPolicy.IsRequireSandboxed(config, "shell", ToolSandboxMode.Prefer) &&
                       string.Equals(config.Execution.DefaultBackend, "local", StringComparison.OrdinalIgnoreCase),
            "process" => !ToolSandboxPolicy.IsRequireSandboxed(config, "process", ToolSandboxMode.Prefer) &&
                         string.Equals(config.Execution.DefaultBackend, "local", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static IReadOnlyList<string> FindRawSecretRefs(GatewayConfig root)
    {
        var hits = new List<string>(capacity: 8);
        var json = JsonSerializer.SerializeToElement(root, CoreJsonContext.Default.GatewayConfig);
        VisitForRawRefs(json, "OpenClaw", hits);
        return hits;
    }

    private static void VisitForRawRefs(JsonElement value, string path, List<string> hits)
    {
        if (hits.Count >= 8)
            return;

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var s = value.GetString();
                if (!string.IsNullOrWhiteSpace(s) && SecretResolver.IsRawRef(s) && LooksLikeSecretPath(path))
                    hits.Add(path);
                return;

            case JsonValueKind.Object:
                foreach (var prop in value.EnumerateObject())
                {
                    VisitForRawRefs(prop.Value, $"{path}:{prop.Name}", hits);
                    if (hits.Count >= 8)
                        return;
                }
                return;

            case JsonValueKind.Array:
                var idx = 0;
                foreach (var item in value.EnumerateArray())
                {
                    VisitForRawRefs(item, $"{path}[{idx++}]", hits);
                    if (hits.Count >= 8)
                        return;
                }
                return;

            default:
                hits.Add(path);
                hits.RemoveAt(hits.Count - 1);
                return;
        }
    }

    private static bool LooksLikeSecretPath(string path)
    {
        return path.Contains("Ref", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("Secret", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("Token", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("ApiKey", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveExecutionBackend(GatewayConfig config, string toolName, out string? backendName)
    {
        backendName = null;
        if (!config.Execution.Enabled)
            return false;

        if (config.Execution.Tools.TryGetValue(toolName, out var route) && !string.IsNullOrWhiteSpace(route.Backend))
        {
            backendName = route.Backend;
            return true;
        }

        if (string.Equals(toolName, "process", StringComparison.OrdinalIgnoreCase) &&
            config.Execution.Tools.TryGetValue("shell", out var shellRoute) &&
            !string.IsNullOrWhiteSpace(shellRoute.Backend))
        {
            backendName = shellRoute.Backend;
            return true;
        }

        return false;
    }
}

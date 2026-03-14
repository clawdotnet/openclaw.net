using System;
using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Gateway;

namespace OpenClaw.Gateway.Extensions;

public static class GatewaySecurityExtensions
{
    public static void EnforcePublicBindHardening(GatewayConfig config, bool isNonLoopbackBind)
    {
        if (!isNonLoopbackBind)
            return;

        var localShellEnabled = config.Tooling.AllowShell &&
            !ToolSandboxPolicy.IsRequireSandboxed(config, "shell", ToolSandboxMode.Prefer);
        var toolingUnsafe =
            localShellEnabled ||
            config.Tooling.AllowedReadRoots.Contains("*", StringComparer.Ordinal) ||
            config.Tooling.AllowedWriteRoots.Contains("*", StringComparer.Ordinal);

        if (toolingUnsafe && !config.Security.AllowUnsafeToolingOnPublicBind)
        {
            throw new InvalidOperationException(
                "Refusing to start with unsafe tooling settings on a non-loopback bind. " +
                "Set OpenClaw:Tooling:AllowShell=false and restrict AllowedReadRoots/AllowedWriteRoots, " +
                "or explicitly opt in via OpenClaw:Security:AllowUnsafeToolingOnPublicBind=true.");
        }

        if ((config.Plugins.Enabled || config.Plugins.DynamicNative.Enabled) && !config.Security.AllowPluginBridgeOnPublicBind)
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
}

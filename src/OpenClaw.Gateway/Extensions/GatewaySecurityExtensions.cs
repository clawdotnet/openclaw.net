using System;
using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Gateway;
using OpenClaw.Payments.Core;

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

                if (!HasConfiguredSecret(config.Channels.WhatsApp.WebhookAppSecretRef, config.Channels.WhatsApp.WebhookAppSecret))
                {
                    throw new InvalidOperationException(
                        "Refusing to start with WhatsApp official webhooks on a non-loopback bind without a webhook app secret. " +
                        "Set OpenClaw:Channels:WhatsApp:WebhookAppSecretRef (recommended) or WebhookAppSecret.");
                }
            }
            else if (string.Equals(config.Channels.WhatsApp.Type, "bridge", StringComparison.OrdinalIgnoreCase))
            {
                if (!HasConfiguredSecret(config.Channels.WhatsApp.BridgeTokenRef, config.Channels.WhatsApp.BridgeToken))
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

            if (!HasConfiguredSecret(config.Channels.Sms.Twilio.AuthTokenRef))
            {
                throw new InvalidOperationException(
                    "Refusing to start with Twilio SMS webhooks on a non-loopback bind without AuthTokenRef. " +
                    "Set OpenClaw:Channels:Sms:Twilio:AuthTokenRef.");
            }
        }

        if (config.Channels.Telegram.Enabled && config.Channels.Telegram.UsesWebhook())
        {
            if (!config.Channels.Telegram.ValidateSignature)
            {
                throw new InvalidOperationException(
                    "Refusing to start with Telegram webhooks on a non-loopback bind without webhook secret validation. " +
                    "Set OpenClaw:Channels:Telegram:ValidateSignature=true.");
            }

            if (!HasConfiguredSecret(config.Channels.Telegram.WebhookSecretTokenRef, config.Channels.Telegram.WebhookSecretToken))
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

            if (!HasConfiguredSecret(config.Channels.Teams.AppIdRef, config.Channels.Teams.AppId) ||
                !HasConfiguredSecret(config.Channels.Teams.AppPasswordRef, config.Channels.Teams.AppPassword) ||
                !HasConfiguredSecret(config.Channels.Teams.TenantIdRef, config.Channels.Teams.TenantId))
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

            if (!HasConfiguredSecret(config.Channels.Slack.SigningSecretRef, config.Channels.Slack.SigningSecret))
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

            if (!HasConfiguredSecret(config.Channels.Discord.PublicKeyRef, config.Channels.Discord.PublicKey))
            {
                throw new InvalidOperationException(
                    "Refusing to start with Discord interactions on a non-loopback bind without a public key. " +
                    "Set OpenClaw:Channels:Discord:PublicKeyRef or PublicKey.");
            }
        }

        var inlineCredentialPaths = FindInlineCredentialPaths(config);
        if (inlineCredentialPaths.Count > 0)
        {
            var sample = string.Join(", ", inlineCredentialPaths.Take(3));
            var suffix = inlineCredentialPaths.Count > 3 ? ", ..." : "";
            throw new InvalidOperationException(
                "Refusing to start with inline credential values on a non-loopback bind. " +
                $"Detected in: {sample}{suffix}. Use env: or vault: references.");
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
        foreach (var (name, value) in root.Payments.StripeLink.EnvironmentVariables)
        {
            if (hits.Count >= 8)
                break;

            if (StripeLinkSecretEnvironmentName.IsSensitive(name) && SecretResolver.IsRawRef(value))
                hits.Add($"OpenClaw:Payments:StripeLink:EnvironmentVariables:{name}");
        }

        return hits;
    }

    private static IReadOnlyList<string> FindInlineCredentialPaths(GatewayConfig root)
    {
        var hits = new List<string>(capacity: 8);
        var json = JsonSerializer.SerializeToElement(root, CoreJsonContext.Default.GatewayConfig);
        VisitForInlineCredentials(json, "OpenClaw", hits);
        foreach (var (name, value) in root.Payments.StripeLink.EnvironmentVariables)
        {
            if (hits.Count >= 8)
                break;

            if (StripeLinkSecretEnvironmentName.IsSensitive(name) && IsInlineCredentialValue(value))
                hits.Add($"OpenClaw:Payments:StripeLink:EnvironmentVariables:{name}");
        }

        if ((!root.Channels.WhatsApp.Enabled ||
             !string.IsNullOrWhiteSpace(root.Channels.WhatsApp.WebhookVerifyTokenRef)) &&
            string.Equals(root.Channels.WhatsApp.WebhookVerifyToken, "openclaw-verify", StringComparison.Ordinal))
        {
            hits.RemoveAll(path => string.Equals(
                path,
                "OpenClaw:Channels:WhatsApp:WebhookVerifyToken",
                StringComparison.OrdinalIgnoreCase));
        }

        return hits;
    }

    private static void VisitForInlineCredentials(JsonElement value, string path, List<string> hits)
    {
        if (hits.Count >= 8)
            return;

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var text = value.GetString();
                if (IsInlineCredentialValue(text) &&
                    (IsWhatsAppProxyPath(path) ? HasProxyUriUserInfo(text) : LooksLikeCredentialPath(path)))
                {
                    hits.Add(FormatCredentialPath(path));
                }
                return;

            case JsonValueKind.Object:
                foreach (var prop in value.EnumerateObject())
                {
                    VisitForInlineCredentials(prop.Value, $"{path}:{prop.Name}", hits);
                    if (hits.Count >= 8)
                        return;
                }
                return;

            case JsonValueKind.Array:
                var idx = 0;
                foreach (var item in value.EnumerateArray())
                {
                    VisitForInlineCredentials(item, $"{path}[{idx++}]", hits);
                    if (hits.Count >= 8)
                        return;
                }
                return;
        }
    }

    private static void VisitForRawRefs(JsonElement value, string path, List<string> hits)
    {
        if (hits.Count >= 8)
            return;

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var s = value.GetString();
                if (!string.IsNullOrWhiteSpace(s) &&
                    SecretResolver.IsRawRef(s) &&
                    (LooksLikeCredentialPath(path) || IsWhatsAppProxyPath(path)))
                    hits.Add(FormatCredentialPath(path));
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

    internal static bool HasProxyUriUserInfo(string? value)
    {
        var proxyUrl = SecretResolver.IsRawRef(value) ? value![4..] : value;
        return Uri.TryCreate(proxyUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo);
    }

    private static bool IsWhatsAppProxyPath(string path)
        => path.EndsWith(":Channels:WhatsApp:FirstPartyWorker:Proxy", StringComparison.OrdinalIgnoreCase)
            || (path.Contains(":Channels:WhatsApp:FirstPartyWorker:Accounts[", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith(":Proxy", StringComparison.OrdinalIgnoreCase));

    private static bool IsInlineCredentialValue(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && !value.StartsWith("env:", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("vault:", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("raw:", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeCredentialPath(string path)
    {
        if (path.Equals("OpenClaw:AuthToken", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(":Pulse:AckToken", StringComparison.OrdinalIgnoreCase))
            return false;

        var separator = path.LastIndexOf(':');
        var propertyName = separator >= 0 ? path[(separator + 1)..] : path;
        if (propertyName.Equals("CredentialsPath", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("CredentialsPathRef", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("TokenEndpoint", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return propertyName.Contains("ApiKey", StringComparison.OrdinalIgnoreCase)
            || propertyName.Contains("Secret", StringComparison.OrdinalIgnoreCase)
            || propertyName.Contains("Token", StringComparison.OrdinalIgnoreCase)
            || propertyName.Contains("Password", StringComparison.OrdinalIgnoreCase)
            || propertyName.Contains("Credential", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("ConnectionString", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("SigningKey", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(":Channels:DingTalk:AppKey", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(":Channels:DingTalk:AppKeyRef", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatCredentialPath(string path)
    {
        var segments = path.Split(':');
        for (var index = 1; index < segments.Length; index++)
        {
            if (segments[index].Length > 0)
                segments[index] = char.ToUpperInvariant(segments[index][0]) + segments[index][1..];
        }

        return string.Join(':', segments);
    }

    private static bool HasConfiguredSecret(string? secretRef, string? legacyValue = null)
    {
        if (secretRef?.StartsWith("vault:", StringComparison.OrdinalIgnoreCase) == true)
            return true;

        return !string.IsNullOrWhiteSpace(SecretResolver.Resolve(secretRef) ?? legacyValue);
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

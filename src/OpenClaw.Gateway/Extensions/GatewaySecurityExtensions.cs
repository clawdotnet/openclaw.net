using System;
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

        var toolingUnsafe =
            config.Tooling.AllowShell ||
            config.Tooling.AllowedReadRoots.Contains("*", StringComparer.Ordinal) ||
            config.Tooling.AllowedWriteRoots.Contains("*", StringComparer.Ordinal);

        if (toolingUnsafe && !config.Security.AllowUnsafeToolingOnPublicBind)
        {
            throw new InvalidOperationException(
                "Refusing to start with unsafe tooling settings on a non-loopback bind. " +
                "Set OpenClaw:Tooling:AllowShell=false and restrict AllowedReadRoots/AllowedWriteRoots, " +
                "or explicitly opt in via OpenClaw:Security:AllowUnsafeToolingOnPublicBind=true.");
        }

        if (config.Plugins.Enabled && !config.Security.AllowPluginBridgeOnPublicBind)
        {
            throw new InvalidOperationException(
                "Refusing to start with the JS plugin bridge enabled on a non-loopback bind. " +
                "Disable OpenClaw:Plugins:Enabled, or explicitly opt in via OpenClaw:Security:AllowPluginBridgeOnPublicBind=true.");
        }

        if (!config.Security.AllowRawSecretRefsOnPublicBind)
        {
            if (SecretResolver.IsRawRef(config.Channels.Sms.Twilio.AuthTokenRef))
            {
                throw new InvalidOperationException(
                    "Refusing to start with a raw: secret ref on a non-loopback bind. " +
                    "Use env:... / OS keychain storage, or explicitly opt in via OpenClaw:Security:AllowRawSecretRefsOnPublicBind=true.");
            }
        }
    }
}

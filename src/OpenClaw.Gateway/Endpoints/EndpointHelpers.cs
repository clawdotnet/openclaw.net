using System.Text;
using Microsoft.AspNetCore.Http.Features;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Bootstrap;

namespace OpenClaw.Gateway.Endpoints;

internal static class EndpointHelpers
{
    internal sealed record OperatorAuthorizationResult(
        bool IsAuthorized,
        string AuthMode,
        bool UsedBrowserSession,
        BrowserSessionTicket? BrowserSession,
        string Role,
        string? AccountId,
        string? Username,
        string? DisplayName,
        bool IsBootstrapAdmin)
    {
        public OperatorIdentitySnapshot ToIdentity()
            => new()
            {
                AuthMode = AuthMode,
                Role = Role,
                AccountId = AccountId,
                Username = Username,
                DisplayName = DisplayName,
                IsBootstrapAdmin = IsBootstrapAdmin
            };
    }

    public static bool IsAuthorizedRequest(HttpContext ctx, GatewayConfig config, bool isNonLoopbackBind)
    {
        if (!isNonLoopbackBind)
            return true;

        if (string.IsNullOrWhiteSpace(config.AuthToken))
            return false;

        var token = GatewaySecurity.GetToken(ctx, config.Security.AllowQueryStringToken);
        return GatewaySecurity.IsTokenValid(token, config.AuthToken);
    }

    public static OperatorAuthorizationResult AuthorizeOperatorRequest(
        HttpContext ctx,
        GatewayStartupContext startup,
        BrowserSessionAuthService browserSessions,
        bool requireCsrf)
    {
        var organizationPolicy = ctx.RequestServices.GetService<OrganizationPolicyService>();
        var operatorAccounts = ctx.RequestServices.GetService<OperatorAccountService>();
        var policy = organizationPolicy?.GetSnapshot() ?? new OrganizationPolicySnapshot();

        if (!startup.IsNonLoopbackBind)
        {
            return new OperatorAuthorizationResult(
                true,
                "loopback-open",
                UsedBrowserSession: false,
                BrowserSession: null,
                Role: OperatorRoleNames.Admin,
                AccountId: null,
                Username: null,
                DisplayName: "Loopback operator",
                IsBootstrapAdmin: false);
        }

        var token = GatewaySecurity.GetToken(ctx, startup.Config.Security.AllowQueryStringToken);
        if (policy.BootstrapTokenEnabled &&
            IsAllowedAuthMode(policy, OrganizationAuthModeNames.BootstrapToken) &&
            !string.IsNullOrWhiteSpace(startup.Config.AuthToken) &&
            GatewaySecurity.IsTokenValid(token, startup.Config.AuthToken))
        {
            return new OperatorAuthorizationResult(
                true,
                "bearer",
                UsedBrowserSession: false,
                BrowserSession: null,
                Role: OperatorRoleNames.Admin,
                AccountId: null,
                Username: null,
                DisplayName: "Bootstrap admin",
                IsBootstrapAdmin: true);
        }

        if (IsAllowedAuthMode(policy, OrganizationAuthModeNames.AccountToken) &&
            !string.IsNullOrWhiteSpace(token) &&
            operatorAccounts is not null &&
            operatorAccounts.TryAuthenticateToken(token, out var accountIdentity))
        {
            return new OperatorAuthorizationResult(
                true,
                OrganizationAuthModeNames.AccountToken,
                UsedBrowserSession: false,
                BrowserSession: null,
                Role: accountIdentity!.Role,
                AccountId: accountIdentity.AccountId,
                Username: accountIdentity.Username,
                DisplayName: accountIdentity.DisplayName,
                IsBootstrapAdmin: false);
        }

        if (IsAllowedAuthMode(policy, OrganizationAuthModeNames.BrowserSession) &&
            browserSessions.TryAuthorize(ctx, requireCsrf, out var ticket))
        {
            return new OperatorAuthorizationResult(
                true,
                "browser-session",
                UsedBrowserSession: true,
                BrowserSession: ticket,
                Role: ticket!.Role,
                AccountId: ticket.AccountId,
                Username: ticket.Username,
                DisplayName: ticket.DisplayName,
                IsBootstrapAdmin: ticket.IsBootstrapAdmin);
        }

        return new OperatorAuthorizationResult(
            false,
            "unauthorized",
            UsedBrowserSession: false,
            BrowserSession: null,
            Role: OperatorRoleNames.Viewer,
            AccountId: null,
            Username: null,
            DisplayName: null,
            IsBootstrapAdmin: false);
    }

    public static bool TrySetMaxRequestBodySize(HttpContext ctx, long maxBytes)
    {
        var feature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = maxBytes;
            return true;
        }

        return false;
    }

    public static string GetHttpRateLimitKey(HttpContext ctx, GatewayConfig config)
    {
        var token = GatewaySecurity.GetToken(ctx, config.Security.AllowQueryStringToken);
        if (!string.IsNullOrWhiteSpace(token))
        {
            var bytes = Encoding.UTF8.GetBytes(token);
            var hash = System.Security.Cryptography.SHA256.HashData(bytes);
            return "token:" + Convert.ToHexString(hash.AsSpan(0, 8));
        }

        var ip = ctx.Connection.RemoteIpAddress?.ToString();
        return "ip:" + (string.IsNullOrWhiteSpace(ip) ? "unknown" : ip);
    }

    public static string GetRemoteIpKey(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString();
        return string.IsNullOrWhiteSpace(ip) ? "unknown" : ip;
    }

    public static string GetOperatorActorId(HttpContext ctx, OperatorAuthorizationResult auth)
    {
        if (!string.IsNullOrWhiteSpace(auth.AccountId))
            return $"account:{auth.AccountId}";

        return auth.AuthMode switch
        {
            "browser-session" when auth.BrowserSession is not null => $"browser:{auth.BrowserSession.SessionId}",
            OrganizationAuthModeNames.AccountToken => $"account-token:{GetRemoteIpKey(ctx)}",
            "bearer" => $"bearer:{GetRemoteIpKey(ctx)}",
            "loopback-open" => "loopback",
            _ => $"operator:{GetRemoteIpKey(ctx)}"
        };
    }

    public static bool TryConsumeOperatorRateLimit(
        HttpContext ctx,
        RuntimeOperationsState operations,
        OperatorAuthorizationResult auth,
        string endpointScope,
        out string? blockedByPolicyId)
    {
        blockedByPolicyId = null;
        if (!string.IsNullOrWhiteSpace(auth.AccountId))
        {
            if (!operations.ActorRateLimits.TryConsume("operator_account", auth.AccountId, endpointScope, out blockedByPolicyId))
                return false;
        }

        if (auth.UsedBrowserSession && auth.BrowserSession is not null)
        {
            if (!operations.ActorRateLimits.TryConsume("browser_session", auth.BrowserSession.SessionId, endpointScope, out blockedByPolicyId))
                return false;
        }

        return operations.ActorRateLimits.TryConsume("ip", GetRemoteIpKey(ctx), endpointScope, out blockedByPolicyId);
    }

    public static bool IsRoleAllowed(string grantedRole, string endpointScope, out string requiredRole)
    {
        requiredRole = GetRequiredRole(endpointScope);
        return OperatorRoleNames.CanAccess(grantedRole, requiredRole);
    }

    private static bool IsAllowedAuthMode(OrganizationPolicySnapshot policy, string authMode)
        => policy.AllowedAuthModes.Any(mode => string.Equals(mode, authMode, StringComparison.OrdinalIgnoreCase));

    private static string GetRequiredRole(string endpointScope)
    {
        if (string.IsNullOrWhiteSpace(endpointScope))
            return OperatorRoleNames.Viewer;

        var scope = endpointScope.Trim().ToLowerInvariant();

        if (scope.StartsWith("admin.operator-accounts", StringComparison.Ordinal) ||
            scope.StartsWith("admin.organization-policy", StringComparison.Ordinal) ||
            scope.StartsWith("admin.settings.mutate", StringComparison.Ordinal) ||
            scope.StartsWith("admin.setup.mutate", StringComparison.Ordinal) ||
            scope.StartsWith("admin.accounts.", StringComparison.Ordinal) ||
            scope.StartsWith("admin.backends", StringComparison.Ordinal) ||
            scope.StartsWith("admin.provider-policies", StringComparison.Ordinal) ||
            scope.StartsWith("admin.providers.reset", StringComparison.Ordinal) ||
            scope.StartsWith("admin.plugins.mutate", StringComparison.Ordinal) ||
            scope.StartsWith("admin.rate-limits.mutate", StringComparison.Ordinal) ||
            scope.StartsWith("integration.accounts", StringComparison.Ordinal))
        {
            return OperatorRoleNames.Admin;
        }

        if (scope.StartsWith("admin.memory.mutate", StringComparison.Ordinal) ||
            scope.StartsWith("admin.agent-bundle.mutate", StringComparison.Ordinal) ||
            scope.StartsWith("admin.profiles.mutate", StringComparison.Ordinal) ||
            scope.StartsWith("admin.learning.mutate", StringComparison.Ordinal) ||
            scope.StartsWith("admin.webhooks.mutate", StringComparison.Ordinal) ||
            scope.StartsWith("admin.automations.mutate", StringComparison.Ordinal) ||
            scope.StartsWith("admin.automations.run", StringComparison.Ordinal) ||
            scope.StartsWith("admin.automations.migrate", StringComparison.Ordinal) ||
            scope.StartsWith("admin.session.promote", StringComparison.Ordinal) ||
            scope.StartsWith("admin.branch.restore", StringComparison.Ordinal) ||
            scope.StartsWith("admin.session.metadata", StringComparison.Ordinal) ||
            scope.StartsWith("admin.approval-policies.mutate", StringComparison.Ordinal) ||
            scope.StartsWith("admin.heartbeat.mutate", StringComparison.Ordinal) ||
            scope.StartsWith("admin.channels.auth.mutate", StringComparison.Ordinal) ||
            scope.StartsWith("admin.channels.auth.restart", StringComparison.Ordinal) ||
            scope.StartsWith("admin.models.evaluate", StringComparison.Ordinal) ||
            scope.StartsWith("contract.mutate", StringComparison.Ordinal) ||
            scope.StartsWith("integration.mutate", StringComparison.Ordinal))
        {
            return OperatorRoleNames.Operator;
        }

        return OperatorRoleNames.Viewer;
    }

    public static async Task<(bool Success, string Text)> TryReadBodyTextAsync(HttpContext ctx, long maxBytes, CancellationToken ct)
    {
        var contentLength = ctx.Request.ContentLength;
        if (contentLength.HasValue && contentLength.Value > maxBytes)
            return (false, "");

        TrySetMaxRequestBodySize(ctx, maxBytes);

        var buffer = new byte[8 * 1024];
        await using var ms = new MemoryStream();
        while (true)
        {
            var read = await ctx.Request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read == 0)
                break;

            if (ms.Length + read > maxBytes)
                return (false, "");

            ms.Write(buffer, 0, read);
        }

        return (true, Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
    }

    public static ChannelAllowlistFile GetConfigAllowlist(GatewayConfig config, string channelId)
    {
        return channelId switch
        {
            "telegram" => new ChannelAllowlistFile { AllowedFrom = config.Channels.Telegram.AllowedFromUserIds },
            "whatsapp" => new ChannelAllowlistFile { AllowedFrom = config.Channels.WhatsApp.AllowedFromIds },
            "sms" => new ChannelAllowlistFile
            {
                AllowedFrom = config.Channels.Sms.Twilio.AllowedFromNumbers,
                AllowedTo = config.Channels.Sms.Twilio.AllowedToNumbers
            },
            "teams" => new ChannelAllowlistFile { AllowedFrom = config.Channels.Teams.AllowedFromIds },
            "slack" => new ChannelAllowlistFile { AllowedFrom = config.Channels.Slack.AllowedFromUserIds },
            "discord" => new ChannelAllowlistFile { AllowedFrom = config.Channels.Discord.AllowedFromUserIds },
            "signal" => new ChannelAllowlistFile { AllowedFrom = config.Channels.Signal.AllowedFromNumbers },
            _ => new ChannelAllowlistFile()
        };
    }

    public static string ResolveWorkspaceRoot(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        if (value.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
            return Environment.GetEnvironmentVariable(value[4..]) ?? "";

        return value;
    }

    public static string ToBoolWord(bool value) => value ? "yes" : "no";
}

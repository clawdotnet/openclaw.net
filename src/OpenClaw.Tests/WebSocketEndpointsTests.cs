using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Claims;
using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Endpoints;
using Xunit;

namespace OpenClaw.Tests;

public sealed class WebSocketEndpointsTests
{
    [Fact]
    public void ResolveCaller_WhenOidcPrincipal_ReturnsSubject()
    {
        var config = new GatewayConfig
        {
            AuthToken = "bootstrap-token"
        };
        config.Security.Oidc.Authority = "https://issuer.example";
        var startup = new GatewayStartupContext
        {
            Config = config,
            RuntimeState = RuntimeModeResolver.Resolve(config.Runtime, dynamicCodeSupported: true),
            IsNonLoopbackBind = true
        };

        var services = new ServiceCollection();
        services.AddSingleton(new BrowserSessionAuthService(config));

        var ctx = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "oidc-user-1"),
                new Claim("exp", DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture))
            ],
            authenticationType: "oidc"))
        };
        const string accessToken = "websocket-oidc-token-marker";
        ctx.Request.Headers.Authorization = $"Bearer {accessToken}";

        var authenticatedUserId = EndpointHelpers.ResolveCaller(ctx, startup).AccountId;
        var credentialContext = EndpointHelpers.ResolveMcpCallerCredentialContext(ctx, startup);

        Assert.Equal("oidc-user-1", authenticatedUserId);
        Assert.NotNull(credentialContext);
        Assert.Equal(accessToken, credentialContext.OidcAccessToken);
        Assert.Equal("oidc-user-1", credentialContext.Subject);
    }

    [Fact]
    public void ResolveMcpCallerCredentialContext_WhenPrincipalUnauthenticated_ReturnsNull()
    {
        var config = new GatewayConfig();
        config.Security.Oidc.Authority = "https://issuer.example";
        var startup = new GatewayStartupContext
        {
            Config = config,
            RuntimeState = RuntimeModeResolver.Resolve(config.Runtime, dynamicCodeSupported: true),
            IsNonLoopbackBind = true
        };
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity())
        };
        ctx.Request.Headers.Authorization = "Bearer static-gateway-token";

        var credentialContext = EndpointHelpers.ResolveMcpCallerCredentialContext(ctx, startup);

        Assert.Null(credentialContext);
    }

    [Fact]
    public void InboundMessage_JsonSerialization_DoesNotExposeCallerCredentialToken()
    {
        const string accessToken = "websocket-oidc-token-marker";
        var message = new InboundMessage
        {
            ChannelId = "websocket",
            SenderId = "client-1",
            Text = "hello",
            McpCallerCredentialContext = new McpCallerCredentialContext(
                accessToken,
                "oidc-user-1",
                DateTimeOffset.UtcNow.AddMinutes(5))
        };

        var json = JsonSerializer.Serialize(message, CoreJsonContext.Default.InboundMessage);

        Assert.DoesNotContain(accessToken, json, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveCaller_WhenBrowserSession_ReturnsAccountId()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-websocket-endpoint-tests", Guid.NewGuid().ToString("N"));
        var config = new GatewayConfig
        {
            AuthToken = "bootstrap-token"
        };
        var startup = new GatewayStartupContext
        {
            Config = config,
            RuntimeState = RuntimeModeResolver.Resolve(config.Runtime, dynamicCodeSupported: true),
            IsNonLoopbackBind = true
        };

        var browserSessions = new BrowserSessionAuthService(config);
        var ticket = browserSessions.Create(remember: false, new OperatorIdentitySnapshot
        {
            AuthMode = OrganizationAuthModeNames.BrowserSession,
            Role = OperatorRoleNames.Admin,
            AccountId = "acct-browser",
            Username = "browser-user",
            DisplayName = "Browser User"
        });

        var services = new ServiceCollection();
        services.AddSingleton(browserSessions);
        services.AddSingleton(new OperatorAccountService(storagePath, NullLogger<OperatorAccountService>.Instance));
        services.AddSingleton(new OrganizationPolicyService(storagePath, NullLogger<OrganizationPolicyService>.Instance));

        var ctx = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };
        ctx.Request.Headers.Cookie = $"{BrowserSessionAuthService.CookieName}={ticket.SessionId}";

        var authenticatedUserId = EndpointHelpers.ResolveCaller(ctx, startup).AccountId;

        Assert.Equal("acct-browser", authenticatedUserId);
    }

    [Fact]
    public void ResolveCaller_WhenLoopbackBindRequiresAuth_ReturnsAccountId()
    {
        // Loopback-bound but with AlwaysRequireAuth, so callers are authenticated accounts, not open loopback.
        // Losing the account here would exempt /ws turns from the session owner check.
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-websocket-endpoint-tests", Guid.NewGuid().ToString("N"));
        var config = new GatewayConfig { AuthToken = "bootstrap-token" };
        config.Security.AlwaysRequireAuth = true;
        var startup = new GatewayStartupContext
        {
            Config = config,
            RuntimeState = RuntimeModeResolver.Resolve(config.Runtime, dynamicCodeSupported: true),
            IsNonLoopbackBind = false
        };

        var operatorAccounts = new OperatorAccountService(storagePath, NullLogger<OperatorAccountService>.Instance);
        var created = operatorAccounts.Create(new OperatorAccountCreateRequest
        {
            Username = "loopback-user",
            Password = "P@ssw0rd123!",
            Role = OperatorRoleNames.Operator
        });
        var token = operatorAccounts.CreateToken(created.Id, new OperatorAccountTokenCreateRequest { Label = "ws-loopback" });

        var services = new ServiceCollection();
        services.AddSingleton(new BrowserSessionAuthService(config));
        services.AddSingleton(operatorAccounts);
        services.AddSingleton(new OrganizationPolicyService(storagePath, NullLogger<OrganizationPolicyService>.Instance));

        var ctx = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        ctx.Request.Headers.Authorization = $"Bearer {token!.Token}";

        var authenticatedUserId = EndpointHelpers.ResolveCaller(ctx, startup).AccountId;

        Assert.Equal(created.Id, authenticatedUserId);
    }

    [Fact]
    public void ResolveCaller_WhenAccountToken_ReturnsAccountId()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-websocket-endpoint-tests", Guid.NewGuid().ToString("N"));
        var config = new GatewayConfig
        {
            AuthToken = "bootstrap-token"
        };
        var startup = new GatewayStartupContext
        {
            Config = config,
            RuntimeState = RuntimeModeResolver.Resolve(config.Runtime, dynamicCodeSupported: true),
            IsNonLoopbackBind = true
        };

        var operatorAccounts = new OperatorAccountService(storagePath, NullLogger<OperatorAccountService>.Instance);
        var created = operatorAccounts.Create(new OperatorAccountCreateRequest
        {
            Username = "token-user",
            Password = "P@ssw0rd123!",
            DisplayName = "Token User",
            Role = OperatorRoleNames.Admin,
            Enabled = true
        });
        var token = operatorAccounts.CreateToken(created.Id, new OperatorAccountTokenCreateRequest
        {
            Label = "ws-test"
        });

        var services = new ServiceCollection();
        services.AddSingleton(new BrowserSessionAuthService(config));
        services.AddSingleton(operatorAccounts);
        services.AddSingleton(new OrganizationPolicyService(storagePath, NullLogger<OrganizationPolicyService>.Instance));

        var ctx = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };
        ctx.Request.Headers.Authorization = $"Bearer {token!.Token}";

        var authenticatedUserId = EndpointHelpers.ResolveCaller(ctx, startup).AccountId;

        Assert.Equal(created.Id, authenticatedUserId);
    }
}
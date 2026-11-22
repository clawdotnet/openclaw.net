using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;
using System.Security.Claims;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Endpoints;
using Xunit;

namespace OpenClaw.Tests;

public sealed class EndpointHelpersAuthenticationTests : IDisposable
{
    private readonly string _storagePath = Path.Combine(Path.GetTempPath(), "openclaw-endpoint-auth-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_storagePath, recursive: true); }
        catch { }
    }

    [Fact]
    public void AccountToken_WhenCheckedTwiceInOneRequest_ShouldBeVerifiedOnce()
    {
        var (startup, services, accounts, accountId, token) = CreateOperatorToken();
        var ctx = CreateRequest(services, token.Token);

        Assert.True(EndpointHelpers.IsAuthorizedRequest(ctx, startup.Config, startup.IsNonLoopbackBind));

        // Revoking after the first check makes a second verification observable: a re-run of the slow
        // token check would now fail, while a reused result for this request still succeeds.
        Assert.True(accounts.RevokeToken(accountId, token.TokenInfo!.Id));
        var auth = EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, services.GetRequiredService<BrowserSessionAuthService>(), requireCsrf: false);

        Assert.True(auth.IsAuthorized);
        Assert.Equal(accountId, auth.AccountId);
    }

    [Fact]
    public void AccountToken_WhenRevokedBeforeNextRequest_ShouldBeRejected()
    {
        var (startup, services, accounts, accountId, token) = CreateOperatorToken();
        Assert.True(EndpointHelpers.IsAuthorizedRequest(CreateRequest(services, token.Token), startup.Config, startup.IsNonLoopbackBind));

        Assert.True(accounts.RevokeToken(accountId, token.TokenInfo!.Id));

        Assert.False(EndpointHelpers.IsAuthorizedRequest(CreateRequest(services, token.Token), startup.Config, startup.IsNonLoopbackBind));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolveMcpCallerCredentialContext_WhenValidatedOidcBearer_ShouldPreserveTokenAndClaims(bool mappedClaims)
    {
        const string accessToken = "oidc-access-token-marker";
        const string subject = "oidc-subject-42";
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var (startup, context) = CreateOidcRequest(
            authenticated: true,
            authorization: $"Bearer {accessToken}",
            subject,
            expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            mappedClaims: mappedClaims);

        var caller = EndpointHelpers.ResolveMcpCallerCredentialContext(context, startup);

        Assert.NotNull(caller);
        Assert.Equal(accessToken, caller.OidcAccessToken);
        Assert.Equal(subject, caller.Subject);
        Assert.Equal(expiresAt.ToUnixTimeSeconds(), caller.ExpiresAtUtc.ToUnixTimeSeconds());
    }

    [Theory]
    [InlineData("authority_missing")]
    [InlineData("unauthenticated")]
    [InlineData("no_bearer")]
    [InlineData("non_bearer")]
    [InlineData("missing_subject")]
    [InlineData("missing_exp")]
    [InlineData("expired_exp")]
    [InlineData("invalid_exp")]
    [InlineData("static_gateway_token")]
    [InlineData("account_token")]
    [InlineData("browser_session")]
    public void ResolveMcpCallerCredentialContext_WhenNotValidatedOidcBearer_ShouldReturnNull(string scenario)
    {
        var authenticated = scenario is not "unauthenticated" and not "static_gateway_token" and not "account_token" and not "browser_session";
        var authorization = scenario switch
        {
            "no_bearer" or "browser_session" => null,
            "non_bearer" => "Basic dXNlcjpwYXNz",
            "static_gateway_token" => "Bearer static-gateway-token",
            "account_token" => "Bearer account-token",
            _ => "Bearer oidc-access-token-marker"
        };
        var subject = scenario == "missing_subject" ? null : "oidc-subject-42";
        var expiresAt = scenario switch
        {
            "missing_exp" => null,
            "expired_exp" => DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            "invalid_exp" => "not-a-timestamp",
            _ => DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
        };
        var (startup, context) = CreateOidcRequest(
            authenticated,
            authorization,
            subject,
            expiresAt,
            configureAuthority: scenario != "authority_missing");

        var caller = EndpointHelpers.ResolveMcpCallerCredentialContext(context, startup);

        Assert.Null(caller);
    }

    private (GatewayStartupContext Startup, ServiceProvider Services, OperatorAccountService Accounts, string AccountId, OperatorAccountTokenCreateResponse Token) CreateOperatorToken()
    {
        var config = new GatewayConfig { AuthToken = "bootstrap-token" };
        var startup = new GatewayStartupContext
        {
            Config = config,
            RuntimeState = RuntimeModeResolver.Resolve(config.Runtime, dynamicCodeSupported: true),
            IsNonLoopbackBind = true
        };
        var accounts = new OperatorAccountService(_storagePath, NullLogger<OperatorAccountService>.Instance);
        var account = accounts.Create(new OperatorAccountCreateRequest
        {
            Username = "memo-operator",
            Password = "P@ssw0rd123!",
            Role = OperatorRoleNames.Operator
        });
        var token = accounts.CreateToken(account.Id, new OperatorAccountTokenCreateRequest { Label = "memo" })!;

        var services = new ServiceCollection();
        services.AddSingleton(new BrowserSessionAuthService(config));
        services.AddSingleton(accounts);
        services.AddSingleton(new OrganizationPolicyService(_storagePath, NullLogger<OrganizationPolicyService>.Instance));
        return (startup, services.BuildServiceProvider(), accounts, account.Id, token);
    }

    private static DefaultHttpContext CreateRequest(IServiceProvider services, string token)
    {
        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Request.Headers.Authorization = $"Bearer {token}";
        return ctx;
    }

    private static (GatewayStartupContext Startup, DefaultHttpContext Context) CreateOidcRequest(
        bool authenticated,
        string? authorization,
        string? subject,
        string? expiresAt,
        bool configureAuthority = true,
        bool mappedClaims = false)
    {
        var config = new GatewayConfig();
        if (configureAuthority)
            config.Security.Oidc.Authority = "https://issuer.example";

        var startup = new GatewayStartupContext
        {
            Config = config,
            RuntimeState = RuntimeModeResolver.Resolve(config.Runtime, dynamicCodeSupported: true),
            IsNonLoopbackBind = true
        };
        var claims = new List<Claim>();
        if (subject is not null)
            claims.Add(new Claim(mappedClaims ? ClaimTypes.NameIdentifier : "sub", subject));
        if (expiresAt is not null)
            claims.Add(new Claim(mappedClaims ? ClaimTypes.Expiration : "exp", expiresAt));

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticated ? "oidc" : null))
        };
        if (authorization is not null)
            context.Request.Headers.Authorization = authorization;
        return (startup, context);
    }

    [Fact]
    public void AuthorizeOperatorRequest_WhenTokenCarriesMultipleRoles_ShouldResolveHighest()
    {
        // End-to-end guard for the silent-downgrade bug: goes through the real
        // claim-reading path rather than calling OperatorRoleNames directly, so
        // reverting FindFirstValue back into EndpointHelpers fails this test.
        var (startup, context) = CreateMultiRoleOidcRequest(
            roleClaim: "realm_roles",
            roles: ["app_user", OperatorRoleNames.Operator, OperatorRoleNames.Admin],
            storagePath: _storagePath);

        var auth = EndpointHelpers.AuthorizeOperatorRequest(
            context, startup,
            context.RequestServices.GetRequiredService<BrowserSessionAuthService>(), requireCsrf: false);

        Assert.True(auth.IsAuthorized);
        Assert.Equal(OperatorRoleNames.Admin, auth.Role);
        Assert.True(OperatorRoleNames.CanAccess(auth.Role, OperatorRoleNames.Admin));
    }

    [Fact]
    public void AuthorizeOperatorRequest_WhenAdminIsNotFirstClaim_ShouldStillResolveAdmin()
    {
        // The original bug: admin was listed second and never reached.
        // Unknown names (like Keycloak realm roles "app_admin") are ignored by
        // design — the IdP must map them onto viewer/operator/admin through a
        // dedicated single-valued claim. See docs/KEYCLOAK.md.
        var (startup, context) = CreateMultiRoleOidcRequest(
            roleClaim: "roles",
            roles: [OperatorRoleNames.Viewer, OperatorRoleNames.Admin],
            storagePath: _storagePath);

        var auth = EndpointHelpers.AuthorizeOperatorRequest(
            context, startup,
            context.RequestServices.GetRequiredService<BrowserSessionAuthService>(), requireCsrf: false);

        Assert.Equal(OperatorRoleNames.Admin, auth.Role);
    }

    [Fact]
    public void AuthorizeOperatorRequest_WhenOnlyUnknownRoles_ShouldStayViewer()
    {
        var (startup, context) = CreateMultiRoleOidcRequest(
            roleClaim: "realm_roles",
            roles: ["app_user", "some_other_role"],
            storagePath: _storagePath);

        var auth = EndpointHelpers.AuthorizeOperatorRequest(
            context, startup,
            context.RequestServices.GetRequiredService<BrowserSessionAuthService>(), requireCsrf: false);

        // Fail closed: unknown role names must not grant operator/admin.
        Assert.Equal(OperatorRoleNames.Viewer, auth.Role);
        Assert.False(OperatorRoleNames.CanAccess(auth.Role, OperatorRoleNames.Operator));
    }

    [Fact]
    public void AuthorizeOperatorRequest_WhenRoleClaimIsMissing_ShouldFallBackToViewer()
    {
        var (startup, context) = CreateMultiRoleOidcRequest(
            roleClaim: "realm_roles",
            roles: [],
            storagePath: _storagePath);

        var auth = EndpointHelpers.AuthorizeOperatorRequest(
            context, startup,
            context.RequestServices.GetRequiredService<BrowserSessionAuthService>(), requireCsrf: false);

        Assert.Equal(OperatorRoleNames.Viewer, auth.Role);
    }

    [Fact]
    public void AuthorizeOperatorRequest_WhenStandardRoleClaimIsUsed_ShouldResolveHighest()
    {
        // Fallback branch: no custom RoleClaim values, but ClaimTypes.Role present.
        var (startup, context) = CreateMultiRoleOidcRequest(
            roleClaim: "custom_role",
            roles: [],
            standardRoles: [OperatorRoleNames.Operator, OperatorRoleNames.Admin],
            storagePath: _storagePath);

        var auth = EndpointHelpers.AuthorizeOperatorRequest(
            context, startup,
            context.RequestServices.GetRequiredService<BrowserSessionAuthService>(), requireCsrf: false);

        Assert.Equal(OperatorRoleNames.Admin, auth.Role);
    }

    private static (GatewayStartupContext Startup, DefaultHttpContext Context) CreateMultiRoleOidcRequest(
        string roleClaim,
        string[] roles,
        string[]? standardRoles = null,
        string storagePath = "")
    {
        var config = new GatewayConfig();
        config.Security.AuthMode = SecurityAuthModeNames.Oidc;
        config.Security.Oidc.Authority = "https://issuer.example";
        config.Security.Oidc.RoleClaim = roleClaim;

        var startup = new GatewayStartupContext
        {
            Config = config,
            RuntimeState = RuntimeModeResolver.Resolve(config.Runtime, dynamicCodeSupported: true),
            IsNonLoopbackBind = true
        };

        var claims = new List<Claim> { new("sub", "multi-role-subject") };
        // One claim per array element — this is what the JWT handler produces for
        // a JSON array claim, and what made FindFirstValue order-dependent.
        claims.AddRange(roles.Select(r => new Claim(roleClaim, r)));
        if (standardRoles is not null)
            claims.AddRange(standardRoles.Select(r => new Claim(ClaimTypes.Role, r)));

        // AuthorizeOperatorRequest reaches for OrganizationPolicyService and
        // OperatorAccountService through ctx.RequestServices, so the context needs
        // a real container.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(config);
        services.AddSingleton(new OperatorAccountService(
            string.IsNullOrEmpty(storagePath) ? Path.GetTempPath() : storagePath,
            NullLogger<OperatorAccountService>.Instance));
        services.AddSingleton(new OrganizationPolicyService(
            string.IsNullOrEmpty(storagePath) ? Path.GetTempPath() : storagePath,
            NullLogger<OrganizationPolicyService>.Instance));
        services.AddSingleton(new BrowserSessionAuthService(config));

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "oidc")),
            RequestServices = services.BuildServiceProvider()
        };
        return (startup, context);
    }
}

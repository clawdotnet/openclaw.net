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
}

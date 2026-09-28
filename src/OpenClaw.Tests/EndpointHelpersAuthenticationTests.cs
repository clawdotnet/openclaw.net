using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Models;
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
}

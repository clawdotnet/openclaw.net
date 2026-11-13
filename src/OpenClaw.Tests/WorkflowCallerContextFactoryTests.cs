using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;
using OpenClaw.Gateway.Workflows;
using Xunit;

namespace OpenClaw.Tests;

public sealed class WorkflowCallerContextFactoryTests
{
    private const string Issuer = "https://identity.example.test";
    private static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes("workflow-tests-signing-key-32-bytes-minimum-only-for-tests"));

    [Fact]
    public async Task Create_WhenHeaderReplacedAfterValidation_ShouldUseValidatedAssertion()
    {
        var token = IssueToken(new() { ["sub"] = "user", ["scp"] = "workflow.read" });
        var result = await InvokeAsync(token, "entra-delegated", ctx => ctx.Request.Headers.Authorization = "Bearer different-token");
        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.Equal(token, result.Caller!.Assertion);
        Assert.Equal(Issuer, result.Caller.Issuer);
        Assert.Equal("user", result.Caller.Subject);
    }

    [Fact]
    public async Task Create_WhenPrincipalReplaced_ShouldRejectStaleFeature()
    {
        var token = IssueToken(new() { ["sub"] = "user", ["scp"] = "read" });
        var result = await InvokeAsync(token, "entra-delegated",
            ctx => ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "other")], "other-scheme")));
        Assert.Equal(HttpStatusCode.Unauthorized, result.Status);
        Assert.Null(result.Caller);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("read", "app")]
    public async Task Create_WhenEntraDelegationProofMissing_ShouldReject(string? scp, string? identityType)
    {
        var claims = new Dictionary<string, object> { ["sub"] = "user", ["roles"] = new[] { "admin" } };
        if (scp is not null) claims["scp"] = scp;
        if (identityType is not null) claims["idtyp"] = identityType;
        var result = await InvokeAsync(IssueToken(claims), "entra-delegated");
        Assert.Equal(HttpStatusCode.Unauthorized, result.Status);
        Assert.Equal("workflow_user_auth_required", result.Error);
    }

    [Fact]
    public async Task Create_WhenRequiredClaimsMatchOriginalNames_ShouldAcceptWithoutEntraScopes()
    {
        var result = await InvokeAsync(IssueToken(new()
        {
            ["sub"] = "user", ["typ"] = "Bearer", ["actor_kind"] = "user"
        }), "required-claims");
        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.Equal("user", result.Caller!.Subject);
    }

    [Theory]
    [InlineData("ID", "user")]
    [InlineData("Bearer", "service")]
    [InlineData("bearer", "user")]
    [InlineData("Bearer", null)]
    public async Task Create_WhenRequiredClaimsDoNotMatch_ShouldReject(string type, string? actor)
    {
        var claims = new Dictionary<string, object> { ["sub"] = "user", ["typ"] = type };
        if (actor is not null) claims["actor_kind"] = actor;
        var result = await InvokeAsync(IssueToken(claims), "required-claims");
        Assert.Equal(HttpStatusCode.Unauthorized, result.Status);
    }

    [Theory]
    [InlineData("sub")]
    [InlineData("scp")]
    [InlineData("actor_kind")]
    public async Task Create_WhenCriticalClaimAmbiguous_ShouldReject(string claim)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = "user", ["scp"] = "read", ["typ"] = "Bearer", ["actor_kind"] = "user"
        };
        claims[claim] = new[] { "user", "other" };
        var result = await InvokeAsync(IssueToken(claims), claim == "actor_kind" ? "required-claims" : "entra-delegated");
        Assert.Equal(HttpStatusCode.Unauthorized, result.Status);
    }

    [Theory]
    [InlineData("https://other-issuer.example", "gateway-api", false)]
    [InlineData(Issuer, "wrong-audience", false)]
    [InlineData(Issuer, "gateway-api", true)]
    public async Task Create_WhenAuthenticationInvalid_ShouldNotCreateContext(string issuer, string audience, bool expired)
    {
        var result = await InvokeAsync(IssueToken(new() { ["sub"] = "user", ["scp"] = "read" }, issuer, audience, expired), "entra-delegated");
        Assert.Equal(HttpStatusCode.Unauthorized, result.Status);
        Assert.Null(result.Caller);
    }

    [Fact]
    public void Create_WhenNoHttpContext_ShouldRejectOnlyDelegatedBackend()
    {
        var config = CreateConfig("entra-delegated");
        config.Workflows.Backends["static"] = new() { BaseUrl = "https://workflow.example" };
        var factory = new WorkflowCallerContextFactory(config, TimeProvider.System);
        Assert.Null(factory.Create(null, "static"));
        var error = Assert.Throws<WorkflowDelegationException>(() => factory.Create(null, "review"));
        Assert.Equal(401, error.StatusCode);
        Assert.Throws<KeyNotFoundException>(() => factory.Create(null, "unknown"));
    }

    [Fact]
    public void Create_WhenPrincipalHasNoValidatedFeature_ShouldReject()
    {
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user"), new Claim("scp", "read")], "Bearer"))
        };
        var factory = new WorkflowCallerContextFactory(CreateConfig("entra-delegated"), TimeProvider.System);
        Assert.Throws<WorkflowDelegationException>(() => factory.Create(ctx, "review"));
    }

    [Fact]
    public async Task Create_WhenClockReachesAssertionExpiry_ShouldReject()
    {
        var token = IssueToken(new() { ["sub"] = "user", ["scp"] = "read" });
        var expiry = new DateTimeOffset(new JsonWebToken(token).ValidTo, TimeSpan.Zero);
        var result = await InvokeAsync(token, "entra-delegated", clock: new FixedTimeProvider(expiry));
        Assert.Equal(HttpStatusCode.Unauthorized, result.Status);
    }

    [Fact]
    public async Task Create_WhenOriginalClaimNameMapped_ShouldNotAcceptAlias()
    {
        var result = await InvokeAsync(IssueToken(new()
        {
            ["sub"] = "user", ["typ"] = "Bearer", ["actor_kind"] = "user"
        }), "required-claims", aliasPolicy: true);
        Assert.Equal(HttpStatusCode.Unauthorized, result.Status);
    }

    [Fact]
    public async Task Create_WhenPreviousValidationCallbackFails_ShouldNotCapture()
    {
        var result = await InvokeAsync(IssueToken(new() { ["sub"] = "user", ["scp"] = "read" }),
            "entra-delegated", rejectCallback: true);
        Assert.Equal(HttpStatusCode.Unauthorized, result.Status);
    }

    private static string IssueToken(Dictionary<string, object> claims, string issuer = Issuer, string audience = "gateway-api", bool expired = false)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer, Audience = audience, Claims = claims,
            NotBefore = now.AddMinutes(-10), Expires = expired ? now.AddMinutes(-1) : now.AddMinutes(5),
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.HmacSha256)
        });
    }

    private static GatewayConfig CreateConfig(string profile) => new()
    {
        Security = new SecurityConfig { Oidc = new OidcConfig { Authority = Issuer, Audience = "gateway-api" } },
        Workflows = new WorkflowsConfig
        {
            Enabled = true,
            Backends =
            {
                ["review"] = new()
                {
                    BaseUrl = "https://workflow.example",
                    Authentication = new()
                    {
                        Mode = "token-exchange", UserAssertionProfile = profile,
                        AccessTokenClaims = profile == "required-claims" ? new() { ["typ"] = "Bearer" } : [],
                        UserDelegationClaims = profile == "required-claims" ? new() { ["actor_kind"] = "user" } : []
                    }
                }
            }
        }
    };

    private static async Task<(HttpStatusCode Status, WorkflowExecutionContext? Caller, string? Error)> InvokeAsync(
        string token, string profile, Action<HttpContext>? afterAuthentication = null,
        TimeProvider? clock = null, bool aliasPolicy = false, bool rejectCallback = false)
    {
        var config = CreateConfig(profile);
        if (aliasPolicy)
            config.Workflows.Backends["review"].Authentication.AccessTokenClaims = new() { [ClaimTypes.NameIdentifier] = "user" };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddOpenClawSecurityServices(new GatewayStartupContext
        {
            Config = config,
            IsNonLoopbackBind = false,
            RuntimeState = new GatewayRuntimeState { RequestedMode = "jit", EffectiveMode = GatewayRuntimeMode.Jit, DynamicCodeSupported = true }
        });
        builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.Configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
            options.Configuration.SigningKeys.Add(Key);
            options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
            if (rejectCallback)
                options.Events.OnTokenValidated = ctx =>
                {
                    ctx.Fail("test callback denied authentication");
                    return Task.CompletedTask;
                };
        });
        if (clock is not null)
            builder.Services.AddSingleton(clock);
        await using var app = builder.Build();
        app.UseAuthentication();
        WorkflowExecutionContext? caller = null;
        string? error = null;
        app.MapGet("/test", (HttpContext ctx, WorkflowCallerContextFactory factory) =>
        {
            afterAuthentication?.Invoke(ctx);
            try
            {
                caller = factory.Create(ctx, "review");
                return Results.Ok();
            }
            catch (WorkflowDelegationException failure)
            {
                error = failure.ErrorCode;
                return Results.StatusCode(failure.StatusCode);
            }
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        using var response = await client.GetAsync("/test", TestContext.Current.CancellationToken);
        return (response.StatusCode, caller, error);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

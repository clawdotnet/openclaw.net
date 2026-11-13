using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;
using OpenClaw.Gateway.Workflows;
using Xunit;

namespace OpenClaw.Tests;

public sealed partial class GatewayAdminEndpointTests
{
    [Theory]
    [InlineData("entra-obo")]
    [InlineData("token-exchange")]
    public async Task WorkflowDelegation_WhenHttpAuthenticated_ShouldDelegateEveryOperation(string mode)
    {
        await using var server = await WorkflowDelegationTestServer.StartAsync(TestContext.Current.CancellationToken);
        await using var harness = await CreateWorkflowHarnessAsync(server, mode);
        harness.Client.DefaultRequestHeaders.Authorization = new("Bearer", server.IssueUserToken("user-a", true));
        using var started = await harness.Client.PostAsync("/api/integration/workflows/review/runs",
            new StringContent("""{"input":"review","senderId":"forged"}""", Encoding.UTF8, "application/json"));
        Assert.True(started.StatusCode == HttpStatusCode.Accepted,
            $"Start status {(int)started.StatusCode}; exchanges={server.ExchangeRequests.Count}; backend={server.BackendRequests.Count}; {await started.Content.ReadAsStringAsync()}");
        harness.Client.DefaultRequestHeaders.Authorization = new("Bearer", server.IssueUserToken("user-b", true));
        using var fetched = await harness.Client.GetAsync("/api/integration/workflows/review/runs/run-1");
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        using var responded = await harness.Client.PostAsync("/api/integration/workflows/review/runs/run-1/responses",
            new StringContent("""{"portId":"approval","actorId":"forged","approved":true}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, responded.StatusCode);
        Assert.Collection(server.BackendRequests,
            call =>
            {
                Assert.Equal("Bearer delegated-user-a", call.Authorization);
                Assert.Equal("user-a", call.StartRequest!.SenderId);
            },
            call => Assert.Equal("Bearer delegated-user-b", call.Authorization),
            call =>
            {
                Assert.Equal("Bearer delegated-user-b", call.Authorization);
                Assert.Equal("user-b", call.Response!.ActorId);
            });
    }

    [Theory]
    [InlineData(false, null, 401, "workflow_user_auth_required")]
    [InlineData(true, "access_denied", 403, "workflow_delegation_denied")]
    [InlineData(true, "invalid_client", 502, "workflow_token_exchange_failed")]
    public async Task WorkflowDelegation_WhenDenied_ShouldNotCallBackend(bool eligible, string? providerError, int status, string code)
    {
        await using var server = await WorkflowDelegationTestServer.StartAsync(TestContext.Current.CancellationToken);
        server.OAuthError = providerError;
        await using var harness = await CreateWorkflowHarnessAsync(server, "entra-obo");
        harness.Client.DefaultRequestHeaders.Authorization = new("Bearer", server.IssueUserToken("user", eligible));
        using var response = await harness.Client.GetAsync("/api/integration/workflows/review/runs/run-1");
        Assert.Equal(status, (int)response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var parsed = JsonDocument.Parse(body);
        Assert.Equal(code, parsed.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain("private-provider-error", body, StringComparison.Ordinal);
        Assert.Empty(server.BackendRequests);
        if (!eligible) Assert.Empty(server.ExchangeRequests);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task WorkflowDelegation_WhenBackendRejects_ShouldPreserveStatus(int status)
    {
        await using var server = await WorkflowDelegationTestServer.StartAsync(TestContext.Current.CancellationToken);
        server.BackendStatusCode = status;
        await using var harness = await CreateWorkflowHarnessAsync(server, "entra-obo");
        harness.Client.DefaultRequestHeaders.Authorization = new("Bearer", server.IssueUserToken("user", true));
        using var response = await harness.Client.GetAsync("/api/integration/workflows/review/runs/run-1");
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Contains("workflow_backend_auth_denied", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Single(server.BackendRequests);
    }

    [Fact]
    public async Task WorkflowDelegation_WhenBootstrapTokenUsed_ShouldRejectWithoutExchange()
    {
        await using var server = await WorkflowDelegationTestServer.StartAsync(TestContext.Current.CancellationToken);
        await using var harness = await CreateWorkflowHarnessAsync(server, "entra-obo");
        harness.Client.DefaultRequestHeaders.Authorization = new("Bearer", "test-admin-token");
        using var response = await harness.Client.GetAsync("/api/integration/workflows/review/runs/run-1");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(server.ExchangeRequests);
        Assert.Empty(server.BackendRequests);
    }

    [Theory]
    [InlineData("unknown", """{"input":"review"}""", 404)]
    [InlineData("review", "{broken-json", 400)]
    public async Task WorkflowDelegation_WhenRequestInvalid_ShouldPreserveValidation(string backend, string json, int expected)
    {
        await using var server = await WorkflowDelegationTestServer.StartAsync(TestContext.Current.CancellationToken);
        await using var harness = await CreateWorkflowHarnessAsync(server, "entra-obo");
        harness.Client.DefaultRequestHeaders.Authorization = new("Bearer", server.IssueUserToken("user", true));
        using var response = await harness.Client.PostAsync($"/api/integration/workflows/{backend}/runs",
            new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Empty(server.ExchangeRequests);
        Assert.Empty(server.BackendRequests);
    }

    [Fact]
    public async Task WorkflowDelegation_WhenStaticBackend_ShouldNotRequireUserAdmission()
    {
        await using var server = await WorkflowDelegationTestServer.StartAsync(TestContext.Current.CancellationToken);
        await using var harness = await CreateWorkflowHarnessAsync(server, "static");
        harness.Client.DefaultRequestHeaders.Authorization = new("Bearer", server.IssueUserToken("service", false));
        using var response = await harness.Client.PostAsync("/api/integration/workflows/review/runs",
            new StringContent("""{"input":"review","senderId":"original"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(server.ExchangeRequests);
        var call = Assert.Single(server.BackendRequests);
        Assert.Equal("original", call.StartRequest!.SenderId);
        Assert.Empty(call.Authorization);
    }

    [Fact]
    public async Task WorkflowDelegation_WhenProviderTokenMalformed_ShouldNotCacheOrDispatch()
    {
        await using var server = await WorkflowDelegationTestServer.StartAsync(TestContext.Current.CancellationToken);
        server.TokenResponseBody = """{"access_token":"bad\r\ntoken","token_type":"Bearer","expires_in":300}""";
        await using var harness = await CreateWorkflowHarnessAsync(server, "entra-obo");
        harness.Client.DefaultRequestHeaders.Authorization = new("Bearer", server.IssueUserToken("user", true));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await harness.Client.GetAsync("/api/integration/workflows/review/runs/run-1");
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("workflow_token_exchange_failed", body.RootElement.GetProperty("error").GetString());
        }
        Assert.Equal(2, server.ExchangeRequests.Count);
        Assert.Empty(server.BackendRequests);
        server.TokenResponseBody = null;
        using var recovered = await harness.Client.GetAsync("/api/integration/workflows/review/runs/run-1");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(3, server.ExchangeRequests.Count);
        Assert.Single(server.BackendRequests);
    }

    private static Task<GatewayTestHarness> CreateWorkflowHarnessAsync(WorkflowDelegationTestServer server, string mode, bool legacySession = false)
        => CreateHarnessAsync(true, config =>
        {
            config.Security.Oidc.Authority = server.Authority.ToString();
            config.Security.Oidc.Audience = "gateway-api";
            config.McpCompatibility.ForceLegacyInitialize = legacySession;
            config.Workflows.Enabled = true;
            config.Workflows.Backends["review"] = new()
            {
                BaseUrl = server.BackendBaseUrl.ToString(),
                Authentication = mode == "static" ? new() : new()
                {
                    Mode = mode, TokenEndpoint = server.TokenEndpoint.ToString(),
                    ClientId = "client", ClientSecretRef = "raw:test-fixture-secret", Scopes = ["workflow.read"],
                    UserAssertionProfile = "entra-delegated"
                }
            };
        }, configureServices: (services, config) =>
        {
            services.AddOpenClawSecurityServices(new GatewayStartupContext
            {
                Config = config, IsNonLoopbackBind = true,
                RuntimeState = new() { RequestedMode = "jit", EffectiveMode = GatewayRuntimeMode.Jit, DynamicCodeSupported = true }
            });
            services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.Configuration = new OpenIdConnectConfiguration { Issuer = server.Authority.ToString() };
                options.Configuration.SigningKeys.Add(server.SigningKey);
            });
            services.AddHttpClient("workflow-token-exchange").ConfigurePrimaryHttpMessageHandler(server.CreateTrustedHandler);
            services.AddHttpClient("workflow-backend").ConfigurePrimaryHttpMessageHandler(server.CreateTrustedHandler);
            services.AddSingleton<AgentWorkflowRegistry>();
        }, configureBeforeAuth: app => app.UseAuthentication());
}

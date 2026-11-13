using System.Net;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Workflows;
using Xunit;

namespace OpenClaw.Tests;

public sealed class WorkflowTokenExchangeTests
{
    [Theory]
    [InlineData("entra-obo")]
    [InlineData("token-exchange")]
    public async Task ExchangeAsync_WhenConfigured_ShouldSendExactProtocol(string mode)
    {
        var auth = WorkflowDelegationTestSupport.CreateAuthentication(mode);
        if (mode == "token-exchange")
        {
            auth.Audience = "workflow-api";
            auth.Resource = "https://workflow.example";
        }
        var result = await ExchangeAsync(auth, async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://identity.example/token", request.RequestUri!.AbsoluteUri);
            Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
            var form = QueryHelpers.ParseQuery(await request.Content.ReadAsStringAsync(ct));
            Assert.Equal("workflow.read workflow.respond", form["scope"].ToString());
            if (mode == "entra-obo")
            {
                Assert.Null(request.Headers.Authorization);
                Assert.Equal("urn:ietf:params:oauth:grant-type:jwt-bearer", form["grant_type"].ToString());
                Assert.Equal("on_behalf_of", form["requested_token_use"].ToString());
                Assert.Equal("user-assertion", form["assertion"].ToString());
                Assert.Equal("client:with+space", form["client_id"].ToString());
                Assert.Equal("secret:+ value", form["client_secret"].ToString());
                Assert.False(form.ContainsKey("audience"));
            }
            else
            {
                Assert.Equal("urn:ietf:params:oauth:grant-type:token-exchange", form["grant_type"].ToString());
                Assert.Equal("user-assertion", form["subject_token"].ToString());
                Assert.Equal("urn:ietf:params:oauth:token-type:access_token", form["subject_token_type"].ToString());
                Assert.Equal("urn:ietf:params:oauth:token-type:access_token", form["requested_token_type"].ToString());
                Assert.Equal("workflow-api", form["audience"].ToString());
                Assert.Equal("https://workflow.example", form["resource"].ToString());
                Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
                Assert.Equal("client%3Awith%2Bspace:secret%3A%2B+value",
                    Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!)));
                Assert.False(form.ContainsKey("client_secret"));
                Assert.False(form.ContainsKey("client_id"));
            }
            return Json("""{"access_token":"delegated-user-token","token_type":"bEaReR","expires_in":300,"issued_token_type":"urn:ietf:params:oauth:token-type:access_token","scope":"workflow.read workflow.respond"}""");
        });
        Assert.Equal("delegated-user-token", result.AccessToken);
        Assert.Equal(300, result.ExpiresInSeconds);
        Assert.DoesNotContain("delegated-user-token", result.ToString());
    }

    [Theory]
    [InlineData("""{"access_token":"","token_type":"Bearer","expires_in":300}""")]
    [InlineData("""{"access_token":"bad\r\ntoken","token_type":"Bearer","expires_in":300}""")]
    [InlineData("""{"access_token":"bad token","token_type":"Bearer","expires_in":300}""")]
    [InlineData("""{"access_token":"bad\ttoken","token_type":"Bearer","expires_in":300}""")]
    [InlineData("""{"access_token":"bad\u0080token","token_type":"Bearer","expires_in":300}""")]
    [InlineData("""{"access_token":"bad=token","token_type":"Bearer","expires_in":300}""")]
    [InlineData("""{"access_token":"==","token_type":"Bearer","expires_in":300}""")]
    [InlineData("""{"access_token":"token","token_type":"mac","expires_in":300}""")]
    [InlineData("""{"access_token":"token","token_type":"Bearer"}""")]
    [InlineData("""{"access_token":"token","token_type":"Bearer","expires_in":0}""")]
    [InlineData("""{"access_token":"token","token_type":"Bearer","expires_in":-1}""")]
    [InlineData("""{"access_token":"token","token_type":"Bearer","expires_in":1.5}""")]
    [InlineData("""{"access_token":"token","token_type":"Bearer","expires_in":"300"}""")]
    [InlineData("""{"access_token":"token","token_type":"Bearer","expires_in":86401}""")]
    [InlineData("""{"access_token":"token","token_type":"Bearer","expires_in":300,"scope":"WORKFLOW.READ"}""")]
    [InlineData("invalid-json")]
    public async Task ExchangeAsync_WhenResponseInvalid_ShouldFailSafely(string body)
    {
        var error = await Assert.ThrowsAsync<WorkflowDelegationException>(() =>
            ExchangeAsync(WorkflowDelegationTestSupport.CreateAuthentication("entra-obo"), (_, _) => Task.FromResult(Json(body))));
        Assert.Equal(502, error.StatusCode);
        Assert.Equal("workflow_token_exchange_failed", error.ErrorCode);
        Assert.DoesNotContain(body, error.ToString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(86400)]
    public async Task ExchangeAsync_WhenExpiryAtLegalBoundary_ShouldAccept(int lifetime)
    {
        var result = await ExchangeAsync(WorkflowDelegationTestSupport.CreateAuthentication("entra-obo"),
            (_, _) => Task.FromResult(Json($$"""{"access_token":"token","token_type":"Bearer","expires_in":{{lifetime}}}""")));
        Assert.Equal(lifetime, result.ExpiresInSeconds);
    }

    [Fact]
    public async Task ExchangeAsync_WhenBearerUsesLegalAlphabetAndPadding_ShouldAccept()
    {
        var result = await ExchangeAsync(WorkflowDelegationTestSupport.CreateAuthentication("entra-obo"),
            (_, _) => Task.FromResult(Json("""{"access_token":"abc-._~+/===","token_type":"Bearer","expires_in":300}""")));
        Assert.Equal("abc-._~+/===", result.AccessToken);
    }

    [Theory]
    [InlineData("access_denied", 403)]
    [InlineData("invalid_grant", 403)]
    [InlineData("interaction_required", 403)]
    [InlineData("consent_required", 403)]
    [InlineData("login_required", 403)]
    [InlineData("invalid_client", 502)]
    [InlineData("unauthorized_client", 502)]
    [InlineData("unknown", 502)]
    public async Task ExchangeAsync_WhenProviderReturnsError_ShouldExposeOnlySafeCode(string code, int status)
    {
        var error = await Assert.ThrowsAsync<WorkflowDelegationException>(() =>
            ExchangeAsync(WorkflowDelegationTestSupport.CreateAuthentication("entra-obo"), (_, _) =>
                Task.FromResult(Json($$"""{"error":"{{code}}","error_description":"secret-private-description"}""", HttpStatusCode.BadRequest))));
        Assert.Equal(status, error.StatusCode);
        Assert.DoesNotContain("secret-private-description", error.ToString());
    }

    [Fact]
    public async Task ExchangeAsync_WhenRfcIssuedTypeMissing_ShouldReject()
    {
        var error = await Assert.ThrowsAsync<WorkflowDelegationException>(() =>
            ExchangeAsync(WorkflowDelegationTestSupport.CreateAuthentication("token-exchange"),
                (_, _) => Task.FromResult(Json("""{"access_token":"token","token_type":"Bearer","expires_in":300}"""))));
        Assert.Equal(502, error.StatusCode);
    }

    [Fact]
    public async Task ExchangeAsync_WhenChunkedBodyExceedsLimit_ShouldReject()
    {
        var body = new string(' ', 65537);
        var error = await Assert.ThrowsAsync<WorkflowDelegationException>(() =>
            ExchangeAsync(WorkflowDelegationTestSupport.CreateAuthentication("entra-obo"), (_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body)))
                })));
        Assert.Equal(502, error.StatusCode);
    }

    [Theory]
    [InlineData(302)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task ExchangeAsync_WhenTransportStatusFails_ShouldNotReturnCredential(int status)
    {
        var error = await Assert.ThrowsAsync<WorkflowDelegationException>(() =>
            ExchangeAsync(WorkflowDelegationTestSupport.CreateAuthentication("entra-obo"),
                (_, _) => Task.FromResult(Json("{}", (HttpStatusCode)status))));
        Assert.Equal(502, error.StatusCode);
    }

    [Fact]
    public async Task ExchangeAsync_WhenCallerCancels_ShouldPreserveCancellation()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ExchangeAsync(WorkflowDelegationTestSupport.CreateAuthentication("entra-obo"), (_, ct) =>
                Task.FromCanceled<HttpResponseMessage>(ct), new CancellationToken(true)));
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static async Task<WorkflowTokenExchangeResult> ExchangeAsync(
        WorkflowAuthenticationConfig auth, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback,
        CancellationToken? cancellationToken = null)
    {
        var name = "WORKFLOW_TEST_SECRET_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "secret:+ value");
        auth.ClientSecretRef = "env:" + name;
        try
        {
            using var client = new HttpClient(new RecordingHttpMessageHandler(callback));
            var logger = new RecordingWorkflowLogger<WorkflowTokenExchangeClient>();
            var exchange = new WorkflowTokenExchangeClient(new WorkflowTestHttpClientFactory(client), TimeProvider.System, logger);
            var result = await exchange.ExchangeAsync("review", auth,
                WorkflowDelegationTestSupport.CreateExecutionContext("review", "issuer", "user", "user-assertion", DateTimeOffset.UtcNow.AddMinutes(5)),
                cancellationToken ?? TestContext.Current.CancellationToken);
            Assert.DoesNotContain(logger.Messages, message => message.Contains("secret", StringComparison.Ordinal));
            return result;
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }
}

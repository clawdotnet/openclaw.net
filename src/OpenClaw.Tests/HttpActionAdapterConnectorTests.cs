using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenClaw.Agent.Actions;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class HttpActionAdapterConnectorTests
{
    [Fact]
    public async Task InvokeAsync_AllowedCall_SendsPostAndReturnsSuccess()
    {
        using var handler = new TestHttpMessageHandler();
        handler.SetResponse("/updateCustomerTier", HttpStatusCode.OK, "{\"ok\":true}");

        var connector = BuildConnector(handler, "http://test.local",
            allowedCalls: ["updateCustomerTier"]);

        var step = new ActionCall { Call = "crm.updateCustomerTier", Args = new Dictionary<string, JsonElement>() };
        var result = await connector.InvokeAsync(step, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Single(handler.Requests);
        Assert.Equal("POST", handler.Requests[0].Method);
        Assert.Equal("/updateCustomerTier", handler.Requests[0].Path);
    }

    [Fact]
    public async Task InvokeAsync_NotInAllowedCalls_ReturnsFailureWithConnectorErrorCode()
    {
        using var handler = new TestHttpMessageHandler();
        var connector = BuildConnector(handler, "http://test.local",
            allowedCalls: ["updateCustomerTier"]);

        var step = new ActionCall { Call = "crm.deleteAllCustomers", Args = new Dictionary<string, JsonElement>() };
        var result = await connector.InvokeAsync(step, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("connector_error", result.ResultCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task InvokeAsync_Http5xx_ReturnsFailure()
    {
        using var handler = new TestHttpMessageHandler();
        handler.SetResponse("/updateCustomerTier", HttpStatusCode.InternalServerError, "boom");

        var connector = BuildConnector(handler, "http://test.local",
            allowedCalls: ["updateCustomerTier"]);

        var step = new ActionCall { Call = "crm.updateCustomerTier", Args = new Dictionary<string, JsonElement>() };
        var result = await connector.InvokeAsync(step, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("connector_error", result.ResultCode);
    }

    [Fact]
    public async Task InvokeAsync_Timeout_ReturnsFailure()
    {
        using var handler = new TestHttpMessageHandler();
        handler.SetDelay("/updateCustomerTier", TimeSpan.FromSeconds(5));

        var connector = BuildConnector(handler, "http://test.local",
            allowedCalls: ["updateCustomerTier"], timeoutSeconds: 1);

        var step = new ActionCall { Call = "crm.updateCustomerTier", Args = new Dictionary<string, JsonElement>() };
        var result = await connector.InvokeAsync(step, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("connector_unavailable", result.ResultCode);
    }

    [Fact]
    public async Task InvokeAsync_BearerAuth_SendsAuthorizationHeader()
    {
        using var handler = new TestHttpMessageHandler();
        handler.SetResponse("/updateCustomerTier", HttpStatusCode.OK, "{\"ok\":true}");

        Environment.SetEnvironmentVariable("TEST_AUTH_TOKEN", "test-token-123");
        try
        {
            var connector = BuildConnectorWithAuth(handler, "http://test.local",
                allowedCalls: ["updateCustomerTier"],
                authType: "Bearer", authToken: "TEST_AUTH_TOKEN");

            var step = new ActionCall { Call = "crm.updateCustomerTier", Args = new Dictionary<string, JsonElement>() };
            await connector.InvokeAsync(step, TestContext.Current.CancellationToken);

            var authHeader = handler.Requests[0].Headers
                .FirstOrDefault(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
            Assert.NotEqual(default, authHeader);
            Assert.Equal("Bearer test-token-123", authHeader.Value);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_AUTH_TOKEN", null);
        }
    }

    [Fact]
    public async Task InvokeAsync_ApiKeyAuth_SendsCustomHeader()
    {
        using var handler = new TestHttpMessageHandler();
        handler.SetResponse("/updateCustomerTier", HttpStatusCode.OK, "{\"ok\":true}");

        Environment.SetEnvironmentVariable("TEST_API_KEY", "key-abc");
        try
        {
            var connector = BuildConnectorWithAuth(handler, "http://test.local",
                allowedCalls: ["updateCustomerTier"],
                authType: "ApiKey", authToken: "TEST_API_KEY", headerName: "X-API-Key");

            var step = new ActionCall { Call = "crm.updateCustomerTier", Args = new Dictionary<string, JsonElement>() };
            await connector.InvokeAsync(step, TestContext.Current.CancellationToken);

            var apiKeyHeader = handler.Requests[0].Headers
                .FirstOrDefault(h => h.Key.Equals("X-API-Key", StringComparison.OrdinalIgnoreCase));
            Assert.NotEqual(default, apiKeyHeader);
            Assert.Equal("key-abc", apiKeyHeader.Value);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_API_KEY", null);
        }
    }

    private static HttpActionAdapterConnector BuildConnector(
        TestHttpMessageHandler handler,
        string baseUrl,
        string[] allowedCalls,
        int timeoutSeconds = 30)
    {
        var config = Options.Create(new ActionAdapterConfig
        {
            Enabled = true,
            Connectors = new Dictionary<string, ConnectorDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                ["crm"] = new ConnectorDefinition
                {
                    BaseUrl = baseUrl,
                    TimeoutSeconds = timeoutSeconds,
                    AllowedCalls = allowedCalls,
                    Auth = new ConnectorAuthConfig { Type = "None" }
                }
            }
        });
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(baseUrl) };
        return new HttpActionAdapterConnector(config, httpClient, NullLogger<HttpActionAdapterConnector>.Instance);
    }

    private static HttpActionAdapterConnector BuildConnectorWithAuth(
        TestHttpMessageHandler handler,
        string baseUrl,
        string[] allowedCalls,
        string authType,
        string authToken,
        string? headerName = null)
    {
        var config = Options.Create(new ActionAdapterConfig
        {
            Enabled = true,
            Connectors = new Dictionary<string, ConnectorDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                ["crm"] = new ConnectorDefinition
                {
                    BaseUrl = baseUrl,
                    TimeoutSeconds = 30,
                    AllowedCalls = allowedCalls,
                    Auth = new ConnectorAuthConfig
                    {
                        Type = authType,
                        TokenEnv = authToken,
                        HeaderName = headerName ?? ""
                    }
                }
            }
        });
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(baseUrl) };
        return new HttpActionAdapterConnector(config, httpClient, NullLogger<HttpActionAdapterConnector>.Instance);
    }
}

internal sealed class TestHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, (HttpStatusCode StatusCode, string Body)> _responses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TimeSpan> _delays = new(StringComparer.Ordinal);
    public readonly List<CapturedRequest> Requests = [];

    public void SetResponse(string path, HttpStatusCode statusCode, string body)
        => _responses[path] = (statusCode, body);

    public void SetDelay(string path, TimeSpan delay)
        => _delays[path] = delay;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        Requests.Add(new CapturedRequest(
            request.Method.Method,
            path,
            request.Headers
                .Select(h => KeyValuePair.Create(h.Key, string.Join(", ", h.Value)))
                .ToList()));

        if (_delays.TryGetValue(path, out var delay))
            await Task.Delay(delay, cancellationToken);

        if (_responses.TryGetValue(path, out var response))
            return new HttpResponseMessage(response.StatusCode)
            {
                Content = new StringContent(response.Body)
            };

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ok\":true}")
        };
    }
}

internal sealed record CapturedRequest(
    string Method,
    string Path,
    List<KeyValuePair<string, string>> Headers);
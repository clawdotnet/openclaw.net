using System.Net;
using System.Text;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Plugins;
using Xunit;

namespace OpenClaw.Tests;

public sealed class HomeAssistantToolTests
{
    [Fact]
    public async Task HomeAssistantRestClient_GetState_SendsBearerAuth()
    {
        var handler = new CaptureHandler(req =>
        {
            Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
            Assert.Equal("test-token", req.Headers.Authorization?.Parameter);
            Assert.EndsWith("/api/states/light.kitchen", req.RequestUri!.ToString(), StringComparison.Ordinal);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"entity_id":"light.kitchen","state":"on","attributes":{"friendly_name":"Kitchen"}}""",
                    Encoding.UTF8, "application/json")
            };
        });

        var http = new HttpClient(handler);
        var config = new HomeAssistantConfig
        {
            Enabled = true,
            BaseUrl = "http://localhost:8123",
            TokenRef = "raw:test-token",
            TimeoutSeconds = 5,
            MaxOutputChars = 1000
        };

        using var client = new HomeAssistantRestClient(config, http);
        var json = await client.GetStateAsync("light.kitchen", CancellationToken.None);
        Assert.Contains("\"entity_id\":\"light.kitchen\"", json);
    }

    [Fact]
    public async Task HomeAssistantWriteTool_PolicyDenies_Service()
    {
        var handler = new CaptureHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") });

        var http = new HttpClient(handler);
        var config = new HomeAssistantConfig
        {
            Enabled = true,
            BaseUrl = "http://localhost:8123",
            TokenRef = "raw:test-token",
            Policy = new HomeAssistantPolicyConfig
            {
                AllowServiceGlobs = ["light.turn_*"],
                DenyServiceGlobs = ["*"]
            }
        };

        using var tool = new HomeAssistantWriteTool(config, http);
        var result = await tool.ExecuteAsync("""{"op":"call_service","domain":"light","service":"turn_on","entity_id":"light.kitchen"}""",
            CancellationToken.None);

        Assert.Contains("not allowed by policy", result);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}


using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NacosLiveAcceptance;
using Xunit;

namespace NacosLiveAcceptance.Tests;

public sealed class WeatherFixtureProtocolTests
{
    [Fact]
    public async Task StreamableHttp_ListsAndExecutesWeatherTool()
    {
        var port = GetFreePort();
        await using var app = WeatherFixture.CreateApplication(port);
        await app.StartAsync(TestContext.Current.CancellationToken);
        await using var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri($"http://127.0.0.1:{port}/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
            }),
            cancellationToken: TestContext.Current.CancellationToken);

        await client.PingAsync(cancellationToken: TestContext.Current.CancellationToken);
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(tools, tool => tool.Name == "get_weather");

        var result = await client.CallToolAsync(
            "get_weather",
            new Dictionary<string, object?> { ["city"] = "Oslo" },
            cancellationToken: TestContext.Current.CancellationToken);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        using var payload = JsonDocument.Parse(text);
        Assert.Equal("Oslo", payload.RootElement.GetProperty("city").GetString());
        Assert.Equal(JsonValueKind.Number, payload.RootElement.GetProperty("temperature_c").ValueKind);
        Assert.Equal("acceptance fixture", payload.RootElement.GetProperty("source").GetString());
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return port;
    }
}
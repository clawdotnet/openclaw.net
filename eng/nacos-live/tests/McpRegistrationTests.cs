using System.Text.Json;
using NacosLiveAcceptance;
using RedNb.Nacos.Ai;
using Xunit;

namespace NacosLiveAcceptance.Tests;

public sealed class McpRegistrationTests
{
    [Fact]
    public void CreateReleaseRequest_UsesStreamableHttpToolAndServiceReference()
    {
        var request = McpRegistration.CreateReleaseRequest("weather-mcp", "weather-service");

        Assert.Equal("weather-mcp", request.Server.Name);
        Assert.Equal(AiConstants.Mcp.ProtocolStreamable, request.Server.Protocol);
        Assert.Equal("1.0.0", request.Server.VersionDetail!.Version);
        var tool = Assert.Single(request.Tools.Tools!);
        Assert.Equal("get_weather", tool.Name);
        Assert.Equal("weather-service", request.Server.RemoteServerConfig!.ServiceRef!.ServiceName);
        Assert.Equal(AiConstants.Mcp.EndpointTypeRef, request.Endpoint.Type);
        using var endpointData = JsonDocument.Parse(JsonSerializer.Serialize(request.Endpoint.Data));
        Assert.Equal(string.Empty, endpointData.RootElement.GetProperty("namespaceId").GetString());
        Assert.Equal("DEFAULT_GROUP", endpointData.RootElement.GetProperty("groupName").GetString());
        Assert.Equal("weather-service", endpointData.RootElement.GetProperty("serviceName").GetString());
    }
}
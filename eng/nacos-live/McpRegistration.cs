using RedNb.Nacos;
using RedNb.Nacos.Ai;
using RedNb.Nacos.Ai.Models.Mcp;
using RedNb.Nacos.Grpc;
using RedNb.Nacos.Http;

namespace NacosLiveAcceptance;

internal static class McpRegistration
{
    private const string Version = "1.0.0";
    private const string GroupName = "DEFAULT_GROUP";

    public static McpReleaseRequest CreateReleaseRequest(string mcpName, string serviceName)
    {
        var namespaceId = string.Empty;
        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["city"] = new Dictionary<string, object> { ["type"] = "string" },
            },
            ["required"] = new[] { "city" },
        };
        var server = new McpServerBasicInfo
        {
            NamespaceId = namespaceId,
            Name = mcpName,
            Protocol = AiConstants.Mcp.ProtocolStreamable,
            Description = "Deterministic weather acceptance fixture for Oslo.",
            Enabled = true,
            VersionDetail = new ServerVersionDetail { Version = Version, IsLatest = true },
            RemoteServerConfig = new McpServerRemoteServiceConfig
            {
                ExportProtocol = AiConstants.Mcp.ProtocolStreamable,
                ExportPath = "/mcp",
                ServiceRef = new McpServiceRef
                {
                    NamespaceId = namespaceId,
                    GroupName = GroupName,
                    ServiceName = serviceName,
                },
            },
        };
        var tools = new McpToolSpecification
        {
            Tools =
            [
                new McpTool
                {
                    Name = "get_weather",
                    Description = "Return deterministic weather data for Oslo.",
                    InputSchema = schema,
                },
            ],
        };
        var endpointData = new Dictionary<string, string>
        {
            ["namespaceId"] = namespaceId,
            ["groupName"] = GroupName,
            ["serviceName"] = serviceName,
        };
        var endpoint = new McpEndpointSpec
        {
            Type = AiConstants.Mcp.EndpointTypeRef,
            Data = endpointData,
        };

        return new McpReleaseRequest(server, tools, endpoint);
    }

    public static async Task<IAsyncDisposable> RegisterAsync(
        NacosDeployment deployment,
        string fixtureAddress,
        int fixturePort,
        CancellationToken cancellationToken)
    {
        var options = new NacosClientOptions
        {
            ServerAddresses = deployment.ServerAddress,
            ConsoleAddresses = $"127.0.0.1:{deployment.ConsolePort}",
            Namespace = string.Empty,
            Username = deployment.Username,
            Password = deployment.Password,
        };
        var httpService = new NacosFactory().CreateAiService(options);
        var grpcService = await NacosGrpcFactory.CreateAiServiceAsync(options);
        var request = CreateReleaseRequest("weather-mcp", "weather-service");
        var handle = new McpRegistrationHandle(
            httpService,
            grpcService,
            "weather-mcp",
            Version,
            fixtureAddress,
            fixturePort);

        try
        {
            await httpService.ReleaseMcpServerAsync(
                request.Server,
                request.Tools,
                request.Endpoint,
                cancellationToken);
            await grpcService.RegisterMcpServerEndpointAsync(
                "weather-mcp",
                fixtureAddress,
                fixturePort,
                cancellationToken);
            return handle;
        }
        catch
        {
            await handle.DisposeAsync();
            throw;
        }
    }

    internal sealed record McpReleaseRequest(
        McpServerBasicInfo Server,
        McpToolSpecification Tools,
        McpEndpointSpec Endpoint);

    private sealed class McpRegistrationHandle(
        IAiService httpService,
        IAiService grpcService,
        string mcpName,
        string version,
        string fixtureAddress,
        int fixturePort) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await grpcService.DeregisterMcpServerEndpointAsync(
                    mcpName,
                    fixtureAddress,
                    fixturePort,
                    CancellationToken.None);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"MCP endpoint deregistration failed: {exception.Message}");
            }

            try
            {
                await httpService.DeleteMcpServerAsync(mcpName, version, CancellationToken.None);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"MCP release deletion failed: {exception.Message}");
            }

            await DisposeServiceAsync(grpcService);
            await DisposeServiceAsync(httpService);
        }

        private static async ValueTask DisposeServiceAsync(IAiService service)
        {
            if (service is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else if (service is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
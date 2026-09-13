using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using OpenClaw.Agent.Plugins;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Plugins;
using Xunit;

namespace OpenClaw.Tests.Tools;

public sealed class ResolveCapabilityToolTests
{
    [Fact]
    public async Task HappyPath_ReturnsBinding_AndCallsSearchThenAddOnce()
    {
        var (tool, _, state, server) = await BuildAsync();
        await using (server)
        {
            var args = """
                {"task_description":"weather city","key_words":"weather,city","selection_policy":"first"}
                """;
            var result = await tool.ExecuteAsync(args, CancellationToken.None);

            using var doc = JsonDocument.Parse(result);
            var root = doc.RootElement;
            Assert.Equal("weather-mcp", root.GetProperty("server").GetString());
            Assert.Equal("get_weather", root.GetProperty("tool").GetString());
            Assert.Equal(1, root.GetProperty("tried").GetArrayLength());

            // No use_tool call ever happens in the resolver.
            Assert.DoesNotContain(state.Calls, c => c.StartsWith("use:"));
            // search then add for the chosen candidate.
            Assert.Contains("search", state.Calls);
            Assert.Contains("add:weather-mcp", state.Calls);
        }
    }

    private static async Task<(ResolveCapabilityTool tool, McpServerToolRegistry registry, NacosRouterFixtureState state, WebApplication server)> BuildAsync()
    {
        var state = new NacosRouterFixtureState();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(state);
        builder.Services.AddMcpServer()
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<FakeNacosRouterMcpTools>();
        var server = builder.Build();
        server.MapMcp("/mcp");
        await server.StartAsync(TestContext.Current.CancellationToken);
        var registry = new McpServerToolRegistry(new McpPluginsConfig(), NullLogger<McpServerToolRegistry>.Instance);
        // Real registry: configured above via ReloadWorkspaceServersAsync against the test MCP server.
        var reload = await registry.ReloadWorkspaceServersAsync(
            new Dictionary<string, McpServerConfig>
            {
                ["nacos-mcp-router"] = new()
                {
                    Enabled = true,
                    Transport = "http",
                    Url = server.Urls.Single() + "/mcp",
                    ToolNamePrefix = "nacos_mcp_router_",
                },
            }, TestContext.Current.CancellationToken);
        var tool = new ResolveCapabilityTool(registry);
        return (tool, registry, state, server);
    }
}

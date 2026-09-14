using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using OpenClaw.Agent.Plugins;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// Wire-contract tests for the #230 resolve_capability native tool, pinned
/// against the shared fake Router fixture.
/// </summary>
[Collection(EnvironmentVariableCollection.Name)]
public sealed class ResolveCapabilityToolTests
{
    private sealed class RouterFixture : IAsyncDisposable
    {
        public required NacosRouterFixtureState State { get; init; }
        public required McpServerToolRegistry Registry { get; init; }
        public required WebApplication Server { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            await Registry.DisposeAsync();
        }
    }

    private static async Task<RouterFixture> CreateFixtureAsync(NacosRouterFixtureState? state = null)
    {
        state ??= new NacosRouterFixtureState();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(state);
        builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true).WithTools<FakeNacosRouterMcpTools>();
        var server = builder.Build();
        server.MapMcp("/mcp");
        await server.StartAsync(TestContext.Current.CancellationToken);
        var registry = new McpServerToolRegistry(new McpPluginsConfig(), NullLogger<McpServerToolRegistry>.Instance);
        await registry.ReloadWorkspaceServersAsync(new Dictionary<string, McpServerConfig>
        {
            ["nacos-mcp-router"] = new() { Enabled = true, Transport = "http", Url = server.Urls.Single() + "/mcp", ToolNamePrefix = "nacos_mcp_router_" }
        }, TestContext.Current.CancellationToken);
        return new RouterFixture { State = state, Registry = registry, Server = server };
    }

    [Fact]
    public async Task Resolve_FirstCandidateAddFails_TriedListsAttemptedCandidatesInRankOrder()
    {
        await using var fixture = await CreateFixtureAsync();
        fixture.State.FailAddNames.Add("weather-mcp");

        var tool = new ResolveCapabilityTool(fixture.Registry);
        var json = await tool.ExecuteAsync(
            """{"task_description":"weather city","key_words":"weather,city","selection_policy":"first"}""",
            TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("server", out _), $"expected a binding, got: {json}");
        Assert.Equal("candidate-1", root.GetProperty("server").GetString());
        Assert.Equal("get_weather", root.GetProperty("tool").GetString());
        var tried = root.GetProperty("tried").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToArray();
        Assert.Equal(new[] { "weather-mcp", "candidate-1" }, tried);
        Assert.Equal(new[] { "search", "add:weather-mcp", "add:candidate-1" }, fixture.State.Calls);
    }
}

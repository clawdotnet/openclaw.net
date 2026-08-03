using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Endpoints;
using OpenClaw.McpApp;
using OpenClaw.McpApp.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class AppsMcpProxyEndpointTests : IAsyncDisposable
{
    private readonly List<WebApplication> _apps = [];
    private readonly List<string> _tempDirs = [];

    [Fact]
    public void GatewayConfig_McpCompatibility_Defaults_AreStrictAndDiscoveryFirst()
    {
        var cfg = new GatewayConfig();
        Assert.True(cfg.McpCompatibility.EnableDiscoveryFirst);
        Assert.False(cfg.McpCompatibility.ForceLegacyInitialize);
    }

    [Fact]
    public async Task ToolsList_PassesThroughAllTools_NoVisibilityFiltering()
    {
        var upstreamUrl = await StartFakeUpstreamAsync();
        await using var gateway = await StartGatewayWithProxyAsync("inventory-app", upstreamUrl);

        await using var mcpClient = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri($"{gateway.BaseAddress}apps/mcp/inventory-app") }),
            cancellationToken: CancellationToken.None);

        var tools = await mcpClient.ListToolsAsync(cancellationToken: CancellationToken.None);

        Assert.Contains(tools, t => t.Name == "echo_session");
        Assert.Contains(tools, t => t.Name == "app_only_tool");
    }

    [Fact]
    public async Task CallTool_InjectsSessionIdFromQueryIntoMeta()
    {
        var upstreamUrl = await StartFakeUpstreamAsync();
        await using var gateway = await StartGatewayWithProxyAsync("inventory-app", upstreamUrl);

        await using var mcpClient = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri($"{gateway.BaseAddress}apps/mcp/inventory-app?sessionId=abc123") }),
            cancellationToken: CancellationToken.None);

        var result = await mcpClient.CallToolAsync("echo_session", cancellationToken: CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        Assert.NotNull(result.StructuredContent);
        var doc = result.StructuredContent!.Value;
        Assert.Equal("abc123", doc.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task CallTool_UnknownAppId_ReturnsActionableErrorPayload()
    {
        var upstreamUrl = await StartFakeUpstreamAsync();
        await using var gateway = await StartGatewayWithProxyAsync("inventory-app", upstreamUrl);

        await using var mcpClient = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri($"{gateway.BaseAddress}apps/mcp/nonexistent") }),
            cancellationToken: CancellationToken.None);

        var result = await mcpClient.CallToolAsync("echo_session", cancellationToken: CancellationToken.None);

        Assert.Equal(true, result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("nonexistent", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProxyConfiguration_DoesNotOverride_DefaultMcpRouteHandlers()
    {
        var upstreamUrl = await StartFakeUpstreamAsync();
        await using var gateway = await StartGatewayWithProxyAndDefaultMcpAsync("inventory-app", upstreamUrl);

        await using var defaultClient = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri($"{gateway.BaseAddress}mcp") }),
            cancellationToken: CancellationToken.None);
        var defaultTools = await defaultClient.ListToolsAsync(cancellationToken: CancellationToken.None);

        Assert.Contains(defaultTools, tool => tool.Name == "default_gateway_tool");
        Assert.DoesNotContain(defaultTools, tool => tool.Name == "echo_session");

        await using var proxiedClient = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri($"{gateway.BaseAddress}apps/mcp/inventory-app") }),
            cancellationToken: CancellationToken.None);
        var proxiedTools = await proxiedClient.ListToolsAsync(cancellationToken: CancellationToken.None);

        Assert.Contains(proxiedTools, tool => tool.Name == "echo_session");
        Assert.DoesNotContain(proxiedTools, tool => tool.Name == "default_gateway_tool");
    }

    [Fact]
    public async Task GatewayMcpServer_AdvertisesTasksExtension_WhenEnabled()
    {
        var upstreamUrl = await StartFakeUpstreamAsync();
        await using var gateway = await StartGatewayWithProxyAndDefaultMcpAsync("inventory-app", upstreamUrl);

        using var httpClient = new HttpClient { BaseAddress = new Uri(gateway.BaseAddress) };
        using var request = new HttpRequestMessage(HttpMethod.Post, "mcp");
        request.Headers.TryAddWithoutValidation("mcp-protocol-version", "2025-03-26");
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = new StringContent(
            "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test\",\"version\":\"1.0.0\"}}}",
            System.Text.Encoding.UTF8,
            "application/json");

        using var response = await httpClient.SendAsync(request, CancellationToken.None);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.True(response.IsSuccessStatusCode, body);
        var json = body.TrimStart();
        if (!json.StartsWith("{", StringComparison.Ordinal))
        {
            var dataLine = json.Split('\n').FirstOrDefault(static line => line.StartsWith("data:", StringComparison.Ordinal));
            Assert.False(string.IsNullOrWhiteSpace(dataLine), body);
            json = dataLine!["data:".Length..].TrimStart();
        }

        using var document = JsonDocument.Parse(json);
        var capabilities = document.RootElement.GetProperty("result").GetProperty("capabilities");
        Assert.True(capabilities.TryGetProperty("extensions", out var extensions), body);
        Assert.True(extensions.TryGetProperty("io.modelcontextprotocol/tasks", out var tasksCapability), body);
        Assert.Equal(JsonValueKind.Object, tasksCapability.ValueKind);
    }

    private async Task<string> StartFakeUpstreamAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "fake-upstream", Version = "1.0.0" };
            })
            .WithHttpTransport(options => { options.Stateless = true; })
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools =
                [
                    new Tool { Name = "echo_session", Description = "echoes back _meta.sessionId" },
                    new Tool { Name = "app_only_tool", Description = "app-only, visibility excludes model" },
                ],
            }))
            .WithCallToolHandler((ctx, _) =>
            {
                var sessionId = ctx.Params?.Meta?["sessionId"]?.ToString();
                return ValueTask.FromResult(new CallToolResult
                {
                    Content = [],
                    StructuredContent = JsonSerializer.SerializeToElement(new { sessionId, tool = ctx.Params?.Name }),
                });
            });

        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        _apps.Add(app);
        return $"{app.Urls.Single().TrimEnd('/')}/mcp";
    }

    private async Task<GatewayProxyTestHarness> StartGatewayWithProxyAsync(string appId, string upstreamUrl)
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-apps-proxy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempDirs.Add(root);

        var manifest = new McpAppManifest
        {
            Id = appId,
            Name = "Inventory App",
            Version = "1.0",
            Transport = "http",
            Url = upstreamUrl,
            ToolNamePrefix = "inventory.",
        };
        var manifestPath = Path.Combine(root, "openclaw.mcpapp.json");
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, McpAppManifestJsonContext.Default.McpAppManifest));

        var config = new GatewayConfig
        {
            BindAddress = "127.0.0.1",
            AuthToken = "test-token",
            McpApps = new McpAppsConfig
            {
                Enabled = true,
                DiscoveryPaths = [root],
            }
        };
        var startup = new GatewayStartupContext
        {
            Config = config,
            RuntimeState = RuntimeModeResolver.Resolve(config.Runtime),
            IsNonLoopbackBind = false,
            WorkspacePath = null,
        };

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddOpenClawMcpAppServices(config.McpApps);
        builder.Services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "OpenClaw Gateway MCP", Version = "1.0.0" };
            })
            .WithHttpTransport(options =>
            {
                options.Stateless = true;
                options.ConfigureSessionOptions = AppsMcpProxyEndpoint.ConfigureSessionOptionsAsync;
            });

        var app = builder.Build();
        app.MapOpenClawAppsMcpProxy(startup);
        await app.StartAsync();
        _apps.Add(app);

        var registry = app.Services.GetRequiredService<McpAppRegistry>();
        await registry.LoadAllAsync(CancellationToken.None);

        return new GatewayProxyTestHarness(app, registry, app.Urls.Single().TrimEnd('/') + "/");
    }

    private async Task<GatewayProxyTestHarness> StartGatewayWithProxyAndDefaultMcpAsync(string appId, string upstreamUrl)
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-apps-proxy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempDirs.Add(root);

        var manifest = new McpAppManifest
        {
            Id = appId,
            Name = "Inventory App",
            Version = "1.0",
            Transport = "http",
            Url = upstreamUrl,
            ToolNamePrefix = "inventory.",
        };
        await File.WriteAllTextAsync(
            Path.Combine(root, "openclaw.mcpapp.json"),
            JsonSerializer.Serialize(manifest, McpAppManifestJsonContext.Default.McpAppManifest));

        var config = new GatewayConfig
        {
            BindAddress = "127.0.0.1",
            AuthToken = "test-token",
            McpApps = new McpAppsConfig
            {
                Enabled = true,
                DiscoveryPaths = [root],
            }
        };
        var startup = new GatewayStartupContext
        {
            Config = config,
            RuntimeState = RuntimeModeResolver.Resolve(config.Runtime),
            IsNonLoopbackBind = false,
            WorkspacePath = null,
        };

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddOpenClawMcpAppServices(config.McpApps);
        builder.Services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "OpenClaw Gateway MCP", Version = "1.0.0" };
            })
            .WithHttpTransport(options =>
            {
                options.Stateless = true;
                options.ConfigureSessionOptions = AppsMcpProxyEndpoint.ConfigureSessionOptionsAsync;
            })
            .WithTasks(new InMemoryMcpTaskStore())
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools =
                [
                    new Tool { Name = "default_gateway_tool", Description = "default route handler tool" },
                ],
            }));

        var app = builder.Build();
        app.MapOpenClawAppsMcpProxy(startup);
        app.MapMcp("/mcp");
        await app.StartAsync();
        _apps.Add(app);

        var registry = app.Services.GetRequiredService<McpAppRegistry>();
        await registry.LoadAllAsync(CancellationToken.None);

        return new GatewayProxyTestHarness(app, registry, app.Urls.Single().TrimEnd('/') + "/");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps)
            await app.DisposeAsync();

        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed record GatewayProxyTestHarness(WebApplication App, McpAppRegistry Registry, string BaseAddress) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Registry.DisposeAsync();
            await App.DisposeAsync();
        }
    }
}
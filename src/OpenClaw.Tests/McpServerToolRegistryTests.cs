using System.Text;
using System.Text.Json;
using System.ComponentModel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenClaw.Agent.Plugins;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using Xunit;

namespace OpenClaw.Tests;

public sealed class McpServerToolRegistryTests : IAsyncDisposable
{
    private readonly List<WebApplication> _apps = [];

    [Fact]
    public async Task LoadAsync_HttpServer_DiscoversAndExecutesTools()
    {
        var (serverUrl, calls) = await StartMcpServerAsync();
        var registry = new McpServerToolRegistry(
            new McpPluginsConfig
            {
                Enabled = true,
                Servers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
                {
                    ["demo"] = new()
                    {
                        Transport = "http",
                        Url = serverUrl
                    }
                }
            },
            NullLogger<McpServerToolRegistry>.Instance);
        using var nativeRegistry = new NativePluginRegistry(new NativePluginsConfig(), NullLogger.Instance, new ToolingConfig());

        await registry.RegisterToolsAsync(nativeRegistry, CancellationToken.None);

        var tool = Assert.Single(nativeRegistry.Tools);
        Assert.Equal("demo.echo", tool.Name);
        Assert.Contains("Demo echo tool", tool.Description, StringComparison.Ordinal);
        Assert.Equal("demo:hello", await tool.ExecuteAsync("""{"text":"hello"}""", CancellationToken.None));
        Assert.True(calls.InitializeCalls >= 1);
        Assert.True(calls.ListCalls >= 1);
        Assert.True(calls.CallCalls >= 1);
    }

    [Fact]
    public async Task LoadAsync_HttpServer_WithHeaders_ResolvesSecrets()
    {
        // Set up environment variable for testing
        Environment.SetEnvironmentVariable("TEST_AUTH_TOKEN", "secret-token-123");
        try
        {
            var (serverUrl, calls, receivedHeaders) = await StartMcpServerWithHeaderCheckAsync();
            var registry = new McpServerToolRegistry(
                new McpPluginsConfig
                {
                    Enabled = true,
                    Servers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
                    {
                        ["demo"] = new()
                        {
                            Transport = "http",
                            Url = serverUrl,
                            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                ["Authorization"] = "env:TEST_AUTH_TOKEN",
                                ["X-Custom-Header"] = "raw:literal-value",
                                ["X-Direct-Value"] = "direct-value"
                            }
                        }
                    }
                },
                NullLogger<McpServerToolRegistry>.Instance);
            using var nativeRegistry = new NativePluginRegistry(new NativePluginsConfig(), NullLogger.Instance, new ToolingConfig());

            await registry.RegisterToolsAsync(nativeRegistry, CancellationToken.None);

            // Verify headers were resolved and sent correctly
            Assert.True(receivedHeaders.ContainsKey("Authorization"));
            Assert.Equal("secret-token-123", receivedHeaders["Authorization"]);
            Assert.True(receivedHeaders.ContainsKey("X-Custom-Header"));
            Assert.Equal("literal-value", receivedHeaders["X-Custom-Header"]);
            Assert.True(receivedHeaders.ContainsKey("X-Direct-Value"));
            Assert.Equal("direct-value", receivedHeaders["X-Direct-Value"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_AUTH_TOKEN", null);
        }
    }

    [Fact]
    public async Task RegisterToolsAsync_MultipleCalls_RegistersOwnedResourceOnlyOnce()
    {
        var (serverUrl, _) = await StartMcpServerAsync();
        var registry = new McpServerToolRegistry(
            new McpPluginsConfig
            {
                Enabled = true,
                Servers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
                {
                    ["demo"] = new()
                    {
                        Transport = "http",
                        Url = serverUrl
                    }
                }
            },
            NullLogger<McpServerToolRegistry>.Instance);
        using var nativeRegistry = new NativePluginRegistry(new NativePluginsConfig(), NullLogger.Instance, new ToolingConfig());

        await registry.RegisterToolsAsync(nativeRegistry, CancellationToken.None);
        await registry.RegisterToolsAsync(nativeRegistry, CancellationToken.None);

        Assert.Single(nativeRegistry.Tools);

        var ownedResourcesField = typeof(NativePluginRegistry).GetField("_ownedResources", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var ownedResources = Assert.IsType<List<IDisposable>>(ownedResourcesField?.GetValue(nativeRegistry));
        Assert.Single(ownedResources);
    }

    [Fact]
    public async Task LoadAsync_WhenFirstAttemptFails_AllowsRetryAndLoadsTools()
    {
        var (serverUrl, _) = await StartMcpServerAsync();
        var config = new McpPluginsConfig
        {
            Enabled = true,
            Servers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
            {
                ["broken"] = new()
                {
                    Transport = "invalid-transport"
                },
                ["demo"] = new()
                {
                    Transport = "http",
                    Url = serverUrl
                }
            }
        };
        var registry = new McpServerToolRegistry(config, NullLogger<McpServerToolRegistry>.Instance);
        using var nativeRegistry = new NativePluginRegistry(new NativePluginsConfig(), NullLogger.Instance, new ToolingConfig());

        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.RegisterToolsAsync(nativeRegistry, CancellationToken.None));
        var clientsField = typeof(McpServerToolRegistry).GetField("_clients", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var clientsAfterFailure = Assert.IsType<List<ModelContextProtocol.Client.McpClient>>(clientsField?.GetValue(registry));
        Assert.Empty(clientsAfterFailure);

        config.Servers["broken"].Enabled = false;

        await registry.RegisterToolsAsync(nativeRegistry, CancellationToken.None);
        var clientsAfterSuccess = Assert.IsType<List<ModelContextProtocol.Client.McpClient>>(clientsField?.GetValue(registry));
        Assert.Single(clientsAfterSuccess);

        var tool = Assert.Single(nativeRegistry.Tools);
        Assert.Equal("demo.echo", tool.Name);
    }

    [Fact]
    public async Task LoadAsync_UsesRequestTimeoutForToolListing_NotStartupTimeout()
    {
        var (serverUrl, _) = await StartMcpServerAsync(TimeSpan.FromSeconds(2));
        var registry = new McpServerToolRegistry(
            new McpPluginsConfig
            {
                Enabled = true,
                Servers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
                {
                    ["demo"] = new()
                    {
                        Transport = "http",
                        Url = serverUrl,
                        StartupTimeoutSeconds = 1,
                        RequestTimeoutSeconds = 5
                    }
                }
            },
            NullLogger<McpServerToolRegistry>.Instance);
        using var nativeRegistry = new NativePluginRegistry(new NativePluginsConfig(), NullLogger.Instance, new ToolingConfig());

        await registry.RegisterToolsAsync(nativeRegistry, CancellationToken.None);

        var tool = Assert.Single(nativeRegistry.Tools);
        Assert.Equal("demo.echo", tool.Name);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps)
            await app.DisposeAsync();
    }

    private async Task<(string ServerUrl, McpCallTracker Tracker)> StartMcpServerAsync(TimeSpan? toolsListDelay = null)
    {
        var tracker = new McpCallTracker();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(tracker);
        builder.Services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "demo",
                    Version = "1.0.0"
                };
            })
            .WithHttpTransport(options => { options.Stateless = true; })
            .WithTools<DemoMcpTools>();
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            await TrackMcpMethodAsync(context, tracker, toolsListDelay);
            await next();
        });
        app.MapMcp("/mcp");

        await app.StartAsync();
        _apps.Add(app);
        var address = app.Urls.Single();
        return ($"{address.TrimEnd('/')}/mcp", tracker);
    }

    private async Task<(string ServerUrl, McpCallTracker Tracker, Dictionary<string, string> ReceivedHeaders)> StartMcpServerWithHeaderCheckAsync()
    {
        var tracker = new McpCallTracker();
        var receivedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(tracker);
        builder.Services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "demo",
                    Version = "1.0.0"
                };
            })
            .WithHttpTransport(options => { options.Stateless = true; })
            .WithTools<DemoMcpTools>();
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (receivedHeaders.Count == 0 &&
                context.Request.Path.StartsWithSegments("/mcp", StringComparison.Ordinal))
            {
                foreach (var header in context.Request.Headers)
                {
                    receivedHeaders[header.Key] = header.Value.ToString();
                }
            }
            await TrackMcpMethodAsync(context, tracker, null);
            await next();
        });
        app.MapMcp("/mcp");

        await app.StartAsync();
        _apps.Add(app);
        var address = app.Urls.Single();
        return ($"{address.TrimEnd('/')}/mcp", tracker, receivedHeaders);
    }

    private static async Task TrackMcpMethodAsync(HttpContext context, McpCallTracker tracker, TimeSpan? toolsListDelay)
    {
        if (!context.Request.Path.StartsWithSegments("/mcp", StringComparison.Ordinal))
            return;
        if (!HttpMethods.IsPost(context.Request.Method))
            return;

        context.Request.EnableBuffering();
        using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
        context.Request.Body.Position = 0;

        if (!document.RootElement.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
            return;
        var method = methodElement.GetString();
        switch (method)
        {
            case "initialize":
                tracker.InitializeCalls++;
                break;
            case "tools/list":
                if (toolsListDelay is { } delay && delay > TimeSpan.Zero)
                    await Task.Delay(delay, context.RequestAborted);
                tracker.ListCalls++;
                break;
            case "tools/call":
                tracker.CallCalls++;
                break;
        }
    }

    private sealed class McpCallTracker
    {
        public int InitializeCalls { get; set; }
        public int ListCalls { get; set; }
        public int CallCalls { get; set; }
    }

    [McpServerToolType]
    private sealed class DemoMcpTools
    {
        [McpServerTool(Name = "echo", ReadOnly = true), Description("Demo echo tool")]
        public string Echo([Description("text")] string text)
            => $"demo:{text}";
    }
}

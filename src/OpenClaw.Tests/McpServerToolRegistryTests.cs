using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.ComponentModel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenClaw.Agent.Plugins;
using OpenClaw.Agent.Tools;
using NSubstitute;
using OpenClaw.Agent;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Plugins;
using Xunit;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Mcp;
using OpenClaw.Core.Security;

namespace OpenClaw.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class McpServerToolRegistryTests : IAsyncDisposable
{
    private readonly List<WebApplication> _apps = [];

    [Fact]
    public async Task McpDelegatedHttpClientFactory_ConcurrentClients_KeepAuthorizationIsolated()
    {
        var (serverUrl, receivedCalls) = await StartMcpServerWithCallHeaderCaptureAsync<DelegatedCredentialMcpTools>();
        var factory = new McpDelegatedHttpClientFactory();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Static"] = "shared-header-value",
            ["Authorization"] = "Bearer static-configured-token"
        };
        var cancellationToken = TestContext.Current.CancellationToken;

        var firstClientTask = factory.CreateAsync(
            "first", new Uri(serverUrl), headers, 30, "delegated-token-one", cancellationToken);
        var secondClientTask = factory.CreateAsync(
            "second", new Uri(serverUrl), headers, 30, "delegated-token-two", cancellationToken);
        var clients = await Task.WhenAll(firstClientTask, secondClientTask);
        await using var firstClient = clients[0];
        await using var secondClient = clients[1];

        await Task.WhenAll(
            firstClient.CallToolAsync("ping", cancellationToken: cancellationToken).AsTask(),
            secondClient.CallToolAsync("ping", cancellationToken: cancellationToken).AsTask());

        var callsByAuthorization = receivedCalls.ToDictionary(call => call.Authorization);
        Assert.Equal(2, callsByAuthorization.Count);
        Assert.Equal("shared-header-value", callsByAuthorization["Bearer delegated-token-one"].StaticHeader);
        Assert.Equal("shared-header-value", callsByAuthorization["Bearer delegated-token-two"].StaticHeader);
        Assert.DoesNotContain("Bearer static-configured-token", callsByAuthorization.Keys);
    }

    [Fact]
    public async Task RegisterToolsAsync_DelegatedHttpServer_UsesCallerCredentialPerCall()
    {
        var (serverUrl, receivedCalls) = await StartMcpServerWithCallHeaderCaptureAsync<DelegatedCredentialMcpTools>();
        var credentialProvider = new CallerSubjectCredentialProvider();
        var invoker = new McpDelegatedToolInvoker(credentialProvider, new McpDelegatedHttpClientFactory());
        using var registry = new McpServerToolRegistry(
            CreateMcpPluginsConfig(serverUrl, delegatedCredentialsEnabled: true),
            NullLogger<McpServerToolRegistry>.Instance,
            invoker);
        using var nativeRegistry = new NativePluginRegistry(new NativePluginsConfig(), NullLogger.Instance, new ToolingConfig());

        await registry.RegisterToolsAsync(nativeRegistry, TestContext.Current.CancellationToken);

        var tool = Assert.IsAssignableFrom<IToolWithContext>(Assert.Single(nativeRegistry.Tools));
        var firstCaller = new McpCallerCredentialContext("caller-token-one", "caller-one", DateTimeOffset.UtcNow.AddMinutes(5));
        var secondCaller = new McpCallerCredentialContext("caller-token-two", "caller-two", DateTimeOffset.UtcNow.AddMinutes(5));
        var results = await Task.WhenAll(
            tool.ExecuteAsync("{}", CreateToolExecutionContext(firstCaller), TestContext.Current.CancellationToken).AsTask(),
            tool.ExecuteAsync("{}", CreateToolExecutionContext(secondCaller), TestContext.Current.CancellationToken).AsTask());

        Assert.All(results, result => Assert.Equal("pong", result));
        var callsByAuthorization = receivedCalls.ToDictionary(call => call.Authorization);
        Assert.Equal(2, callsByAuthorization.Count);
        Assert.Equal("static-header-value", callsByAuthorization["Bearer delegated-caller-one"].StaticHeader);
        Assert.Equal("static-header-value", callsByAuthorization["Bearer delegated-caller-two"].StaticHeader);
        Assert.DoesNotContain("Bearer static-configured-token", callsByAuthorization.Keys);
        Assert.DoesNotContain("Bearer caller-token-one", callsByAuthorization.Keys);
        Assert.DoesNotContain("Bearer caller-token-two", callsByAuthorization.Keys);
        Assert.Equal(2, credentialProvider.Subjects.Count);
        Assert.Contains("caller-one", credentialProvider.Subjects);
        Assert.Contains("caller-two", credentialProvider.Subjects);
    }

    [Fact]
    public async Task RegisterToolsAsync_HttpServerWithoutDelegation_PreservesStaticAuthorization()
    {
        var (serverUrl, receivedCalls) = await StartMcpServerWithCallHeaderCaptureAsync<DelegatedCredentialMcpTools>(expectedCallCount: 1);
        using var registry = new McpServerToolRegistry(
            CreateMcpPluginsConfig(serverUrl, delegatedCredentialsEnabled: false),
            NullLogger<McpServerToolRegistry>.Instance);
        using var nativeRegistry = new NativePluginRegistry(new NativePluginsConfig(), NullLogger.Instance, new ToolingConfig());

        await registry.RegisterToolsAsync(nativeRegistry, TestContext.Current.CancellationToken);

        var tool = Assert.IsAssignableFrom<IToolWithContext>(Assert.Single(nativeRegistry.Tools));
        var caller = new McpCallerCredentialContext("caller-token", "caller", DateTimeOffset.UtcNow.AddMinutes(5));
        var result = await tool.ExecuteAsync("{}", CreateToolExecutionContext(caller), TestContext.Current.CancellationToken);

        Assert.Equal("pong", result);
        var call = Assert.Single(receivedCalls);
        Assert.Equal("Bearer static-configured-token", call.Authorization);
        Assert.Equal("static-header-value", call.StaticHeader);
        Assert.DoesNotContain("caller-token", call.Authorization, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisterToolsAsync_DelegatedProviderFailure_DoesNotExposeExceptionDetails()
    {
        var (serverUrl, receivedCalls) = await StartMcpServerWithCallHeaderCaptureAsync<DelegatedCredentialMcpTools>(expectedCallCount: 1);
        var credentialProvider = Substitute.For<IMcpDelegatedCredentialProvider>();
        credentialProvider.GetCredentialAsync(
                Arg.Any<McpDelegatedCredentialsConfig>(),
                Arg.Any<McpCallerCredentialContext>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<McpDelegatedCredential>>(_ => throw new InvalidOperationException(
                "caller-token-marker delegated-token-marker"));
        var invoker = new McpDelegatedToolInvoker(credentialProvider, new McpDelegatedHttpClientFactory());
        using var registry = new McpServerToolRegistry(
            CreateMcpPluginsConfig(serverUrl, delegatedCredentialsEnabled: true),
            NullLogger<McpServerToolRegistry>.Instance,
            invoker);
        using var nativeRegistry = new NativePluginRegistry(new NativePluginsConfig(), NullLogger.Instance, new ToolingConfig());

        await registry.RegisterToolsAsync(nativeRegistry, TestContext.Current.CancellationToken);

        var tool = Assert.IsAssignableFrom<IToolWithContext>(Assert.Single(nativeRegistry.Tools));
        var result = await tool.ExecuteAsync(
            "{}",
            CreateToolExecutionContext(new McpCallerCredentialContext(
                "caller-token-marker",
                "caller-subject",
                DateTimeOffset.UtcNow.AddMinutes(5))),
            TestContext.Current.CancellationToken);

        Assert.Equal("Error: MCP_DELEGATED_CREDENTIAL_PROVIDER_FAILED", result);
        Assert.DoesNotContain("caller-token-marker", result, StringComparison.Ordinal);
        Assert.DoesNotContain("delegated-token-marker", result, StringComparison.Ordinal);
        Assert.Empty(receivedCalls);
    }

    [Fact]
    public async Task ReloadWorkspaceServersAsync_AddsNewWorkspaceTools_AndRemovesDeletedOnes()
    {
        var (serverUrl, _) = await StartMcpServerAsync<DemoMcpTools>();
        using var registry = CreateRegistryWithConfig(enabled: false);

        var initial = await registry.ReloadWorkspaceServersAsync(
            new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
            {
                ["alpha"] = CreateHttpServerConfig(serverUrl)
            },
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(initial.AddedTools);
        Assert.Contains(initial.AddedTools, tool => tool.Name == "alpha_echo");

        var second = await registry.ReloadWorkspaceServersAsync(
            new Dictionary<string, McpServerConfig>(StringComparer.Ordinal),
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(second.RemovedToolNames);
        Assert.Contains("alpha_echo", second.RemovedToolNames);
    }

    [Fact]
    public async Task GetClientByServerId_ReturnsWorkspaceClientAfterReload()
    {
        var (serverUrl, _) = await StartMcpServerAsync<DemoMcpTools>();
        using var registry = CreateRegistryWithConfig(enabled: false);

        await registry.ReloadWorkspaceServersAsync(
            new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
            {
                ["alpha"] = CreateHttpServerConfig(serverUrl)
            },
            TestContext.Current.CancellationToken);

        Assert.NotNull(registry.GetClientByServerId("alpha"));
    }

    [Fact]
    public async Task ReloadWorkspaceServersAsync_SkipsFailedServer_AndContinuesLoadingLaterServers()
    {
        var (serverUrl, _) = await StartMcpServerAsync<DemoMcpTools>();
        using var registry = CreateRegistryWithConfig(enabled: false);

        var reload = await registry.ReloadWorkspaceServersAsync(
            new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
            {
                ["broken"] = new()
                {
                    Enabled = true,
                    Transport = "http",
                    Url = "http://127.0.0.1:1/mcp",
                    StartupTimeoutSeconds = 1
                },
                ["alpha"] = CreateHttpServerConfig(serverUrl)
            },
            TestContext.Current.CancellationToken);

        Assert.Contains(reload.AddedTools, tool => tool.Name == "alpha_echo");
        Assert.NotNull(registry.GetClientByServerId("alpha"));
        Assert.Null(registry.GetClientByServerId("broken"));
    }

    [Fact]
    public async Task WorkspaceWatcher_Start_FallsBackToWorkspaceMcpFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-mcp-workspace-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspacePath = Path.Combine(root, "workspace");
        Directory.CreateDirectory(Path.Combine(workspacePath, ".kingcrab"));
        try
        {
            var (serverUrl, _) = await StartMcpServerAsync<DemoMcpTools>();
            await File.WriteAllTextAsync(
                Path.Combine(workspacePath, ".kingcrab", "mcp.json"),
                $"{{\"enabled\":true,\"servers\":{{\"alpha\":{{\"enabled\":true,\"transport\":\"http\",\"url\":\"{serverUrl}\"}}}}}}",
                TestContext.Current.CancellationToken);
            await using var registry = CreateRegistryWithConfig(enabled: false);
            var runtime = Substitute.For<IAgentRuntime>();
            var store = new McpConfigStore(root, NullLogger<McpConfigStore>.Instance);

            using var service = new McpWorkspaceWatcherService(
                registry,
                runtime,
                workspacePath,
                NullLogger<McpWorkspaceWatcherService>.Instance,
                store,
                new CapabilityBindingCache());

            using var cts = new CancellationTokenSource();
            service.Start(cts.Token);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (DateTime.UtcNow < deadline && registry.GetClientByServerId("alpha") is null)
                await Task.Delay(50, TestContext.Current.CancellationToken);

            Assert.NotNull(registry.GetClientByServerId("alpha"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceWatcher_Start_PrefersOpenClawWorkspaceMcpFile_WhenBothPathsExist()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-mcp-workspace-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspacePath = Path.Combine(root, "workspace");
        Directory.CreateDirectory(Path.Combine(workspacePath, ".openclaw"));
        Directory.CreateDirectory(Path.Combine(workspacePath, ".kingcrab"));
        try
        {
            var (serverUrl, _) = await StartMcpServerAsync<DemoMcpTools>();
            await File.WriteAllTextAsync(
                Path.Combine(workspacePath, ".openclaw", "mcp.json"),
                $"{{\"enabled\":true,\"servers\":{{\"alpha\":{{\"enabled\":true,\"transport\":\"http\",\"url\":\"{serverUrl}\"}}}}}}",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(workspacePath, ".kingcrab", "mcp.json"),
                $"{{\"enabled\":true,\"servers\":{{\"beta\":{{\"enabled\":true,\"transport\":\"http\",\"url\":\"{serverUrl}\"}}}}}}",
                TestContext.Current.CancellationToken);
            await using var registry = CreateRegistryWithConfig(enabled: false);
            var runtime = Substitute.For<IAgentRuntime>();
            var store = new McpConfigStore(root, NullLogger<McpConfigStore>.Instance);

            using var service = new McpWorkspaceWatcherService(
                registry,
                runtime,
                workspacePath,
                NullLogger<McpWorkspaceWatcherService>.Instance,
                store,
                new CapabilityBindingCache());

            using var cts = new CancellationTokenSource();
            service.Start(cts.Token);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (DateTime.UtcNow < deadline && registry.GetClientByServerId("alpha") is null)
                await Task.Delay(50, TestContext.Current.CancellationToken);

            Assert.NotNull(registry.GetClientByServerId("alpha"));
            Assert.Null(registry.GetClientByServerId("beta"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_HttpServer_DiscoversAndExecutesTools()
    {
        var (serverUrl, calls) = await StartMcpServerAsync<DemoMcpTools>();
        using var registry = new McpServerToolRegistry(
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

        await registry.RegisterToolsAsync(nativeRegistry, TestContext.Current.CancellationToken);

        var tool = Assert.Single(nativeRegistry.Tools);
        Assert.Equal("demo_echo", tool.Name);
        Assert.Contains("Demo echo tool", tool.Description, StringComparison.Ordinal);
        using (var schemaDocument = JsonDocument.Parse(tool.ParameterSchema))
        {
            var schemaRoot = schemaDocument.RootElement;
            Assert.Equal(JsonValueKind.Object, schemaRoot.ValueKind);
            Assert.True(schemaRoot.TryGetProperty("properties", out var properties));
            Assert.True(properties.TryGetProperty("text", out var textProperty));
            Assert.Equal(JsonValueKind.Object, textProperty.ValueKind);
        }
        Assert.Equal("demo:hello", await tool.ExecuteAsync("""{"text":"hello"}""", TestContext.Current.CancellationToken));
        Assert.True(calls.InitializeCalls >= 1 || calls.DiscoverCalls >= 1);
        Assert.True(calls.ListCalls >= 1);
        Assert.True(calls.CallCalls >= 1);
    }

    [Fact]
    public async Task LoadAsync_HttpServer_WithHeaders_ResolvesSecrets()
    {
        Environment.SetEnvironmentVariable("TEST_AUTH_TOKEN", "secret-token-123");
        try
        {
            var (serverUrl, calls, receivedHeaders) = await StartMcpServerWithHeaderCheckAsync<DemoMcpTools>();
            using var registry = new McpServerToolRegistry(
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

            await registry.RegisterToolsAsync(nativeRegistry, TestContext.Current.CancellationToken);

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
    public async Task LoadAsync_HttpServer_WithStructuredContentOnly_ReturnsStructuredJson()
    {
        var (serverUrl, _) = await StartMcpServerAsync<StructuredMcpTools>();
        using var registry = new McpServerToolRegistry(
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

        await registry.RegisterToolsAsync(nativeRegistry, TestContext.Current.CancellationToken);

        var tool = Assert.Single(nativeRegistry.Tools);
        var result = await tool.ExecuteAsync("{}", TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(result);
        Assert.Equal(123, document.RootElement.GetProperty("value").GetInt32());
        Assert.Equal("ok", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task LoadAsync_HttpServer_ToolWithoutInputSchema_FailsWithClearMessage()
    {
        await Task.CompletedTask;
        using var nullSchema = JsonDocument.Parse("null");

        var ex = Assert.Throws<ArgumentException>(() =>
        {
            _ = new Tool
            {
                Name = "bad_tool",
                Description = "missing schema",
                InputSchema = nullSchema.RootElement.Clone()
            };
        });

        Assert.Equal("InputSchema", ex.ParamName);
    }

    [Fact]
    public async Task LoadAsync_HttpServer_SkipsAppOnlyToolsFromModelRegistry()
    {
        var serverUrl = await StartCustomMcpServerAsync(
            new ListToolsResult
            {
                Tools =
                [
                    new Tool { Name = "visible_tool", Description = "visible" },
                    new Tool
                    {
                        Name = "app_only_tool",
                        Description = "app-only",
                        Meta = new JsonObject
                        {
                            ["ui"] = new JsonObject
                            {
                                ["visibility"] = new JsonArray("app")
                            }
                        }
                    }
                ]
            },
            (_, _) => ValueTask.FromResult(new CallToolResult()));
        using var registry = new McpServerToolRegistry(
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

        await registry.RegisterToolsAsync(nativeRegistry, TestContext.Current.CancellationToken);

        Assert.Contains(nativeRegistry.Tools, tool => tool.Name == "demo_visible_tool");
        Assert.DoesNotContain(nativeRegistry.Tools, tool => tool.Name == "demo_app_only_tool");
    }

    [Fact]
    public async Task ExecuteAsync_HttpServerTool_ForwardsSessionMetadataToUpstream()
    {
        var serverUrl = await StartCustomMcpServerAsync(
            new ListToolsResult
            {
                Tools = [new Tool { Name = "echo_meta", Description = "echo meta" }]
            },
            (ctx, _) =>
            {
                var meta = ctx.Params?.Meta;
                var sessionId = meta?["sessionId"]?.ToString();
                var userId = meta?["userId"]?.ToString();
                return ValueTask.FromResult(new CallToolResult
                {
                    StructuredContent = JsonSerializer.SerializeToElement(new { sessionId, userId })
                });
            });
        using var registry = new McpServerToolRegistry(
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

        await registry.RegisterToolsAsync(nativeRegistry, TestContext.Current.CancellationToken);

        var tool = Assert.Single(nativeRegistry.Tools);
        var executor = new OpenClawToolExecutor(
            [tool],
            toolTimeoutSeconds: 30,
            requireToolApproval: false,
            approvalRequiredTools: [],
            hooks: []);

        var result = await executor.ExecuteAsync(
            tool.Name,
            "{}",
            callId: null,
            new Session
            {
                Id = "sess-meta",
                ChannelId = "test-channel",
                SenderId = "user-meta"
            },
            new TurnContext
            {
                SessionId = "sess-meta",
                ChannelId = "test-channel"
            },
            isStreaming: false,
            approvalCallback: null,
            TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(result.ResultText);
        Assert.Equal("sess-meta", document.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal("user-meta", document.RootElement.GetProperty("userId").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_HttpServerTool_PrefersAuthenticatedUserIdForUpstreamMetadata()
    {
        var serverUrl = await StartCustomMcpServerAsync(
            new ListToolsResult
            {
                Tools = [new Tool { Name = "echo_meta", Description = "echo meta" }]
            },
            (ctx, _) =>
            {
                var meta = ctx.Params?.Meta;
                var sessionId = meta?["sessionId"]?.ToString();
                var userId = meta?["userId"]?.ToString();
                return ValueTask.FromResult(new CallToolResult
                {
                    StructuredContent = JsonSerializer.SerializeToElement(new { sessionId, userId })
                });
            });
        using var registry = new McpServerToolRegistry(
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

        await registry.RegisterToolsAsync(nativeRegistry, TestContext.Current.CancellationToken);

        var tool = Assert.Single(nativeRegistry.Tools);
        var executor = new OpenClawToolExecutor(
            [tool],
            toolTimeoutSeconds: 30,
            requireToolApproval: false,
            approvalRequiredTools: [],
            hooks: []);

        var result = await executor.ExecuteAsync(
            tool.Name,
            "{}",
            callId: null,
            new Session
            {
                Id = "sess-auth-meta",
                ChannelId = "test-channel",
                SenderId = "route-sender",
                AuthenticatedUserId = "oidc-user-42"
            },
            new TurnContext
            {
                SessionId = "sess-auth-meta",
                ChannelId = "test-channel"
            },
            isStreaming: false,
            approvalCallback: null,
            TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(result.ResultText);
        Assert.Equal("sess-auth-meta", document.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal("oidc-user-42", document.RootElement.GetProperty("userId").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_HttpServerUiTool_SuppressesStructuredContent()
    {
        var serverUrl = await StartCustomMcpServerAsync(
            new ListToolsResult
            {
                Tools =
                [
                    new Tool
                    {
                        Name = "ui_tool",
                        Description = "ui tool",
                        Meta = new JsonObject
                        {
                            ["ui"] = new JsonObject
                            {
                                ["resourceUri"] = "ui://inventory/card"
                            }
                        }
                    }
                ]
            },
            (_, _) => ValueTask.FromResult(new CallToolResult
            {
                Content = [new TextContentBlock { Text = "rendered" }],
                StructuredContent = JsonSerializer.SerializeToElement(new { hidden = true })
            }));
        using var registry = new McpServerToolRegistry(
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

        await registry.RegisterToolsAsync(nativeRegistry, TestContext.Current.CancellationToken);

        var tool = Assert.Single(nativeRegistry.Tools);
        var result = await tool.ExecuteAsync("{}", TestContext.Current.CancellationToken);

        Assert.Equal("rendered", result);
        Assert.DoesNotContain("hidden", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_HttpServer_FollowsListToolsPagination()
    {
        var serverUrl = await StartCustomMcpServerAsync(
            (ctx, _) =>
            {
                var cursor = ctx.Params?.Cursor;
                return ValueTask.FromResult(string.IsNullOrEmpty(cursor)
                    ? new ListToolsResult
                    {
                        Tools = [new Tool { Name = "first_tool", Description = "first" }],
                        NextCursor = "page-2"
                    }
                    : new ListToolsResult
                    {
                        Tools = [new Tool { Name = "second_tool", Description = "second" }]
                    });
            },
            (_, _) => ValueTask.FromResult(new CallToolResult()));
        using var registry = new McpServerToolRegistry(
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

        await registry.RegisterToolsAsync(nativeRegistry, TestContext.Current.CancellationToken);

        Assert.Contains(nativeRegistry.Tools, tool => tool.Name == "demo_first_tool");
        Assert.Contains(nativeRegistry.Tools, tool => tool.Name == "demo_second_tool");
    }

    [Fact]
    public async Task LoadAsync_HttpServer_WithImageContentBlock_ReturnsSerializedContent()
    {
        var (serverUrl, _) = await StartMcpServerAsync<BinaryMcpTools>();
        using var registry = new McpServerToolRegistry(
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

        await registry.RegisterToolsAsync(nativeRegistry, TestContext.Current.CancellationToken);

        var tool = Assert.Single(nativeRegistry.Tools);
        var result = await tool.ExecuteAsync("{}", TestContext.Current.CancellationToken);
        Assert.Contains("image/png", result, StringComparison.Ordinal);
        Assert.Contains("type", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RegisterToolsAsync_MultipleCalls_DoesNotRegisterSelfAsOwnedResource()
    {
        var (serverUrl, _) = await StartMcpServerAsync<DemoMcpTools>();
        using var registry = new McpServerToolRegistry(
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

        await registry.RegisterToolsAsync(nativeRegistry, TestContext.Current.CancellationToken);
        await registry.RegisterToolsAsync(nativeRegistry, TestContext.Current.CancellationToken);

        Assert.Single(nativeRegistry.Tools);

        var ownedResourcesField = typeof(NativePluginRegistry).GetField("_ownedResources", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var ownedResources = Assert.IsType<List<IDisposable>>(ownedResourcesField?.GetValue(nativeRegistry));
        Assert.Empty(ownedResources);
    }

    [Fact]
    public async Task LoadAsync_ConcurrentCalls_LoadsToolsOnce()
    {
        var (serverUrl, calls) = await StartMcpServerAsync<DemoMcpTools>();
        using var registry = new McpServerToolRegistry(
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

        var loads = await Task.WhenAll(
            registry.LoadAsync(TestContext.Current.CancellationToken),
            registry.LoadAsync(TestContext.Current.CancellationToken),
            registry.LoadAsync(TestContext.Current.CancellationToken),
            registry.LoadAsync(TestContext.Current.CancellationToken));

        Assert.All(loads, tools => Assert.Single(tools));
        Assert.Equal(1, calls.ListCalls);
    }

    [Fact]
    public async Task LoadAsync_WhenFirstAttemptFails_AllowsRetryAndLoadsTools()
    {
        var (serverUrl, _) = await StartMcpServerAsync<DemoMcpTools>();
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
        using var registry = new McpServerToolRegistry(config, NullLogger<McpServerToolRegistry>.Instance);
        using var nativeRegistry = new NativePluginRegistry(new NativePluginsConfig(), NullLogger.Instance, new ToolingConfig());

        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.RegisterToolsAsync(nativeRegistry, TestContext.Current.CancellationToken));
        var clientsField = typeof(McpServerToolRegistry).GetField("_clients", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var clientsAfterFailure = Assert.IsType<List<ModelContextProtocol.Client.McpClient>>(clientsField?.GetValue(registry));
        Assert.Empty(clientsAfterFailure);

        config.Servers["broken"].Enabled = false;

        await registry.RegisterToolsAsync(nativeRegistry, TestContext.Current.CancellationToken);
        var clientsAfterSuccess = Assert.IsType<List<ModelContextProtocol.Client.McpClient>>(clientsField?.GetValue(registry));
        Assert.Single(clientsAfterSuccess);

        var tool = Assert.Single(nativeRegistry.Tools);
        Assert.Equal("demo_echo", tool.Name);
    }

    [Fact]
    public async Task LoadAsync_UsesRequestTimeoutForToolListing_NotStartupTimeout()
    {
        var (serverUrl, _) = await StartMcpServerAsync<DemoMcpTools>(TimeSpan.FromSeconds(2));
        using var registry = new McpServerToolRegistry(
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

        await registry.RegisterToolsAsync(nativeRegistry, TestContext.Current.CancellationToken);

        var tool = Assert.Single(nativeRegistry.Tools);
        Assert.Equal("demo_echo", tool.Name);
    }

    [Fact]
    public async Task LoadAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        using var registry = new McpServerToolRegistry(
            new McpPluginsConfig
            {
                Enabled = true,
                Servers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
            },
            NullLogger<McpServerToolRegistry>.Instance);

        registry.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => registry.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsync_AfterDisposeAsync_ThrowsObjectDisposedException()
    {
        var registry = new McpServerToolRegistry(
            new McpPluginsConfig
            {
                Enabled = true,
                Servers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
            },
            NullLogger<McpServerToolRegistry>.Instance);

        await registry.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => registry.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Dispose_MayBeCalledTwice_AfterToolRegistration()
    {
        var (serverUrl, _) = await StartMcpServerAsync<DemoMcpTools>();
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

        await registry.RegisterToolsAsync(nativeRegistry, TestContext.Current.CancellationToken);

        var ex = Record.Exception(() =>
        {
            registry.Dispose();
            registry.Dispose();
        });

        Assert.Null(ex);
    }

    [Fact]
    public async Task DisposeAsync_MayBeCalledTwice_AfterToolRegistration()
    {
        var (serverUrl, _) = await StartMcpServerAsync<DemoMcpTools>();
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

        await registry.RegisterToolsAsync(nativeRegistry, TestContext.Current.CancellationToken);

        var ex = await Record.ExceptionAsync(async () =>
        {
            await registry.DisposeAsync();
            await registry.DisposeAsync();
        });

        Assert.Null(ex);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps)
            await app.DisposeAsync();
    }

    private async Task<string> StartCustomMcpServerAsync(
        ListToolsResult listToolsResult,
        McpRequestHandler<CallToolRequestParams, CallToolResult> callToolHandler)
        => await StartCustomMcpServerAsync((_, _) => ValueTask.FromResult(listToolsResult), callToolHandler);

    private async Task<string> StartCustomMcpServerAsync(
        McpRequestHandler<ListToolsRequestParams, ListToolsResult> listToolsHandler,
        McpRequestHandler<CallToolRequestParams, CallToolResult> callToolHandler)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "demo",
                    Version = "1.0.0"
                };
            })
            .WithHttpTransport(options => { options.Stateless = true; })
            .WithListToolsHandler(listToolsHandler)
            .WithCallToolHandler(callToolHandler);
        var app = builder.Build();
        app.MapMcp("/mcp");

        await app.StartAsync();
        _apps.Add(app);
        return $"{app.Urls.Single().TrimEnd('/')}/mcp";
    }

    private async Task<(string ServerUrl, McpCallTracker Tracker)> StartMcpServerAsync<TTools>(TimeSpan? toolsListDelay = null)
        where TTools : class
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
            .WithTools<TTools>();
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

    private async Task<(string ServerUrl, McpCallTracker Tracker, Dictionary<string, string> ReceivedHeaders)> StartMcpServerWithHeaderCheckAsync<TTools>()
        where TTools : class
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
            .WithTools<TTools>();
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

    private async Task<(string ServerUrl, ConcurrentQueue<ReceivedMcpCallHeaders> ReceivedCalls)> StartMcpServerWithCallHeaderCaptureAsync<TTools>(int expectedCallCount = 2)
        where TTools : class
    {
        var receivedCalls = new ConcurrentQueue<ReceivedMcpCallHeaders>();
        var bothCallsReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "demo",
                    Version = "1.0.0"
                };
            })
            .WithHttpTransport(options => { options.Stateless = true; })
            .WithTools<TTools>();
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/mcp", StringComparison.Ordinal) &&
                HttpMethods.IsPost(context.Request.Method))
            {
                context.Request.EnableBuffering();
                using var document = await JsonDocument.ParseAsync(
                    context.Request.Body,
                    cancellationToken: context.RequestAborted);
                context.Request.Body.Position = 0;

                if (document.RootElement.TryGetProperty("method", out var methodElement) &&
                    methodElement.ValueKind == JsonValueKind.String &&
                    methodElement.GetString() == "tools/call")
                {
                    receivedCalls.Enqueue(new ReceivedMcpCallHeaders(
                        context.Request.Headers["Authorization"].ToString(),
                        context.Request.Headers["X-Static"].ToString()));
                    if (Interlocked.Increment(ref callCount) == expectedCallCount)
                        bothCallsReceived.TrySetResult(true);

                    await bothCallsReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), context.RequestAborted);
                }
            }

            await next();
        });
        app.MapMcp("/mcp");

        await app.StartAsync();
        _apps.Add(app);
        var address = app.Urls.Single();
        return ($"{address.TrimEnd('/')}/mcp", receivedCalls);
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
            case "server/discover":
                tracker.DiscoverCalls++;
                break;
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
        public int DiscoverCalls { get; set; }
        public int InitializeCalls { get; set; }
        public int ListCalls { get; set; }
        public int CallCalls { get; set; }
    }

    private sealed record ReceivedMcpCallHeaders(string Authorization, string StaticHeader);

    private sealed class CallerSubjectCredentialProvider : IMcpDelegatedCredentialProvider
    {
        private readonly ConcurrentQueue<string> _subjects = new();

        public IReadOnlyCollection<string> Subjects => _subjects.ToArray();

        public Task<McpDelegatedCredential> GetCredentialAsync(
            McpDelegatedCredentialsConfig policy,
            McpCallerCredentialContext caller,
            CancellationToken ct)
        {
            _subjects.Enqueue(caller.Subject);
            return Task.FromResult(new McpDelegatedCredential(
                $"delegated-{caller.Subject}",
                DateTimeOffset.UtcNow.AddMinutes(5)));
        }
    }

    private static McpPluginsConfig CreateMcpPluginsConfig(string serverUrl, bool delegatedCredentialsEnabled)
        => new()
        {
            Enabled = true,
            Servers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
            {
                ["demo"] = new()
                {
                    Enabled = true,
                    Transport = "http",
                    Url = serverUrl,
                    RequestTimeoutSeconds = 30,
                    Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Authorization"] = "Bearer static-configured-token",
                        ["X-Static"] = "static-header-value"
                    },
                    DelegatedCredentials = delegatedCredentialsEnabled
                        ? new McpDelegatedCredentialsConfig
                        {
                            Enabled = true,
                            Mode = "token_exchange",
                            Audience = "inventory-api",
                            Scopes = ["inventory.read"]
                        }
                        : null
                }
            }
        };

    private static ToolExecutionContext CreateToolExecutionContext(McpCallerCredentialContext caller)
        => new()
        {
            Session = null!,
            TurnContext = null!,
            McpCallerCredentialContext = caller
        };

    private static McpServerToolRegistry CreateRegistryWithConfig(bool enabled)
        => new(
            new McpPluginsConfig
            {
                Enabled = enabled,
                Servers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
            },
            NullLogger<McpServerToolRegistry>.Instance);

    private static McpServerConfig CreateHttpServerConfig(string serverUrl)
        => new()
        {
            Enabled = true,
            Transport = "http",
            Url = serverUrl
        };

    [McpServerToolType]
    private sealed class DemoMcpTools
    {
        [McpServerTool(Name = "echo", ReadOnly = true), Description("Demo echo tool")]
        public string Echo([Description("text")] string text)
            => $"demo:{text}";
    }

    [McpServerToolType]
    private sealed class DelegatedCredentialMcpTools
    {
        [McpServerTool(Name = "ping", ReadOnly = true)]
        public string Ping()
            => "pong";
    }

    [McpServerToolType]
    private sealed class StructuredMcpTools
    {
        [McpServerTool(Name = "structured", ReadOnly = true), Description("Structured response tool")]
        public CallToolResult Structured()
            => new()
            {
                StructuredContent = JsonSerializer.SerializeToElement(new { value = 123, status = "ok" })
            };
    }

    [McpServerToolType]
    private sealed class BinaryMcpTools
    {
        [McpServerTool(Name = "image", ReadOnly = true), Description("Image response tool")]
        public CallToolResult Image()
            => new()
            {
                Content = [ImageContentBlock.FromBytes("png-bytes"u8.ToArray(), "image/png")]
            };
    }
}

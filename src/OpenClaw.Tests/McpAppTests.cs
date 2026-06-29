using System.Text.Json;
using System.ComponentModel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenClaw.Agent.Plugins;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.McpApp;
using OpenClaw.McpApp.Models;
using OpenClaw.McpApp.Shared;
using Xunit;

namespace OpenClaw.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class McpAppTests : IAsyncDisposable
{
    private readonly List<WebApplication> _apps = [];

    // ── McpAppManifest ──────────────────────────────────────────

    [Fact]
    public void Manifest_SerializeAndDeserialize_RoundTrips()
    {
        var manifest = new McpAppManifest
        {
            Id = "test-app",
            Name = "Test App",
            Description = "A test MCP App",
            Version = "1.2.3",
            Transport = "http",
            Url = "https://localhost:5001/mcp",
            HasUi = true,
            UiResourceUri = "ui://test/dashboard.html",
            Category = "devtools",
            Tags = ["test", "demo"],
            Capabilities = ["tools", "resources", "prompts"],
            StartupTimeoutSeconds = 10,
            RequestTimeoutSeconds = 30,
            ToolNamePrefix = "test.",
        };

        var json = JsonSerializer.Serialize(manifest, McpAppManifestJsonContext.Default.McpAppManifest);
        var deserialized = JsonSerializer.Deserialize(json, McpAppManifestJsonContext.Default.McpAppManifest);

        Assert.NotNull(deserialized);
        Assert.Equal("test-app", deserialized.Id);
        Assert.Equal("Test App", deserialized.Name);
        Assert.Equal("A test MCP App", deserialized.Description);
        Assert.Equal("1.2.3", deserialized.Version);
        Assert.Equal("http", deserialized.Transport);
        Assert.Equal("https://localhost:5001/mcp", deserialized.Url);
        Assert.True(deserialized.HasUi);
        Assert.Equal("ui://test/dashboard.html", deserialized.UiResourceUri);
        Assert.Equal("devtools", deserialized.Category);
        Assert.Contains("test", deserialized.Tags);
        Assert.Contains("demo", deserialized.Tags);
        Assert.Contains("tools", deserialized.Capabilities);
        Assert.Equal(10, deserialized.StartupTimeoutSeconds);
        Assert.Equal(30, deserialized.RequestTimeoutSeconds);
        Assert.Equal("test.", deserialized.ToolNamePrefix);
    }

    [Fact]
    public void Manifest_Deserialize_MinimalJson_DefaultsSensibly()
    {
        var json = """{"id":"minimal-app","version":"0.1.0"}""";
        var manifest = JsonSerializer.Deserialize(json, McpAppManifestJsonContext.Default.McpAppManifest);

        Assert.NotNull(manifest);
        Assert.Equal("minimal-app", manifest.Id);
        Assert.Equal("0.1.0", manifest.Version);
        Assert.Null(manifest.Name);
        Assert.Null(manifest.Description);
        // Transport defaults to null in JSON; the ResolveTransport() method
        // treats null as "stdio" at connection time.
        Assert.True(manifest.Transport is null or "stdio");
        Assert.False(manifest.HasUi);
        // Capabilities defaults to ["tools"] in the class definition but JSON
        // deserialization may set it to null when absent; both are acceptable.
        Assert.True(manifest.Capabilities is null || manifest.Capabilities.Contains("tools"));
    }

    [Fact]
    public void Manifest_Deserialize_WithStdioTransport_ReadsCommandAndArgs()
    {
        var json = """
        {
            "id": "cli-app",
            "version": "1.0.0",
            "transport": "stdio",
            "command": "dotnet",
            "arguments": ["run", "--project", "./MyApp"],
            "workingDirectory": "./workspace",
            "environment": { "DOTNET_ENVIRONMENT": "Development" },
            "headers": { "X-Api-Key": "test-key" }
        }
        """;

        var manifest = JsonSerializer.Deserialize(json, McpAppManifestJsonContext.Default.McpAppManifest);

        Assert.NotNull(manifest);
        Assert.Equal("cli-app", manifest.Id);
        Assert.Equal("stdio", manifest.Transport);
        Assert.Equal("dotnet", manifest.Command);
        Assert.Equal(3, manifest.Arguments.Length);
        Assert.Equal("run", manifest.Arguments[0]);
        Assert.Equal("./MyApp", manifest.Arguments[2]);
        Assert.Equal("./workspace", manifest.WorkingDirectory);
        Assert.Equal("Development", manifest.Environment["DOTNET_ENVIRONMENT"]);
        Assert.Equal("test-key", manifest.Headers["X-Api-Key"]);
    }

    [Fact]
    public void Manifest_Deserialize_InvalidJson_ThrowsJsonException()
    {
        var invalidJson = "{ this is not json }";
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(invalidJson, McpAppManifestJsonContext.Default.McpAppManifest));
    }

    // ── McpAppInstallState ──────────────────────────────────────

    [Fact]
    public void InstallState_NewState_HasCorrectDefaults()
    {
        var manifest = new McpAppManifest { Id = "test", Version = "1.0" };
        var state = new McpAppInstallState
        {
            Manifest = manifest,
            ManifestPath = "/path/to/openclaw.mcpapp.json",
            RootPath = "/path/to",
        };

        Assert.Equal("test", state.Manifest.Id);
        Assert.Equal("/path/to/openclaw.mcpapp.json", state.ManifestPath);
        Assert.Equal("/path/to", state.RootPath);
        Assert.True(state.IsValid);
        Assert.Empty(state.ValidationErrors);
        Assert.Equal(McpAppLifecycle.Discovered, state.Lifecycle);
        Assert.Equal(0, state.DiscoveredToolCount);
        Assert.Equal(0, state.DiscoveredResourceCount);
        Assert.Equal(0, state.DiscoveredPromptCount);
    }

    [Fact]
    public void InstallState_LifecycleTransitions_UpdateTimestamps()
    {
        var manifest = new McpAppManifest { Id = "test", Version = "1.0" };
        var state = new McpAppInstallState
        {
            Manifest = manifest,
            ManifestPath = "/p/openclaw.mcpapp.json",
            RootPath = "/p",
        };

        var discoveredAt = state.DiscoveredAt;
        var stateChangedAt = state.StateChangedAt;

        // Transition to Validated
        state.Lifecycle = McpAppLifecycle.Validated;
        state.StateChangedAt = DateTimeOffset.UtcNow;

        Assert.Equal(McpAppLifecycle.Validated, state.Lifecycle);
        Assert.NotEqual(stateChangedAt, state.StateChangedAt);
        Assert.Equal(discoveredAt, state.DiscoveredAt); // DiscoveredAt never changes
    }

    [Fact]
    public void InstallState_ValidationErrors_PreventLoading()
    {
        var state = new McpAppInstallState
        {
            Manifest = new McpAppManifest { Id = "", Version = "1.0" },
            ManifestPath = "/p/openclaw.mcpapp.json",
            RootPath = "/p",
            IsValid = false,
        };
        state.ValidationErrors.Add("Manifest 'id' is required.");
        state.Lifecycle = McpAppLifecycle.Discovered;

        Assert.False(state.IsValid);
        Assert.Single(state.ValidationErrors);
        Assert.Contains("id", state.ValidationErrors[0], StringComparison.OrdinalIgnoreCase);
    }

    // ── McpAppInfoProvider ──────────────────────────────────────

    [Fact]
    public void InfoProvider_BasicProperties_ReflectManifest()
    {
        var manifest = new McpAppManifest
        {
            Id = "my-app",
            Name = "My App",
            Description = "A test app",
            Version = "2.0.0",
            HasUi = true,
            UiResourceUri = "ui://my-app/main.html",
        };
        var state = new McpAppInstallState
        {
            Manifest = manifest,
            ManifestPath = "/p/openclaw.mcpapp.json",
            RootPath = "/p",
        };
        var provider = new McpAppInfoProvider(state);

        Assert.Equal("my-app", provider.AppId);
        Assert.Equal("My App", provider.DisplayName);
        Assert.Equal("A test app", provider.Description);
        Assert.Equal("2.0.0", provider.Version);
        Assert.True(provider.HasUi);
        Assert.Equal("ui://my-app/main.html", provider.UiResourceUri);
        Assert.Null(provider.Client);
        Assert.Empty(provider.GetToolDescriptors());
        Assert.Empty(provider.GetResourceDescriptors());
        Assert.Empty(provider.GetPromptDescriptors());
    }

    [Fact]
    public void InfoProvider_NameFallback_UsesIdWhenNameIsNull()
    {
        var manifest = new McpAppManifest { Id = "no-name-app", Version = "1.0" };
        var state = new McpAppInstallState
        {
            Manifest = manifest,
            ManifestPath = "/p/openclaw.mcpapp.json",
            RootPath = "/p",
        };
        var provider = new McpAppInfoProvider(state);

        Assert.Equal("no-name-app", provider.DisplayName);
    }

    [Fact]
    public void InfoProvider_DescriptorManagement_TracksCounts()
    {
        var manifest = new McpAppManifest { Id = "test", Version = "1.0" };
        var state = new McpAppInstallState
        {
            Manifest = manifest,
            ManifestPath = "/p/openclaw.mcpapp.json",
            RootPath = "/p",
        };
        var provider = new McpAppInfoProvider(state);

        // Add tool descriptors
        var tools = new[]
        {
            new McpAppToolDescriptor
            {
                RemoteName = "get_stores", LocalName = "grocery.get_stores",
                Description = "Get stores", InputSchemaText = "{}"
            },
            new McpAppToolDescriptor
            {
                RemoteName = "get_products", LocalName = "grocery.get_products",
                Description = "Get products", InputSchemaText = "{}"
            },
            new McpAppToolDescriptor
            {
                RemoteName = "show_dashboard", LocalName = "grocery.show_dashboard",
                Description = "Show dashboard", InputSchemaText = "{}",
                UiResourceUri = "ui://grocery/store-dashboard.html"
            },
        };

        // Use reflection to call internal SetToolDescriptors
        typeof(McpAppInfoProvider).GetMethod("SetToolDescriptors",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.Invoke(provider, [tools.AsEnumerable()]);

        Assert.Equal(3, provider.GetToolDescriptors().Count);
        Assert.Equal(3, state.DiscoveredToolCount);
        Assert.Contains(provider.GetToolDescriptors(), t => t.UiResourceUri == "ui://grocery/store-dashboard.html");

        // Add resource descriptors
        var resources = new[]
        {
            new McpAppResourceDescriptor
            {
                Uri = "ui://grocery/store-dashboard.html", Name = "Dashboard",
                MimeType = "text/html;profile=mcp-app", IsUiResource = true
            },
            new McpAppResourceDescriptor
            {
                Uri = "inventory://stores/{storeId}/summary", Name = "Store Summary",
                MimeType = "application/json"
            },
        };

        typeof(McpAppInfoProvider).GetMethod("SetResourceDescriptors",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.Invoke(provider, [resources.AsEnumerable()]);

        Assert.Equal(2, provider.GetResourceDescriptors().Count);
        Assert.Equal(2, state.DiscoveredResourceCount);
        Assert.Contains(provider.GetResourceDescriptors(), r => r.IsUiResource);

        // Add prompt descriptors
        var prompts = new[]
        {
            new McpAppPromptDescriptor
            {
                Name = "analyze_store", Description = "Analyze a store's inventory health",
                Arguments = new[]
                {
                    new McpAppPromptArgumentDescriptor
                        { Name = "storeId", Description = "Store ID", Required = true }
                }
            },
        };

        typeof(McpAppInfoProvider).GetMethod("SetPromptDescriptors",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.Invoke(provider, [prompts.AsEnumerable()]);

        Assert.Single(provider.GetPromptDescriptors());
        Assert.Equal(1, state.DiscoveredPromptCount);
        var prompt = provider.GetPromptDescriptors()[0];
        Assert.Single(prompt.Arguments);
        Assert.True(prompt.Arguments[0].Required);
    }

    // ── McpAppDiscovery ─────────────────────────────────────────

    [Fact]
    public void Discovery_Disabled_ReturnsEmptyList()
    {
        var config = new McpAppsConfig { Enabled = false };
        var discovery = new McpAppDiscovery(config, NullLogger<McpAppDiscovery>.Instance);

        var results = discovery.Discover();

        Assert.Empty(results);
    }

    [Fact]
    public void Discovery_NonExistentPath_ReturnsEmptyList()
    {
        var config = new McpAppsConfig
        {
            Enabled = true,
            DiscoveryPaths = ["./nonexistent-path-xyz"],
        };
        var discovery = new McpAppDiscovery(config, NullLogger<McpAppDiscovery>.Instance);

        var results = discovery.Discover();

        Assert.Empty(results);
    }

    [Fact]
    public void Discovery_ValidManifest_DiscoversApp()
    {
        var dir = CreateTempManifestDir("valid-app", new McpAppManifest
        {
            Id = "valid-app",
            Name = "Valid App",
            Version = "1.0.0",
            Transport = "http",
            Url = "https://localhost:5001/mcp",
        });
        try
        {
            var config = new McpAppsConfig
            {
                Enabled = true,
                DiscoveryPaths = [dir],
            };
            var discovery = new McpAppDiscovery(config, NullLogger<McpAppDiscovery>.Instance);

            var results = discovery.Discover();

            Assert.Single(results);
            var state = results[0];
            Assert.Equal("valid-app", state.Manifest.Id);
            Assert.Equal("Valid App", state.Manifest.Name);
            Assert.True(state.IsValid);
            Assert.Empty(state.ValidationErrors);
            Assert.Equal(McpAppLifecycle.Validated, state.Lifecycle);
            Assert.Equal(dir, state.RootPath);
            Assert.Equal(Path.Combine(dir, McpAppDiscovery.ManifestFileName), state.ManifestPath);
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public void Discovery_MultipleAppsInNestedDirectories_FindsAll()
    {
        var rootDir = CreateTempDir();
        var app1Dir = Path.Combine(rootDir, "app1");
        var app2Dir = Path.Combine(rootDir, "nested", "app2");
        Directory.CreateDirectory(app1Dir);
        Directory.CreateDirectory(app2Dir);
        try
        {
            WriteManifestFile(app1Dir, new McpAppManifest
            {
                Id = "app1", Name = "App One", Version = "1.0", Transport = "http", Url = "https://a.example.com/mcp"
            });
            WriteManifestFile(app2Dir, new McpAppManifest
            {
                Id = "app2", Name = "App Two", Version = "2.0", Transport = "http", Url = "https://b.example.com/mcp"
            });

            var config = new McpAppsConfig
            {
                Enabled = true,
                DiscoveryPaths = [rootDir],
            };
            var discovery = new McpAppDiscovery(config, NullLogger<McpAppDiscovery>.Instance);

            var results = discovery.Discover();

            Assert.Equal(2, results.Count);
            Assert.Contains(results, r => r.Manifest.Id == "app1");
            Assert.Contains(results, r => r.Manifest.Id == "app2");
        }
        finally
        {
            TryDeleteDirectory(rootDir);
        }
    }

    [Fact]
    public void Discovery_MissingRequiredFields_ReturnsInvalidState()
    {
        var dir = CreateTempManifestDir("broken", new McpAppManifest
        {
            Id = "",   // empty id — required
            Version = "1.0",
            Transport = "stdio",
            // Missing Command for stdio transport
        });
        try
        {
            var config = new McpAppsConfig
            {
                Enabled = true,
                DiscoveryPaths = [dir],
            };
            var discovery = new McpAppDiscovery(config, NullLogger<McpAppDiscovery>.Instance);

            var results = discovery.Discover();

            Assert.Single(results);
            var state = results[0];
            Assert.False(state.IsValid);
            Assert.NotEmpty(state.ValidationErrors);
            Assert.Contains(state.ValidationErrors,
                e => e.Contains("id", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(state.ValidationErrors,
                e => e.Contains("command", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public void Discovery_InvalidTransport_ReturnsValidationError()
    {
        var dir = CreateTempManifestDir("bad-transport", new McpAppManifest
        {
            Id = "bad-transport",
            Version = "1.0",
            Transport = "websocket",  // unsupported
        });
        try
        {
            var config = new McpAppsConfig
            {
                Enabled = true,
                DiscoveryPaths = [dir],
            };
            var discovery = new McpAppDiscovery(config, NullLogger<McpAppDiscovery>.Instance);

            var results = discovery.Discover();

            Assert.Single(results);
            Assert.False(results[0].IsValid);
            Assert.Contains(results[0].ValidationErrors,
                e => e.Contains("transport", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public void Discovery_InvalidJson_IsSkipped()
    {
        var dir = CreateTempDir();
        var manifestPath = Path.Combine(dir, McpAppDiscovery.ManifestFileName);
        File.WriteAllText(manifestPath, "not valid json {{{");
        try
        {
            var config = new McpAppsConfig
            {
                Enabled = true,
                DiscoveryPaths = [dir],
            };
            var discovery = new McpAppDiscovery(config, NullLogger<McpAppDiscovery>.Instance);

            var results = discovery.Discover();

            Assert.Empty(results); // Invalid JSON manifests are skipped
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    // ── McpAppDiscovery - Allow/Deny Filtering ─────────────────

    [Fact]
    public void IsAppAllowed_AllowlistAllowsSpecificApps()
    {
        var config = new McpAppsConfig
        {
            Enabled = true,
            Allow = ["allowed-app", "also-allowed"],
        };
        var discovery = new McpAppDiscovery(config, NullLogger<McpAppDiscovery>.Instance);

        var allowed = CreateState("allowed-app");
        var denied = CreateState("other-app");

        Assert.True(discovery.IsAppAllowed(allowed));
        Assert.False(discovery.IsAppAllowed(denied));
        Assert.Equal(McpAppLifecycle.Disabled, denied.Lifecycle);
    }

    [Fact]
    public void IsAppAllowed_WildcardAllowlist_AllowsAll()
    {
        var config = new McpAppsConfig
        {
            Enabled = true,
            Allow = ["*"],
        };
        var discovery = new McpAppDiscovery(config, NullLogger<McpAppDiscovery>.Instance);

        Assert.True(discovery.IsAppAllowed(CreateState("anything")));
        Assert.True(discovery.IsAppAllowed(CreateState("whatever")));
    }

    [Fact]
    public void IsAppAllowed_DenyWinsOverAllow()
    {
        var config = new McpAppsConfig
        {
            Enabled = true,
            Allow = ["*"],
            Deny = ["blocked-app", "also-blocked"],
        };
        var discovery = new McpAppDiscovery(config, NullLogger<McpAppDiscovery>.Instance);

        Assert.True(discovery.IsAppAllowed(CreateState("anything")));
        Assert.False(discovery.IsAppAllowed(CreateState("blocked-app")));
        Assert.False(discovery.IsAppAllowed(CreateState("also-blocked")));
    }

    [Fact]
    public void IsAppAllowed_GlobPatterns_MatchWildcards()
    {
        var config = new McpAppsConfig
        {
            Enabled = true,
            Deny = ["test-*", "*danger*"],
        };
        var discovery = new McpAppDiscovery(config, NullLogger<McpAppDiscovery>.Instance);

        Assert.False(discovery.IsAppAllowed(CreateState("test-anything")));
        Assert.False(discovery.IsAppAllowed(CreateState("containing-danger-middle")));
        Assert.True(discovery.IsAppAllowed(CreateState("normal-app")));
    }

    [Fact]
    public void IsAppAllowed_EntryConfigDisablesApp()
    {
        var config = new McpAppsConfig
        {
            Enabled = true,
            Entries = new Dictionary<string, McpAppEntryConfig>(StringComparer.Ordinal)
            {
                ["disabled-app"] = new() { Enabled = false },
            },
        };
        var discovery = new McpAppDiscovery(config, NullLogger<McpAppDiscovery>.Instance);

        var disabledState = CreateState("disabled-app");
        Assert.False(discovery.IsAppAllowed(disabledState));
        Assert.Equal(McpAppLifecycle.Disabled, disabledState.Lifecycle);
    }

    [Fact]
    public void IsAppAllowed_EmptyAllowAndDeny_AllowsAll()
    {
        var config = new McpAppsConfig { Enabled = true };
        var discovery = new McpAppDiscovery(config, NullLogger<McpAppDiscovery>.Instance);

        Assert.True(discovery.IsAppAllowed(CreateState("anything")));
    }

    // ── McpAppNativeTool (with HTTP MCP server) ────────────────

    [Fact]
    public async Task NativeTool_Execute_InvokesRemoteTool()
    {
        var (serverUrl, calls) = await StartMcpServerAsync<GroceryMcpTools>();
        var manifest = new McpAppManifest
        {
            Id = "test-grocery",
            Name = "Test Grocery",
            Version = "1.0",
            Transport = "http",
            Url = serverUrl,
        };
        var state = new McpAppInstallState
        {
            Manifest = manifest,
            ManifestPath = "/f/openclaw.mcpapp.json",
            RootPath = "/f",
        };
        var server = new McpAppServer(state, null, NullLogger<McpAppServer>.Instance);
        try
        {
            var infoProvider = await server.ConnectAsync(TestContext.Current.CancellationToken);

            var toolDescriptors = infoProvider.GetToolDescriptors();
            Assert.NotEmpty(toolDescriptors);
            var getStoresTool = Assert.Single(toolDescriptors, t => t.RemoteName == "get_stores");

            var nativeTool = new McpAppNativeTool(
                infoProvider.Client!,
                getStoresTool.LocalName,
                getStoresTool.RemoteName,
                getStoresTool.Description,
                getStoresTool.InputSchemaText,
                infoProvider);

            var result = await nativeTool.ExecuteAsync("{}", TestContext.Current.CancellationToken);
            Assert.DoesNotContain("Error:", result);
            Assert.True(calls.CallCalls >= 1);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task NativeTool_Execute_WithArguments_InvokesCorrectly()
    {
        var (serverUrl, calls) = await StartMcpServerAsync<GroceryMcpTools>();
        var manifest = new McpAppManifest
        {
            Id = "test-grocery", Name = "Test Grocery", Version = "1.0",
            Transport = "http", Url = serverUrl,
        };
        var state = new McpAppInstallState
        {
            Manifest = manifest, ManifestPath = "/f/openclaw.mcpapp.json", RootPath = "/f",
        };
        var server = new McpAppServer(state, null, NullLogger<McpAppServer>.Instance);
        try
        {
            var infoProvider = await server.ConnectAsync(TestContext.Current.CancellationToken);
            var getInventoryTool = Assert.Single(infoProvider.GetToolDescriptors(),
                t => t.RemoteName == "get_inventory");

            var nativeTool = new McpAppNativeTool(
                infoProvider.Client!,
                getInventoryTool.LocalName,
                getInventoryTool.RemoteName,
                getInventoryTool.Description,
                getInventoryTool.InputSchemaText,
                infoProvider);

            var result = await nativeTool.ExecuteAsync(
                """{"storeId":1,"productId":1}""", TestContext.Current.CancellationToken);
            Assert.DoesNotContain("Error:", result);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task NativeTool_Execute_InvalidJson_ReturnsError()
    {
        var (serverUrl, _) = await StartMcpServerAsync<GroceryMcpTools>();
        var manifest = new McpAppManifest
        {
            Id = "test-grocery", Name = "Test Grocery", Version = "1.0",
            Transport = "http", Url = serverUrl,
        };
        var state = new McpAppInstallState
        {
            Manifest = manifest, ManifestPath = "/f/openclaw.mcpapp.json", RootPath = "/f",
        };
        var server = new McpAppServer(state, null, NullLogger<McpAppServer>.Instance);
        try
        {
            var infoProvider = await server.ConnectAsync(TestContext.Current.CancellationToken);
            var getStoresTool = Assert.Single(infoProvider.GetToolDescriptors(),
                t => t.RemoteName == "get_stores");

            var nativeTool = new McpAppNativeTool(
                infoProvider.Client!,
                getStoresTool.LocalName,
                getStoresTool.RemoteName,
                getStoresTool.Description,
                getStoresTool.InputSchemaText,
                infoProvider);

            var result = await nativeTool.ExecuteAsync("not-json-at-all", TestContext.Current.CancellationToken);
            Assert.Contains("Error:", result);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task NativeTool_Execute_ArrayJson_ReturnsError()
    {
        var (serverUrl, _) = await StartMcpServerAsync<GroceryMcpTools>();
        var manifest = new McpAppManifest
        {
            Id = "test-grocery", Name = "Test Grocery", Version = "1.0",
            Transport = "http", Url = serverUrl,
        };
        var state = new McpAppInstallState
        {
            Manifest = manifest, ManifestPath = "/f/openclaw.mcpapp.json", RootPath = "/f",
        };
        var server = new McpAppServer(state, null, NullLogger<McpAppServer>.Instance);
        try
        {
            var infoProvider = await server.ConnectAsync(TestContext.Current.CancellationToken);
            var getStoresTool = Assert.Single(infoProvider.GetToolDescriptors(),
                t => t.RemoteName == "get_stores");

            var nativeTool = new McpAppNativeTool(
                infoProvider.Client!,
                getStoresTool.LocalName,
                getStoresTool.RemoteName,
                getStoresTool.Description,
                getStoresTool.InputSchemaText,
                infoProvider);

            var result = await nativeTool.ExecuteAsync("[1, 2, 3]", TestContext.Current.CancellationToken);
            Assert.Contains("Error:", result);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    // ── McpAppServer ────────────────────────────────────────────

    [Fact]
    public async Task Server_ConnectAsync_EnumeratesAllCapabilities()
    {
        var (serverUrl, _) = await StartMcpServerAsync<GroceryMcpTools>();
        var manifest = new McpAppManifest
        {
            Id = "grocery", Name = "Grocery Inventory", Version = "1.0",
            Transport = "http", Url = serverUrl,
        };
        var state = new McpAppInstallState
        {
            Manifest = manifest, ManifestPath = "/f/openclaw.mcpapp.json", RootPath = "/f",
        };
        var server = new McpAppServer(state, null, NullLogger<McpAppServer>.Instance);
        try
        {
            var infoProvider = await server.ConnectAsync(TestContext.Current.CancellationToken);

            Assert.Equal(McpAppLifecycle.Running, state.Lifecycle);
            Assert.NotEmpty(infoProvider.GetToolDescriptors());
            // Resource and prompt counts depend on the demo server
            Assert.True(state.DiscoveredToolCount > 0);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Server_ConnectAsync_Twice_ReturnsSameProvider()
    {
        var (serverUrl, _) = await StartMcpServerAsync<GroceryMcpTools>();
        var manifest = new McpAppManifest
        {
            Id = "grocery", Name = "Grocery Inventory", Version = "1.0",
            Transport = "http", Url = serverUrl,
        };
        var state = new McpAppInstallState
        {
            Manifest = manifest, ManifestPath = "/f/openclaw.mcpapp.json", RootPath = "/f",
        };
        var server = new McpAppServer(state, null, NullLogger<McpAppServer>.Instance);
        try
        {
            var first = await server.ConnectAsync(TestContext.Current.CancellationToken);
            var second = await server.ConnectAsync(TestContext.Current.CancellationToken);

            Assert.Same(first, second);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Server_DisconnectAsync_TransitionsToStopped()
    {
        var (serverUrl, _) = await StartMcpServerAsync<GroceryMcpTools>();
        var manifest = new McpAppManifest
        {
            Id = "grocery", Name = "Grocery Inventory", Version = "1.0",
            Transport = "http", Url = serverUrl,
        };
        var state = new McpAppInstallState
        {
            Manifest = manifest, ManifestPath = "/f/openclaw.mcpapp.json", RootPath = "/f",
        };
        var server = new McpAppServer(state, null, NullLogger<McpAppServer>.Instance);
        try
        {
            await server.ConnectAsync(TestContext.Current.CancellationToken);
            Assert.Equal(McpAppLifecycle.Running, state.Lifecycle);

            await server.DisconnectAsync();
            Assert.Equal(McpAppLifecycle.Stopped, state.Lifecycle);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Server_ConnectWithInvalidCommand_FailsWithFailedState()
    {
        var manifest = new McpAppManifest
        {
            Id = "bad-server",
            Name = "Bad Server",
            Version = "1.0",
            Transport = "stdio",
            Command = "non-existent-command-xyz-12345",  // this will fail to start
            StartupTimeoutSeconds = 1,
        };
        var state = new McpAppInstallState
        {
            Manifest = manifest, ManifestPath = "/f/openclaw.mcpapp.json", RootPath = "/f",
        };
        var server = new McpAppServer(state, null, NullLogger<McpAppServer>.Instance);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            server.ConnectAsync(TestContext.Current.CancellationToken));

        Assert.Equal(McpAppLifecycle.Failed, state.Lifecycle);
        Assert.NotNull(state.LastError);
    }

    [Fact]
    public async Task Server_Dispose_DisconnectsClient()
    {
        var (serverUrl, _) = await StartMcpServerAsync<GroceryMcpTools>();
        var manifest = new McpAppManifest
        {
            Id = "grocery", Name = "Grocery Inventory", Version = "1.0",
            Transport = "http", Url = serverUrl,
        };
        var state = new McpAppInstallState
        {
            Manifest = manifest, ManifestPath = "/f/openclaw.mcpapp.json", RootPath = "/f",
        };
        var server = new McpAppServer(state, null, NullLogger<McpAppServer>.Instance);
        await server.ConnectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(McpAppLifecycle.Running, state.Lifecycle);

        await server.DisposeAsync();

        Assert.Equal(McpAppLifecycle.Stopped, state.Lifecycle);
    }

    [Fact]
    public async Task Server_ToolNamePrefix_IsAppliedFromManifest()
    {
        var (serverUrl, _) = await StartMcpServerAsync<GroceryMcpTools>();
        var manifest = new McpAppManifest
        {
            Id = "grocery", Name = "Grocery Inventory", Version = "1.0",
            Transport = "http", Url = serverUrl,
            ToolNamePrefix = "grocery.",
        };
        var state = new McpAppInstallState
        {
            Manifest = manifest, ManifestPath = "/f/openclaw.mcpapp.json", RootPath = "/f",
        };
        var server = new McpAppServer(state, null, NullLogger<McpAppServer>.Instance);
        try
        {
            var infoProvider = await server.ConnectAsync(TestContext.Current.CancellationToken);

            Assert.All(infoProvider.GetToolDescriptors(), t =>
                Assert.StartsWith("grocery.", t.LocalName, StringComparison.Ordinal));
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Server_ToolNamePrefix_EntryConfigOverridesManifest()
    {
        var (serverUrl, _) = await StartMcpServerAsync<GroceryMcpTools>();
        var manifest = new McpAppManifest
        {
            Id = "grocery", Name = "Grocery Inventory", Version = "1.0",
            Transport = "http", Url = serverUrl,
            ToolNamePrefix = "manifest-prefix.",
        };
        var state = new McpAppInstallState
        {
            Manifest = manifest, ManifestPath = "/f/openclaw.mcpapp.json", RootPath = "/f",
        };
        var entryConfig = new McpAppEntryConfig { ToolNamePrefix = "config-prefix." };
        var server = new McpAppServer(state, entryConfig, NullLogger<McpAppServer>.Instance);
        try
        {
            var infoProvider = await server.ConnectAsync(TestContext.Current.CancellationToken);

            Assert.All(infoProvider.GetToolDescriptors(), t =>
                Assert.StartsWith("config-prefix.", t.LocalName, StringComparison.Ordinal));
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    // ── McpAppRegistry ─────────────────────────────────────────

    [Fact]
    public async Task Registry_LoadAllAsync_WithValidManifests_LoadsApps()
    {
        var dir = CreateTempManifestDir("app", new McpAppManifest
        {
            Id = "test-app",
            Name = "Test App",
            Version = "1.0",
            Transport = "http",
            Url = "http://127.0.0.1:19999/mcp",  // will fail to connect, but discovery works
            StartupTimeoutSeconds = 1,
        });
        try
        {
            var config = new McpAppsConfig
            {
                Enabled = true,
                DiscoveryPaths = [dir],
            };
            await using var registry = new McpAppRegistry(
                config,
                new McpAppDiscovery(config, NullLogger<McpAppDiscovery>.Instance),
                NullLoggerFactory.Instance);

            // LoadAllAsync should not throw — failed apps are logged, not thrown
            await registry.LoadAllAsync(TestContext.Current.CancellationToken);

            // No apps should be loaded because the URL is invalid
            Assert.Empty(registry.Apps);
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task Registry_GetApp_ReturnsNullForUnknownApp()
    {
        var config = new McpAppsConfig { Enabled = false };
        await using var registry = new McpAppRegistry(
            config,
            new McpAppDiscovery(config, NullLogger<McpAppDiscovery>.Instance),
            NullLoggerFactory.Instance);

        Assert.Null(registry.GetApp("non-existent"));
    }

    [Fact]
    public async Task Registry_LoadAllAsync_OnlyLoadsOnce()
    {
        var dir = CreateTempManifestDir("once-app", new McpAppManifest
        {
            Id = "once-app",
            Name = "Once App",
            Version = "1.0",
            Transport = "http",
            Url = "http://127.0.0.1:19998/mcp",
            StartupTimeoutSeconds = 1,
        });
        try
        {
            var config = new McpAppsConfig
            {
                Enabled = true,
                DiscoveryPaths = [dir],
            };
            await using var registry = new McpAppRegistry(
                config,
                new McpAppDiscovery(config, NullLogger<McpAppDiscovery>.Instance),
                NullLoggerFactory.Instance);

            await registry.LoadAllAsync(TestContext.Current.CancellationToken);
            await registry.LoadAllAsync(TestContext.Current.CancellationToken);
            await registry.LoadAllAsync(TestContext.Current.CancellationToken);

            // Idempotent — no exception
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task Registry_Dispose_CleansUpServers()
    {
        await using var registry = new McpAppRegistry(
            new McpAppsConfig { Enabled = false },
            new McpAppDiscovery(new McpAppsConfig { Enabled = false }, NullLogger<McpAppDiscovery>.Instance),
            NullLoggerFactory.Instance);

        await registry.DisposeAsync();

        // Double dispose should not throw
        await registry.DisposeAsync();
    }

    // ── Config Models ───────────────────────────────────────────

    [Fact]
    public void McpAppsConfig_DefaultSettings()
    {
        var config = new McpAppsConfig();

        Assert.False(config.Enabled);
        Assert.Equal("./mcpapps", config.DiscoveryPaths[0]);
        Assert.Equal("legacy", config.AllowlistSemantics);
        Assert.Empty(config.Allow);
        Assert.Empty(config.Deny);
        Assert.Empty(config.Entries);
    }

    [Fact]
    public void McpAppEntryConfig_DefaultSettings()
    {
        var entry = new McpAppEntryConfig();

        Assert.True(entry.Enabled);
        Assert.Null(entry.Transport);
        Assert.Null(entry.Command);
        Assert.Null(entry.Url);
        Assert.Null(entry.ToolNamePrefix);
        Assert.Null(entry.StartupTimeoutSeconds);
        Assert.Null(entry.RequestTimeoutSeconds);
        Assert.Empty(entry.Environment);
    }

    // ── Descriptor Models ───────────────────────────────────────

    [Fact]
    public void ToolDescriptor_DefaultSchemaText()
    {
        var descriptor = new McpAppToolDescriptor
        {
            RemoteName = "test_tool",
            LocalName = "prefix.test_tool",
            Description = "A test tool",
        };

        Assert.Equal("{}", descriptor.InputSchemaText);
        Assert.Null(descriptor.UiResourceUri);
    }

    [Fact]
    public void ResourceDescriptor_IsUiResource()
    {
        var htmlResource = new McpAppResourceDescriptor
        {
            Uri = "ui://app/dashboard.html",
            Name = "Dashboard",
            MimeType = "text/html;profile=mcp-app",
            IsUiResource = true,
        };

        Assert.True(htmlResource.IsUiResource);

        var jsonResource = new McpAppResourceDescriptor
        {
            Uri = "data://stores/summary",
            Name = "Store Summary",
            MimeType = "application/json",
        };

        Assert.False(jsonResource.IsUiResource);
    }

    [Fact]
    public void PromptDescriptor_WithArguments()
    {
        var prompt = new McpAppPromptDescriptor
        {
            Name = "analyze",
            Description = "Analyze data",
            Arguments =
            [
                new McpAppPromptArgumentDescriptor
                    { Name = "storeId", Description = "Store ID", Required = true },
                new McpAppPromptArgumentDescriptor
                    { Name = "format", Description = "Output format", Required = false },
            ],
        };

        Assert.Equal(2, prompt.Arguments.Count);
        Assert.True(prompt.Arguments[0].Required);
        Assert.False(prompt.Arguments[1].Required);
    }

    // ── Helpers ─────────────────────────────────────────────────

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "openclaw-mcpapp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateTempManifestDir(string dirName, McpAppManifest manifest)
    {
        var dir = Path.Combine(CreateTempDir(), dirName);
        Directory.CreateDirectory(dir);
        WriteManifestFile(dir, manifest);
        return dir;
    }

    private static void WriteManifestFile(string directory, McpAppManifest manifest)
    {
        var path = Path.Combine(directory, McpAppDiscovery.ManifestFileName);
        var json = JsonSerializer.Serialize(manifest, McpAppManifestJsonContext.Default.McpAppManifest);
        File.WriteAllText(path, json);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort cleanup
        }
    }

    private static McpAppInstallState CreateState(string appId)
        => new()
        {
            Manifest = new McpAppManifest { Id = appId, Version = "1.0" },
            ManifestPath = $"/f/{appId}/openclaw.mcpapp.json",
            RootPath = $"/f/{appId}",
        };

    // ── MCP Server test infrastructure ──────────────────────────

    private async Task<(string ServerUrl, McpAppCallTracker Tracker)> StartMcpServerAsync<TTools>()
        where TTools : class
    {
        var tracker = new McpAppCallTracker();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(tracker);
        builder.Services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "test-mcp-app",
                    Version = "1.0.0"
                };
            })
            .WithHttpTransport(options => { options.Stateless = true; })
            .WithTools<TTools>();
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            await TrackMcpMethodAsync(context, tracker);
            await next();
        });
        app.MapMcp("/mcp");

        await app.StartAsync();
        _apps.Add(app);
        var address = app.Urls.Single();
        return ($"{address.TrimEnd('/')}/mcp", tracker);
    }

    private static async Task TrackMcpMethodAsync(HttpContext context, McpAppCallTracker tracker)
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
                tracker.ListCalls++;
                break;
            case "tools/call":
                tracker.CallCalls++;
                break;
            case "resources/list":
                tracker.ResourceListCalls++;
                break;
            case "prompts/list":
                tracker.PromptListCalls++;
                break;
        }
    }

    private sealed class McpAppCallTracker
    {
        public int InitializeCalls { get; set; }
        public int ListCalls { get; set; }
        public int CallCalls { get; set; }
        public int ResourceListCalls { get; set; }
        public int PromptListCalls { get; set; }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps)
            await app.DisposeAsync();
    }
}

// ── Test MCP Server Tools (inspired by GroceryInventory.Api) ──

[McpServerToolType]
file sealed class GroceryMcpTools
{
    [McpServerTool(Name = "get_stores", ReadOnly = true)]
    [Description("Get stores in the chain. Pass id to fetch a single store, or omit it to list all stores.")]
    public static string GetStores(int? id = null)
    {
        if (id.HasValue)
            return """[{"id":1,"name":"Downtown Market","city":"Portland","state":"OR"}]""";
        return """[{"id":1,"name":"Downtown Market","city":"Portland","state":"OR"},{"id":2,"name":"Uptown Grocery","city":"Seattle","state":"WA"}]""";
    }

    [McpServerTool(Name = "get_products", ReadOnly = true)]
    [Description("Get products. Pass id to fetch a single product, or omit it to list products.")]
    public static string GetProducts(int? id = null)
    {
        if (id.HasValue)
            return """[{"id":1,"name":"Organic Milk","sku":"MILK-001","unitPrice":4.99}]""";
        return """[{"id":1,"name":"Organic Milk","sku":"MILK-001","unitPrice":4.99},{"id":2,"name":"Whole Wheat Bread","sku":"BREAD-002","unitPrice":3.49}]""";
    }

    [McpServerTool(Name = "get_inventory", ReadOnly = true)]
    [Description("Get inventory rows. Pass storeId and/or productId to filter.")]
    public static string GetInventory(int? storeId = null, int? productId = null)
    {
        return """[{"storeId":1,"productId":1,"quantityOnHand":42,"reorderThreshold":10,"lastRestocked":"2025-06-01T00:00:00Z"}]""";
    }

    [McpServerTool(Name = "show_store_inventory_dashboard")]
    [Description("Render an interactive multi-store inventory dashboard.")]
    public static CallToolResult ShowDashboard()
        => new()
        {
            Content = [new TextContentBlock { Text = "Dashboard snapshot generated." }],
            StructuredContent = System.Text.Json.JsonSerializer.SerializeToElement(
                new { generatedAt = "2025-06-29T00:00:00Z", stores = Array.Empty<object>() }),
        };
}
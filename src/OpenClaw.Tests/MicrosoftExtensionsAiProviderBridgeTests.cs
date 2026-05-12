using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent;
using OpenClaw.Agent.Plugins;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Plugins;
using OpenClaw.Gateway;
using OpenClaw.PluginKit;
using OpenClaw.Providers.MicrosoftExtensionsAI;
using OpenClaw.TestPluginFixtures;
using Xunit;

namespace OpenClaw.Tests;

public sealed class MicrosoftExtensionsAiProviderBridgeTests : IDisposable
{
    private readonly string _tempDir;

    public MicrosoftExtensionsAiProviderBridgeTests()
    {
        var tempRoot = Path.Join(Path.GetTempPath(), "openclaw-meai-provider-tests");
        Directory.CreateDirectory(tempRoot);
        _tempDir = Path.Join(tempRoot, Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            Debug.WriteLine($"Failed to delete temporary test directory '{_tempDir}': {ex}");
        }
    }

    [Fact]
    public void Register_ValidProvider_RegistersChatClient()
    {
        var context = CreateContext(new
        {
            providers = new[]
            {
                new
                {
                    providerId = "meai-test",
                    models = new[] { "model-a" },
                    factoryTypeName = typeof(DeterministicMicrosoftExtensionsAiChatClientFactory).AssemblyQualifiedName
                }
            }
        });

        new MicrosoftExtensionsAiProviderPlugin().Register(context);

        var provider = Assert.Single(context.Providers);
        Assert.Equal("meai-test", provider.ProviderId);
        Assert.Equal(["model-a"], provider.Models);
        Assert.NotNull(provider.Client);
    }

    [Fact]
    public void Register_MultipleProviders_RegistersEachProvider()
    {
        var factoryType = typeof(DeterministicMicrosoftExtensionsAiChatClientFactory).AssemblyQualifiedName;
        var context = CreateContext(new
        {
            providers = new[]
            {
                new { providerId = "meai-a", models = new[] { "model-a" }, factoryTypeName = factoryType },
                new { providerId = "meai-b", models = new[] { "model-b", "model-c" }, factoryTypeName = factoryType }
            }
        });

        new MicrosoftExtensionsAiProviderPlugin().Register(context);

        Assert.Equal(2, context.Providers.Count);
        Assert.Contains(context.Providers, provider => provider.ProviderId == "meai-a");
        Assert.Contains(context.Providers, provider => provider.ProviderId == "meai-b");
    }

    [Fact]
    public void Register_BlankProviderId_Throws()
    {
        var context = CreateContext(new
        {
            providers = new[]
            {
                new
                {
                    providerId = " ",
                    models = new[] { "model-a" },
                    factoryTypeName = typeof(DeterministicMicrosoftExtensionsAiChatClientFactory).AssemblyQualifiedName
                }
            }
        });

        var ex = Assert.Throws<InvalidOperationException>(() => new MicrosoftExtensionsAiProviderPlugin().Register(context));
        Assert.Contains("providerId", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_MissingFactoryType_Throws()
    {
        var context = CreateContext(new
        {
            providers = new[]
            {
                new
                {
                    providerId = "meai-test",
                    models = new[] { "model-a" },
                    factoryTypeName = ""
                }
            }
        });

        var ex = Assert.Throws<InvalidOperationException>(() => new MicrosoftExtensionsAiProviderPlugin().Register(context));
        Assert.Contains("factoryTypeName", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_InvalidFactoryType_Throws()
    {
        var context = CreateContext(new
        {
            providers = new[]
            {
                new
                {
                    providerId = "meai-test",
                    models = new[] { "model-a" },
                    factoryTypeName = typeof(InvalidMicrosoftExtensionsAiChatClientFactory).AssemblyQualifiedName
                }
            }
        });

        var ex = Assert.Throws<InvalidOperationException>(() => new MicrosoftExtensionsAiProviderPlugin().Register(context));
        Assert.Contains(nameof(IMicrosoftExtensionsAiChatClientFactory), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_NullFactoryResult_Throws()
    {
        var context = CreateContext(new
        {
            providers = new[]
            {
                new
                {
                    providerId = "meai-test",
                    models = new[] { "model-a" },
                    factoryTypeName = typeof(NullMicrosoftExtensionsAiChatClientFactory).AssemblyQualifiedName
                }
            }
        });

        var ex = Assert.Throws<InvalidOperationException>(() => new MicrosoftExtensionsAiProviderPlugin().Register(context));
        Assert.Contains("returned null", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Register_RelativeFactoryAssemblyOutsidePluginDirectory_Throws()
    {
        var context = CreateContext(new
        {
            providers = new[]
            {
                new
                {
                    providerId = "meai-test",
                    models = new[] { "model-a" },
                    factoryAssemblyPath = "../OpenClaw.TestPluginFixtures.dll",
                    factoryTypeName = typeof(DeterministicMicrosoftExtensionsAiChatClientFactory).FullName
                }
            }
        });

        var ex = Assert.Throws<InvalidOperationException>(() => new MicrosoftExtensionsAiProviderPlugin().Register(context));
        Assert.Contains("must stay within the bridge plugin directory", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_NativeDynamicPlugin_RegistersChatClientProvider()
    {
        var pluginDir = CreateBridgePluginDirectory("meai-native-load");
        var config = CreateNativeConfig(pluginDir, "model-a", "native fixture");

        await using var host = new NativeDynamicPluginHost(
            config,
            RuntimeModeResolver.Resolve(new RuntimeConfig { Mode = "jit" }, dynamicCodeSupported: true),
            NullLogger.Instance);

        _ = await host.LoadAsync(null, CancellationToken.None);

        var provider = Assert.Single(host.ProviderRegistrationsDetailed);
        Assert.Equal("meai-native-load", provider.ProviderId);
        Assert.Equal(["model-a"], provider.Models);
        Assert.NotNull(provider.Client);
    }

    [Fact]
    public async Task GatewayExecution_UsesBridgeClientThroughProviderRegistry()
    {
        var pluginDir = CreateBridgePluginDirectory("meai-runtime");
        var config = CreateNativeConfig(pluginDir, "runtime-model", "runtime fixture", providerId: "meai-runtime");

        await using var host = new NativeDynamicPluginHost(
            config,
            RuntimeModeResolver.Resolve(new RuntimeConfig { Mode = "jit" }, dynamicCodeSupported: true),
            NullLogger.Instance);

        _ = await host.LoadAsync(null, CancellationToken.None);
        var provider = Assert.Single(host.ProviderRegistrationsDetailed);

        var gatewayConfig = new GatewayConfig
        {
            Llm = new LlmProviderConfig
            {
                Provider = provider.ProviderId,
                Model = "runtime-model",
                RetryCount = 0,
                TimeoutSeconds = 0
            }
        };
        var providerRegistry = new LlmProviderRegistry();
        Assert.True(providerRegistry.TryRegisterDynamic(provider.ProviderId, provider.Client, "test", provider.Models));
        var storagePath = Path.Join(_tempDir, "runtime");
        Directory.CreateDirectory(storagePath);
        var providerUsage = new ProviderUsageTracker();
        var service = new GatewayLlmExecutionService(
            gatewayConfig,
            providerRegistry,
            new ProviderPolicyService(storagePath, NullLogger<ProviderPolicyService>.Instance),
            new RuntimeEventStore(storagePath, NullLogger<RuntimeEventStore>.Instance),
            new RuntimeMetrics(),
            providerUsage,
            NullLogger<GatewayLlmExecutionService>.Instance);

        var session = new Session
        {
            Id = "meai-session",
            ChannelId = "test",
            SenderId = "user"
        };
        var result = await service.GetResponseAsync(
            session,
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions(),
            new TurnContext { SessionId = session.Id, ChannelId = session.ChannelId },
            new LlmExecutionEstimate
            {
                EstimatedInputTokens = 4,
                EstimatedInputTokensByComponent = new InputTokenComponentEstimate()
            },
            CancellationToken.None);

        Assert.Equal("meai-runtime", result.ProviderId);
        Assert.Equal("runtime-model", result.ModelId);
        Assert.Contains("runtime fixture provider=meai-runtime model=runtime-model user=hello", result.Response.Text, StringComparison.Ordinal);
        var usage = Assert.Single(providerUsage.Snapshot());
        Assert.Equal("meai-runtime", usage.ProviderId);
        Assert.Equal("runtime-model", usage.ModelId);
        Assert.Equal(1, usage.Requests);
    }

    private NativeDynamicPluginsConfig CreateNativeConfig(
        string pluginDir,
        string model,
        string responseText,
        string providerId = "meai-native-load")
        => new()
        {
            Enabled = true,
            Load = new PluginLoadConfig { Paths = [pluginDir] },
            Entries = new Dictionary<string, PluginEntryConfig>(StringComparer.Ordinal)
            {
                ["openclaw-microsoft-extensions-ai-provider"] = new()
                {
                    Config = JsonSerializer.SerializeToElement(new
                    {
                        providers = new[]
                        {
                            new
                            {
                                providerId,
                                models = new[] { model },
                                factoryAssemblyPath = Path.GetFileName(typeof(DeterministicMicrosoftExtensionsAiChatClientFactory).Assembly.Location),
                                factoryTypeName = typeof(DeterministicMicrosoftExtensionsAiChatClientFactory).FullName,
                                config = new { responseText }
                            }
                        }
                    })
                }
            }
        };

    private string CreateBridgePluginDirectory(string directoryName)
    {
        var pluginDir = Path.Join(_tempDir, directoryName);
        Directory.CreateDirectory(pluginDir);
        var bridgeAssembly = typeof(MicrosoftExtensionsAiProviderPlugin).Assembly.Location;
        var fixtureAssembly = typeof(DeterministicMicrosoftExtensionsAiChatClientFactory).Assembly.Location;
        File.Copy(bridgeAssembly, Path.Join(pluginDir, Path.GetFileName(bridgeAssembly)), overwrite: true);
        File.Copy(fixtureAssembly, Path.Join(pluginDir, Path.GetFileName(fixtureAssembly)), overwrite: true);
        File.WriteAllText(
            Path.Join(pluginDir, "openclaw.native-plugin.json"),
            $$"""
            {
              "id": "openclaw-microsoft-extensions-ai-provider",
              "name": "OpenClaw Microsoft.Extensions.AI provider bridge",
              "version": "1.0.0",
              "assemblyPath": {{JsonSerializer.Serialize(Path.GetFileName(bridgeAssembly))}},
              "typeName": "OpenClaw.Providers.MicrosoftExtensionsAI.MicrosoftExtensionsAiProviderPlugin",
              "capabilities": ["providers"],
              "jitOnly": true
            }
            """);
        return pluginDir;
    }

    private static CapturingPluginContext CreateContext(object config)
        => new(JsonSerializer.SerializeToElement(config));

    private sealed class CapturingPluginContext(JsonElement? config) : INativeDynamicPluginContext
    {
        public string PluginId => "test-meai-plugin";
        public JsonElement? Config { get; } = config;
        public ILogger Logger => NullLogger.Instance;
        public List<(string ProviderId, string[] Models, IChatClient Client)> Providers { get; } = [];

        public void RegisterTool(ITool tool) { }
        public void RegisterChannel(IChannelAdapter adapter) { }
        public void RegisterCommand(string name, string description, Func<string, CancellationToken, Task<string>> handler) { }
        public void RegisterProvider(string providerId, string[] models, IChatClient client) => Providers.Add((providerId, models, client));
        public void RegisterMemoryProvider(string providerId, Func<NativeDynamicMemoryProviderContext, IMemoryStore> factory) { }
        public void RegisterHook(IToolHook hook) { }
        public void RegisterService(INativeDynamicPluginService service) { }
    }
}

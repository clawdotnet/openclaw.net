using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using OpenClaw.Agent.Routing;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;
using OpenClaw.TestPluginFixtures;
using Xunit;

namespace OpenClaw.Tests;

public sealed class CoreServicesExtensionsTests
{
    [Fact]
    public void AddOpenClawCoreServices_RegistersResolvedDynamicTurnRoutingConfig_WithNormalizerPrecedence()
    {
        var tempPath = Path.Join(Path.GetTempPath(), "openclaw-core-services-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);
        try
        {
            var bundlePath = Path.Join(tempPath, "router-bundle");
            var config = new GatewayConfig
            {
                Memory = new MemoryConfig
                {
                    StoragePath = tempPath
                },
                DynamicTurnRouting = new DynamicTurnRoutingConfig
                {
                    Enabled = true,
                    BundlePath = bundlePath,
                    Assets = new DynamicTurnRoutingAssetsConfig
                    {
                        ClassifierModelPath = "override/classifier.onnx"
                    },
                    Policy = new DynamicTurnRoutingPolicyConfig
                    {
                        EnableStickyTier = false
                    }
                }
            };

            var startup = new GatewayStartupContext
            {
                Config = config,
                RuntimeState = new GatewayRuntimeState
                {
                    RequestedMode = "jit",
                    EffectiveMode = GatewayRuntimeMode.Jit,
                    DynamicCodeSupported = true
                },
                IsNonLoopbackBind = false
            };

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddOptions();
            services.AddOpenClawCoreServices(startup);

            using var provider = services.BuildServiceProvider();
            var resolved = provider.GetRequiredService<ResolvedDynamicTurnRoutingConfig>();

            Assert.True(resolved.Enabled);
            Assert.Equal("bundle", resolved.Source);
            Assert.Equal("override/classifier.onnx", resolved.Assets.ClassifierModelPath);
            Assert.Equal(Path.Join(bundlePath, "embeddings.onnx"), resolved.Assets.EmbeddingModelPath);
            Assert.Equal(Path.Join(bundlePath, "tokenizer.json"), resolved.Assets.TokenizerPath);
            Assert.Equal(384, resolved.Assets.EmbeddingDimensions);
            Assert.False(resolved.Policy.EnableStickyTier);
            Assert.True(resolved.Policy.EnableMarginUpgrade);
            Assert.True(resolved.Policy.EnableUnderRoutingSafety);
        }
        finally
        {
            DeleteDirectoryIfPresent(tempPath);
        }
    }

    [Fact]
    public void AddOpenClawCoreServices_DisabledDynamicTurnRouting_UsesNoopRoutingPolicy()
    {
        var tempPath = Path.Join(Path.GetTempPath(), "openclaw-core-services-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);
        try
        {
            var config = new GatewayConfig
            {
                Memory = new MemoryConfig
                {
                    StoragePath = tempPath
                },
                DynamicTurnRouting = new DynamicTurnRoutingConfig
                {
                    Enabled = false
                }
            };

            var startup = new GatewayStartupContext
            {
                Config = config,
                RuntimeState = new GatewayRuntimeState
                {
                    RequestedMode = "jit",
                    EffectiveMode = GatewayRuntimeMode.Jit,
                    DynamicCodeSupported = true
                },
                IsNonLoopbackBind = false
            };

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddOptions();
            services.AddOpenClawCoreServices(startup);

            using var provider = services.BuildServiceProvider();
            var routingPolicy = provider.GetRequiredService<ITurnRoutingPolicy>();

            Assert.Same(NoopTurnRoutingPolicy.Instance, routingPolicy);
        }
        finally
        {
            DeleteDirectoryIfPresent(tempPath);
        }
    }

    [Fact]
    public void AddOpenClawCoreServices_NoBundlePath_UsesDirectAssetsInResolvedConfig()
    {
        var tempPath = Path.Join(Path.GetTempPath(), "openclaw-core-services-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);
        try
        {
            var config = new GatewayConfig
            {
                Memory = new MemoryConfig
                {
                    StoragePath = tempPath
                },
                DynamicTurnRouting = new DynamicTurnRoutingConfig
                {
                    Enabled = true,
                    Assets = new DynamicTurnRoutingAssetsConfig
                    {
                        ClassifierModelPath = "direct/classifier.onnx",
                        EmbeddingModelPath = "direct/embeddings.onnx",
                        TokenizerPath = "direct/tokenizer.json",
                        Dimensions = 256
                    }
                }
            };

            var startup = new GatewayStartupContext
            {
                Config = config,
                RuntimeState = new GatewayRuntimeState
                {
                    RequestedMode = "jit",
                    EffectiveMode = GatewayRuntimeMode.Jit,
                    DynamicCodeSupported = true
                },
                IsNonLoopbackBind = false
            };

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddOptions();
            services.AddOpenClawCoreServices(startup);

            using var provider = services.BuildServiceProvider();
            var resolved = provider.GetRequiredService<ResolvedDynamicTurnRoutingConfig>();

            Assert.True(resolved.Enabled);
            Assert.Equal("direct", resolved.Source);
            Assert.Equal("direct/classifier.onnx", resolved.Assets.ClassifierModelPath);
            Assert.Equal("direct/embeddings.onnx", resolved.Assets.EmbeddingModelPath);
            Assert.Equal("direct/tokenizer.json", resolved.Assets.TokenizerPath);
            Assert.Equal(256, resolved.Assets.EmbeddingDimensions);
        }
        finally
        {
            DeleteDirectoryIfPresent(tempPath);
        }
    }

    [Fact]
    public void AddOpenClawCoreServices_RegistersLearningConfigForLearningService()
    {
        var tempPath = Path.Join(Path.GetTempPath(), "openclaw-core-services-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);
        try
        {
            var config = new GatewayConfig
            {
                Memory = new MemoryConfig
                {
                    StoragePath = tempPath
                }
            };
            var startup = new GatewayStartupContext
            {
                Config = config,
                RuntimeState = new GatewayRuntimeState
                {
                    RequestedMode = "jit",
                    EffectiveMode = GatewayRuntimeMode.Jit,
                    DynamicCodeSupported = true
                },
                IsNonLoopbackBind = false
            };

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddOptions();
            services.AddOpenClawCoreServices(startup);

            using var provider = services.BuildServiceProvider();

            Assert.Same(config.Learning, provider.GetRequiredService<LearningConfig>());
            Assert.NotNull(provider.GetRequiredService<LearningService>());
            Assert.NotNull(provider.GetRequiredService<ISessionAdminStore>());
        }
        finally
        {
            DeleteDirectoryIfPresent(tempPath);
        }
    }

    [Fact]
    public void AddOpenClawCoreServices_WithSecurityServices_AllowsGatewayLlmExecutionServiceToResolveDuringValidation()
    {
        var tempPath = Path.Join(Path.GetTempPath(), "openclaw-core-services-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);
        try
        {
            var config = new GatewayConfig
            {
                Memory = new MemoryConfig
                {
                    StoragePath = tempPath
                }
            };
            var startup = new GatewayStartupContext
            {
                Config = config,
                RuntimeState = new GatewayRuntimeState
                {
                    RequestedMode = "jit",
                    EffectiveMode = GatewayRuntimeMode.Jit,
                    DynamicCodeSupported = true
                },
                IsNonLoopbackBind = false
            };

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddOptions();
            services.AddOpenClawCoreServices(startup);
            services.AddOpenClawSecurityServices(startup);

            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });

            Assert.NotNull(provider.GetRequiredService<GatewayLlmExecutionService>());
        }
        finally
        {
            DeleteDirectoryIfPresent(tempPath);
        }
    }

    [Fact]
    public async Task AddOpenClawCoreServices_RegistersEmbeddingBackfillHostedService()
    {
        var tempPath = Path.Join(Path.GetTempPath(), "openclaw-core-services-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);
        try
        {
            var config = new GatewayConfig
            {
                Memory = new MemoryConfig
                {
                    StoragePath = tempPath
                }
            };
            var startup = new GatewayStartupContext
            {
                Config = config,
                RuntimeState = new GatewayRuntimeState
                {
                    RequestedMode = "jit",
                    EffectiveMode = GatewayRuntimeMode.Jit,
                    DynamicCodeSupported = true
                },
                IsNonLoopbackBind = false
            };

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddOptions();
            services.AddOpenClawCoreServices(startup);

            await using var provider = services.BuildServiceProvider();

            var backfillService = provider.GetRequiredService<SqliteEmbeddingBackfillService>();
            var hostedDescriptor = services.Last(static descriptor => descriptor.ServiceType == typeof(IHostedService));
            var hostedService = Assert.IsAssignableFrom<IHostedService>(hostedDescriptor.ImplementationFactory!(provider));

            Assert.Same(backfillService, hostedService);
        }
        finally
        {
            DeleteDirectoryIfPresent(tempPath);
        }
    }

    [Fact]
    public void AddOpenClawCoreServices_MempalaceMemoryProvider_UsesApplicationStoppingCancellation()
    {
        var tempPath = Path.Join(Path.GetTempPath(), "openclaw-core-services-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);
        try
        {
            var pluginDir = CreateNativePlugin(
                tempPath,
                "native-dynamic-memory-fixture",
                typeof(ToolAndCommandPlugin).Assembly.Location,
                typeof(ToolAndCommandPlugin).FullName!,
                ["memory"]);

            var config = new GatewayConfig
            {
                Memory = new MemoryConfig
                {
                    Provider = "mempalace",
                    StoragePath = tempPath
                },
                Plugins = new PluginsConfig
                {
                    DynamicNative = new NativeDynamicPluginsConfig
                    {
                        Enabled = true,
                        Load = new PluginLoadConfig { Paths = [pluginDir] },
                        Entries = new Dictionary<string, PluginEntryConfig>(StringComparer.Ordinal)
                        {
                            ["native-dynamic-memory-fixture"] = new()
                            {
                                Config = JsonSerializer.SerializeToElement(new { memoryProviderId = "mempalace" })
                            }
                        }
                    }
                }
            };
            var startup = new GatewayStartupContext
            {
                Config = config,
                RuntimeState = new GatewayRuntimeState
                {
                    RequestedMode = "jit",
                    EffectiveMode = GatewayRuntimeMode.Jit,
                    DynamicCodeSupported = true
                },
                IsNonLoopbackBind = false
            };

            using var stopping = new CancellationTokenSource();
            stopping.Cancel();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddOptions();
            services.AddSingleton<IHostApplicationLifetime>(new TestHostApplicationLifetime(stopping.Token));
            services.AddOpenClawCoreServices(startup);

            using var provider = services.BuildServiceProvider();

            Assert.Throws<OperationCanceledException>(() => provider.GetRequiredService<IMemoryStore>());
        }
        finally
        {
            DeleteDirectoryIfPresent(tempPath);
        }
    }

    [Fact]
    public void AddOpenClawCoreServices_MempalaceMemoryProvider_RespectsBlockedDynamicPlugins()
    {
        var tempPath = Path.Join(Path.GetTempPath(), "openclaw-core-services-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);
        try
        {
            var startPath = Path.Join(tempPath, "plugin.start");
            var pluginId = "native-dynamic-memory-blocked";
            var pluginDir = CreateNativePlugin(
                tempPath,
                pluginId,
                typeof(ToolAndCommandPlugin).Assembly.Location,
                typeof(ToolAndCommandPlugin).FullName!,
                ["memory", "services"]);

            var config = new GatewayConfig
            {
                Memory = new MemoryConfig
                {
                    Provider = "mempalace",
                    StoragePath = tempPath
                },
                Plugins = new PluginsConfig
                {
                    DynamicNative = new NativeDynamicPluginsConfig
                    {
                        Enabled = true,
                        Load = new PluginLoadConfig { Paths = [pluginDir] },
                        Entries = new Dictionary<string, PluginEntryConfig>(StringComparer.Ordinal)
                        {
                            [pluginId] = new()
                            {
                                Config = JsonSerializer.SerializeToElement(new { memoryProviderId = "mempalace", startPath })
                            }
                        }
                    }
                }
            };
            var startup = new GatewayStartupContext
            {
                Config = config,
                RuntimeState = new GatewayRuntimeState
                {
                    RequestedMode = "jit",
                    EffectiveMode = GatewayRuntimeMode.Jit,
                    DynamicCodeSupported = true
                },
                IsNonLoopbackBind = false
            };

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddOptions();
            services.AddOpenClawCoreServices(startup);
            services.AddSingleton(sp =>
            {
                var pluginHealth = new PluginHealthService(
                    tempPath,
                    sp.GetRequiredService<ILogger<PluginHealthService>>(),
                    config.Plugins);
                pluginHealth.SetDisabled(pluginId, disabled: true, reason: "maintenance");
                return pluginHealth;
            });

            using var provider = services.BuildServiceProvider();

            var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IMemoryStore>());
            Assert.Contains("no dynamic native memory provider registered", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(startPath));
        }
        finally
        {
            DeleteDirectoryIfPresent(tempPath);
        }
    }

    [Fact]
    public void AddOpenClawCoreServices_MempalaceMemoryProvider_DisposesDynamicHostWhenNoProviderMatches()
    {
        var tempPath = Path.Join(Path.GetTempPath(), "openclaw-core-services-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);
        try
        {
            var startPath = Path.Join(tempPath, "plugin.start");
            var stopPath = Path.Join(tempPath, "plugin.stop");
            var pluginId = "native-dynamic-memory-mismatch";
            var pluginDir = CreateNativePlugin(
                tempPath,
                pluginId,
                typeof(ToolAndCommandPlugin).Assembly.Location,
                typeof(ToolAndCommandPlugin).FullName!,
                ["memory", "services"]);

            var config = new GatewayConfig
            {
                Memory = new MemoryConfig
                {
                    Provider = "mempalace",
                    StoragePath = tempPath
                },
                Plugins = new PluginsConfig
                {
                    DynamicNative = new NativeDynamicPluginsConfig
                    {
                        Enabled = true,
                        Load = new PluginLoadConfig { Paths = [pluginDir] },
                        Entries = new Dictionary<string, PluginEntryConfig>(StringComparer.Ordinal)
                        {
                            [pluginId] = new()
                            {
                                Config = JsonSerializer.SerializeToElement(new
                                {
                                    memoryProviderId = "other-memory",
                                    startPath,
                                    stopPath
                                })
                            }
                        }
                    }
                }
            };
            var startup = new GatewayStartupContext
            {
                Config = config,
                RuntimeState = new GatewayRuntimeState
                {
                    RequestedMode = "jit",
                    EffectiveMode = GatewayRuntimeMode.Jit,
                    DynamicCodeSupported = true
                },
                IsNonLoopbackBind = false
            };

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddOptions();
            services.AddOpenClawCoreServices(startup);

            using var provider = services.BuildServiceProvider();

            var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IMemoryStore>());
            Assert.Contains("no dynamic native memory provider registered", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(startPath));
            Assert.True(File.Exists(stopPath));
            Assert.Null(startup.NativeDynamicPluginHost);
        }
        finally
        {
            DeleteDirectoryIfPresent(tempPath);
        }
    }

    private static void DeleteDirectoryIfPresent(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string CreateNativePlugin(
        string rootPath,
        string id,
        string assemblyPath,
        string typeName,
        string[] capabilities)
    {
        var pluginDir = Path.Join(rootPath, id);
        Directory.CreateDirectory(pluginDir);
        var localAssemblyName = Path.GetFileName(assemblyPath);
        File.Copy(assemblyPath, Path.Join(pluginDir, localAssemblyName), overwrite: true);

        var manifest = $$"""
        {
          "id": "{{id}}",
          "name": "{{id}}",
          "version": "1.0.0",
          "assemblyPath": {{JsonSerializer.Serialize(localAssemblyName)}},
          "typeName": {{JsonSerializer.Serialize(typeName)}},
          "capabilities": {{JsonSerializer.Serialize(capabilities)}},
          "jitOnly": true
        }
        """;
        File.WriteAllText(Path.Join(pluginDir, "openclaw.native-plugin.json"), manifest);
        return pluginDir;
    }

    private sealed class TestHostApplicationLifetime(CancellationToken applicationStopping) : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => TestContext.Current.CancellationToken;
        public CancellationToken ApplicationStopping => applicationStopping;
        public CancellationToken ApplicationStopped => TestContext.Current.CancellationToken;
        public void StopApplication()
        {
        }
    }
}

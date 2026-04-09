using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Validation;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Extensions;
using OpenClaw.Gateway.Models;
using Xunit;

namespace OpenClaw.Tests;

[Collection(DynamicProviderRegistryCollection.Name)]
public sealed class ModelProfileSelectionTests
{
    [Fact]
    public void Registry_WhenProfilesMissing_CreatesImplicitDefaultProfile()
    {
        LlmClientFactory.ResetDynamicProviders();
        LlmClientFactory.RegisterProvider("fake-profile-tests", new EvaluationChatClient());

        var config = new GatewayConfig
        {
            Llm = new LlmProviderConfig
            {
                Provider = "fake-profile-tests",
                Model = "legacy-model"
            }
        };

        var registry = new ConfiguredModelProfileRegistry(config, NullLogger<ConfiguredModelProfileRegistry>.Instance);
        var statuses = registry.ListStatuses();

        var profile = Assert.Single(statuses);
        Assert.Equal("default", registry.DefaultProfileId);
        Assert.Equal("default", profile.Id);
        Assert.True(profile.IsDefault);
        Assert.True(profile.IsImplicit);
        Assert.Equal("legacy-model", profile.ModelId);
    }

    [Fact]
    public void SelectionPolicy_ExplicitProfileFallsBackWhenCapabilitiesMissing()
    {
        LlmClientFactory.ResetDynamicProviders();
        LlmClientFactory.RegisterProvider("fake-profile-tests", new EvaluationChatClient());

        var config = BuildProfileConfig();
        var registry = new ConfiguredModelProfileRegistry(config, NullLogger<ConfiguredModelProfileRegistry>.Instance);
        var policy = new DefaultModelSelectionPolicy(registry);
        var session = new Session
        {
            Id = "s1",
            ChannelId = "test",
            SenderId = "user",
            ModelProfileId = "gemma4-local",
            FallbackModelProfileIds = ["frontier-tools"],
            ModelRequirements = new ModelSelectionRequirements
            {
                SupportsTools = true
            }
        };

        var selection = policy.Resolve(new OpenClaw.Core.Abstractions.ModelSelectionRequest
        {
            Session = session,
            Messages = [new ChatMessage(ChatRole.User, "Use a tool")],
            Options = new ChatOptions
            {
                Tools =
                [
                    AIFunctionFactory.CreateDeclaration(
                        "record_observation",
                        "Record an observation",
                        JsonDocument.Parse("""{"type":"object","properties":{"value":{"type":"string"}},"required":["value"]}""").RootElement.Clone(),
                        returnJsonSchema: null)
                ]
            },
            Streaming = false
        });

        Assert.Equal("frontier-tools", selection.SelectedProfileId);
        Assert.Contains("Falling back from 'gemma4-local'", selection.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectionPolicy_PrefersTaggedProfileWhenRequirementsAreEqual()
    {
        LlmClientFactory.ResetDynamicProviders();
        LlmClientFactory.RegisterProvider("fake-profile-tests", new EvaluationChatClient());

        var config = BuildProfileConfig();
        var registry = new ConfiguredModelProfileRegistry(config, NullLogger<ConfiguredModelProfileRegistry>.Instance);
        var policy = new DefaultModelSelectionPolicy(registry);
        var session = new Session
        {
            Id = "s2",
            ChannelId = "test",
            SenderId = "user",
            PreferredModelTags = ["private", "local"]
        };

        var selection = policy.Resolve(new OpenClaw.Core.Abstractions.ModelSelectionRequest
        {
            Session = session,
            Messages = [new ChatMessage(ChatRole.User, "Hello")],
            Options = new ChatOptions(),
            Streaming = false
        });

        Assert.Equal("gemma4-local", selection.SelectedProfileId);
    }

    [Fact]
    public void ConfigValidator_RejectsUnknownDefaultModelProfile()
    {
        var config = new GatewayConfig
        {
            Models = new ModelsConfig
            {
                DefaultProfile = "missing-profile",
                Profiles =
                [
                    new ModelProfileConfig
                    {
                        Id = "gemma4-local",
                        Provider = "ollama",
                        Model = "gemma4"
                    }
                ]
            }
        };

        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, error => error.Contains("Models.DefaultProfile", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigValidator_RejectsUnknownFallbackProfileIds()
    {
        var config = new GatewayConfig
        {
            Models = new ModelsConfig
            {
                Profiles =
                [
                    new ModelProfileConfig
                    {
                        Id = "gemma4-local",
                        Provider = "ollama",
                        Model = "gemma4",
                        FallbackProfileIds = ["missing-profile"]
                    }
                ]
            },
            Routing = new RoutingConfig
            {
                Routes = new Dictionary<string, AgentRouteConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["telegram:coder"] = new()
                    {
                        ModelProfileId = "missing-profile"
                    }
                }
            }
        };

        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, error => error.Contains("Models.Profiles.gemma4-local.FallbackProfileIds", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("Routing.Routes.telegram:coder.ModelProfileId", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigValidator_RejectsExplicitRouteProfileWhenUsingImplicitDefaultOnly()
    {
        var config = new GatewayConfig
        {
            Llm = new LlmProviderConfig
            {
                Provider = "openai",
                Model = "gpt-4.1"
            },
            Routing = new RoutingConfig
            {
                Routes = new Dictionary<string, AgentRouteConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["telegram:coder"] = new()
                    {
                        ModelProfileId = "gemma4-local"
                    }
                }
            }
        };

        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, error => error.Contains("Routing.Routes.telegram:coder.ModelProfileId", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadGatewayConfig_BindsModelProfiles()
    {
        var values = new Dictionary<string, string?>
        {
            ["OpenClaw:Llm:Provider"] = "openai",
            ["OpenClaw:Llm:Model"] = "gpt-4.1",
            ["OpenClaw:Models:DefaultProfile"] = "gemma4-prod",
            ["OpenClaw:Models:Profiles:0:Id"] = "gemma4-local",
            ["OpenClaw:Models:Profiles:0:Provider"] = "ollama",
            ["OpenClaw:Models:Profiles:0:Model"] = "gemma4",
            ["OpenClaw:Models:Profiles:0:Tags:0"] = "local",
            ["OpenClaw:Models:Profiles:0:Capabilities:SupportsStreaming"] = "true",
            ["OpenClaw:Models:Profiles:1:Id"] = "gemma4-prod",
            ["OpenClaw:Models:Profiles:1:Provider"] = "openai-compatible",
            ["OpenClaw:Models:Profiles:1:Model"] = "gemma-4",
            ["OpenClaw:Models:Profiles:1:BaseUrl"] = "https://example.invalid/v1",
            ["OpenClaw:Models:Profiles:1:Capabilities:SupportsTools"] = "true"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var config = GatewayBootstrapExtensions.LoadGatewayConfig(configuration);

        Assert.Equal("gemma4-prod", config.Models.DefaultProfile);
        Assert.Equal(2, config.Models.Profiles.Count);
        Assert.Equal("ollama", config.Models.Profiles[0].Provider);
        Assert.Equal("https://example.invalid/v1", config.Models.Profiles[1].BaseUrl);
        Assert.NotNull(config.Models.Profiles[1].Capabilities);
        Assert.True(config.Models.Profiles[1].Capabilities!.SupportsTools);
    }

    [Fact]
    public void Registry_WhenCapabilitiesOmitted_UsesProviderGuessAndResolvesSecrets()
    {
        LlmClientFactory.ResetDynamicProviders();
        LlmClientFactory.RegisterProvider("openai-compatible", new EvaluationChatClient());

        Environment.SetEnvironmentVariable("MODEL_PROFILE_ENDPOINT", "https://example.invalid/v1");
        Environment.SetEnvironmentVariable("MODEL_PROFILE_KEY", "secret-token");
        try
        {
            var config = new GatewayConfig
            {
                Llm = new LlmProviderConfig
                {
                    Provider = "openai",
                    Model = "gpt-4.1",
                    ApiKey = "fallback-key"
                },
                Models = new ModelsConfig
                {
                    Profiles =
                    [
                        new ModelProfileConfig
                        {
                            Id = "gemma4-prod",
                            Provider = "openai-compatible",
                            Model = "gemma-4",
                            BaseUrl = "env:MODEL_PROFILE_ENDPOINT",
                            ApiKey = "env:MODEL_PROFILE_KEY"
                        }
                    ]
                }
            };

            var registry = new ConfiguredModelProfileRegistry(config, NullLogger<ConfiguredModelProfileRegistry>.Instance);
            Assert.True(registry.TryGet("gemma4-prod", out var profile));
            Assert.NotNull(profile);
            Assert.Equal("https://example.invalid/v1", profile!.BaseUrl);
            Assert.Equal("secret-token", profile.ApiKey);
            Assert.True(profile.Capabilities.SupportsTools);
            Assert.True(profile.Capabilities.SupportsStructuredOutputs);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MODEL_PROFILE_ENDPOINT", null);
            Environment.SetEnvironmentVariable("MODEL_PROFILE_KEY", null);
        }
    }

    [Fact]
    public void SelectionPolicy_SkipsUnavailableExplicitProfileAndFallsBack()
    {
        LlmClientFactory.ResetDynamicProviders();
        LlmClientFactory.RegisterProvider("fake-profile-tests", new EvaluationChatClient());

        var config = new GatewayConfig
        {
            Llm = new LlmProviderConfig
            {
                Provider = "fake-profile-tests",
                Model = "legacy-model"
            },
            Models = new ModelsConfig
            {
                Profiles =
                [
                    new ModelProfileConfig
                    {
                        Id = "broken-remote",
                        Provider = "openai-compatible",
                        Model = "gemma-4",
                        FallbackProfileIds = ["frontier-tools"],
                        Capabilities = new ModelCapabilities
                        {
                            SupportsTools = true
                        }
                    },
                    new ModelProfileConfig
                    {
                        Id = "frontier-tools",
                        Provider = "fake-profile-tests",
                        Model = "frontier",
                        Capabilities = new ModelCapabilities
                        {
                            SupportsTools = true,
                            SupportsStreaming = true,
                            SupportsSystemMessages = true
                        }
                    }
                ]
            }
        };

        var registry = new ConfiguredModelProfileRegistry(config, NullLogger<ConfiguredModelProfileRegistry>.Instance);
        var policy = new DefaultModelSelectionPolicy(registry);
        var selection = policy.Resolve(new OpenClaw.Core.Abstractions.ModelSelectionRequest
        {
            ExplicitProfileId = "broken-remote",
            Session = new Session
            {
                Id = "s3",
                ChannelId = "test",
                SenderId = "user"
            },
            Messages = [new ChatMessage(ChatRole.User, "Need tools")],
            Options = new ChatOptions
            {
                Tools =
                [
                    AIFunctionFactory.CreateDeclaration(
                        "record_observation",
                        "Record an observation",
                        JsonDocument.Parse("""{"type":"object","properties":{"value":{"type":"string"}},"required":["value"]}""").RootElement.Clone(),
                        returnJsonSchema: null)
                ]
            }
        });

        Assert.Equal("frontier-tools", selection.SelectedProfileId);
        Assert.Contains("broken-remote", selection.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GatewayExecution_FallsBackWhenSelectedProfileContextTooSmall()
    {
        LlmClientFactory.ResetDynamicProviders();
        LlmClientFactory.RegisterProvider("fake-profile-tests", new EvaluationChatClient());

        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-model-selection", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);
        var config = BuildProfileConfig();
        var registry = new ConfiguredModelProfileRegistry(config, NullLogger<ConfiguredModelProfileRegistry>.Instance);
        var policy = new DefaultModelSelectionPolicy(registry);
        var service = new GatewayLlmExecutionService(
            config,
            registry,
            policy,
            new ProviderPolicyService(storagePath, NullLogger<ProviderPolicyService>.Instance),
            new RuntimeEventStore(storagePath, NullLogger<RuntimeEventStore>.Instance),
            new RuntimeMetrics(),
            new ProviderUsageTracker(),
            NullLogger<GatewayLlmExecutionService>.Instance);

        var session = new Session
        {
            Id = "s4",
            ChannelId = "test",
            SenderId = "user",
            ModelProfileId = "gemma4-local"
        };

        var result = await service.GetResponseAsync(
            session,
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions(),
            new TurnContext { SessionId = session.Id, ChannelId = session.ChannelId },
            new LlmExecutionEstimate
            {
                EstimatedInputTokens = 200_000,
                EstimatedInputTokensByComponent = new InputTokenComponentEstimate()
            },
            CancellationToken.None);

        Assert.Equal("frontier-tools", result.ProfileId);
        Assert.Equal("frontier", result.ModelId);
    }

    [Fact]
    public async Task GatewayExecutionService_CompatibilityConstructor_UsesInjectedProviderRegistry()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-model-compat", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);
        var config = new GatewayConfig
        {
            Llm = new LlmProviderConfig
            {
                Provider = "fake-injected-provider",
                Model = "legacy-model"
            },
            Models = new ModelsConfig
            {
                DefaultProfile = "default",
                Profiles = new List<ModelProfileConfig>
                {
                    new ModelProfileConfig
                    {             
                        Id = "default",
                        Provider = "fake-injected-provider",
                        Model = "legacy-model"
                    }
                }
            }
        };
        var providerRegistry = new LlmProviderRegistry(); 
        providerRegistry.RegisterDefault(config.Llm, new EvaluationChatClient());
        var service = new GatewayLlmExecutionService(
            config,
            providerRegistry,
            new ProviderPolicyService(storagePath, NullLogger<ProviderPolicyService>.Instance),
            new RuntimeEventStore(storagePath, NullLogger<RuntimeEventStore>.Instance),
            new RuntimeMetrics(),
            new ProviderUsageTracker(),
            NullLogger<GatewayLlmExecutionService>.Instance);

        var session = new Session
        {
            Id = "s5",
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
                EstimatedInputTokens = 16,
                EstimatedInputTokensByComponent = new InputTokenComponentEstimate()
            },
            CancellationToken.None);

        Assert.Equal("default", result.ProfileId);
        Assert.Equal("fake-injected-provider", result.ProviderId);
        Assert.Equal("legacy-model", result.ModelId);
    }

    [Fact]
    public async Task EvaluationRunner_PersistsJsonAndMarkdownReport()
    {
        LlmClientFactory.ResetDynamicProviders();
        LlmClientFactory.RegisterProvider("fake-profile-tests", new EvaluationChatClient());

        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-model-evals", Guid.NewGuid().ToString("N"));
        var config = BuildProfileConfig();
        config.Memory.StoragePath = storagePath;
        var registry = new ConfiguredModelProfileRegistry(config, NullLogger<ConfiguredModelProfileRegistry>.Instance);
        var runner = new ModelEvaluationRunner(registry, config, NullLogger<ModelEvaluationRunner>.Instance);

        var report = await runner.RunAsync(new ModelEvaluationRequest
        {
            ProfileId = "frontier-tools",
            ScenarioIds = ["plain-chat", "json-extraction", "tool-invocation"],
            IncludeMarkdown = true
        }, CancellationToken.None);

        Assert.Equal("frontier-tools", Assert.Single(report.Profiles).ProfileId);
        Assert.True(File.Exists(report.JsonPath));
        Assert.True(File.Exists(report.MarkdownPath));
        Assert.Contains("Model Evaluation Report", report.Markdown, StringComparison.Ordinal);
        Assert.All(report.Profiles.SelectMany(static profile => profile.Scenarios), result => Assert.NotEqual("failed", result.Status));
    }

    private static GatewayConfig BuildProfileConfig()
        => new()
        {
            Llm = new LlmProviderConfig
            {
                Provider = "fake-profile-tests",
                Model = "legacy-model"
            },
            Models = new ModelsConfig
            {
                DefaultProfile = "gemma4-local",
                Profiles =
                [
                    new ModelProfileConfig
                    {
                        Id = "gemma4-local",
                        Provider = "fake-profile-tests",
                        Model = "gemma4",
                        Tags = ["local", "private", "cheap"],
                        FallbackProfileIds = ["frontier-tools"],
                        Capabilities = new ModelCapabilities
                        {
                            SupportsStreaming = true,
                            SupportsSystemMessages = true,
                            SupportsVision = true,
                            SupportsImageInput = true,
                            MaxContextTokens = 131072,
                            MaxOutputTokens = 8192
                        }
                    },
                    new ModelProfileConfig
                    {
                        Id = "frontier-tools",
                        Provider = "fake-profile-tests",
                        Model = "frontier",
                        Tags = ["tool-reliable", "frontier"],
                        Capabilities = new ModelCapabilities
                        {
                            SupportsTools = true,
                            SupportsJsonSchema = true,
                            SupportsStructuredOutputs = true,
                            SupportsStreaming = true,
                            SupportsParallelToolCalls = true,
                            SupportsReasoningEffort = true,
                            SupportsSystemMessages = true,
                            SupportsVision = true,
                            SupportsImageInput = true,
                            MaxContextTokens = 1_000_000,
                            MaxOutputTokens = 32768
                        }
                    }
                ]
            }
        };

    private sealed class EvaluationChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var lastUser = messages.LastOrDefault(static message => message.Role == ChatRole.User);
            var prompt = string.Join("\n", lastUser?.Contents.OfType<TextContent>().Select(static content => content.Text) ?? []);

            if (options?.Tools is { Count: > 0 })
            {
                var call = new FunctionCallContent("call_1", "record_observation", new Dictionary<string, object?> { ["value"] = "gemma4" });
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
            }

            if (options?.ResponseFormat is not null)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"animal":"fox","count":3}""")));
            }

            if (prompt.Contains("code word", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "The code word was maple-42.")));
            if (prompt.Contains("branch name", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "The branch name was gemma4-rollout.")));
            if (prompt.Contains("color", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "red")));

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "READY")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("Gemma")]);
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(" streams.")]);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

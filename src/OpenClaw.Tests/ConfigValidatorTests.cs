using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Validation;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ConfigValidatorTests
{
    [Fact]
    public void ConfigValidator_RejectsInvalidTelegramLongPollingSettings()
    {
        var config = new GatewayConfig
        {
            Channels = new ChannelsConfig
            {
                Telegram = new TelegramChannelConfig
                {
                    UpdateMode = "poll",
                    PollingTimeoutSeconds = 0,
                    PollingRetryDelaySeconds = 301
                }
            }
        };

        var errors = ConfigValidator.Validate(config);

        Assert.Contains(errors, error => error.Contains("Channels.Telegram.UpdateMode", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("Channels.Telegram.PollingTimeoutSeconds", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("Channels.Telegram.PollingRetryDelaySeconds", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigValidator_AcceptsCaseInsensitiveTelegramUpdateMode()
    {
        var config = new GatewayConfig
        {
            Channels = new ChannelsConfig
            {
                Telegram = new TelegramChannelConfig
                {
                    UpdateMode = " Long-Polling "
                }
            }
        };

        var errors = ConfigValidator.Validate(config);

        Assert.DoesNotContain(
            errors,
            error => error.Contains("Channels.Telegram.UpdateMode", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigValidator_RejectsDynamicTurnRoutingTierWithUnknownProfile()
    {
        var config = new GatewayConfig
        {
            Llm = new LlmProviderConfig { Provider = "fake-profile-tests", Model = "legacy" },
            Models = new ModelsConfig
            {
                Profiles =
                [
                    new ModelProfileConfig
                    {
                        Id = "local-freeform",
                        Provider = "fake-profile-tests",
                        Model = "gemma-3n",
                        Tags = ["local"],
                        Capabilities = new ModelCapabilities { SupportsStreaming = true, SupportsSystemMessages = true }
                    },
                    new ModelProfileConfig
                    {
                        Id = "frontier-tools",
                        Provider = "fake-profile-tests",
                        Model = "gpt-4.1-mini",
                        Tags = ["tools"],
                        Capabilities = new ModelCapabilities { SupportsTools = true, SupportsStreaming = true, SupportsSystemMessages = true }
                    }
                ]
            },
            DynamicTurnRouting = new DynamicTurnRoutingConfig
            {
                Enabled = true,
                Assets = new DynamicTurnRoutingAssetsConfig
                {
                    ClassifierModelPath = "models/routing/squilla_classifier.onnx",
                    EmbeddingModelPath = "models/routing/minilm/model.onnx",
                    TokenizerPath = "models/routing/minilm/tokenizer.json",
                    Dimensions = 384
                },
                Policy = new DynamicTurnRoutingPolicyConfig
                {
                    Tiers = new DynamicTurnRoutingTierMap
                    {
                        T0 = new DynamicTurnRoutingTierTarget { ModelProfileId = "local-freeform", DisableTools = true },
                        T1 = new DynamicTurnRoutingTierTarget { ModelProfileId = "missing-mini" },
                        T2 = new DynamicTurnRoutingTierTarget { ModelProfileId = "frontier-tools" },
                        T3 = new DynamicTurnRoutingTierTarget { ModelProfileId = "frontier-tools" }
                    }
                }
            }
        };

        var errors = ConfigValidator.Validate(config);

        Assert.Contains(errors, error =>
            error.Contains("DynamicTurnRouting.Policy.Tiers.T1.ModelProfileId", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigValidator_AllowsBundlePathOnlyDynamicTurnRoutingConfig()
    {
        var config = new GatewayConfig
        {
            DynamicTurnRouting = new DynamicTurnRoutingConfig
            {
                Enabled = true,
                BundlePath = "artifacts/tmp/router-bundle"
            }
        };

        var errors = ConfigValidator.Validate(config);

        Assert.DoesNotContain(errors, error => error.Contains("DynamicTurnRouting.BundlePath", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, error => error.Contains("DynamicTurnRouting requires an embedding model", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, error => error.Contains("DynamicTurnRouting requires a tokenizer path", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigValidator_RejectsDynamicTurnRoutingEmbeddingWithoutTokenizerPath()
    {
        var config = new GatewayConfig
        {
            DynamicTurnRouting = new DynamicTurnRoutingConfig
            {
                Enabled = true,
                Assets = new DynamicTurnRoutingAssetsConfig
                {
                    ClassifierModelPath = "models/routing/squilla_classifier.onnx",
                    EmbeddingModelPath = "models/routing/minilm/model.onnx",
                    TokenizerPath = ""
                },
                Policy = new DynamicTurnRoutingPolicyConfig
                {
                    Tiers = new DynamicTurnRoutingTierMap
                    {
                        T0 = new DynamicTurnRoutingTierTarget { ModelProfileId = "frontier-tools" },
                        T1 = new DynamicTurnRoutingTierTarget { ModelProfileId = "frontier-tools" },
                        T2 = new DynamicTurnRoutingTierTarget { ModelProfileId = "frontier-tools" },
                        T3 = new DynamicTurnRoutingTierTarget { ModelProfileId = "frontier-tools" }
                    }
                }
            },
            Models = new ModelsConfig
            {
                Profiles =
                [
                    new ModelProfileConfig { Id = "frontier-tools", Provider = "fake", Model = "gpt-4.1" }
                ]
            }
        };

        var errors = ConfigValidator.Validate(config);

        Assert.Contains(errors, error => error.Contains("DynamicTurnRouting requires a tokenizer path", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigValidator_RejectsModernPolicyTierWithUnknownProfile()
    {
        var config = new GatewayConfig
        {
            Models = new ModelsConfig
            {
                Profiles =
                [
                    new ModelProfileConfig { Id = "frontier-tools", Provider = "fake", Model = "gpt-4.1" }
                ]
            },
            DynamicTurnRouting = new DynamicTurnRoutingConfig
            {
                Enabled = true,
                Assets = new DynamicTurnRoutingAssetsConfig
                {
                    ClassifierModelPath = "classifier.onnx",
                    EmbeddingModelPath = "embeddings.onnx",
                    TokenizerPath = "tokenizer.json"
                },
                Policy = new DynamicTurnRoutingPolicyConfig
                {
                    Tiers = new DynamicTurnRoutingTierMap
                    {
                        T0 = new DynamicTurnRoutingTierTarget { ModelProfileId = "missing-profile" },
                        T1 = new DynamicTurnRoutingTierTarget { ModelProfileId = "frontier-tools" },
                        T2 = new DynamicTurnRoutingTierTarget { ModelProfileId = "frontier-tools" },
                        T3 = new DynamicTurnRoutingTierTarget { ModelProfileId = "frontier-tools" }
                    }
                }
            }
        };

        var errors = ConfigValidator.Validate(config);

        Assert.Contains(errors, error => error.Contains("DynamicTurnRouting.Policy.Tiers.T0.ModelProfileId", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigValidator_AllowsModernAssetsWithPolicyTierMapping()
    {
        var config = new GatewayConfig
        {
            Models = new ModelsConfig
            {
                Profiles =
                [
                    new ModelProfileConfig { Id = "frontier-tools", Provider = "fake", Model = "gpt-4.1" }
                ]
            },
            DynamicTurnRouting = new DynamicTurnRoutingConfig
            {
                Enabled = true,
                Assets = new DynamicTurnRoutingAssetsConfig
                {
                    ClassifierModelPath = "override/classifier.onnx",
                    EmbeddingModelPath = "modern/embeddings.onnx",
                    TokenizerPath = "modern/tokenizer.json",
                    Dimensions = 256
                },
                Policy = new DynamicTurnRoutingPolicyConfig
                {
                    Tiers = new DynamicTurnRoutingTierMap
                    {
                        T0 = new DynamicTurnRoutingTierTarget { ModelProfileId = "frontier-tools" },
                        T1 = new DynamicTurnRoutingTierTarget { ModelProfileId = "frontier-tools" },
                        T2 = new DynamicTurnRoutingTierTarget { ModelProfileId = "frontier-tools" },
                        T3 = new DynamicTurnRoutingTierTarget { ModelProfileId = "frontier-tools" }
                    }
                }
            }
        };

        var errors = ConfigValidator.Validate(config);

        Assert.DoesNotContain(errors, error => error.Contains("DynamicTurnRouting requires an embedding model", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, error => error.Contains("DynamicTurnRouting requires a tokenizer path", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigValidator_RejectsDynamicTurnRoutingPolicyThresholdsOutOfRange()
    {
        var config = new GatewayConfig
        {
            Models = new ModelsConfig
            {
                Profiles =
                [
                    new ModelProfileConfig { Id = "frontier-tools", Provider = "fake", Model = "gpt-4.1" }
                ]
            },
            DynamicTurnRouting = new DynamicTurnRoutingConfig
            {
                Enabled = true,
                Assets = new DynamicTurnRoutingAssetsConfig
                {
                    ClassifierModelPath = "classifier.onnx",
                    EmbeddingModelPath = "embeddings.onnx",
                    TokenizerPath = "tokenizer.json"
                },
                Policy = new DynamicTurnRoutingPolicyConfig
                {
                    MarginUpgradeThreshold = 1.2f,
                    R1RescueThreshold = -0.1f,
                    UnderRoutingSafetyThreshold = 1.5f,
                    DeepConversationTurnIndexThreshold = -1,
                    Tiers = new DynamicTurnRoutingTierMap
                    {
                        T0 = new DynamicTurnRoutingTierTarget { ModelProfileId = "frontier-tools" },
                        T1 = new DynamicTurnRoutingTierTarget { ModelProfileId = "frontier-tools" },
                        T2 = new DynamicTurnRoutingTierTarget { ModelProfileId = "frontier-tools" },
                        T3 = new DynamicTurnRoutingTierTarget { ModelProfileId = "frontier-tools" }
                    }
                }
            }
        };

        var errors = ConfigValidator.Validate(config);

        Assert.Contains(errors, error => error.Contains("DynamicTurnRouting.Policy.MarginUpgradeThreshold", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("DynamicTurnRouting.Policy.R1RescueThreshold", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("DynamicTurnRouting.Policy.UnderRoutingSafetyThreshold", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("DynamicTurnRouting.Policy.DeepConversationTurnIndexThreshold", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_CronStepZero_ReturnsError()
    {
        var config = new GatewayConfig
        {
            Cron = new CronConfig
            {
                Enabled = true,
                Jobs =
                [
                    new CronJobConfig
                    {
                        Name = "invalid",
                        CronExpression = "*/0 * * * *",
                        Prompt = "run"
                    }
                ]
            }
        };

        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, error => error.Contains("invalid CronExpression", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_CronValidExpression_NoCronError()
    {
        var config = new GatewayConfig
        {
            Cron = new CronConfig
            {
                Enabled = true,
                Jobs =
                [
                    new CronJobConfig
                    {
                        Name = "valid",
                        CronExpression = "*/5 * * * *",
                        Prompt = "run"
                    }
                ]
            }
        };

        var errors = ConfigValidator.Validate(config);
        Assert.DoesNotContain(errors, error => error.Contains("CronExpression", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RootApertureBearerWithoutEndpointOrApiKey_ReturnsErrors()
    {
        var config = new GatewayConfig
        {
            Llm = new LlmProviderConfig
            {
                Provider = "aperture",
                Model = "team/default",
                AuthMode = "bearer"
            }
        };

        var errors = ConfigValidator.Validate(config);

        Assert.Contains(errors, error => error.Contains("Llm.Endpoint", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("Llm.ApiKey", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ApertureProfileTailnetIdentityWithoutApiKey_IsAccepted()
    {
        var config = new GatewayConfig
        {
            Models = new ModelsConfig
            {
                Profiles =
                [
                    new ModelProfileConfig
                    {
                        Id = "aperture-default",
                        Provider = "aperture",
                        Model = "team/default",
                        BaseUrl = "https://aperture.example.test/v1",
                        AuthMode = "tailnet-identity"
                    }
                ],
                DefaultProfile = "aperture-default"
            }
        };

        var errors = ConfigValidator.Validate(config);

        Assert.DoesNotContain(errors, error => error.Contains("Models.Profiles.aperture-default.ApiKey", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, error => error.Contains("Models.Profiles.aperture-default.Endpoint", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_UnsupportedTailnetIdentityProvider_ReturnsError()
    {
        var config = new GatewayConfig
        {
            Llm = new LlmProviderConfig
            {
                Provider = "openai",
                Model = "gpt-4.1",
                ApiKey = "env:MODEL_PROVIDER_KEY",
                AuthMode = "tailnet-identity"
            }
        };

        var errors = ConfigValidator.Validate(config);

        Assert.Contains(errors, error => error.Contains("tailnet-identity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_WebhookHmacEnabledWithoutSecret_ReturnsError()
    {
        var config = new GatewayConfig
        {
            Webhooks = new WebhooksConfig
            {
                Enabled = true,
                Endpoints = new Dictionary<string, WebhookEndpointConfig>
                {
                    ["audit"] = new()
                    {
                        ValidateHmac = true,
                        Secret = null
                    }
                }
            }
        };

        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, e => e.Contains("ValidateHmac=true", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WhatsAppSignatureEnabledWithoutAppSecret_ReturnsError()
    {
        var config = new GatewayConfig
        {
            Channels = new ChannelsConfig
            {
                WhatsApp = new WhatsAppChannelConfig
                {
                    ValidateSignature = true,
                    WebhookAppSecret = null,
                    WebhookAppSecretRef = ""
                }
            }
        };

        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, e => e.Contains("WhatsApp.ValidateSignature", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("baileys")]
    [InlineData("baileys_csharp")]
    [InlineData("whatsmeow")]
    [InlineData("simulated")]
    public void Validate_WhatsAppFirstPartyWorkerSupportedDrivers_AreAccepted(string driver)
    {
        var config = new GatewayConfig
        {
            Channels = new ChannelsConfig
            {
                WhatsApp = new WhatsAppChannelConfig
                {
                    Type = "first_party_worker",
                    FirstPartyWorker = new WhatsAppFirstPartyWorkerConfig
                    {
                        Driver = driver,
                        Accounts = [new WhatsAppWorkerAccountConfig { AccountId = "default", SessionPath = "./session/default" }]
                    }
                }
            }
        };

        var errors = ConfigValidator.Validate(config);
        Assert.DoesNotContain(errors, error => error.Contains("FirstPartyWorker.Driver", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_TeamsEnabledWithoutCredentials_ReturnsErrors()
    {
        var config = new GatewayConfig
        {
            Channels = new ChannelsConfig
            {
                Teams = new TeamsChannelConfig
                {
                    Enabled = true,
                    AppIdRef = "",
                    AppPasswordRef = "",
                    TenantIdRef = ""
                }
            }
        };

        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, e => e.Contains("Channels.Teams.AppId", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Channels.Teams.AppPassword", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Channels.Teams.TenantId", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_TeamsInvalidPolicies_ReturnsErrors()
    {
        var config = new GatewayConfig
        {
            Channels = new ChannelsConfig
            {
                Teams = new TeamsChannelConfig
                {
                    GroupPolicy = "custom",
                    ReplyStyle = "reply",
                    ChunkMode = "words",
                    TextChunkLimit = 0
                }
            }
        };

        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, e => e.Contains("Channels.Teams.GroupPolicy", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Channels.Teams.ReplyStyle", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Channels.Teams.ChunkMode", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Channels.Teams.TextChunkLimit", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RetentionLimitsBelowMinimum_ReturnsErrors()
    {
        var config = new GatewayConfig
        {
            Memory = new MemoryConfig
            {
                Retention = new MemoryRetentionConfig
                {
                    SweepIntervalMinutes = 1,
                    SessionTtlDays = 0,
                    BranchTtlDays = 0,
                    ArchiveRetentionDays = 0,
                    MaxItemsPerSweep = 5
                }
            }
        };

        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, e => e.Contains("Memory.Retention.SweepIntervalMinutes", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Memory.Retention.SessionTtlDays", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Memory.Retention.BranchTtlDays", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Memory.Retention.ArchiveRetentionDays", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Memory.Retention.MaxItemsPerSweep", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_CompactionThresholdMustExceedMaxHistoryTurns_ReturnsError()
    {
        var config = new GatewayConfig
        {
            Memory = new MemoryConfig
            {
                MaxHistoryTurns = 50,
                EnableCompaction = true,
                CompactionThreshold = 50,
                CompactionKeepRecent = 10
            }
        };

        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, e => e.Contains("greater than MaxHistoryTurns", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_InvalidMemoryProvider_ReturnsError()
    {
        var config = new GatewayConfig
        {
            Memory = new MemoryConfig
            {
                Provider = "redis"
            }
        };

        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, e => e.Contains("Memory.Provider", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_InvalidRuntimeMode_ReturnsError()
    {
        var config = new GatewayConfig
        {
            Runtime = new RuntimeConfig
            {
                Mode = "turbo"
            }
        };

        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, e => e.Contains("Runtime.Mode", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_InvalidRuntimeOrchestrator_ReturnsError()
    {
        var config = new GatewayConfig
        {
            Runtime = new RuntimeConfig
            {
                Orchestrator = "experimental"
            }
        };

        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, e => e.Contains("Runtime.Orchestrator", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WorkflowsEnabledWithoutBackends_ReturnsError()
    {
        var config = new GatewayConfig
        {
            Workflows = new WorkflowsConfig
            {
                Enabled = true
            }
        };

        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, e => e.Contains("Workflows", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WorkflowBackendWithoutAbsoluteBaseUrl_ReturnsError()
    {
        var config = new GatewayConfig
        {
            Workflows = new WorkflowsConfig
            {
                Enabled = true,
                Backends =
                {
                    ["review"] = new WorkflowBackendConfig
                    {
                        BaseUrl = "/relative"
                    }
                }
            }
        };

        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, e => e.Contains("Workflows.Backends.review.BaseUrl", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WorkspaceOnlyWithoutAbsoluteWorkspaceRoot_ReturnsError()
    {
        var config = new GatewayConfig
        {
            Tooling = new ToolingConfig
            {
                WorkspaceOnly = true,
                WorkspaceRoot = "relative/workspace"
            }
        };

        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, e => e.Contains("Tooling.WorkspaceRoot", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WildcardFilesystemRootsMixedWithExplicitRoots_ReturnsError()
    {
        var config = new GatewayConfig
        {
            Tooling = new ToolingConfig
            {
                AllowedReadRoots = ["*", "/tmp/read"],
                AllowedWriteRoots = ["*", "/tmp/write"]
            }
        };

        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, e => e.Contains("Tooling.AllowedReadRoots", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Tooling.AllowedWriteRoots", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_McpHttpServerWithoutUrl_ReturnsError()
    {
        var config = new GatewayConfig
        {
            Plugins = new PluginsConfig
            {
                Mcp = new McpPluginsConfig
                {
                    Enabled = true,
                    Servers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
                    {
                        ["demo"] = new()
                        {
                            Transport = "http",
                            Url = ""
                        }
                    }
                }
            }
        };

        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, e => e.Contains("Plugins.Mcp.Servers.demo.Url", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_McpStdioServerWithoutCommand_ReturnsError()
    {
        var config = new GatewayConfig
        {
            Plugins = new PluginsConfig
            {
                Mcp = new McpPluginsConfig
                {
                    Enabled = true,
                    Servers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
                    {
                        ["demo"] = new()
                        {
                            Transport = "stdio",
                            Command = ""
                        }
                    }
                }
            }
        };

        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, e => e.Contains("Plugins.Mcp.Servers.demo.Command", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_DisabledMcpServerWithMissingRequiredFields_DoesNotReturnError()
    {
        var config = new GatewayConfig
        {
            Plugins = new PluginsConfig
            {
                Mcp = new McpPluginsConfig
                {
                    Enabled = true,
                    Servers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
                    {
                        ["stdio-disabled"] = new()
                        {
                            Enabled = false,
                            Transport = "stdio",
                            Command = ""
                        },
                        ["http-disabled"] = new()
                        {
                            Enabled = false,
                            Transport = "http",
                            Url = ""
                        }
                    }
                }
            }
        };

        var errors = ConfigValidator.Validate(config);
        Assert.DoesNotContain(errors, e => e.Contains("Plugins.Mcp.Servers.stdio-disabled", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, e => e.Contains("Plugins.Mcp.Servers.http-disabled", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_OpenSandboxProviderWithoutEndpoint_ReturnsError()
    {
        var config = new GatewayConfig
        {
            Tooling = new ToolingConfig
            {
                AllowShell = false,
                EnableBrowserTool = false,
                ReadOnlyMode = true
            },
            Plugins = new PluginsConfig
            {
                Native = new OpenClaw.Core.Plugins.NativePluginsConfig
                {
                    CodeExec = new OpenClaw.Core.Plugins.CodeExecConfig
                    {
                        Enabled = false
                    }
                }
            },
            Sandbox = new SandboxConfig
            {
                Provider = SandboxProviderNames.OpenSandbox,
                Endpoint = null
            }
        };

        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, e => e.Contains("Sandbox.Endpoint", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_OpenSandboxMissingTemplateForDefaultSandboxedShell_ReturnsError()
    {
        var config = new GatewayConfig
        {
            Tooling = new ToolingConfig
            {
                AllowShell = true,
                EnableBrowserTool = false
            },
            Plugins = new PluginsConfig
            {
                Native = new OpenClaw.Core.Plugins.NativePluginsConfig
                {
                    CodeExec = new OpenClaw.Core.Plugins.CodeExecConfig
                    {
                        Enabled = false
                    }
                }
            },
            Sandbox = new SandboxConfig
            {
                Provider = SandboxProviderNames.OpenSandbox,
                Endpoint = "http://localhost:5000"
            }
        };

        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, e => e.Contains("Sandbox.Tools.shell.Template", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_SandboxProviderNone_AllowsConfiguredToolOverrides()
    {
        var config = new GatewayConfig
        {
            Sandbox = new SandboxConfig
            {
                Provider = SandboxProviderNames.None,
                Tools = new Dictionary<string, SandboxToolConfig>(StringComparer.Ordinal)
                {
                    ["shell"] = new()
                    {
                        Mode = nameof(ToolSandboxMode.Require),
                        Template = "alpine:3.20",
                        TTL = 300
                    }
                }
            }
        };

        var errors = ConfigValidator.Validate(config);
        Assert.DoesNotContain(errors, error => error.Contains("Sandbox.", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_NotionEnabledWithoutToken_ReturnsError()
    {
        var config = new GatewayConfig
        {
            Plugins = new PluginsConfig
            {
                Native = new NativePluginsConfig
                {
                    Notion = new NotionConfig
                    {
                        Enabled = true,
                        ApiKeyRef = "",
                        DefaultPageId = "page-1"
                    }
                }
            }
        };

        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, error => error.Contains("Plugins.Native.Notion.ApiKeyRef", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_NotionEnabledWithoutTargets_ReturnsError()
    {
        var config = new GatewayConfig
        {
            Plugins = new PluginsConfig
            {
                Native = new NativePluginsConfig
                {
                    Notion = new NotionConfig
                    {
                        Enabled = true,
                        ApiKeyRef = "raw:test-token"
                    }
                }
            }
        };

        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, error => error.Contains("at least one allowed/default page or database id", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_NotionDefaultTargets_AreSufficient()
    {
        var config = new GatewayConfig
        {
            Plugins = new PluginsConfig
            {
                Native = new NativePluginsConfig
                {
                    Notion = new NotionConfig
                    {
                        Enabled = true,
                        ApiKeyRef = "raw:test-token",
                        DefaultDatabaseId = "db-1"
                    }
                }
            }
        };

        var errors = ConfigValidator.Validate(config);
        Assert.DoesNotContain(errors, error => error.Contains("Plugins.Native.Notion", StringComparison.Ordinal));
    }
}

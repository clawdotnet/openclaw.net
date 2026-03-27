using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Validation;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ConfigValidatorTests
{
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

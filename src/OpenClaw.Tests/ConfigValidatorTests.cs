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
}

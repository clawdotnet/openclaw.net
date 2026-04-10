using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using Xunit;

namespace OpenClaw.Tests;

public sealed class AdminSettingsServiceTests
{
    [Fact]
    public void Update_PersistsSnapshot_AndMarksRestartRequiredFields()
    {
        var root = CreateTempDirectory();
        try
        {
            var config = CreateConfig(root);
            var service = CreateService(config);

            var result = service.Update(new AdminSettingsSnapshot
            {
                UsageFooter = "tokens",
                MaxConcurrentSessions = 128,
                SessionTimeoutMinutes = 45,
                SessionTokenBudget = 10_000,
                SessionRateLimitPerMinute = 20,
                AllowQueryStringToken = true,
                BrowserSessionIdleMinutes = 90,
                BrowserRememberDays = 14,
                AutonomyMode = "supervised",
                RequireToolApproval = true,
                ToolApprovalTimeoutSeconds = 180,
                ParallelToolExecution = false,
                AllowShell = false,
                ReadOnlyMode = true,
                EnableBrowserTool = true,
                AllowBrowserEvaluate = false,
                MaxHistoryTurns = 80,
                EnableCompaction = true,
                CompactionThreshold = 100,
                CompactionKeepRecent = 12,
                RetentionEnabled = true,
                RetentionRunOnStartup = true,
                RetentionSweepIntervalMinutes = 60,
                RetentionSessionTtlDays = 20,
                RetentionBranchTtlDays = 10,
                RetentionArchiveEnabled = true,
                RetentionArchiveRetentionDays = 45,
                RetentionMaxItemsPerSweep = 2000,
                AllowlistSemantics = "strict",
                SmsEnabled = true,
                SmsValidateSignature = false,
                SmsDmPolicy = "open",
                TelegramEnabled = true,
                TelegramValidateSignature = true,
                TelegramDmPolicy = "closed",
                WhatsAppEnabled = true,
                WhatsAppValidateSignature = false,
                WhatsAppDmPolicy = "pairing"
            });

            Assert.True(result.Success);
            Assert.True(result.RestartRequired);
            Assert.Contains("general.maxConcurrentSessions", result.RestartRequiredFields);
            Assert.Contains("tooling.allowShell", result.RestartRequiredFields);
            Assert.Contains("retention.enabled", result.RestartRequiredFields);
            Assert.Contains("channels.allowlistSemantics", result.RestartRequiredFields);
            Assert.Contains("channels.sms.enabled", result.RestartRequiredFields);
            Assert.Contains("channels.whatsapp.enabled", result.RestartRequiredFields);
            Assert.True(File.Exists(AdminSettingsService.GetSettingsPath(config)));
            Assert.Equal("tokens", config.UsageFooter);
            Assert.True(config.Security.AllowQueryStringToken);
            Assert.Equal("strict", config.Channels.AllowlistSemantics);
            Assert.True(config.Channels.Sms.Twilio.Enabled);
            Assert.False(config.Channels.Sms.Twilio.ValidateSignature);
            Assert.Equal("open", config.Channels.Sms.DmPolicy);
            Assert.Equal("closed", config.Channels.Telegram.DmPolicy);

            var loaded = AdminSettingsService.TryLoadPersistedSnapshot(
                AdminSettingsService.GetSettingsPath(config),
                out var persisted,
                out var error);

            Assert.True(loaded);
            Assert.Null(error);
            Assert.NotNull(persisted);
            Assert.Equal(128, persisted!.MaxConcurrentSessions);
            Assert.True(persisted.RetentionEnabled);
            Assert.Equal("strict", persisted.AllowlistSemantics);
            Assert.True(persisted.SmsEnabled);
            Assert.False(persisted.WhatsAppValidateSignature);
            Assert.Equal("open", persisted.SmsDmPolicy);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Update_WithInvalidSnapshot_ReturnsValidationErrors()
    {
        var root = CreateTempDirectory();
        try
        {
            var config = CreateConfig(root);
            var service = CreateService(config);

            var result = service.Update(new AdminSettingsSnapshot
            {
                UsageFooter = "off",
                MaxConcurrentSessions = 64,
                SessionTimeoutMinutes = 30,
                SessionTokenBudget = 0,
                SessionRateLimitPerMinute = 0,
                AllowQueryStringToken = false,
                BrowserSessionIdleMinutes = 60,
                BrowserRememberDays = 30,
                AutonomyMode = "supervised",
                RequireToolApproval = false,
                ToolApprovalTimeoutSeconds = 300,
                ParallelToolExecution = true,
                AllowShell = true,
                ReadOnlyMode = false,
                EnableBrowserTool = true,
                AllowBrowserEvaluate = true,
                MaxHistoryTurns = 50,
                EnableCompaction = true,
                CompactionThreshold = 40,
                CompactionKeepRecent = 10,
                RetentionEnabled = false,
                RetentionRunOnStartup = true,
                RetentionSweepIntervalMinutes = 30,
                RetentionSessionTtlDays = 30,
                RetentionBranchTtlDays = 14,
                RetentionArchiveEnabled = true,
                RetentionArchiveRetentionDays = 30,
                RetentionMaxItemsPerSweep = 1000,
                AllowlistSemantics = "legacy",
                SmsDmPolicy = "invalid",
                TelegramDmPolicy = "pairing",
                WhatsAppDmPolicy = "pairing"
            });

            Assert.False(result.Success);
            Assert.Contains(result.Errors, error => error.Contains("CompactionThreshold", StringComparison.Ordinal));
            Assert.Contains(result.Errors, error => error.Contains("Channels.Sms.DmPolicy", StringComparison.Ordinal));
            Assert.False(File.Exists(AdminSettingsService.GetSettingsPath(config)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Reset_RemovesOverrideFile_AndRestoresBaseSettings()
    {
        var root = CreateTempDirectory();
        try
        {
            var config = CreateConfig(root);
            var service = CreateService(config);

            _ = service.Update(new AdminSettingsSnapshot
            {
                UsageFooter = "full",
                MaxConcurrentSessions = 96,
                SessionTimeoutMinutes = 35,
                SessionTokenBudget = 100,
                SessionRateLimitPerMinute = 10,
                AllowQueryStringToken = true,
                BrowserSessionIdleMinutes = 120,
                BrowserRememberDays = 21,
                AutonomyMode = "supervised",
                RequireToolApproval = true,
                ToolApprovalTimeoutSeconds = 300,
                ParallelToolExecution = true,
                AllowShell = false,
                ReadOnlyMode = false,
                EnableBrowserTool = true,
                AllowBrowserEvaluate = true,
                MaxHistoryTurns = 60,
                EnableCompaction = false,
                CompactionThreshold = 70,
                CompactionKeepRecent = 10,
                RetentionEnabled = false,
                RetentionRunOnStartup = true,
                RetentionSweepIntervalMinutes = 30,
                RetentionSessionTtlDays = 30,
                RetentionBranchTtlDays = 14,
                RetentionArchiveEnabled = true,
                RetentionArchiveRetentionDays = 30,
                RetentionMaxItemsPerSweep = 1000,
                AllowlistSemantics = "strict",
                SmsEnabled = true,
                SmsValidateSignature = false,
                SmsDmPolicy = "closed",
                TelegramEnabled = true,
                TelegramValidateSignature = true,
                TelegramDmPolicy = "open",
                WhatsAppEnabled = true,
                WhatsAppValidateSignature = true,
                WhatsAppDmPolicy = "pairing"
            });

            var result = service.Reset();

            Assert.True(result.Success);
            Assert.False(result.Persistence.Exists);
            Assert.Equal("off", config.UsageFooter);
            Assert.False(config.Security.AllowQueryStringToken);
            Assert.Equal(64, config.MaxConcurrentSessions);
            Assert.Equal("legacy", config.Channels.AllowlistSemantics);
            Assert.False(config.Channels.Sms.Twilio.Enabled);
            Assert.Equal("pairing", config.Channels.Sms.DmPolicy);
            Assert.False(File.Exists(AdminSettingsService.GetSettingsPath(config)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UpdateWhatsAppSettings_BlankSecretsPreserveExistingValues_UntilRefsAreCleared()
    {
        var root = CreateTempDirectory();
        try
        {
            var config = CreateConfig(root);
            config.Channels.WhatsApp.WebhookVerifyToken = "verify-existing";
            config.Channels.WhatsApp.WebhookVerifyTokenRef = "env:WA_VERIFY";
            config.Channels.WhatsApp.WebhookAppSecret = "secret-existing";
            config.Channels.WhatsApp.WebhookAppSecretRef = "env:WA_SECRET";
            config.Channels.WhatsApp.CloudApiToken = "cloud-existing";
            config.Channels.WhatsApp.CloudApiTokenRef = "env:WA_TOKEN";
            config.Channels.WhatsApp.BridgeToken = "bridge-existing";
            config.Channels.WhatsApp.BridgeTokenRef = "env:WA_BRIDGE";

            var service = CreateService(config);

            var preserved = service.UpdateWhatsAppSettings(new WhatsAppSetupRequest
            {
                Enabled = true,
                Type = "official",
                DmPolicy = "pairing",
                WebhookPath = "/whatsapp/inbound",
                WebhookVerifyToken = "",
                WebhookVerifyTokenRef = "env:WA_VERIFY",
                WebhookAppSecret = null,
                WebhookAppSecretRef = "env:WA_SECRET",
                CloudApiToken = null,
                CloudApiTokenRef = "env:WA_TOKEN",
                BridgeToken = null,
                BridgeTokenRef = "env:WA_BRIDGE"
            });

            Assert.True(preserved.Success);
            Assert.Equal("verify-existing", config.Channels.WhatsApp.WebhookVerifyToken);
            Assert.Equal("secret-existing", config.Channels.WhatsApp.WebhookAppSecret);
            Assert.Equal("cloud-existing", config.Channels.WhatsApp.CloudApiToken);
            Assert.Equal("bridge-existing", config.Channels.WhatsApp.BridgeToken);

            var cleared = service.UpdateWhatsAppSettings(new WhatsAppSetupRequest
            {
                Enabled = true,
                Type = "official",
                DmPolicy = "pairing",
                WebhookPath = "/whatsapp/inbound",
                WebhookVerifyToken = "",
                WebhookVerifyTokenRef = "",
                WebhookAppSecret = null,
                WebhookAppSecretRef = "",
                CloudApiToken = null,
                CloudApiTokenRef = "",
                BridgeToken = null,
                BridgeTokenRef = ""
            });

            Assert.True(cleared.Success);
            Assert.Equal("", config.Channels.WhatsApp.WebhookVerifyToken);
            Assert.Null(config.Channels.WhatsApp.WebhookAppSecret);
            Assert.Null(config.Channels.WhatsApp.CloudApiToken);
            Assert.Null(config.Channels.WhatsApp.BridgeToken);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AdminSettingsService CreateService(GatewayConfig config)
        => new(
            config,
            AdminSettingsService.CreateSnapshot(config),
            AdminSettingsService.GetSettingsPath(config),
            NullLogger<AdminSettingsService>.Instance);

    private static GatewayConfig CreateConfig(string root)
        => new()
        {
            Memory = new MemoryConfig
            {
                StoragePath = root,
                MaxHistoryTurns = 50,
                Retention = new MemoryRetentionConfig
                {
                    Enabled = false,
                    RunOnStartup = true,
                    SweepIntervalMinutes = 30,
                    SessionTtlDays = 30,
                    BranchTtlDays = 14,
                    ArchiveEnabled = true,
                    ArchiveRetentionDays = 30,
                    MaxItemsPerSweep = 1000
                }
            },
            Security = new SecurityConfig
            {
                AllowQueryStringToken = false,
                BrowserSessionIdleMinutes = 60,
                BrowserRememberDays = 30
            },
            Tooling = new ToolingConfig
            {
                AutonomyMode = "supervised",
                RequireToolApproval = false,
                ToolApprovalTimeoutSeconds = 300,
                ParallelToolExecution = true,
                AllowShell = true,
                ReadOnlyMode = false,
                EnableBrowserTool = true,
                AllowBrowserEvaluate = true
            },
            UsageFooter = "off",
            MaxConcurrentSessions = 64,
            SessionTimeoutMinutes = 30,
            SessionTokenBudget = 0,
            SessionRateLimitPerMinute = 0,
            Channels = new ChannelsConfig
            {
                AllowlistSemantics = "legacy",
                Sms = new SmsChannelConfig
                {
                    DmPolicy = "pairing"
                },
                Telegram = new TelegramChannelConfig
                {
                    DmPolicy = "pairing"
                },
                WhatsApp = new WhatsAppChannelConfig
                {
                    DmPolicy = "pairing"
                }
            }
        };

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }
}

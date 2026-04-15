using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;
using OpenClaw.Core.Validation;

namespace OpenClaw.Gateway;

internal sealed class AdminSettingsService
{
    private const string SettingsFileName = "admin-settings.json";
    private readonly object _gate = new();
    private readonly GatewayConfig _config;
    private readonly AdminSettingsSnapshot _baseSnapshot;
    private readonly string _settingsPath;
    private readonly ILogger<AdminSettingsService> _logger;

    public AdminSettingsService(
        GatewayConfig config,
        AdminSettingsSnapshot baseSnapshot,
        string settingsPath,
        ILogger<AdminSettingsService> logger)
    {
        _config = config;
        _baseSnapshot = baseSnapshot;
        _settingsPath = settingsPath;
        _logger = logger;
    }

    public static string GetSettingsPath(GatewayConfig config)
    {
        var storagePath = config.Memory.StoragePath;
        if (!Path.IsPathRooted(storagePath))
            storagePath = Path.GetFullPath(storagePath);

        return Path.Combine(storagePath, SettingsFileName);
    }

    public static AdminSettingsSnapshot CreateSnapshot(GatewayConfig config)
        => new()
        {
            UsageFooter = config.UsageFooter,
            MaxConcurrentSessions = config.MaxConcurrentSessions,
            SessionTimeoutMinutes = config.SessionTimeoutMinutes,
            SessionTokenBudget = config.SessionTokenBudget,
            SessionRateLimitPerMinute = config.SessionRateLimitPerMinute,
            AllowQueryStringToken = config.Security.AllowQueryStringToken,
            BrowserSessionIdleMinutes = config.Security.BrowserSessionIdleMinutes,
            BrowserRememberDays = config.Security.BrowserRememberDays,
            AutonomyMode = config.Tooling.AutonomyMode,
            RequireToolApproval = config.Tooling.RequireToolApproval,
            ToolApprovalTimeoutSeconds = config.Tooling.ToolApprovalTimeoutSeconds,
            ParallelToolExecution = config.Tooling.ParallelToolExecution,
            AllowShell = config.Tooling.AllowShell,
            ReadOnlyMode = config.Tooling.ReadOnlyMode,
            EnableBrowserTool = config.Tooling.EnableBrowserTool,
            AllowBrowserEvaluate = config.Tooling.AllowBrowserEvaluate,
            MaxHistoryTurns = config.Memory.MaxHistoryTurns,
            EnableCompaction = config.Memory.EnableCompaction,
            CompactionThreshold = config.Memory.CompactionThreshold,
            CompactionKeepRecent = config.Memory.CompactionKeepRecent,
            RetentionEnabled = config.Memory.Retention.Enabled,
            RetentionRunOnStartup = config.Memory.Retention.RunOnStartup,
            RetentionSweepIntervalMinutes = config.Memory.Retention.SweepIntervalMinutes,
            RetentionSessionTtlDays = config.Memory.Retention.SessionTtlDays,
            RetentionBranchTtlDays = config.Memory.Retention.BranchTtlDays,
            RetentionArchiveEnabled = config.Memory.Retention.ArchiveEnabled,
            RetentionArchiveRetentionDays = config.Memory.Retention.ArchiveRetentionDays,
            RetentionMaxItemsPerSweep = config.Memory.Retention.MaxItemsPerSweep,
            AllowlistSemantics = config.Channels.AllowlistSemantics,
            SmsEnabled = config.Channels.Sms.Twilio.Enabled,
            SmsValidateSignature = config.Channels.Sms.Twilio.ValidateSignature,
            SmsDmPolicy = config.Channels.Sms.DmPolicy,
            TelegramEnabled = config.Channels.Telegram.Enabled,
            TelegramValidateSignature = config.Channels.Telegram.ValidateSignature,
            TelegramDmPolicy = config.Channels.Telegram.DmPolicy,
            TeamsEnabled = config.Channels.Teams.Enabled,
            TeamsValidateToken = config.Channels.Teams.ValidateToken,
            TeamsDmPolicy = config.Channels.Teams.DmPolicy,
            SlackEnabled = config.Channels.Slack.Enabled,
            SlackValidateSignature = config.Channels.Slack.ValidateSignature,
            SlackDmPolicy = config.Channels.Slack.DmPolicy,
            DiscordEnabled = config.Channels.Discord.Enabled,
            DiscordValidateSignature = config.Channels.Discord.ValidateSignature,
            DiscordDmPolicy = config.Channels.Discord.DmPolicy,
            SignalEnabled = config.Channels.Signal.Enabled,
            SignalDmPolicy = config.Channels.Signal.DmPolicy,
            WhatsAppEnabled = config.Channels.WhatsApp.Enabled,
            WhatsAppValidateSignature = config.Channels.WhatsApp.ValidateSignature,
            WhatsAppDmPolicy = config.Channels.WhatsApp.DmPolicy,
            WhatsAppType = config.Channels.WhatsApp.Type,
            WhatsAppWebhookPath = config.Channels.WhatsApp.WebhookPath,
            WhatsAppWebhookPublicBaseUrl = config.Channels.WhatsApp.WebhookPublicBaseUrl,
            WhatsAppWebhookVerifyToken = config.Channels.WhatsApp.WebhookVerifyToken,
            WhatsAppWebhookVerifyTokenRef = config.Channels.WhatsApp.WebhookVerifyTokenRef,
            WhatsAppWebhookAppSecret = config.Channels.WhatsApp.WebhookAppSecret,
            WhatsAppWebhookAppSecretRef = config.Channels.WhatsApp.WebhookAppSecretRef,
            WhatsAppCloudApiToken = config.Channels.WhatsApp.CloudApiToken,
            WhatsAppCloudApiTokenRef = config.Channels.WhatsApp.CloudApiTokenRef,
            WhatsAppPhoneNumberId = config.Channels.WhatsApp.PhoneNumberId,
            WhatsAppBusinessAccountId = config.Channels.WhatsApp.BusinessAccountId,
            WhatsAppBridgeUrl = config.Channels.WhatsApp.BridgeUrl,
            WhatsAppBridgeToken = config.Channels.WhatsApp.BridgeToken,
            WhatsAppBridgeTokenRef = config.Channels.WhatsApp.BridgeTokenRef,
            WhatsAppBridgeSuppressSendExceptions = config.Channels.WhatsApp.BridgeSuppressSendExceptions,
            WhatsAppFirstPartyWorker = CloneWhatsAppFirstPartyWorker(config.Channels.WhatsApp.FirstPartyWorker)
        };

    public static bool TryLoadPersistedSnapshot(string settingsPath, out AdminSettingsSnapshot? snapshot, out string? error)
    {
        snapshot = null;
        error = null;

        if (!File.Exists(settingsPath))
            return false;

        try
        {
            var json = File.ReadAllText(settingsPath);
            snapshot = JsonSerializer.Deserialize(json, CoreJsonContext.Default.AdminSettingsSnapshot);
            if (snapshot is null)
            {
                error = $"Admin settings file '{settingsPath}' is empty or invalid.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to load admin settings from '{settingsPath}': {ex.Message}";
            return false;
        }
    }

    public static void ApplySnapshot(GatewayConfig config, AdminSettingsSnapshot snapshot)
    {
        config.UsageFooter = snapshot.UsageFooter;
        config.MaxConcurrentSessions = snapshot.MaxConcurrentSessions;
        config.SessionTimeoutMinutes = snapshot.SessionTimeoutMinutes;
        config.SessionTokenBudget = snapshot.SessionTokenBudget;
        config.SessionRateLimitPerMinute = snapshot.SessionRateLimitPerMinute;
        config.Security.AllowQueryStringToken = snapshot.AllowQueryStringToken;
        config.Security.BrowserSessionIdleMinutes = snapshot.BrowserSessionIdleMinutes;
        config.Security.BrowserRememberDays = snapshot.BrowserRememberDays;
        config.Tooling.AutonomyMode = snapshot.AutonomyMode;
        config.Tooling.RequireToolApproval = snapshot.RequireToolApproval;
        config.Tooling.ToolApprovalTimeoutSeconds = snapshot.ToolApprovalTimeoutSeconds;
        config.Tooling.ParallelToolExecution = snapshot.ParallelToolExecution;
        config.Tooling.AllowShell = snapshot.AllowShell;
        config.Tooling.ReadOnlyMode = snapshot.ReadOnlyMode;
        config.Tooling.EnableBrowserTool = snapshot.EnableBrowserTool;
        config.Tooling.AllowBrowserEvaluate = snapshot.AllowBrowserEvaluate;
        config.Memory.MaxHistoryTurns = snapshot.MaxHistoryTurns;
        config.Memory.EnableCompaction = snapshot.EnableCompaction;
        config.Memory.CompactionThreshold = snapshot.CompactionThreshold;
        config.Memory.CompactionKeepRecent = snapshot.CompactionKeepRecent;
        config.Memory.Retention.Enabled = snapshot.RetentionEnabled;
        config.Memory.Retention.RunOnStartup = snapshot.RetentionRunOnStartup;
        config.Memory.Retention.SweepIntervalMinutes = snapshot.RetentionSweepIntervalMinutes;
        config.Memory.Retention.SessionTtlDays = snapshot.RetentionSessionTtlDays;
        config.Memory.Retention.BranchTtlDays = snapshot.RetentionBranchTtlDays;
        config.Memory.Retention.ArchiveEnabled = snapshot.RetentionArchiveEnabled;
        config.Memory.Retention.ArchiveRetentionDays = snapshot.RetentionArchiveRetentionDays;
        config.Memory.Retention.MaxItemsPerSweep = snapshot.RetentionMaxItemsPerSweep;
        config.Channels.AllowlistSemantics = snapshot.AllowlistSemantics;
        config.Channels.Sms.Twilio.Enabled = snapshot.SmsEnabled;
        config.Channels.Sms.Twilio.ValidateSignature = snapshot.SmsValidateSignature;
        config.Channels.Sms.DmPolicy = snapshot.SmsDmPolicy;
        config.Channels.Telegram.Enabled = snapshot.TelegramEnabled;
        config.Channels.Telegram.ValidateSignature = snapshot.TelegramValidateSignature;
        config.Channels.Telegram.DmPolicy = snapshot.TelegramDmPolicy;
        config.Channels.Teams.Enabled = snapshot.TeamsEnabled;
        config.Channels.Teams.ValidateToken = snapshot.TeamsValidateToken;
        config.Channels.Teams.DmPolicy = snapshot.TeamsDmPolicy;
        config.Channels.Slack.Enabled = snapshot.SlackEnabled;
        config.Channels.Slack.ValidateSignature = snapshot.SlackValidateSignature;
        config.Channels.Slack.DmPolicy = snapshot.SlackDmPolicy;
        config.Channels.Discord.Enabled = snapshot.DiscordEnabled;
        config.Channels.Discord.ValidateSignature = snapshot.DiscordValidateSignature;
        config.Channels.Discord.DmPolicy = snapshot.DiscordDmPolicy;
        config.Channels.Signal.Enabled = snapshot.SignalEnabled;
        config.Channels.Signal.DmPolicy = snapshot.SignalDmPolicy;
        config.Channels.WhatsApp.Enabled = snapshot.WhatsAppEnabled;
        config.Channels.WhatsApp.ValidateSignature = snapshot.WhatsAppValidateSignature;
        config.Channels.WhatsApp.DmPolicy = snapshot.WhatsAppDmPolicy;
        config.Channels.WhatsApp.Type = snapshot.WhatsAppType;
        config.Channels.WhatsApp.WebhookPath = snapshot.WhatsAppWebhookPath;
        config.Channels.WhatsApp.WebhookPublicBaseUrl = snapshot.WhatsAppWebhookPublicBaseUrl;
        config.Channels.WhatsApp.WebhookVerifyToken = snapshot.WhatsAppWebhookVerifyToken;
        config.Channels.WhatsApp.WebhookVerifyTokenRef = snapshot.WhatsAppWebhookVerifyTokenRef;
        config.Channels.WhatsApp.WebhookAppSecret = snapshot.WhatsAppWebhookAppSecret;
        config.Channels.WhatsApp.WebhookAppSecretRef = snapshot.WhatsAppWebhookAppSecretRef;
        config.Channels.WhatsApp.CloudApiToken = snapshot.WhatsAppCloudApiToken;
        config.Channels.WhatsApp.CloudApiTokenRef = snapshot.WhatsAppCloudApiTokenRef;
        config.Channels.WhatsApp.PhoneNumberId = snapshot.WhatsAppPhoneNumberId;
        config.Channels.WhatsApp.BusinessAccountId = snapshot.WhatsAppBusinessAccountId;
        config.Channels.WhatsApp.BridgeUrl = snapshot.WhatsAppBridgeUrl;
        config.Channels.WhatsApp.BridgeToken = snapshot.WhatsAppBridgeToken;
        config.Channels.WhatsApp.BridgeTokenRef = snapshot.WhatsAppBridgeTokenRef;
        config.Channels.WhatsApp.BridgeSuppressSendExceptions = snapshot.WhatsAppBridgeSuppressSendExceptions;
        config.Channels.WhatsApp.FirstPartyWorker = CloneWhatsAppFirstPartyWorker(snapshot.WhatsAppFirstPartyWorker);
    }

    public AdminSettingsSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return CreateSnapshot(_config);
        }
    }

    public AdminSettingsPersistenceInfo GetPersistence()
    {
        lock (_gate)
        {
            return BuildPersistenceInfo();
        }
    }

    public AdminSettingsResult Update(AdminSettingsSnapshot snapshot)
    {
        lock (_gate)
        {
            var previous = CreateSnapshot(_config);
            var clone = CloneConfig(_config);
            ApplySnapshot(clone, snapshot);

            var errors = ConfigValidator.Validate(clone);
            if (errors.Count > 0)
            {
                return new AdminSettingsResult(
                    false,
                    previous,
                    BuildPersistenceInfo(),
                    false,
                    [],
                    errors);
            }

            ApplySnapshot(_config, snapshot);
            PersistSnapshot(snapshot);
            var changedRestartFields = GetRestartRequiredChanges(previous, snapshot);
            return new AdminSettingsResult(
                true,
                CreateSnapshot(_config),
                BuildPersistenceInfo(),
                changedRestartFields.Count > 0,
                changedRestartFields,
                []);
        }
    }

    public AdminSettingsResult Reset()
    {
        lock (_gate)
        {
            var previous = CreateSnapshot(_config);
            ApplySnapshot(_config, _baseSnapshot);

            try
            {
                if (File.Exists(_settingsPath))
                    File.Delete(_settingsPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete admin settings override file at {Path}", _settingsPath);
            }

            var changedRestartFields = GetRestartRequiredChanges(previous, _baseSnapshot);
            return new AdminSettingsResult(
                true,
                CreateSnapshot(_config),
                BuildPersistenceInfo(),
                changedRestartFields.Count > 0,
                changedRestartFields,
                []);
        }
    }

    public AdminSettingsResult UpdateWhatsAppSettings(WhatsAppSetupRequest request)
    {
        lock (_gate)
        {
            var current = CreateSnapshot(_config);
            return Update(CloneSnapshotWithWhatsApp(current, request));
        }
    }

    public static IReadOnlyList<string> ImmediateFieldKeys { get; } =
    [
        "usageFooter",
        "security.allowQueryStringToken",
        "security.browserSessionIdleMinutes",
        "security.browserRememberDays",
        "channels.sms.dmPolicy",
        "channels.telegram.dmPolicy",
        "channels.teams.dmPolicy",
        "channels.slack.dmPolicy",
        "channels.discord.dmPolicy",
        "channels.signal.dmPolicy",
        "channels.whatsapp.dmPolicy"
    ];

    public static IReadOnlyList<string> RestartFieldKeys { get; } =
    [
        "general.maxConcurrentSessions",
        "general.sessionTimeoutMinutes",
        "general.sessionTokenBudget",
        "general.sessionRateLimitPerMinute",
        "tooling.autonomyMode",
        "tooling.requireToolApproval",
        "tooling.toolApprovalTimeoutSeconds",
        "tooling.parallelToolExecution",
        "tooling.allowShell",
        "tooling.readOnlyMode",
        "tooling.enableBrowserTool",
        "tooling.allowBrowserEvaluate",
        "memory.maxHistoryTurns",
        "memory.enableCompaction",
        "memory.compactionThreshold",
        "memory.compactionKeepRecent",
        "retention.enabled",
        "retention.runOnStartup",
        "retention.sweepIntervalMinutes",
        "retention.sessionTtlDays",
        "retention.branchTtlDays",
        "retention.archiveEnabled",
        "retention.archiveRetentionDays",
        "retention.maxItemsPerSweep",
        "channels.allowlistSemantics",
        "channels.sms.enabled",
        "channels.sms.validateSignature",
        "channels.telegram.enabled",
        "channels.telegram.validateSignature",
        "channels.teams.enabled",
        "channels.teams.validateToken",
        "channels.slack.enabled",
        "channels.slack.validateSignature",
        "channels.discord.enabled",
        "channels.discord.validateSignature",
        "channels.signal.enabled",
        "channels.whatsapp.enabled",
        "channels.whatsapp.validateSignature",
        "channels.whatsapp.type",
        "channels.whatsapp.webhookPath",
        "channels.whatsapp.webhookPublicBaseUrl",
        "channels.whatsapp.webhookVerifyToken",
        "channels.whatsapp.webhookVerifyTokenRef",
        "channels.whatsapp.webhookAppSecret",
        "channels.whatsapp.webhookAppSecretRef",
        "channels.whatsapp.cloudApiToken",
        "channels.whatsapp.cloudApiTokenRef",
        "channels.whatsapp.phoneNumberId",
        "channels.whatsapp.businessAccountId",
        "channels.whatsapp.bridgeUrl",
        "channels.whatsapp.bridgeToken",
        "channels.whatsapp.bridgeTokenRef",
        "channels.whatsapp.bridgeSuppressSendExceptions",
        "channels.whatsapp.firstPartyWorker"
    ];

    private void PersistSnapshot(AdminSettingsSnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var tempPath = _settingsPath + ".tmp";
        var json = JsonSerializer.Serialize(snapshot, CoreJsonContext.Default.AdminSettingsSnapshot);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _settingsPath, overwrite: true);
    }

    private AdminSettingsPersistenceInfo BuildPersistenceInfo()
    {
        var fileInfo = new FileInfo(_settingsPath);
        return new AdminSettingsPersistenceInfo
        {
            Path = _settingsPath,
            Exists = fileInfo.Exists,
            LastModifiedAtUtc = fileInfo.Exists ? fileInfo.LastWriteTimeUtc : null
        };
    }

    private static GatewayConfig CloneConfig(GatewayConfig config)
    {
        var json = JsonSerializer.Serialize(config, CoreJsonContext.Default.GatewayConfig);
        var clone = JsonSerializer.Deserialize(json, CoreJsonContext.Default.GatewayConfig);
        if (clone is null)
            throw new InvalidOperationException("Failed to clone gateway configuration.");

        return clone;
    }

    private static List<string> GetRestartRequiredChanges(AdminSettingsSnapshot before, AdminSettingsSnapshot after)
    {
        var changed = new List<string>();
        AddIfChanged(changed, "general.maxConcurrentSessions", before.MaxConcurrentSessions, after.MaxConcurrentSessions);
        AddIfChanged(changed, "general.sessionTimeoutMinutes", before.SessionTimeoutMinutes, after.SessionTimeoutMinutes);
        AddIfChanged(changed, "general.sessionTokenBudget", before.SessionTokenBudget, after.SessionTokenBudget);
        AddIfChanged(changed, "general.sessionRateLimitPerMinute", before.SessionRateLimitPerMinute, after.SessionRateLimitPerMinute);
        AddIfChanged(changed, "tooling.autonomyMode", before.AutonomyMode, after.AutonomyMode);
        AddIfChanged(changed, "tooling.requireToolApproval", before.RequireToolApproval, after.RequireToolApproval);
        AddIfChanged(changed, "tooling.toolApprovalTimeoutSeconds", before.ToolApprovalTimeoutSeconds, after.ToolApprovalTimeoutSeconds);
        AddIfChanged(changed, "tooling.parallelToolExecution", before.ParallelToolExecution, after.ParallelToolExecution);
        AddIfChanged(changed, "tooling.allowShell", before.AllowShell, after.AllowShell);
        AddIfChanged(changed, "tooling.readOnlyMode", before.ReadOnlyMode, after.ReadOnlyMode);
        AddIfChanged(changed, "tooling.enableBrowserTool", before.EnableBrowserTool, after.EnableBrowserTool);
        AddIfChanged(changed, "tooling.allowBrowserEvaluate", before.AllowBrowserEvaluate, after.AllowBrowserEvaluate);
        AddIfChanged(changed, "memory.maxHistoryTurns", before.MaxHistoryTurns, after.MaxHistoryTurns);
        AddIfChanged(changed, "memory.enableCompaction", before.EnableCompaction, after.EnableCompaction);
        AddIfChanged(changed, "memory.compactionThreshold", before.CompactionThreshold, after.CompactionThreshold);
        AddIfChanged(changed, "memory.compactionKeepRecent", before.CompactionKeepRecent, after.CompactionKeepRecent);
        AddIfChanged(changed, "retention.enabled", before.RetentionEnabled, after.RetentionEnabled);
        AddIfChanged(changed, "retention.runOnStartup", before.RetentionRunOnStartup, after.RetentionRunOnStartup);
        AddIfChanged(changed, "retention.sweepIntervalMinutes", before.RetentionSweepIntervalMinutes, after.RetentionSweepIntervalMinutes);
        AddIfChanged(changed, "retention.sessionTtlDays", before.RetentionSessionTtlDays, after.RetentionSessionTtlDays);
        AddIfChanged(changed, "retention.branchTtlDays", before.RetentionBranchTtlDays, after.RetentionBranchTtlDays);
        AddIfChanged(changed, "retention.archiveEnabled", before.RetentionArchiveEnabled, after.RetentionArchiveEnabled);
        AddIfChanged(changed, "retention.archiveRetentionDays", before.RetentionArchiveRetentionDays, after.RetentionArchiveRetentionDays);
        AddIfChanged(changed, "retention.maxItemsPerSweep", before.RetentionMaxItemsPerSweep, after.RetentionMaxItemsPerSweep);
        AddIfChanged(changed, "channels.allowlistSemantics", before.AllowlistSemantics, after.AllowlistSemantics);
        AddIfChanged(changed, "channels.sms.enabled", before.SmsEnabled, after.SmsEnabled);
        AddIfChanged(changed, "channels.sms.validateSignature", before.SmsValidateSignature, after.SmsValidateSignature);
        AddIfChanged(changed, "channels.telegram.enabled", before.TelegramEnabled, after.TelegramEnabled);
        AddIfChanged(changed, "channels.telegram.validateSignature", before.TelegramValidateSignature, after.TelegramValidateSignature);
        AddIfChanged(changed, "channels.teams.enabled", before.TeamsEnabled, after.TeamsEnabled);
        AddIfChanged(changed, "channels.teams.validateToken", before.TeamsValidateToken, after.TeamsValidateToken);
        AddIfChanged(changed, "channels.slack.enabled", before.SlackEnabled, after.SlackEnabled);
        AddIfChanged(changed, "channels.slack.validateSignature", before.SlackValidateSignature, after.SlackValidateSignature);
        AddIfChanged(changed, "channels.discord.enabled", before.DiscordEnabled, after.DiscordEnabled);
        AddIfChanged(changed, "channels.discord.validateSignature", before.DiscordValidateSignature, after.DiscordValidateSignature);
        AddIfChanged(changed, "channels.signal.enabled", before.SignalEnabled, after.SignalEnabled);
        AddIfChanged(changed, "channels.whatsapp.enabled", before.WhatsAppEnabled, after.WhatsAppEnabled);
        AddIfChanged(changed, "channels.whatsapp.validateSignature", before.WhatsAppValidateSignature, after.WhatsAppValidateSignature);
        AddIfChanged(changed, "channels.whatsapp.type", before.WhatsAppType, after.WhatsAppType);
        AddIfChanged(changed, "channels.whatsapp.webhookPath", before.WhatsAppWebhookPath, after.WhatsAppWebhookPath);
        AddIfChanged(changed, "channels.whatsapp.webhookPublicBaseUrl", before.WhatsAppWebhookPublicBaseUrl ?? "", after.WhatsAppWebhookPublicBaseUrl ?? "");
        AddIfChanged(changed, "channels.whatsapp.webhookVerifyToken", before.WhatsAppWebhookVerifyToken, after.WhatsAppWebhookVerifyToken);
        AddIfChanged(changed, "channels.whatsapp.webhookVerifyTokenRef", before.WhatsAppWebhookVerifyTokenRef, after.WhatsAppWebhookVerifyTokenRef);
        AddIfChanged(changed, "channels.whatsapp.webhookAppSecret", before.WhatsAppWebhookAppSecret ?? "", after.WhatsAppWebhookAppSecret ?? "");
        AddIfChanged(changed, "channels.whatsapp.webhookAppSecretRef", before.WhatsAppWebhookAppSecretRef, after.WhatsAppWebhookAppSecretRef);
        AddIfChanged(changed, "channels.whatsapp.cloudApiToken", before.WhatsAppCloudApiToken ?? "", after.WhatsAppCloudApiToken ?? "");
        AddIfChanged(changed, "channels.whatsapp.cloudApiTokenRef", before.WhatsAppCloudApiTokenRef, after.WhatsAppCloudApiTokenRef);
        AddIfChanged(changed, "channels.whatsapp.phoneNumberId", before.WhatsAppPhoneNumberId ?? "", after.WhatsAppPhoneNumberId ?? "");
        AddIfChanged(changed, "channels.whatsapp.businessAccountId", before.WhatsAppBusinessAccountId ?? "", after.WhatsAppBusinessAccountId ?? "");
        AddIfChanged(changed, "channels.whatsapp.bridgeUrl", before.WhatsAppBridgeUrl ?? "", after.WhatsAppBridgeUrl ?? "");
        AddIfChanged(changed, "channels.whatsapp.bridgeToken", before.WhatsAppBridgeToken ?? "", after.WhatsAppBridgeToken ?? "");
        AddIfChanged(changed, "channels.whatsapp.bridgeTokenRef", before.WhatsAppBridgeTokenRef, after.WhatsAppBridgeTokenRef);
        AddIfChanged(changed, "channels.whatsapp.bridgeSuppressSendExceptions", before.WhatsAppBridgeSuppressSendExceptions, after.WhatsAppBridgeSuppressSendExceptions);
        AddIfChanged(
            changed,
            "channels.whatsapp.firstPartyWorker",
            JsonSerializer.Serialize(before.WhatsAppFirstPartyWorker, CoreJsonContext.Default.WhatsAppFirstPartyWorkerConfig),
            JsonSerializer.Serialize(after.WhatsAppFirstPartyWorker, CoreJsonContext.Default.WhatsAppFirstPartyWorkerConfig));
        return changed;
    }

    private static void AddIfChanged<T>(ICollection<string> changes, string fieldKey, T before, T after)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(before, after))
            changes.Add(fieldKey);
    }

    private static AdminSettingsSnapshot CloneSnapshotWithWhatsApp(AdminSettingsSnapshot source, WhatsAppSetupRequest request)
    {
        var webhookVerifyToken = string.IsNullOrWhiteSpace(request.WebhookVerifyToken)
            ? string.IsNullOrWhiteSpace(request.WebhookVerifyTokenRef) ? "" : source.WhatsAppWebhookVerifyToken
            : request.WebhookVerifyToken;
        var webhookAppSecret = ResolveNullableSecretValue(source.WhatsAppWebhookAppSecret, request.WebhookAppSecret, request.WebhookAppSecretRef);
        var cloudApiToken = ResolveNullableSecretValue(source.WhatsAppCloudApiToken, request.CloudApiToken, request.CloudApiTokenRef);
        var bridgeToken = ResolveNullableSecretValue(source.WhatsAppBridgeToken, request.BridgeToken, request.BridgeTokenRef);

        return new AdminSettingsSnapshot
        {
            UsageFooter = source.UsageFooter,
            MaxConcurrentSessions = source.MaxConcurrentSessions,
            SessionTimeoutMinutes = source.SessionTimeoutMinutes,
            SessionTokenBudget = source.SessionTokenBudget,
            SessionRateLimitPerMinute = source.SessionRateLimitPerMinute,
            AllowQueryStringToken = source.AllowQueryStringToken,
            BrowserSessionIdleMinutes = source.BrowserSessionIdleMinutes,
            BrowserRememberDays = source.BrowserRememberDays,
            AutonomyMode = source.AutonomyMode,
            RequireToolApproval = source.RequireToolApproval,
            ToolApprovalTimeoutSeconds = source.ToolApprovalTimeoutSeconds,
            ParallelToolExecution = source.ParallelToolExecution,
            AllowShell = source.AllowShell,
            ReadOnlyMode = source.ReadOnlyMode,
            EnableBrowserTool = source.EnableBrowserTool,
            AllowBrowserEvaluate = source.AllowBrowserEvaluate,
            MaxHistoryTurns = source.MaxHistoryTurns,
            EnableCompaction = source.EnableCompaction,
            CompactionThreshold = source.CompactionThreshold,
            CompactionKeepRecent = source.CompactionKeepRecent,
            RetentionEnabled = source.RetentionEnabled,
            RetentionRunOnStartup = source.RetentionRunOnStartup,
            RetentionSweepIntervalMinutes = source.RetentionSweepIntervalMinutes,
            RetentionSessionTtlDays = source.RetentionSessionTtlDays,
            RetentionBranchTtlDays = source.RetentionBranchTtlDays,
            RetentionArchiveEnabled = source.RetentionArchiveEnabled,
            RetentionArchiveRetentionDays = source.RetentionArchiveRetentionDays,
            RetentionMaxItemsPerSweep = source.RetentionMaxItemsPerSweep,
            AllowlistSemantics = source.AllowlistSemantics,
            SmsEnabled = source.SmsEnabled,
            SmsValidateSignature = source.SmsValidateSignature,
            SmsDmPolicy = source.SmsDmPolicy,
            TelegramEnabled = source.TelegramEnabled,
            TelegramValidateSignature = source.TelegramValidateSignature,
            TelegramDmPolicy = source.TelegramDmPolicy,
            TeamsEnabled = source.TeamsEnabled,
            TeamsValidateToken = source.TeamsValidateToken,
            TeamsDmPolicy = source.TeamsDmPolicy,
            SlackEnabled = source.SlackEnabled,
            SlackValidateSignature = source.SlackValidateSignature,
            SlackDmPolicy = source.SlackDmPolicy,
            DiscordEnabled = source.DiscordEnabled,
            DiscordValidateSignature = source.DiscordValidateSignature,
            DiscordDmPolicy = source.DiscordDmPolicy,
            SignalEnabled = source.SignalEnabled,
            SignalDmPolicy = source.SignalDmPolicy,
            WhatsAppEnabled = request.Enabled,
            WhatsAppValidateSignature = request.ValidateSignature,
            WhatsAppDmPolicy = request.DmPolicy,
            WhatsAppType = request.Type,
            WhatsAppWebhookPath = request.WebhookPath,
            WhatsAppWebhookPublicBaseUrl = request.WebhookPublicBaseUrl,
            WhatsAppWebhookVerifyToken = webhookVerifyToken,
            WhatsAppWebhookVerifyTokenRef = request.WebhookVerifyTokenRef,
            WhatsAppWebhookAppSecret = webhookAppSecret,
            WhatsAppWebhookAppSecretRef = request.WebhookAppSecretRef,
            WhatsAppCloudApiToken = cloudApiToken,
            WhatsAppCloudApiTokenRef = request.CloudApiTokenRef,
            WhatsAppPhoneNumberId = request.PhoneNumberId,
            WhatsAppBusinessAccountId = request.BusinessAccountId,
            WhatsAppBridgeUrl = request.BridgeUrl,
            WhatsAppBridgeToken = bridgeToken,
            WhatsAppBridgeTokenRef = request.BridgeTokenRef,
            WhatsAppBridgeSuppressSendExceptions = request.BridgeSuppressSendExceptions,
            WhatsAppFirstPartyWorker = CloneWhatsAppFirstPartyWorker(request.FirstPartyWorker ?? source.WhatsAppFirstPartyWorker)
        };
    }

    private static string? ResolveNullableSecretValue(string? existingValue, string? requestedValue, string? requestedRef)
    {
        if (!string.IsNullOrWhiteSpace(requestedValue))
            return requestedValue;

        return string.IsNullOrWhiteSpace(requestedRef) ? null : existingValue;
    }

    private static WhatsAppFirstPartyWorkerConfig CloneWhatsAppFirstPartyWorker(WhatsAppFirstPartyWorkerConfig? source)
    {
        if (source is null)
            return new WhatsAppFirstPartyWorkerConfig();

        return new WhatsAppFirstPartyWorkerConfig
        {
            Driver = source.Driver,
            ExecutablePath = source.ExecutablePath,
            WorkingDirectory = source.WorkingDirectory,
            StoragePath = source.StoragePath,
            MediaCachePath = source.MediaCachePath,
            HistorySync = source.HistorySync,
            Proxy = source.Proxy,
            Accounts = source.Accounts.Select(static account => new WhatsAppWorkerAccountConfig
            {
                AccountId = account.AccountId,
                SessionPath = account.SessionPath,
                DeviceName = account.DeviceName,
                PairingMode = account.PairingMode,
                PhoneNumber = account.PhoneNumber,
                SendReadReceipts = account.SendReadReceipts,
                AckReaction = account.AckReaction,
                MediaCachePath = account.MediaCachePath,
                HistorySync = account.HistorySync,
                Proxy = account.Proxy
            }).ToList()
        };
    }
}

internal sealed record AdminSettingsResult(
    bool Success,
    AdminSettingsSnapshot Snapshot,
    AdminSettingsPersistenceInfo Persistence,
    bool RestartRequired,
    IReadOnlyList<string> RestartRequiredFields,
    IReadOnlyList<string> Errors);

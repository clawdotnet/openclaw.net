namespace OpenClaw.Core.Models;

// kingcrab-specific channel configuration types.
// Restored from pre-content-sync state (GatewayConfig.cs.bak) because upstream
// openclaw.net does not ship Feishu / DingTalk / WeCom channels but kingcrab
// does (see OpenClaw.Channels/{Feishu,DingTalk,WeCom}Channel.cs).

/// <summary>
/// Configuration for the Feishu (Lark) channel.
/// Uses WebSocket long connection; no public webhook endpoint needed — suitable for intranet/sandbox deployments.
/// Supports config hot-reload: change values in appsettings and the channel reconnects automatically.
/// </summary>
public sealed class FeishuChannelConfig
{
    public bool Enabled { get; set; } = false;

    /// <summary>Feishu App ID (direct value). Takes precedence over AppIdRef when set.</summary>
    public string? AppId { get; set; }

    /// <summary>Secret reference for App ID (e.g. "env:FEISHU_APP_ID"). Used when AppId is null.</summary>
    public string AppIdRef { get; set; } = "env:FEISHU_APP_ID";

    /// <summary>Feishu App Secret (direct value). Avoid in production; prefer AppSecretRef.</summary>
    public string? AppSecret { get; set; }

    /// <summary>Secret reference for App Secret (e.g. "env:FEISHU_APP_SECRET").</summary>
    public string AppSecretRef { get; set; } = "env:FEISHU_APP_SECRET";

    /// <summary>Group chat policy: "open" allows all groups, "allowlist" restricts to AllowedGroupIds, "disabled" drops group messages.</summary>
    public string GroupPolicy { get; set; } = "open"; // open, allowlist, disabled

    /// <summary>Allowed sender open_ids. Empty = allow all (subject to DmPolicy/GroupPolicy).</summary>
    public string[] AllowedFromUserIds { get; set; } = [];

    /// <summary>Allowed group chat_ids (oc_xxx). Only used when GroupPolicy is "allowlist".</summary>
    public string[] AllowedGroupIds { get; set; } = [];

    public int MaxInboundChars { get; set; } = 4096;

    /// <summary>
    /// When true, the bot only responds to group messages where it is explicitly @mentioned.
    /// Recommended when multiple bots share the same group.
    /// Default is false (respond to all group messages allowed by GroupPolicy).
    /// </summary>
    public bool RequireMentionInGroup { get; set; } = false;

    /// <summary>
    /// When true, inbound media file keys are included as feishu-resource:// URLs in MediaUrl.
    /// The pipeline can pass these to tools that understand the scheme.
    /// </summary>
    public bool ExposeInboundMediaUrls { get; set; } = true;
}

public sealed class DingTalkChannelConfig
{
    public bool Enabled { get; set; } = false;

    public string? AppId { get; set; }
    public string AppIdRef { get; set; } = "env:DINGTALK_APP_ID";

    public string? AppKey { get; set; }
    public string AppKeyRef { get; set; } = "env:DINGTALK_APP_KEY";

    public string? AppSecret { get; set; }
    public string AppSecretRef { get; set; } = "env:DINGTALK_APP_SECRET";

    /// <summary>Robot RobotCode, defaults to AppKey, required when sending messages.</summary>
    public string? RobotCode { get; set; }
    public string RobotCodeRef { get; set; } = "env:DINGTALK_ROBOT_CODE";

    public string GroupPolicy { get; set; } = "open";
    public string[] AllowedFromUserIds { get; set; } = [];
    public string[] AllowedGroupIds { get; set; } = [];
    public int MaxInboundChars { get; set; } = 4096;
    public bool RequireMentionInGroup { get; set; } = true;
    public bool ExposeInboundMediaUrls { get; set; } = true;
    public int StreamPollIntervalMs { get; set; } = 500;
}

/// <summary>
/// WeCom (Enterprise WeChat) AI Bot channel configuration.
/// Uses WebSocket long connection for receiving messages, REST API for sending.
/// </summary>
public sealed class WeComChannelConfig
{
    public bool Enabled { get; set; } = true;

    // ── WebSocket long-connection credentials (AI Bot) ──
    /// <summary>AI Bot BotId, format: aib-xxxxx</summary>
    public string? BotId { get; set; }
    public string BotIdRef { get; set; } = "env:WECOM_BOT_ID";

    /// <summary>AI Bot long-connection dedicated Secret</summary>
    public string? BotSecret { get; set; }
    public string BotSecretRef { get; set; } = "env:WECOM_BOT_SECRET";

    // ── REST API credentials (self-built app, for proactive messaging and media upload) ──
    /// <summary>Enterprise CorpID</summary>
    public string? CorpId { get; set; }
    public string CorpIdRef { get; set; } = "env:WECOM_CORP_ID";

    /// <summary>Self-built app AgentId</summary>
    public int AgentId { get; set; }
    public string AgentIdRef { get; set; } = "env:WECOM_AGENT_ID";

    /// <summary>Self-built app Secret, used to obtain access_token</summary>
    public string? CorpSecret { get; set; }
    public string CorpSecretRef { get; set; } = "env:WECOM_CORP_SECRET";

    // ── General settings ──
    /// <summary>Group chat policy: open (allow all), allowlist (whitelist), disabled (drop group messages)</summary>
    public string GroupPolicy { get; set; } = "open";

    /// <summary>Allowed sender userid list, empty array means allow all</summary>
    public string[] AllowedFromUserIds { get; set; } = [];

    /// <summary>Allowed group chat chatid list, only effective when GroupPolicy=allowlist</summary>
    public string[] AllowedGroupIds { get; set; } = [];

    /// <summary>Max inbound message characters</summary>
    public int MaxInboundChars { get; set; } = 4096;

    /// <summary>Whether @mention of the bot is required in group chats</summary>
    public bool RequireMentionInGroup { get; set; } = true;
}

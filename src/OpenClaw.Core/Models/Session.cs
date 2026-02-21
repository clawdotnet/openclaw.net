using System.Text.Json.Serialization;
using OpenClaw.Core.Contacts;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Skills;

namespace OpenClaw.Core.Models;

/// <summary>
/// Represents a conversation session between a user and the agent.
/// Designed for zero-allocation serialization via source generators.
/// </summary>
public sealed class Session
{
    public required string Id { get; init; }
    public required string ChannelId { get; init; }
    public required string SenderId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastActiveAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ChatTurn> History { get; } = [];
    public SessionState State { get; set; } = SessionState.Active;

    /// <summary>Total input tokens consumed across all turns in this session.</summary>
    public long TotalInputTokens { get; set; }

    /// <summary>Total output tokens consumed across all turns in this session.</summary>
    public long TotalOutputTokens { get; set; }
}

public enum SessionState : byte
{
    Active,
    Paused,
    Expired
}

public sealed record ChatTurn
{
    public required string Role { get; init; }
    public required string Content { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public List<ToolInvocation>? ToolCalls { get; init; }
}

public sealed record ToolInvocation
{
    public required string ToolName { get; init; }
    public required string Arguments { get; init; }
    public string? Result { get; init; }
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// AOT-compatible JSON serialization context for all core models.
/// </summary>
[JsonSerializable(typeof(Session))]
[JsonSerializable(typeof(ChatTurn))]
[JsonSerializable(typeof(ToolInvocation))]
[JsonSerializable(typeof(InboundMessage))]
[JsonSerializable(typeof(OutboundMessage))]
[JsonSerializable(typeof(WsClientEnvelope))]
[JsonSerializable(typeof(WsServerEnvelope))]
[JsonSerializable(typeof(GatewayConfig))]
[JsonSerializable(typeof(LlmProviderConfig))]
[JsonSerializable(typeof(MemoryConfig))]
[JsonSerializable(typeof(SecurityConfig))]
[JsonSerializable(typeof(WebSocketConfig))]
[JsonSerializable(typeof(ToolingConfig))]
[JsonSerializable(typeof(ChannelsConfig))]
[JsonSerializable(typeof(SmsChannelConfig))]
[JsonSerializable(typeof(TwilioSmsConfig))]
[JsonSerializable(typeof(Contact))]
[JsonSerializable(typeof(ContactStoreState))]
[JsonSerializable(typeof(List<ChatTurn>))]
[JsonSerializable(typeof(IDictionary<string, object?>))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(PluginManifest))]
[JsonSerializable(typeof(PluginsConfig))]
[JsonSerializable(typeof(PluginLoadConfig))]
[JsonSerializable(typeof(PluginEntryConfig))]
[JsonSerializable(typeof(PluginToolRegistration))]
[JsonSerializable(typeof(BridgeRequest))]
[JsonSerializable(typeof(BridgeResponse))]
[JsonSerializable(typeof(BridgeError))]
[JsonSerializable(typeof(BridgeToolResult))]
[JsonSerializable(typeof(ToolContentItem))]
[JsonSerializable(typeof(Dictionary<string, PluginEntryConfig>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(NativePluginsConfig))]
[JsonSerializable(typeof(WebSearchConfig))]
[JsonSerializable(typeof(WebFetchConfig))]
[JsonSerializable(typeof(GitToolsConfig))]
[JsonSerializable(typeof(CodeExecConfig))]
[JsonSerializable(typeof(ImageGenConfig))]
[JsonSerializable(typeof(PdfReadConfig))]
[JsonSerializable(typeof(CalendarConfig))]
[JsonSerializable(typeof(EmailConfig))]
[JsonSerializable(typeof(DatabaseConfig))]
[JsonSerializable(typeof(SkillsConfig))]
[JsonSerializable(typeof(SkillLoadConfig))]
[JsonSerializable(typeof(SkillEntryConfig))]
[JsonSerializable(typeof(Dictionary<string, SkillEntryConfig>))]
[JsonSerializable(typeof(MetricsSnapshot))]
[JsonSerializable(typeof(SessionBranch))]
[JsonSerializable(typeof(List<SessionBranch>))]
[JsonSerializable(typeof(AgentProfile))]
[JsonSerializable(typeof(DelegationConfig))]
[JsonSerializable(typeof(Dictionary<string, AgentProfile>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
public partial class CoreJsonContext : JsonSerializerContext;

using System.Collections.Concurrent;
using System.Collections.Frozen;
using OpenClaw.Agent;
using OpenClaw.Agent.Plugins;
using OpenClaw.Channels;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Middleware;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;
using OpenClaw.Core.Sessions;
using OpenClaw.Core.Skills;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Extensions;
using OpenClaw.Payments.Core;

namespace OpenClaw.Gateway.Composition;

internal sealed class GatewayAppRuntime
{
    public required IAgentRuntime AgentRuntime { get; init; }
    public required string OrchestratorId { get; init; }
    public required MessagePipeline Pipeline { get; init; }
    public required MiddlewarePipeline MiddlewarePipeline { get; init; }
    public required WebSocketChannel WebSocketChannel { get; init; }
    public required IReadOnlyDictionary<string, IChannelAdapter> ChannelAdapters { get; init; }
    public required SessionManager SessionManager { get; init; }
    public required IMemoryRetentionCoordinator RetentionCoordinator { get; init; }
    public required PairingManager PairingManager { get; init; }
    public required AllowlistManager Allowlists { get; init; }
    public required AllowlistSemantics AllowlistSemantics { get; init; }
    public required RecentSendersStore RecentSenders { get; init; }
    public required ChatCommandProcessor CommandProcessor { get; init; }
    public required ToolApprovalService ToolApprovalService { get; init; }
    public required ApprovalAuditStore ApprovalAuditStore { get; init; }
    public required RuntimeMetrics RuntimeMetrics { get; init; }
    public required ProviderUsageTracker ProviderUsage { get; init; }
    public PaymentRuntimeService PaymentRuntime { get; init; } = new(
        [new MockPaymentProvider()],
        new InMemoryPaymentSecretVault(),
        new DefaultPaymentPolicy(),
        new InMemoryPaymentAuditSink(),
        defaultProviderId: "mock");
    public required HeartbeatService Heartbeat { get; init; }
    public required IReadOnlyList<SkillDefinition> LoadedSkills { get; set; }
    public required SkillWatcherService SkillWatcher { get; init; }
    public required IReadOnlyList<PluginLoadReport> PluginReports { get; init; }
    public required RuntimeOperationsState Operations { get; init; }
    public required bool EffectiveRequireToolApproval { get; init; }
    public required IReadOnlyList<string> EffectiveApprovalRequiredTools { get; init; }
    public required NativePluginRegistry NativeRegistry { get; init; }
    public required ConcurrentDictionary<string, SemaphoreSlim> SessionLocks { get; init; }
    public required ConcurrentDictionary<string, DateTimeOffset> LockLastUsed { get; init; }
    public required FrozenSet<string>? AllowedOriginsSet { get; init; }
    public required IReadOnlyList<string> DynamicProviderOwners { get; init; }
    public required int EstimatedSkillPromptChars { get; init; }
    public required CronScheduler? CronTask { get; init; }
    public TwilioSmsWebhookHandler? TwilioSmsWebhookHandler { get; init; }
    public PluginHost? PluginHost { get; init; }
    public NativeDynamicPluginHost? NativeDynamicPluginHost { get; init; }
    public FirstPartyWhatsAppWorkerHost? WhatsAppWorkerHost { get; init; }
    public required ChannelAuthEventStore ChannelAuthEvents { get; init; }

    /// <summary>Names of all registered tools (built-in + native plugins + bridge plugins).</summary>
    public required FrozenSet<string> RegisteredToolNames { get; init; }
}

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenClaw.Agent;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.MicrosoftAgentFrameworkAdapter;

public sealed class MafAgentRuntimeFactory : IAgentRuntimeFactory
{
    private readonly MafAgentFactory _agentFactory;
    private readonly MafSessionStateStore _sessionStateStore;
    private readonly MafTelemetryAdapter _telemetry;
    private readonly MafOptions _options;
    private readonly ILoggerFactory _loggerFactory;

    public MafAgentRuntimeFactory(
        MafAgentFactory agentFactory,
        MafSessionStateStore sessionStateStore,
        MafTelemetryAdapter telemetry,
        IOptions<MafOptions> options,
        ILoggerFactory loggerFactory)
    {
        _agentFactory = agentFactory;
        _sessionStateStore = sessionStateStore;
        _telemetry = telemetry;
        _options = options.Value;
        _loggerFactory = loggerFactory;
    }

    public string OrchestratorId => MafCapabilities.OrchestratorId;

    private MafAgentRuntime CreateRuntime(AgentRuntimeFactoryContext context)
        => new(
            context,
            _options,
            _agentFactory,
            _sessionStateStore,
            _telemetry,
            _loggerFactory.CreateLogger("MafAgentRuntime"));

    private MafAgentRuntime CreateDelegatedRuntime(
        AgentRuntimeFactoryContext context,
        IReadOnlyList<ITool> tools,
        LlmProviderConfig llmConfig,
        AgentProfile profile)
        => CreateRuntime(new AgentRuntimeFactoryContext
        {
            Services = context.Services,
            Config = CreateDelegatedConfig(context.Config, llmConfig, profile.MaxHistoryTurns),
            RuntimeState = context.RuntimeState,
            ChatClient = context.ChatClient,
            Tools = tools,
            MemoryStore = context.MemoryStore,
            RuntimeMetrics = context.RuntimeMetrics,
            ProviderUsage = context.ProviderUsage,
            LlmExecutionService = context.LlmExecutionService,
            Skills = context.Skills,
            SkillsConfig = context.SkillsConfig,
            WorkspacePath = context.WorkspacePath,
            PluginSkillDirs = context.PluginSkillDirs,
            Logger = context.Logger,
            Hooks = context.Hooks,
            RequireToolApproval = context.RequireToolApproval,
            ApprovalRequiredTools = context.ApprovalRequiredTools,
            ToolSandbox = context.ToolSandbox,
            ToolUsageTracker = context.ToolUsageTracker
        });

    private static GatewayConfig CreateDelegatedConfig(
        GatewayConfig config,
        LlmProviderConfig llmConfig,
        int maxHistoryTurns)
        => new()
        {
            BindAddress = config.BindAddress,
            Port = config.Port,
            AuthToken = config.AuthToken,
            Runtime = config.Runtime,
            Llm = llmConfig,
            Memory = new MemoryConfig
            {
                Provider = config.Memory.Provider,
                StoragePath = config.Memory.StoragePath,
                MaxHistoryTurns = maxHistoryTurns,
                MaxCachedSessions = config.Memory.MaxCachedSessions,
                Sqlite = config.Memory.Sqlite,
                Recall = config.Memory.Recall,
                Retention = config.Memory.Retention,
                EnableCompaction = config.Memory.EnableCompaction,
                CompactionThreshold = config.Memory.CompactionThreshold,
                CompactionKeepRecent = config.Memory.CompactionKeepRecent,
                ProjectId = config.Memory.ProjectId
            },
            Security = config.Security,
            WebSocket = config.WebSocket,
            Tooling = config.Tooling,
            Sandbox = config.Sandbox,
            Channels = config.Channels,
            Plugins = config.Plugins,
            Skills = config.Skills,
            Delegation = config.Delegation,
            Cron = config.Cron,
            Webhooks = config.Webhooks,
            UsageFooter = config.UsageFooter,
            MaxConcurrentSessions = config.MaxConcurrentSessions,
            SessionTimeoutMinutes = config.SessionTimeoutMinutes,
            SessionTokenBudget = config.SessionTokenBudget,
            SessionRateLimitPerMinute = config.SessionRateLimitPerMinute,
            GracefulShutdownSeconds = config.GracefulShutdownSeconds,
            TokenCostRates = config.TokenCostRates
        };

    public IAgentRuntime Create(AgentRuntimeFactoryContext context)
    {
        MafCapabilities.EnsureSupported(context.RuntimeState);

        if (!context.Config.Delegation.Enabled || context.Config.Delegation.Profiles.Count == 0)
            return CreateRuntime(context);

        var delegateTool = new DelegateTool(
            context.ChatClient,
            context.Tools,
            context.MemoryStore,
            context.Config.Llm,
            context.Config.Delegation,
            currentDepth: 0,
            metrics: context.RuntimeMetrics,
            logger: context.Logger,
            recall: context.Config.Memory.Recall,
            runtimeFactory: (tools, llmConfig, profile) => CreateDelegatedRuntime(context, tools, llmConfig, profile));

        return CreateRuntime(new AgentRuntimeFactoryContext
        {
            Services = context.Services,
            Config = context.Config,
            RuntimeState = context.RuntimeState,
            ChatClient = context.ChatClient,
            Tools = [.. context.Tools, delegateTool],
            MemoryStore = context.MemoryStore,
            RuntimeMetrics = context.RuntimeMetrics,
            ProviderUsage = context.ProviderUsage,
            LlmExecutionService = context.LlmExecutionService,
            Skills = context.Skills,
            SkillsConfig = context.SkillsConfig,
            WorkspacePath = context.WorkspacePath,
            PluginSkillDirs = context.PluginSkillDirs,
            Logger = context.Logger,
            Hooks = context.Hooks,
            RequireToolApproval = context.RequireToolApproval,
            ApprovalRequiredTools = context.ApprovalRequiredTools,
            ToolSandbox = context.ToolSandbox,
            ToolUsageTracker = context.ToolUsageTracker
        });
    }
}

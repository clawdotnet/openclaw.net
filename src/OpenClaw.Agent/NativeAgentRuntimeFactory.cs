using OpenClaw.Agent.Tools;

namespace OpenClaw.Agent;

public sealed class NativeAgentRuntimeFactory : IAgentRuntimeFactory
{
    public string OrchestratorId => OpenClaw.Core.Models.RuntimeOrchestrator.Native;

    private AgentRuntime CreateRuntime(
        Microsoft.Extensions.AI.IChatClient chatClient,
        IReadOnlyList<OpenClaw.Core.Abstractions.ITool> tools,
        AgentRuntimeFactoryContext context)
        => new(
            chatClient,
            tools,
            context.MemoryStore,
            context.Config.Llm,
            context.Config.Memory.MaxHistoryTurns,
            context.Skills,
            skillsConfig: context.SkillsConfig,
            skillWorkspacePath: context.WorkspacePath,
            pluginSkillDirs: context.PluginSkillDirs,
            logger: context.Logger,
            toolTimeoutSeconds: context.Config.Tooling.ToolTimeoutSeconds,
            metrics: context.RuntimeMetrics,
            providerUsage: context.ProviderUsage,
            llmExecutionService: context.LlmExecutionService,
            parallelToolExecution: context.Config.Tooling.ParallelToolExecution,
            enableCompaction: context.Config.Memory.EnableCompaction,
            compactionThreshold: context.Config.Memory.CompactionThreshold,
            compactionKeepRecent: context.Config.Memory.CompactionKeepRecent,
            requireToolApproval: context.RequireToolApproval,
            approvalRequiredTools: [.. context.ApprovalRequiredTools],
            hooks: context.Hooks,
            sessionTokenBudget: context.Config.SessionTokenBudget,
            recall: context.Config.Memory.Recall,
            toolSandbox: context.ToolSandbox,
            gatewayConfig: context.Config);

    public IAgentRuntime Create(AgentRuntimeFactoryContext context)
    {
        IAgentRuntime agentRuntime = CreateRuntime(context.ChatClient, context.Tools, context);

        if (!context.Config.Delegation.Enabled || context.Config.Delegation.Profiles.Count == 0)
            return agentRuntime;

        var delegateTool = new DelegateTool(
            context.ChatClient,
            context.Tools,
            context.MemoryStore,
            context.Config.Llm,
            context.Config.Delegation,
            currentDepth: 0,
            metrics: context.RuntimeMetrics,
            logger: context.Logger,
            recall: context.Config.Memory.Recall);

        return CreateRuntime(context.ChatClient, [.. context.Tools, delegateTool], context);
    }
}

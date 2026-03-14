using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Skills;

namespace OpenClaw.Agent;

public sealed class AgentRuntimeFactoryContext
{
    public required IServiceProvider Services { get; init; }
    public required GatewayConfig Config { get; init; }
    public required GatewayRuntimeState RuntimeState { get; init; }
    public required IChatClient ChatClient { get; init; }
    public required IReadOnlyList<ITool> Tools { get; init; }
    public required IMemoryStore MemoryStore { get; init; }
    public required RuntimeMetrics RuntimeMetrics { get; init; }
    public required ProviderUsageTracker ProviderUsage { get; init; }
    public required ILlmExecutionService LlmExecutionService { get; init; }
    public required IReadOnlyList<SkillDefinition> Skills { get; init; }
    public required SkillsConfig SkillsConfig { get; init; }
    public required string? WorkspacePath { get; init; }
    public required IReadOnlyList<string> PluginSkillDirs { get; init; }
    public required ILogger Logger { get; init; }
    public required IReadOnlyList<IToolHook> Hooks { get; init; }
    public required bool RequireToolApproval { get; init; }
    public required IReadOnlyList<string> ApprovalRequiredTools { get; init; }
    public IToolSandbox? ToolSandbox { get; init; }
}

public interface IAgentRuntimeFactory
{
    string OrchestratorId { get; }

    IAgentRuntime Create(AgentRuntimeFactoryContext context);
}

public static class AgentRuntimeFactorySelector
{
    public static IAgentRuntimeFactory Select(
        IEnumerable<IAgentRuntimeFactory> factories,
        string? orchestratorId)
    {
        var normalizedOrchestrator = RuntimeOrchestrator.Normalize(orchestratorId);
        var factory = factories.FirstOrDefault(candidate =>
            string.Equals(candidate.OrchestratorId, normalizedOrchestrator, StringComparison.OrdinalIgnoreCase));

        if (factory is not null)
            return factory;

        throw new InvalidOperationException(
            normalizedOrchestrator == RuntimeOrchestrator.Maf
                ? "Runtime.Orchestrator='maf' requires the Microsoft Agent Framework experiment adapter. Build with OpenClawEnableMafExperiment=true."
                : $"No agent runtime factory is registered for orchestrator '{normalizedOrchestrator}'.");
    }
}

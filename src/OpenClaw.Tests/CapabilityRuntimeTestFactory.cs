using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using NSubstitute;
using OpenClaw.Agent;
using OpenClaw.Agent.Plugins;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Skills;
using OpenClaw.MicrosoftAgentFrameworkAdapter;
using OpenClaw.Testing;
using Xunit;

namespace OpenClaw.Tests;

internal static class CapabilityRuntimeTestFactory
{
    internal static (object Runtime, IChatClient Chat, ILlmExecutionService Execution) Create(
        bool maf, IReadOnlyList<ITool> tools, IMemoryStore memory, SkillDefinition skill, GatewayConfig gatewayConfig,
        CapabilitySlotExecutor capabilitySlotExecutor, IReadOnlyList<IToolHook>? hooks = null)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var chat = Substitute.For<IChatClient>();
        var execution = Substitute.For<ILlmExecutionService>();
        if (maf)
        {
            var options = new MafOptions();
            var runtime = new MafAgentRuntimeFactory(new MafAgentFactory(Options.Create(options), NullLoggerFactory.Instance, services),
                new MafSessionStateStore(gatewayConfig, Options.Create(options), NullLogger<MafSessionStateStore>.Instance),
                new MafTelemetryAdapter(), Options.Create(options), NullLoggerFactory.Instance).Create(new AgentRuntimeFactoryContext
            {
                Services = services,
                Config = gatewayConfig,
                RuntimeState = new GatewayRuntimeState { RequestedMode = "jit", EffectiveMode = GatewayRuntimeMode.Jit, DynamicCodeSupported = true },
                ChatClient = chat,
                Tools = tools,
                MemoryStore = memory,
                RuntimeMetrics = new RuntimeMetrics(),
                ProviderUsage = new ProviderUsageTracker(),
                LlmExecutionService = execution,
                Skills = [skill],
                SkillsConfig = new SkillsConfig(),
                WorkspacePath = null,
                PluginSkillDirs = [],
                Logger = NullLogger.Instance,
                Hooks = hooks ?? [],
                RequireToolApproval = gatewayConfig.Tooling.RequireToolApproval,
                ApprovalRequiredTools = gatewayConfig.Tooling.ApprovalRequiredTools,
                CapabilitySlotExecutor = capabilitySlotExecutor
            });
            return (runtime, chat, execution);
        }
        var native = new AgentRuntime(chat, tools, memory, gatewayConfig.Llm, maxHistoryTurns: 5, skills: [skill], capabilitySlotExecutor: capabilitySlotExecutor, gatewayConfig: gatewayConfig,
            hooks: hooks, requireToolApproval: gatewayConfig.Tooling.RequireToolApproval, approvalRequiredTools: gatewayConfig.Tooling.ApprovalRequiredTools);
        return (native, chat, execution);
    }

}

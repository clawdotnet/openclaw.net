using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Core.Skills;

namespace OpenClaw.Agent;

public interface IAgentRuntime
{
    CircuitState CircuitBreakerState { get; }
    IReadOnlyList<string> LoadedSkillNames { get; }

    /// <summary>
    /// Snapshot of the currently loaded skill definitions. Used by the
    /// <c>load_skill</c> tool to resolve a skill body on demand (progressive disclosure).
    /// </summary>
    IReadOnlyList<SkillDefinition> LoadedSkills { get; }

    Task<string> RunAsync(
        Session session,
        string userMessage,
        CancellationToken ct,
        ToolApprovalCallback? approvalCallback = null,
        JsonElement? responseSchema = null,
        string? correlationId = null);

    Task<IReadOnlyList<string>> ReloadSkillsAsync(CancellationToken ct = default);

    IAsyncEnumerable<AgentStreamEvent> RunStreamingAsync(
        Session session,
        string userMessage,
        CancellationToken ct,
        ToolApprovalCallback? approvalCallback = null,
        string? correlationId = null);
}

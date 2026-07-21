using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;

namespace OpenClaw.Agent;

internal sealed class AgentExecutionContext
{
    public required Session Session { get; init; }
    public required TurnContext TurnContext { get; init; }
    public required int SystemPromptLength { get; init; }
    public required int SkillPromptLength { get; init; }
    public required long SessionTokenBudget { get; init; }
    public required List<ToolInvocation> ToolInvocations { get; init; }
    public ITurnTokenUsageObserver? TurnTokenUsageObserver { get; init; }
    public Action<Session, string, string, long, long>? RecordContractTurnUsage { get; init; }
    public ToolApprovalCallback? ApprovalCallback { get; init; }
    public Func<AgentStreamEvent, CancellationToken, ValueTask>? StreamEventWriter { get; init; }
}

internal static class AgentExecutionContextScope
{
    private static readonly AsyncLocal<AgentExecutionContext?> CurrentValue = new();

    public static AgentExecutionContext Current
        => CurrentValue.Value
            ?? throw new InvalidOperationException(
                "Microsoft Agent Framework execution was invoked outside an OpenClaw runtime context.");

    /// <summary>
    /// Returns the current execution context, or null if not running inside the OpenClaw runtime (without throwing an exception).
    /// Suitable for scenarios such as tool execution where the presence of a context cannot be guaranteed.
    /// </summary>
    public static AgentExecutionContext? TryGetCurrent() => CurrentValue.Value;

    public static IDisposable Push(AgentExecutionContext context)
    {
        var prior = CurrentValue.Value;
        CurrentValue.Value = context;
        return new RestoreScope(prior);
    }

    private sealed class RestoreScope(AgentExecutionContext? prior) : IDisposable
    {
        public void Dispose() => CurrentValue.Value = prior;
    }
}

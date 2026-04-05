using OpenClaw.Agent;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;

namespace OpenClaw.MicrosoftAgentFrameworkAdapter;

internal sealed class MafExecutionContext
{
    public required Session Session { get; init; }
    public required TurnContext TurnContext { get; init; }
    public required int SystemPromptLength { get; init; }
    public required int SkillPromptLength { get; init; }
    public required long SessionTokenBudget { get; init; }
    public required List<ToolInvocation> ToolInvocations { get; init; }
    public Action<Session, string, string, long, long>? RecordContractTurnUsage { get; init; }
    public ToolApprovalCallback? ApprovalCallback { get; init; }
    public Func<AgentStreamEvent, CancellationToken, ValueTask>? StreamEventWriter { get; init; }
}

internal static class MafExecutionContextScope
{
    private static readonly AsyncLocal<MafExecutionContext?> CurrentValue = new();

    public static MafExecutionContext Current
        => CurrentValue.Value
            ?? throw new InvalidOperationException(
                "Microsoft Agent Framework execution was invoked outside an OpenClaw runtime context.");

    public static IDisposable Push(MafExecutionContext context)
    {
        var prior = CurrentValue.Value;
        CurrentValue.Value = context;
        return new RestoreScope(prior);
    }

    private sealed class RestoreScope(MafExecutionContext? prior) : IDisposable
    {
        public void Dispose() => CurrentValue.Value = prior;
    }
}

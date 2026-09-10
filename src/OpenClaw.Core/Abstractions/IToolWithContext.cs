using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;

namespace OpenClaw.Core.Abstractions;

public sealed class ToolExecutionContext
{
    public string? IdempotencyKey { get; init; }
    public required Session Session { get; init; }
    public required TurnContext TurnContext { get; init; }
}

public interface IToolWithContext : ITool
{
    ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct);
}

namespace OpenClaw.Core.Abstractions;

/// <summary>
/// Optional extension interface for tool hooks that need correlation/session metadata.
/// Implementers should also implement <see cref="IToolHook"/> to participate in the hook pipeline.
/// </summary>
public interface IToolHookWithContext : IToolHook
{
    ValueTask<bool> BeforeExecuteAsync(ToolHookContext context, CancellationToken ct);

    ValueTask AfterExecuteAsync(ToolHookContext context, string result, TimeSpan duration, bool failed, CancellationToken ct);
}

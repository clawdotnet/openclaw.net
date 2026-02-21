namespace OpenClaw.Core.Abstractions;

/// <summary>
/// Extension point for observing and intercepting tool executions.
/// Hooks run in registration order. If <see cref="BeforeExecuteAsync"/> returns false,
/// the tool is skipped and the LLM receives "Tool execution denied by hook: {hookName}".
/// </summary>
public interface IToolHook
{
    /// <summary>Human-readable hook name for logging.</summary>
    string Name { get; }

    /// <summary>
    /// Called before a tool executes. Return false to deny execution.
    /// </summary>
    ValueTask<bool> BeforeExecuteAsync(string toolName, string arguments, CancellationToken ct);

    /// <summary>
    /// Called after a tool executes (even if it failed).
    /// </summary>
    ValueTask AfterExecuteAsync(string toolName, string arguments, string result, TimeSpan duration, bool failed, CancellationToken ct);
}

using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;

namespace OpenClaw.Agent;

/// <summary>
/// Built-in hook that logs every tool execution for audit purposes.
/// Always allows execution (returns true from BeforeExecuteAsync).
/// </summary>
public sealed class AuditLogHook : IToolHookWithContext
{
    private readonly ILogger _logger;
    public string Name => "AuditLog";

    public AuditLogHook(ILogger logger) => _logger = logger;

    public ValueTask<bool> BeforeExecuteAsync(string toolName, string arguments, CancellationToken ct)
        => ValueTask.FromResult(true);

    public ValueTask<bool> BeforeExecuteAsync(ToolHookContext context, CancellationToken ct)
    {
        _logger.LogInformation(
            "[Audit] Tool {Tool} invoked for session {SessionId} channel {ChannelId} sender {SenderId} args length={ArgsLen}",
            context.ToolName,
            context.SessionId,
            context.ChannelId,
            context.SenderId,
            context.ArgumentsJson.Length);
        return ValueTask.FromResult(true);
    }

    public ValueTask AfterExecuteAsync(string toolName, string arguments, string result, TimeSpan duration, bool failed, CancellationToken ct)
        => ValueTask.CompletedTask;

    public ValueTask AfterExecuteAsync(ToolHookContext context, string result, TimeSpan duration, bool failed, CancellationToken ct)
    {
        if (failed)
        {
            _logger.LogWarning(
                "[Audit] Tool {Tool} FAILED for session {SessionId} channel {ChannelId} sender {SenderId} in {Duration}ms — result length={ResultLen}",
                context.ToolName,
                context.SessionId,
                context.ChannelId,
                context.SenderId,
                duration.TotalMilliseconds,
                result.Length);
        }
        else
        {
            _logger.LogInformation(
                "[Audit] Tool {Tool} completed for session {SessionId} channel {ChannelId} sender {SenderId} in {Duration}ms — result length={ResultLen}",
                context.ToolName,
                context.SessionId,
                context.ChannelId,
                context.SenderId,
                duration.TotalMilliseconds,
                result.Length);
        }

        return ValueTask.CompletedTask;
    }
}

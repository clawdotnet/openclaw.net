using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;

namespace OpenClaw.Agent;

/// <summary>
/// Built-in hook that logs every tool execution for audit purposes.
/// Always allows execution (returns true from BeforeExecuteAsync).
/// </summary>
public sealed class AuditLogHook : IToolHook
{
    private readonly ILogger _logger;
    public string Name => "AuditLog";

    public AuditLogHook(ILogger logger) => _logger = logger;

    public ValueTask<bool> BeforeExecuteAsync(string toolName, string arguments, CancellationToken ct)
    {
        _logger.LogInformation("[Audit] Tool {Tool} invoked with args length={ArgsLen}",
            toolName, arguments.Length);
        return ValueTask.FromResult(true);
    }

    public ValueTask AfterExecuteAsync(string toolName, string arguments, string result, TimeSpan duration, bool failed, CancellationToken ct)
    {
        if (failed)
        {
            _logger.LogWarning("[Audit] Tool {Tool} FAILED in {Duration}ms — result length={ResultLen}",
                toolName, duration.TotalMilliseconds, result.Length);
        }
        else
        {
            _logger.LogInformation("[Audit] Tool {Tool} completed in {Duration}ms — result length={ResultLen}",
                toolName, duration.TotalMilliseconds, result.Length);
        }

        return ValueTask.CompletedTask;
    }
}

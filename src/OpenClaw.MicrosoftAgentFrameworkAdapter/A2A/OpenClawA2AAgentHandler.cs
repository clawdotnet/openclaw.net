using A2A;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using A2ATaskStatus = A2A.TaskStatus;

namespace OpenClaw.MicrosoftAgentFrameworkAdapter.A2A;

/// <summary>
/// Bridges the OpenClaw MAF <see cref="MafAgentFactory"/> into the A2A
/// <see cref="IAgentHandler"/> contract so that the A2A server
/// can dispatch protocol messages through the existing agent pipeline.
/// </summary>
public sealed class OpenClawA2AAgentHandler : IAgentHandler
{
    private readonly MafOptions _options;
    private readonly ILogger<OpenClawA2AAgentHandler> _logger;

    public OpenClawA2AAgentHandler(
        IOptions<MafOptions> options,
        ILogger<OpenClawA2AAgentHandler> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(
        RequestContext context,
        AgentEventQueue eventQueue,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "A2A ExecuteAsync: taskId={TaskId}, contextId={ContextId}, streaming={Streaming}",
            context.TaskId,
            context.ContextId,
            context.StreamingResponse);

        var userText = context.UserText ?? string.Empty;

        // Signal that the agent is working
        await eventQueue.EnqueueStatusUpdateAsync(
            new TaskStatusUpdateEvent
            {
                TaskId = context.TaskId,
                ContextId = context.ContextId,
                Status = new A2ATaskStatus { State = TaskState.Working }
            },
            cancellationToken);

        // Process through the echo-style handler (placeholder for full MAF integration)
        var responseText = $"[{_options.AgentName}] {userText}";

        // Return the result as a completed task
        await eventQueue.EnqueueStatusUpdateAsync(
            new TaskStatusUpdateEvent
            {
                TaskId = context.TaskId,
                ContextId = context.ContextId,
                Status = new A2ATaskStatus
                {
                    State = TaskState.Completed,
                    Message = new Message
                    {
                        Role = Role.Agent,
                        Parts = [Part.FromText(responseText)]
                    }
                }
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task CancelAsync(
        RequestContext context,
        AgentEventQueue eventQueue,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("A2A CancelAsync: taskId={TaskId}", context.TaskId);

        await eventQueue.EnqueueStatusUpdateAsync(
            new TaskStatusUpdateEvent
            {
                TaskId = context.TaskId,
                ContextId = context.ContextId,
                Status = new A2ATaskStatus { State = TaskState.Canceled }
            },
            cancellationToken);
    }
}

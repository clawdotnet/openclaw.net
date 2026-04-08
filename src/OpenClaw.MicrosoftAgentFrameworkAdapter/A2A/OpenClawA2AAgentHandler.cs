using A2A;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenClaw.Core.Models;
using System.Text;

namespace OpenClaw.MicrosoftAgentFrameworkAdapter.A2A;

/// <summary>
/// Bridges the A2A <see cref="IAgentHandler"/> contract into the OpenClaw runtime
/// through an injected execution bridge.
/// </summary>
public sealed class OpenClawA2AAgentHandler : IAgentHandler
{
    private readonly MafOptions _options;
    private readonly IOpenClawA2AExecutionBridge _bridge;
    private readonly ILogger<OpenClawA2AAgentHandler> _logger;

    public OpenClawA2AAgentHandler(
        IOptions<MafOptions> options,
        IOpenClawA2AExecutionBridge bridge,
        ILogger<OpenClawA2AAgentHandler> logger)
    {
        _options = options.Value;
        _bridge = bridge;
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

        var updater = new TaskUpdater(eventQueue, context.TaskId, context.ContextId);
        await updater.SubmitAsync(cancellationToken);
        await updater.StartWorkAsync(null, cancellationToken);

        var request = new OpenClawA2AExecutionRequest
        {
            SessionId = context.TaskId,
            ChannelId = "a2a",
            SenderId = string.IsNullOrWhiteSpace(context.ContextId) ? "a2a-client" : context.ContextId,
            UserText = ExtractUserText(context),
            MessageId = context.Message?.MessageId
        };

        var responseText = new StringBuilder();
        string? errorMessage = null;

        try
        {
            await _bridge.ExecuteStreamingAsync(
                request,
                (evt, ct) =>
                {
                    switch (evt.Type)
                    {
                        case AgentStreamEventType.TextDelta when !string.IsNullOrEmpty(evt.Content):
                            responseText.Append(evt.Content);
                            break;
                        case AgentStreamEventType.Error when !string.IsNullOrWhiteSpace(evt.Content):
                            errorMessage = evt.Content;
                            break;
                    }

                    return ValueTask.CompletedTask;
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A2A execution failed for task {TaskId}", context.TaskId);
            await updater.FailAsync(CreateAgentMessage("A2A request failed."), cancellationToken);
            return;
        }

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            await updater.FailAsync(CreateAgentMessage(errorMessage), cancellationToken);
            return;
        }

        await updater.CompleteAsync(
            responseText.Length > 0
                ? CreateAgentMessage(responseText.ToString())
                : CreateAgentMessage($"[{_options.AgentName}] Request completed."),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task CancelAsync(
        RequestContext context,
        AgentEventQueue eventQueue,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("A2A CancelAsync: taskId={TaskId}", context.TaskId);
        var updater = new TaskUpdater(eventQueue, context.TaskId, context.ContextId);
        await updater.CancelAsync(cancellationToken);
    }

    private static string ExtractUserText(RequestContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.UserText))
            return context.UserText;

        if (context.Message?.Parts is not null)
        {
            var text = string.Concat(context.Message.Parts.Select(static part => part.Text));
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return string.Empty;
    }

    private static Message CreateAgentMessage(string text)
        => new()
        {
            Role = Role.Agent,
            Parts = [Part.FromText(text)]
        };
}

using A2A;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenClaw.Core.Models;
using System.Text;

namespace OpenClaw.MicrosoftAgentFrameworkAdapter.A2A;

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

    public async Task ExecuteAsync(
        RequestContext context,
        AgentEventQueue eventQueue,
        CancellationToken cancellationToken)
    {
        var taskId = string.IsNullOrWhiteSpace(context.TaskId)
            ? Guid.NewGuid().ToString("N")
            : context.TaskId;
        var contextId = string.IsNullOrWhiteSpace(context.ContextId)
            ? taskId
            : context.ContextId;

        if (context.StreamingResponse)
        {
            await ExecuteStreamingAsync(context, eventQueue, taskId, contextId, cancellationToken);
            return;
        }

        var responseText = new StringBuilder();
        string? errorMessage = null;

        try
        {
            await _bridge.ExecuteStreamingAsync(
                new OpenClawA2AExecutionRequest
                {
                    SessionId = taskId,
                    ChannelId = "a2a",
                    SenderId = contextId,
                    UserText = ExtractUserText(context),
                    MessageId = context.Message?.MessageId
                },
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
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A2A execution failed for task {TaskId}", taskId);
            await eventQueue.EnqueueMessageAsync(
                CreateAgentMessage("A2A request failed.", taskId, contextId),
                cancellationToken);
            return;
        }

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            _logger.LogWarning("A2A execution failed for task {TaskId}: {Message}", taskId, errorMessage);
            await eventQueue.EnqueueMessageAsync(
                CreateAgentMessage(errorMessage, taskId, contextId),
                cancellationToken);
            return;
        }

        await eventQueue.EnqueueMessageAsync(
            responseText.Length > 0
                ? CreateAgentMessage(responseText.ToString(), taskId, contextId)
                : CreateAgentMessage($"[{OpenClawA2AAgent.GetDisplayName(_options)}] Request completed.", taskId, contextId),
            cancellationToken);
    }

    private async Task ExecuteStreamingAsync(
        RequestContext context,
        AgentEventQueue eventQueue,
        string taskId,
        string contextId,
        CancellationToken cancellationToken)
    {
        var updater = new TaskUpdater(eventQueue, taskId, contextId);
        var responseText = new StringBuilder();
        string? errorMessage = null;
        var deltaCount = 0;
        string? pendingDelta = null;

        await updater.SubmitAsync(cancellationToken);
        await updater.StartWorkAsync(null!, cancellationToken);

        try
        {
            await _bridge.ExecuteStreamingAsync(
                new OpenClawA2AExecutionRequest
                {
                    SessionId = taskId,
                    ChannelId = "a2a",
                    SenderId = contextId,
                    UserText = ExtractUserText(context),
                    MessageId = context.Message?.MessageId
                },
                async (evt, ct) =>
                {
                    switch (evt.Type)
                    {
                        case AgentStreamEventType.TextDelta when !string.IsNullOrEmpty(evt.Content):
                            deltaCount++;
                            responseText.Append(evt.Content);
                            if (!string.IsNullOrEmpty(pendingDelta))
                            {
                                await updater.AddArtifactAsync(
                                    [Part.FromText(pendingDelta)],
                                    artifactId: "text-delta",
                                    name: "Streaming response",
                                    description: "Incremental text from agent",
                                    lastChunk: false,
                                    append: true,
                                    cancellationToken: ct);
                            }

                            pendingDelta = evt.Content;
                            break;
                        case AgentStreamEventType.Error when !string.IsNullOrWhiteSpace(evt.Content):
                            errorMessage = evt.Content;
                            break;
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A2A streaming execution failed for task {TaskId} after {DeltaCount} deltas", taskId, deltaCount);
            if (!string.IsNullOrEmpty(pendingDelta))
            {
                await updater.AddArtifactAsync(
                    [Part.FromText(pendingDelta)],
                    artifactId: "text-delta",
                    name: "Streaming response",
                    description: "Incremental text from agent",
                    lastChunk: false,
                    append: true,
                    cancellationToken: cancellationToken);
            }

            await updater.FailAsync(
                CreateAgentMessage("A2A request failed.", taskId, contextId),
                cancellationToken);
            return;
        }

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            _logger.LogWarning(
                "A2A streaming execution failed for task {TaskId} after {DeltaCount} deltas: {Message}",
                taskId,
                deltaCount,
                errorMessage);

            if (!string.IsNullOrEmpty(pendingDelta))
            {
                await updater.AddArtifactAsync(
                    [Part.FromText(pendingDelta)],
                    artifactId: "text-delta",
                    name: "Streaming response",
                    description: "Incremental text from agent",
                    lastChunk: false,
                    append: true,
                    cancellationToken: cancellationToken);
            }

            await updater.FailAsync(CreateAgentMessage(errorMessage, taskId, contextId), cancellationToken);
            return;
        }

        if (deltaCount == 0)
        {
            var fallbackText = $"[{OpenClawA2AAgent.GetDisplayName(_options)}] Request completed.";
            responseText.Append(fallbackText);
            await updater.AddArtifactAsync(
                [Part.FromText(fallbackText)],
                artifactId: "text-delta",
                name: "Streaming response",
                description: "Incremental text from agent",
                lastChunk: true,
                append: true,
                cancellationToken: cancellationToken);
        }
        else if (!string.IsNullOrEmpty(pendingDelta))
        {
            await updater.AddArtifactAsync(
                [Part.FromText(pendingDelta)],
                artifactId: "text-delta",
                name: "Streaming response",
                description: "Incremental text from agent",
                lastChunk: true,
                append: true,
                cancellationToken: cancellationToken);
        }

        await updater.CompleteAsync(
            CreateAgentMessage(responseText.ToString(), taskId, contextId),
            cancellationToken);
    }

    public async Task CancelAsync(
        RequestContext context,
        AgentEventQueue eventQueue,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.TaskId))
        {
            _logger.LogDebug("Ignoring A2A cancellation request without a task id.");
            return;
        }

        var taskId = context.TaskId;
        var contextId = string.IsNullOrWhiteSpace(context.ContextId)
            ? taskId
            : context.ContextId;
        var updater = new TaskUpdater(eventQueue, taskId, contextId);
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

    private static Message CreateAgentMessage(string text, string taskId, string contextId)
        => new()
        {
            Role = Role.Agent,
            MessageId = Guid.NewGuid().ToString("N"),
            TaskId = taskId,
            ContextId = contextId,
            Parts = [Part.FromText(text)]
        };
}

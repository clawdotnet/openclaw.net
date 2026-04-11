#if OPENCLAW_ENABLE_MAF_EXPERIMENT
using OpenClaw.Core.Middleware;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Mcp;
using OpenClaw.MicrosoftAgentFrameworkAdapter.A2A;

namespace OpenClaw.Gateway.A2A;

internal sealed class OpenClawA2AExecutionBridge : IOpenClawA2AExecutionBridge
{
    private readonly GatewayRuntimeHolder _runtimeHolder;
    private readonly ILogger<OpenClawA2AExecutionBridge> _logger;

    public OpenClawA2AExecutionBridge(
        GatewayRuntimeHolder runtimeHolder,
        ILogger<OpenClawA2AExecutionBridge> logger)
    {
        _runtimeHolder = runtimeHolder;
        _logger = logger;
    }

    public async Task ExecuteStreamingAsync(
        OpenClawA2AExecutionRequest request,
        Func<AgentStreamEvent, CancellationToken, ValueTask> onEvent,
        CancellationToken cancellationToken)
    {
        var runtime = _runtimeHolder.Runtime;
        var session = await runtime.SessionManager.GetOrCreateByIdAsync(
            request.SessionId,
            request.ChannelId,
            request.SenderId,
            cancellationToken);

        await using var sessionLock = await runtime.SessionManager.AcquireSessionLockAsync(session.Id, cancellationToken);

        var (handled, commandResponse) = await runtime.CommandProcessor.TryProcessCommandAsync(
            session,
            request.UserText,
            cancellationToken);
        if (handled)
        {
            if (!string.IsNullOrWhiteSpace(commandResponse))
                await onEvent(AgentStreamEvent.TextDelta(commandResponse), cancellationToken);

            await onEvent(AgentStreamEvent.Complete(), cancellationToken);
            await runtime.SessionManager.PersistAsync(session, cancellationToken, sessionLockHeld: true);
            return;
        }

        var messageContext = new MessageContext
        {
            ChannelId = request.ChannelId,
            SenderId = request.SenderId,
            Text = request.UserText,
            MessageId = request.MessageId,
            SessionId = session.Id,
            SessionInputTokens = session.TotalInputTokens,
            SessionOutputTokens = session.TotalOutputTokens
        };

        if (!await runtime.MiddlewarePipeline.ExecuteAsync(messageContext, cancellationToken))
        {
            await onEvent(
                AgentStreamEvent.TextDelta(messageContext.ShortCircuitResponse ?? "Request blocked."),
                cancellationToken);
            await onEvent(AgentStreamEvent.Complete(), cancellationToken);
            await runtime.SessionManager.PersistAsync(session, cancellationToken, sessionLockHeld: true);
            return;
        }

        try
        {
            await foreach (var evt in runtime.AgentRuntime.RunStreamingAsync(session, messageContext.Text, cancellationToken))
                await onEvent(evt, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A2A execution failed for session {SessionId}", session.Id);
            await onEvent(AgentStreamEvent.ErrorOccurred("A2A request failed."), cancellationToken);
            await onEvent(AgentStreamEvent.Complete(), cancellationToken);
        }
        finally
        {
            await runtime.SessionManager.PersistAsync(session, cancellationToken, sessionLockHeld: true);
        }
    }
}
#endif

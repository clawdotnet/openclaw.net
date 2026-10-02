using OpenClaw.Core.Middleware;
using OpenClaw.Core.Models;
using OpenClaw.Core.Sessions;
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
            cancellationToken,
            A2ACallerContext.AccountId);

        await using var sessionLock = await runtime.SessionManager.AcquireSessionLockAsync(session.Id, cancellationToken);

        // A2A task ids can name an existing session, so apply the same ownership rule as the other surfaces.
        if (!SessionAccess.CanWrite(session, A2ACallerContext.AccountId, A2ACallerContext.IsAdmin))
        {
            await onEvent(AgentStreamEvent.ErrorOccurred(SessionAccess.DeniedMessage), cancellationToken);
            await onEvent(AgentStreamEvent.Complete(), cancellationToken);
            return;
        }

        // The A2A request's SenderId is the caller's contextId, so the turn's identity comes from the signed-in account.
        session.AuthenticatedUserId = A2ACallerContext.AccountId;

        var (handled, commandResponse) = await runtime.CommandProcessor.TryProcessCommandAsync(
            session,
            request.UserText,
            cancellationToken,
            sessionLockHeld: true);
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
            await foreach (var evt in runtime.AgentRuntime.RunStreamingAsync(
                session,
                messageContext.Text,
                cancellationToken,
                callerCredentialContext: A2ACallerContext.McpCallerCredentialContext))
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

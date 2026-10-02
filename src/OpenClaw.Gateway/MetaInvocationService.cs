using System.Security.Cryptography;
using System.Text.Json;
using OpenClaw.Agent;
using OpenClaw.Core.Sessions;
using OpenClaw.Gateway.Models;

namespace OpenClaw.Gateway;

public sealed class MetaInvocationService(
    MetaInvocationStore store,
    SessionManager sessionManager,
    IAgentRuntime agentRuntime)
{
    public async Task<MetaInvocationExecutionResult> InvokeAsync(
        string callerId,
        string? accountId,
        bool isAdmin,
        string idempotencyKey,
        MetaSkillInvocationRequest request,
        CancellationToken cancellationToken)
    {
        var session = await sessionManager.LoadAsync(request.SessionId, cancellationToken);
        if (session is null)
            return new MetaInvocationExecutionResult(StatusCodes.Status404NotFound, Error: "Session not found.");

        if (!SessionAccess.CanWrite(session, accountId, isAdmin))
            return new MetaInvocationExecutionResult(StatusCodes.Status403Forbidden, Error: SessionAccess.DeniedMessage);

        session = await sessionManager.GetOrCreateByIdAsync(
            session.Id,
            session.ChannelId,
            session.SenderId,
            cancellationToken,
            session.OwnerAccountId);

        var requestJson = JsonSerializer.SerializeToUtf8Bytes(
            request,
            MetaInvocationJsonContext.Default.MetaSkillInvocationRequest);
        var requestHash = Convert.ToHexString(SHA256.HashData(requestJson));
        var claim = await store.BeginOrGetAsync(callerId, idempotencyKey, requestHash, cancellationToken);

        if (claim.Kind == MetaInvocationClaimKind.Conflict)
        {
            return new MetaInvocationExecutionResult(
                StatusCodes.Status409Conflict,
                Error: "Idempotency-Key was already used for a different request.");
        }

        if (claim.Kind == MetaInvocationClaimKind.Existing)
        {
            var statusCode = claim.Record.Status switch
            {
                MetaInvocationStatus.Running => StatusCodes.Status202Accepted,
                MetaInvocationStatus.Completed => StatusCodes.Status200OK,
                MetaInvocationStatus.Uncertain => StatusCodes.Status409Conflict,
                _ => throw new InvalidOperationException($"Unsupported meta invocation status '{claim.Record.Status}'.")
            };
            return new MetaInvocationExecutionResult(statusCode, ToResponse(claim.Record));
        }

        try
        {
            await using var sessionLock = await sessionManager.AcquireSessionLockAsync(session.Id, cancellationToken);
            if (!SessionAccess.CanWrite(session, accountId, isAdmin))
            {
                const string denied = "Session ownership changed before invocation execution.";
                await store.MarkUncertainAsync(callerId, idempotencyKey, denied, CancellationToken.None);
                return new MetaInvocationExecutionResult(
                    StatusCodes.Status409Conflict,
                    ToResponse(claim.Record with { Status = MetaInvocationStatus.Uncertain, Error = denied }, result: null));
            }

            session.AuthenticatedUserId = accountId;
            var result = await agentRuntime.InvokeMetaSkillAsync(session, request.Skill, request.Input, cancellationToken);
            await store.CompleteAsync(callerId, idempotencyKey, result, CancellationToken.None);
            return new MetaInvocationExecutionResult(
                StatusCodes.Status200OK,
                ToResponse(claim.Record with { Status = MetaInvocationStatus.Completed, Result = result, Error = null }));
        }
        catch (Exception exception)
        {
            var error = exception is OperationCanceledException
                ? "MetaSkill execution was cancelled; this invocation will not be retried automatically."
                : $"MetaSkill execution failed ({exception.GetType().Name}); this invocation will not be retried automatically.";
            await store.MarkUncertainAsync(callerId, idempotencyKey, error, CancellationToken.None);
            return new MetaInvocationExecutionResult(
                StatusCodes.Status409Conflict,
                ToResponse(claim.Record with { Status = MetaInvocationStatus.Uncertain, Error = error }, result: null));
        }
    }

    private static MetaInvocationResponse ToResponse(
        MetaInvocationRecord record,
        string? result = null,
        string? error = null)
        => new(
            record.InvocationId,
            record.Status,
            result ?? record.Result,
            error ?? record.Error,
            record.CreatedAtUtc);
}
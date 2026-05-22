using OpenClaw.Agent;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway;

internal static class ToolApprovalCallbackFactory
{
    public static ToolApprovalCallback Create(
        GatewayConfig config,
        GatewayAppRuntime runtime,
        Session session,
        string approvalChannelId,
        string senderId,
        GovernanceLedgerService? governanceLedger = null,
        Func<ToolApprovalRequest, string, CancellationToken, Task>? onApprovalRequested = null)
        => Create(
            config,
            runtime.ToolApprovalService,
            runtime.ApprovalAuditStore,
            runtime.Operations,
            session,
            approvalChannelId,
            senderId,
            governanceLedger,
            onApprovalRequested);

    public static ToolApprovalCallback Create(
        GatewayConfig config,
        ToolApprovalService toolApprovalService,
        ApprovalAuditStore approvalAuditStore,
        RuntimeOperationsState operations,
        Session session,
        string approvalChannelId,
        string senderId,
        GovernanceLedgerService? governanceLedger = null,
        Func<ToolApprovalRequest, string, CancellationToken, Task>? onApprovalRequested = null)
    {
        var approvalTimeout = TimeSpan.FromSeconds(Math.Clamp(config.Tooling.ToolApprovalTimeoutSeconds, 5, 3600));

        return async (toolName, argsJson, ct) =>
        {
            var actionDescriptor = ToolActionPolicyResolver.Resolve(toolName, argsJson);
            var grant = operations.ApprovalGrants.TryConsume(session.Id, approvalChannelId, senderId, toolName);
            if (grant is not null)
            {
                operations.RuntimeEvents.Append(new RuntimeEventEntry
                {
                    Id = $"evt_{Guid.NewGuid():N}"[..20],
                    SessionId = session.Id,
                    ChannelId = approvalChannelId,
                    SenderId = senderId,
                    Component = "approval",
                    Action = "grant_consumed",
                    Severity = "info",
                    Summary = $"Reusable approval grant '{grant.Id}' applied for tool '{toolName}'.",
                    Metadata = new Dictionary<string, string>
                    {
                        ["toolName"] = toolName,
                        ["grantId"] = grant.Id,
                        ["scope"] = grant.Scope
                    }
                });
                if (governanceLedger is not null)
                {
                    await governanceLedger.TryRecordApprovalGrantConsumedAsync(
                        grant,
                        new ToolApprovalRequest
                        {
                            ApprovalId = grant.Id,
                            SessionId = session.Id,
                            ChannelId = approvalChannelId,
                            SenderId = senderId,
                            ToolName = toolName,
                            Arguments = argsJson,
                            Action = actionDescriptor.Action,
                            IsMutation = actionDescriptor.IsMutation,
                            Summary = string.IsNullOrWhiteSpace(actionDescriptor.Summary)
                                ? $"Reusable approval grant '{grant.Id}' applied for tool '{toolName}'."
                                : actionDescriptor.Summary
                        },
                        ct);
                }
                return true;
            }

            var request = toolApprovalService.Create(
                session.Id,
                approvalChannelId,
                senderId,
                toolName,
                argsJson,
                approvalTimeout,
                action: actionDescriptor.Action,
                isMutation: actionDescriptor.IsMutation,
                summary: actionDescriptor.Summary);
            approvalAuditStore.RecordCreated(request);

            operations.RuntimeEvents.Append(new RuntimeEventEntry
            {
                Id = $"evt_{Guid.NewGuid():N}"[..20],
                SessionId = session.Id,
                ChannelId = approvalChannelId,
                SenderId = senderId,
                Component = "approval",
                Action = "requested",
                Severity = "info",
                Summary = string.IsNullOrWhiteSpace(actionDescriptor.Summary)
                    ? $"Tool approval requested for '{toolName}'."
                    : actionDescriptor.Summary,
                Metadata = new Dictionary<string, string>
                {
                    ["toolName"] = toolName,
                    ["approvalId"] = request.ApprovalId,
                    ["action"] = actionDescriptor.Action,
                    ["isMutation"] = actionDescriptor.IsMutation ? "true" : "false"
                }
            });

            var preview = argsJson.Length <= 800 ? argsJson : argsJson[..800] + "…";
            if (onApprovalRequested is not null)
                await onApprovalRequested(request, preview, ct);

            var outcome = await toolApprovalService.WaitForDecisionOutcomeAsync(request.ApprovalId, approvalTimeout, ct);
            if (outcome.Result == ToolApprovalWaitResult.TimedOut && outcome.Request is not null)
            {
                approvalAuditStore.RecordDecision(
                    outcome.Request,
                    approved: false,
                    "timeout",
                    actorChannelId: null,
                    actorSenderId: null);
                if (governanceLedger is not null)
                {
                    await governanceLedger.TryRecordExpiredAsync(
                        outcome.Request,
                        GovernanceLedgerSources.ApprovalTimeout,
                        decidedBy: "timeout",
                        ct);
                }
                RecordApprovalTimedOutEvent(operations, outcome.Request);
            }

            return outcome.Result == ToolApprovalWaitResult.Approved;
        };
    }

    internal static void RecordApprovalTimedOutEvent(
        RuntimeOperationsState operations,
        ToolApprovalRequest request)
    {
        operations.RuntimeEvents.Append(new RuntimeEventEntry
        {
            Id = $"evt_{Guid.NewGuid():N}"[..20],
            SessionId = request.SessionId,
            ChannelId = request.ChannelId,
            SenderId = request.SenderId,
            Component = "approval",
            Action = "timed_out",
            Severity = "warning",
            Summary = $"Tool approval timed out for '{request.ToolName}'.",
            Metadata = new Dictionary<string, string>
            {
                ["approvalId"] = request.ApprovalId,
                ["toolName"] = request.ToolName
            }
        });
    }
}

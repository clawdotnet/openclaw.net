using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Payments.Abstractions;

namespace OpenClaw.Gateway;

internal sealed class GatewayPaymentApprovalService : IPaymentApprovalService
{
    private readonly GatewayConfig _config;
    private readonly ToolApprovalService _approvals;
    private readonly ApprovalAuditStore _audit;

    public GatewayPaymentApprovalService(
        GatewayConfig config,
        ToolApprovalService approvals,
        ApprovalAuditStore audit)
    {
        _config = config;
        _approvals = approvals;
        _audit = audit;
    }

    public async ValueTask<ApprovalResult> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct)
    {
        var arguments = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["action"] = request.Action,
            ["provider"] = request.ProviderId ?? "",
            ["merchant"] = request.MerchantName ?? "",
            ["amountMinor"] = request.AmountMinor?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            ["currency"] = request.Currency ?? "",
            ["environment"] = request.Environment
        }, CoreJsonContext.Default.DictionaryStringString);

        var approval = _approvals.Create(
            request.SessionId ?? "payment",
            request.ChannelId ?? "payment",
            request.SenderId ?? "payment",
            "payment",
            arguments,
            TimeSpan.FromSeconds(Math.Clamp(_config.Tooling.ToolApprovalTimeoutSeconds, 5, 3600)),
            action: request.Action,
            isMutation: true,
            summary: request.Summary);
        _audit.RecordCreated(approval);

        var outcome = await _approvals.WaitForDecisionOutcomeAsync(
            approval.ApprovalId,
            TimeSpan.FromSeconds(Math.Clamp(_config.Tooling.ToolApprovalTimeoutSeconds, 5, 3600)),
            ct);

        var approved = outcome.Result == ToolApprovalWaitResult.Approved;
        if (outcome.Request is not null)
        {
            _audit.RecordDecision(
                outcome.Request,
                approved,
                outcome.Result == ToolApprovalWaitResult.TimedOut ? "timeout" : "payment",
                actorChannelId: null,
                actorSenderId: null);
        }

        return new ApprovalResult
        {
            Approved = approved,
            Source = "gateway-tool-approval",
            Reason = outcome.Result switch
            {
                ToolApprovalWaitResult.Approved => "approved",
                ToolApprovalWaitResult.Denied => "denied",
                ToolApprovalWaitResult.TimedOut => "timed out",
                ToolApprovalWaitResult.NotFound => "approval request not found",
                _ => "unknown"
            }
        };
    }
}

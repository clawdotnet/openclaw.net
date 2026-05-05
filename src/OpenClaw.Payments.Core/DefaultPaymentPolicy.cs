using OpenClaw.Payments.Abstractions;

namespace OpenClaw.Payments.Core;

public sealed class DefaultPaymentPolicy : IPaymentPolicy
{
    private readonly bool _allowTestModeWithoutApproval;
    private readonly bool _denyLiveWithoutApprovalService;
    private readonly long? _maxLiveAmountMinor;

    public DefaultPaymentPolicy(
        bool allowTestModeWithoutApproval = true,
        bool denyLiveWithoutApprovalService = true,
        long? maxLiveAmountMinor = null)
    {
        _allowTestModeWithoutApproval = allowTestModeWithoutApproval;
        _denyLiveWithoutApprovalService = denyLiveWithoutApprovalService;
        _maxLiveAmountMinor = maxLiveAmountMinor;
    }

    public ValueTask<PaymentPolicyDecision> EvaluateAsync(ApprovalRequest request, bool approvalServiceAvailable, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var live = string.Equals(request.Environment, PaymentEnvironments.Live, StringComparison.OrdinalIgnoreCase);
        if (live && _maxLiveAmountMinor is { } max && request.AmountMinor is { } amount && amount > max)
        {
            return ValueTask.FromResult(Deny($"Live payment amount {amount} exceeds configured limit {max}."));
        }

        if (!live && _allowTestModeWithoutApproval)
        {
            return ValueTask.FromResult(new PaymentPolicyDecision
            {
                Decision = PaymentDecisionKinds.Allow,
                Reason = "Deterministic test-mode payment allowed by policy."
            });
        }

        if (live && !approvalServiceAvailable && _denyLiveWithoutApprovalService)
            return ValueTask.FromResult(Deny("Live payment denied because no payment approval service is registered."));

        return ValueTask.FromResult(new PaymentPolicyDecision
        {
            Decision = PaymentDecisionKinds.RequireApproval,
            Reason = "Money-moving payment action requires critical approval."
        });
    }

    private static PaymentPolicyDecision Deny(string reason)
        => new()
        {
            Decision = PaymentDecisionKinds.Deny,
            Reason = reason
        };
}

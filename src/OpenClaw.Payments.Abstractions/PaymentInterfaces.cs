namespace OpenClaw.Payments.Abstractions;

public interface IPaymentProvider
{
    string ProviderId { get; }

    ValueTask<PaymentSetupStatus> GetSetupStatusAsync(CancellationToken ct);

    ValueTask<IReadOnlyList<FundingSource>> ListFundingSourcesAsync(PaymentExecutionContext context, CancellationToken ct);

    ValueTask<VirtualCardIssueResult> IssueVirtualCardAsync(
        VirtualCardRequest request,
        PaymentExecutionContext context,
        CancellationToken ct);

    ValueTask<MachinePaymentProviderResult> ExecuteMachinePaymentAsync(
        MachinePaymentRequest request,
        PaymentExecutionContext context,
        CancellationToken ct);

    ValueTask<PaymentStatus> GetPaymentStatusAsync(
        string paymentIdOrHandleId,
        PaymentExecutionContext context,
        CancellationToken ct);
}

public interface IPaymentApprovalService
{
    ValueTask<ApprovalResult> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct);
}

public interface IPaymentSecretVault
{
    ValueTask<string> StoreAsync(PaymentSecret secret, TimeSpan ttl, bool retrieveOnce, CancellationToken ct);
    ValueTask<PaymentSecret?> TryRetrieveAsync(string handleId, string purpose, CancellationToken ct);
    ValueTask RevokeAsync(string handleId, string reason, CancellationToken ct);
}

public interface IPaymentPolicy
{
    ValueTask<PaymentPolicyDecision> EvaluateAsync(ApprovalRequest request, bool approvalServiceAvailable, CancellationToken ct);
}

public interface IPaymentAuditSink
{
    ValueTask RecordAsync(PaymentAuditEvent auditEvent, CancellationToken ct);
}

public interface IPaymentRedactor
{
    string Redact(string? value);
}

public interface ISentinelSubstitutionProvider
{
    string ProviderId { get; }
    bool CanSubstitute(string value);
    ValueTask<string> SubstituteAsync(string value, PaymentExecutionContext context, CancellationToken ct);
}

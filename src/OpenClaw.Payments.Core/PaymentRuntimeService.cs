using OpenClaw.Payments.Abstractions;

namespace OpenClaw.Payments.Core;

public sealed class PaymentRuntimeService
{
    private readonly Dictionary<string, IPaymentProvider> _providers;
    private readonly IPaymentSecretVault _vault;
    private readonly IPaymentPolicy _policy;
    private readonly IPaymentAuditSink _audit;
    private readonly IPaymentApprovalService? _approval;
    private readonly TimeSpan _secretTtl;
    private readonly string _defaultProviderId;

    public PaymentRuntimeService(
        IEnumerable<IPaymentProvider> providers,
        IPaymentSecretVault vault,
        IPaymentPolicy policy,
        IPaymentAuditSink audit,
        IPaymentApprovalService? approval = null,
        string? defaultProviderId = null,
        TimeSpan? secretTtl = null)
    {
        _providers = providers.ToDictionary(p => p.ProviderId, StringComparer.OrdinalIgnoreCase);
        _vault = vault;
        _policy = policy;
        _audit = audit;
        _approval = approval;
        _defaultProviderId = string.IsNullOrWhiteSpace(defaultProviderId)
            ? _providers.Keys.FirstOrDefault() ?? "mock"
            : defaultProviderId;
        _secretTtl = secretTtl ?? TimeSpan.FromMinutes(30);
    }

    public bool HasApprovalService => _approval is not null;

    public ValueTask<PaymentSetupStatus> GetSetupStatusAsync(string? providerId, CancellationToken ct)
        => ResolveProvider(providerId).GetSetupStatusAsync(ct);

    public ValueTask<IReadOnlyList<FundingSource>> ListFundingSourcesAsync(
        string? providerId,
        PaymentExecutionContext context,
        CancellationToken ct)
        => ResolveProvider(providerId).ListFundingSourcesAsync(NormalizeContext(context), ct);

    public async ValueTask<VirtualCardHandle> IssueVirtualCardAsync(
        VirtualCardRequest request,
        PaymentExecutionContext context,
        CancellationToken ct)
    {
        var provider = ResolveProvider(request.ProviderId);
        var effectiveContext = NormalizeContext(context, request.Environment);
        var approval = BuildApprovalRequest(
            PaymentActions.IssueVirtualCard,
            $"Issue virtual card for {request.MerchantName} ({request.AmountMinor} {request.Currency}) using {provider.ProviderId}.",
            provider.ProviderId,
            request.MerchantName,
            request.AmountMinor,
            request.Currency,
            request.ValidUntilUtc,
            effectiveContext);

        await RequireApprovalOrPolicyAllowAsync(approval, ct);

        try
        {
            var issue = await provider.IssueVirtualCardAsync(request with
            {
                ProviderId = provider.ProviderId,
                Environment = effectiveContext.Environment
            }, effectiveContext, ct);

            if (issue.Secret is null)
                throw new InvalidOperationException("Payment provider did not return a vaultable virtual card secret.");

            await _vault.StoreAsync(issue.Secret, ResolveSecretTtl(issue.Handle.ValidUntilUtc), retrieveOnce: false, ct);
            await _audit.RecordAsync(new PaymentAuditEvent
            {
                EventType = "virtual_card_issued",
                ProviderId = provider.ProviderId,
                HandleId = issue.Handle.HandleId,
                Last4 = issue.Handle.Last4,
                MerchantName = issue.Handle.TargetMerchantName,
                AmountMinor = request.AmountMinor,
                Currency = request.Currency,
                IssuedAtUtc = issue.Handle.IssuedAtUtc,
                ValidUntilUtc = issue.Handle.ValidUntilUtc,
                Status = issue.Handle.Status,
                Environment = effectiveContext.Environment
            }, ct);
            return issue.Handle;
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(request.ProviderId))
                await _audit.RecordAsync(new PaymentAuditEvent
                {
                    EventType = "virtual_card_failed",
                    ProviderId = provider.ProviderId,
                    MerchantName = request.MerchantName,
                    AmountMinor = request.AmountMinor,
                    Currency = request.Currency,
                    Status = "failed",
                    Environment = effectiveContext.Environment
                }, CancellationToken.None);
            throw;
        }
    }

    public async ValueTask<MachinePaymentResult> ExecuteMachinePaymentAsync(
        MachinePaymentRequest request,
        PaymentExecutionContext context,
        CancellationToken ct)
    {
        var provider = ResolveProvider(request.ProviderId ?? request.Challenge.ProviderId);
        var effectiveContext = NormalizeContext(context, request.Environment);
        var approval = BuildApprovalRequest(
            PaymentActions.ExecuteMachinePayment,
            $"Execute machine payment for {request.Challenge.MerchantName ?? request.Challenge.ResourceUrl ?? "paid resource"} ({request.Challenge.AmountMinor} {request.Challenge.Currency}) using {provider.ProviderId}.",
            provider.ProviderId,
            request.Challenge.MerchantName,
            request.Challenge.AmountMinor,
            request.Challenge.Currency,
            expiresAtUtc: null,
            effectiveContext);

        await RequireApprovalOrPolicyAllowAsync(approval, ct);
        await _audit.RecordAsync(new PaymentAuditEvent
        {
            EventType = "machine_payment_attempted",
            ProviderId = provider.ProviderId,
            MerchantName = request.Challenge.MerchantName,
            AmountMinor = request.Challenge.AmountMinor,
            Currency = request.Challenge.Currency,
            Environment = effectiveContext.Environment
        }, ct);

        var providerResult = await provider.ExecuteMachinePaymentAsync(request with
        {
            ProviderId = provider.ProviderId,
            Environment = effectiveContext.Environment
        }, effectiveContext, ct);

        if (providerResult.ScopedAuthorizationSecret is not null)
            await _vault.StoreAsync(providerResult.ScopedAuthorizationSecret, TimeSpan.FromMinutes(5), retrieveOnce: true, ct);

        await _audit.RecordAsync(new PaymentAuditEvent
        {
            EventType = providerResult.Result.Status.Equals("completed", StringComparison.OrdinalIgnoreCase)
                ? "machine_payment_completed"
                : "machine_payment_failed",
            ProviderId = provider.ProviderId,
            PaymentId = providerResult.Result.PaymentId,
            MerchantName = providerResult.Result.MerchantName,
            AmountMinor = providerResult.Result.AmountMinor,
            Currency = providerResult.Result.Currency,
            Status = providerResult.Result.Status,
            Environment = effectiveContext.Environment
        }, ct);
        return providerResult.Result;
    }

    public async ValueTask<PaymentStatus> GetPaymentStatusAsync(
        string paymentIdOrHandleId,
        string? providerId,
        PaymentExecutionContext context,
        CancellationToken ct)
    {
        var provider = ResolveProvider(providerId);
        var status = await provider.GetPaymentStatusAsync(paymentIdOrHandleId, NormalizeContext(context), ct);
        await _audit.RecordAsync(new PaymentAuditEvent
        {
            EventType = "payment_status_checked",
            ProviderId = provider.ProviderId,
            PaymentId = status.PaymentId,
            MerchantName = status.MerchantName,
            AmountMinor = status.AmountMinor,
            Currency = status.Currency,
            Status = status.Status,
            Environment = context.Environment
        }, ct);
        return status;
    }

    public async ValueTask<string> ResolveSecretFieldForApprovedBoundaryAsync(
        string handleId,
        PaymentSecretField field,
        ApprovalRequest approval,
        CancellationToken ct)
    {
        await RequireApprovalOrPolicyAllowAsync(approval, ct);
        var secret = await _vault.TryRetrieveAsync(handleId, "sentinel-substitution", ct)
            ?? throw new InvalidOperationException("Payment secret is missing, expired, or revoked.");
        var value = secret.Resolve(field);
        if (string.IsNullOrEmpty(value))
            throw new InvalidOperationException($"Payment secret field '{field}' is not available.");
        return value;
    }

    public async ValueTask<PaymentSecret?> RetrieveMachineAuthorizationOnceAsync(string paymentId, CancellationToken ct)
        => await _vault.TryRetrieveAsync(paymentId, "machine-payment-authorization", ct);

    private async ValueTask RequireApprovalOrPolicyAllowAsync(ApprovalRequest request, CancellationToken ct)
    {
        await _audit.RecordAsync(new PaymentAuditEvent
        {
            EventType = "approval_requested",
            ProviderId = request.ProviderId ?? _defaultProviderId,
            MerchantName = request.MerchantName,
            AmountMinor = request.AmountMinor,
            Currency = request.Currency,
            Environment = request.Environment
        }, ct);

        var decision = await _policy.EvaluateAsync(request, _approval is not null || request.CliConfirmed, ct);
        if (decision.Denied)
        {
            await _audit.RecordAsync(new PaymentAuditEvent
            {
                EventType = "policy_denied",
                ProviderId = request.ProviderId ?? _defaultProviderId,
                MerchantName = request.MerchantName,
                AmountMinor = request.AmountMinor,
                Currency = request.Currency,
                Decision = decision.Decision,
                Reason = decision.Reason,
                Environment = request.Environment
            }, ct);
            throw new PaymentPolicyDeniedException(decision.Reason);
        }

        if (decision.Allowed)
            return;

        ApprovalResult approval;
        if (request.CliConfirmed)
        {
            approval = new ApprovalResult
            {
                Approved = true,
                Source = "cli --yes",
                Reason = "Operator confirmed live payment command noninteractively."
            };
        }
        else if (_approval is null)
        {
            throw new PaymentPolicyDeniedException("Payment requires approval but no approval service is registered.");
        }
        else
        {
            approval = await _approval.RequestApprovalAsync(request, ct);
        }

        await _audit.RecordAsync(new PaymentAuditEvent
        {
            EventType = approval.Approved ? "approval_granted" : "approval_denied",
            ProviderId = request.ProviderId ?? _defaultProviderId,
            MerchantName = request.MerchantName,
            AmountMinor = request.AmountMinor,
            Currency = request.Currency,
            Decision = approval.Approved ? "approved" : "denied",
            Reason = approval.Reason,
            Environment = request.Environment
        }, ct);

        if (!approval.Approved)
            throw new PaymentPolicyDeniedException(approval.Reason ?? "Payment approval denied.");
    }

    private IPaymentProvider ResolveProvider(string? providerId)
    {
        var id = string.IsNullOrWhiteSpace(providerId) ? _defaultProviderId : providerId;
        if (_providers.TryGetValue(id, out var provider))
            return provider;

        throw new InvalidOperationException($"Payment provider '{id}' is not registered.");
    }

    private PaymentExecutionContext NormalizeContext(PaymentExecutionContext context, string? environment = null)
        => context with
        {
            Environment = string.IsNullOrWhiteSpace(environment)
                ? (string.IsNullOrWhiteSpace(context.Environment) ? PaymentEnvironments.Test : context.Environment)
                : environment
        };

    private TimeSpan ResolveSecretTtl(DateTimeOffset? validUntilUtc)
    {
        if (validUntilUtc is null)
            return _secretTtl;
        var ttl = validUntilUtc.Value - DateTimeOffset.UtcNow;
        return ttl <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : ttl;
    }

    private static ApprovalRequest BuildApprovalRequest(
        string action,
        string summary,
        string providerId,
        string? merchantName,
        long? amountMinor,
        string? currency,
        DateTimeOffset? expiresAtUtc,
        PaymentExecutionContext context)
        => new()
        {
            Action = action,
            Summary = summary,
            MerchantName = merchantName,
            AmountMinor = amountMinor,
            Currency = currency,
            ProviderId = providerId,
            ExpiresAtUtc = expiresAtUtc,
            Environment = context.Environment,
            AgentId = context.SenderId,
            WorkspaceId = context.WorkspaceId,
            SessionId = context.SessionId,
            ChannelId = context.ChannelId,
            SenderId = context.SenderId,
            CliConfirmed = context.CliConfirmed
        };
}

public sealed class PaymentPolicyDeniedException : InvalidOperationException
{
    public PaymentPolicyDeniedException(string message) : base(message)
    {
    }
}

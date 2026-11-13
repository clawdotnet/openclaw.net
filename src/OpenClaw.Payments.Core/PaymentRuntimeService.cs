using System.Security.Cryptography;
using System.Text.Json;
using OpenClaw.Payments.Abstractions;

namespace OpenClaw.Payments.Core;

public sealed class PaymentRuntimeService
{
    private readonly Dictionary<string, IPaymentProvider> _providers;
    private readonly IPaymentSecretVault _vault;
    private readonly IPaymentIdempotencyStore _idempotency;
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
        TimeSpan? secretTtl = null,
        IPaymentIdempotencyStore? idempotencyStore = null)
    {
        _providers = providers.ToDictionary(p => p.ProviderId, StringComparer.OrdinalIgnoreCase);
        _vault = vault;
        _idempotency = idempotencyStore ?? new InMemoryPaymentIdempotencyStore();
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
        ValidateVirtualCardRequest(request);
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

            var secret = PrepareSecretForVault(
                issue.Secret,
                issue.Handle.HandleId,
                provider.ProviderId,
                effectiveContext.Environment,
                issue.Handle.ValidUntilUtc);
            await _vault.StoreAsync(secret, ResolveSecretTtl(issue.Handle.ValidUntilUtc), retrieveOnce: false, ct);
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
        ArgumentNullException.ThrowIfNull(request.Challenge);
        var provider = ResolveProvider(request.ProviderId ?? request.Challenge.ProviderId);
        var effectiveContext = NormalizeContext(context, request.Environment);
        ValidateMachinePaymentRequest(request);
        var normalizedRequest = request with
        {
            ProviderId = provider.ProviderId,
            Environment = effectiveContext.Environment,
            Challenge = request.Challenge with
            {
                ProviderId = provider.ProviderId,
                SafeMetadata = new Dictionary<string, string>((request.Challenge.SafeMetadata ?? []).OrderBy(p => p.Key, StringComparer.Ordinal))
            }
        };
        var fingerprintRequest = request.IdempotencyKey!.StartsWith("auto-http-v1-", StringComparison.Ordinal)
            ? normalizedRequest with { Challenge = normalizedRequest.Challenge with { ChallengeId = null } }
            : normalizedRequest;
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            fingerprintRequest, PaymentJsonContext.Default.MachinePaymentRequest)));
        var scope = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["provider"] = provider.ProviderId.ToLowerInvariant(),
            ["environment"] = effectiveContext.Environment,
            ["caller"] = effectiveContext.CallerId ?? "",
            ["channel"] = effectiveContext.CallerId is null ? effectiveContext.ChannelId ?? "" : "",
            ["sender"] = effectiveContext.CallerId is null ? effectiveContext.SenderId ?? "" : "",
            ["workspace"] = effectiveContext.WorkspaceId ?? ""
        }, PaymentJsonContext.Default.DictionaryStringString);

        var replay = await _idempotency.TryReplayAsync(scope, request.IdempotencyKey!, fingerprint, ct);
        if (replay is not null)
        {
            await RecordMachinePaymentResultAsync(provider.ProviderId, replay, effectiveContext,
                eventType: "machine_payment_replayed", ct);
            return replay;
        }

        // Provider configuration checks must happen before an idempotency reservation,
        // but a completed replay remains safe even if deployment settings later change.
        if (provider is IPaymentProviderPreflight preflight)
            preflight.ValidateMachinePayment(normalizedRequest, effectiveContext);

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

        MachinePaymentProviderResult? providerResult = null;
        var result = await _idempotency.ExecuteOnceAsync(scope, request.IdempotencyKey!, fingerprint, async dispatchCt =>
        {
            try
            {
                providerResult = await provider.ExecuteMachinePaymentAsync(normalizedRequest, effectiveContext, dispatchCt);
                if (providerResult.ScopedAuthorizationSecret is not null)
                {
                    var secret = PrepareSecretForVault(
                        providerResult.ScopedAuthorizationSecret,
                        providerResult.Result.PaymentId,
                        provider.ProviderId,
                        effectiveContext.Environment,
                        DateTimeOffset.UtcNow.AddMinutes(5));
                    // The payment already completed. Persist its one-time authorization
                    // independently of a disconnected HTTP caller before publishing completion.
                    await _vault.StoreAsync(secret, TimeSpan.FromMinutes(5), retrieveOnce: true, CancellationToken.None);
                }
                return providerResult.Result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not PaymentPolicyDeniedException)
            {
                throw new PaymentIdempotencyException("payment_outcome_unknown",
                    "Payment provider outcome is unknown. Reconcile with the provider before making another payment. Do not retry with a new key.", ex);
            }
        }, ct);
        var eventType = providerResult is null ? "machine_payment_replayed" : result.Status.ToLowerInvariant() switch
        {
            "completed" => "machine_payment_completed",
            "failed" or "declined" or "canceled" => "machine_payment_failed",
            _ => "machine_payment_pending"
        };
        await RecordMachinePaymentResultAsync(provider.ProviderId, result, effectiveContext, eventType, ct);
        return result;
    }

    private ValueTask RecordMachinePaymentResultAsync(
        string providerId,
        MachinePaymentResult result,
        PaymentExecutionContext context,
        string eventType,
        CancellationToken ct)
        => _audit.RecordAsync(new PaymentAuditEvent
        {
            EventType = eventType,
            ProviderId = providerId,
            PaymentId = result.PaymentId,
            MerchantName = result.MerchantName,
            AmountMinor = result.AmountMinor,
            Currency = result.Currency,
            Status = result.Status,
            Environment = context.Environment
        }, ct);

    public async ValueTask<PaymentStatus> GetPaymentStatusAsync(
        string paymentIdOrHandleId,
        string? providerId,
        PaymentExecutionContext context,
        CancellationToken ct)
    {
        var provider = ResolveProvider(providerId);
        var effectiveContext = NormalizeContext(context);
        var status = await provider.GetPaymentStatusAsync(paymentIdOrHandleId, effectiveContext, ct);
        await _audit.RecordAsync(new PaymentAuditEvent
        {
            EventType = "payment_status_checked",
            ProviderId = provider.ProviderId,
            PaymentId = status.PaymentId,
            MerchantName = status.MerchantName,
            AmountMinor = status.AmountMinor,
            Currency = status.Currency,
            Status = status.Status,
            Environment = effectiveContext.Environment
        }, ct);
        return status;
    }

    public async ValueTask<string> ResolveSecretFieldForApprovedBoundaryAsync(
        string handleId,
        PaymentSecretField field,
        ApprovalRequest approval,
        CancellationToken ct)
    {
        var secret = await _vault.TryRetrieveAsync(handleId, "sentinel-substitution", ct)
            ?? throw new InvalidOperationException("Payment secret is missing, expired, or revoked.");
        var effectiveApproval = approval with
        {
            ProviderId = secret.ProviderId,
            Environment = secret.Environment
        };

        await _audit.RecordAsync(new PaymentAuditEvent
        {
            EventType = "browser_sentinel_substitution_attempted",
            ProviderId = effectiveApproval.ProviderId ?? secret.ProviderId,
            HandleId = handleId,
            MerchantName = effectiveApproval.MerchantName,
            AmountMinor = effectiveApproval.AmountMinor,
            Currency = effectiveApproval.Currency,
            Environment = effectiveApproval.Environment
        }, ct);

        try
        {
            await RequireApprovalOrPolicyAllowAsync(effectiveApproval, ct);
        }
        catch (PaymentPolicyDeniedException)
        {
            await _audit.RecordAsync(new PaymentAuditEvent
            {
                EventType = "browser_sentinel_substitution_denied",
                ProviderId = effectiveApproval.ProviderId ?? secret.ProviderId,
                HandleId = handleId,
                MerchantName = effectiveApproval.MerchantName,
                AmountMinor = effectiveApproval.AmountMinor,
                Currency = effectiveApproval.Currency,
                Status = "denied",
                Environment = effectiveApproval.Environment
            }, CancellationToken.None);
            throw;
        }

        var value = secret.Resolve(field);
        if (string.IsNullOrEmpty(value))
            throw new InvalidOperationException($"Payment secret field '{field}' is not available.");

        await _audit.RecordAsync(new PaymentAuditEvent
        {
            EventType = "browser_sentinel_substitution_approved",
            ProviderId = effectiveApproval.ProviderId ?? secret.ProviderId,
            HandleId = handleId,
            MerchantName = effectiveApproval.MerchantName,
            AmountMinor = effectiveApproval.AmountMinor,
            Currency = effectiveApproval.Currency,
            Status = "approved",
            Environment = effectiveApproval.Environment
        }, ct);
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
            Environment = PaymentEnvironments.Normalize(
                string.IsNullOrWhiteSpace(environment) ? context.Environment : environment)
        };

    private TimeSpan ResolveSecretTtl(DateTimeOffset? validUntilUtc)
    {
        if (validUntilUtc is null)
            return _secretTtl;
        var ttl = validUntilUtc.Value - DateTimeOffset.UtcNow;
        if (ttl <= TimeSpan.Zero)
            return TimeSpan.FromMinutes(1);
        return ttl < _secretTtl ? ttl : _secretTtl;
    }

    private static void ValidateVirtualCardRequest(VirtualCardRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.MerchantName))
            throw new ArgumentException("merchant is required.", nameof(request));
        if (request.AmountMinor <= 0)
            throw new ArgumentException("amount_minor must be greater than 0.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Currency))
            throw new ArgumentException("currency is required.", nameof(request));
    }

    private static void ValidateMachinePaymentRequest(MachinePaymentRequest request)
    {
        if (request.Challenge.AmountMinor <= 0)
            throw new ArgumentException("amount_minor must be greater than 0.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Challenge.Currency))
            throw new ArgumentException("currency is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200)
            throw new ArgumentException("idempotencyKey is required and must not exceed 200 characters. Reuse it for retries of the same payment.", nameof(request));
    }

    private static PaymentSecret PrepareSecretForVault(
        PaymentSecret secret,
        string handleId,
        string providerId,
        string environment,
        DateTimeOffset? expiresAtUtc)
        => new(
            handleId,
            providerId,
            pan: secret.Resolve(PaymentSecretField.Pan),
            cvv: secret.Resolve(PaymentSecretField.Cvv),
            expMonth: secret.Resolve(PaymentSecretField.ExpMonth),
            expYear: secret.Resolve(PaymentSecretField.ExpYear),
            postalCode: secret.Resolve(PaymentSecretField.PostalCode),
            authorizationToken: secret.Resolve(PaymentSecretField.AuthorizationToken),
            authorizationHeader: secret.Resolve(PaymentSecretField.AuthorizationHeader),
            expiresAtUtc: expiresAtUtc ?? secret.ExpiresAtUtc,
            environment: environment);

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

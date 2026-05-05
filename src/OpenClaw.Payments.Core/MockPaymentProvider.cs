using System.Collections.Concurrent;
using OpenClaw.Payments.Abstractions;

namespace OpenClaw.Payments.Core;

public sealed class MockPaymentProvider : IPaymentProvider
{
    private readonly ConcurrentDictionary<string, PaymentStatus> _statuses = new(StringComparer.Ordinal);
    private long _sequence;

    public string ProviderId { get; }
    public string FundingSourceDisplayName { get; }

    public MockPaymentProvider(string providerId = "mock", string fundingSourceDisplayName = "Mock Visa ending 4242")
    {
        ProviderId = string.IsNullOrWhiteSpace(providerId) ? "mock" : providerId;
        FundingSourceDisplayName = fundingSourceDisplayName;
    }

    public ValueTask<PaymentSetupStatus> GetSetupStatusAsync(CancellationToken ct)
        => ValueTask.FromResult(new PaymentSetupStatus
        {
            ProviderId = ProviderId,
            Enabled = true,
            Installed = true,
            Version = "mock-1",
            Mode = PaymentEnvironments.Test,
            Status = "ready",
            Message = "Deterministic mock payment provider is ready."
        });

    public ValueTask<IReadOnlyList<FundingSource>> ListFundingSourcesAsync(PaymentExecutionContext context, CancellationToken ct)
        => ValueTask.FromResult<IReadOnlyList<FundingSource>>([
            new FundingSource
            {
                FundingSourceId = "mock_fs_visa_4242",
                ProviderId = ProviderId,
                DisplayName = FundingSourceDisplayName,
                Type = "card",
                Last4 = "4242",
                Currency = "USD",
                TestMode = true,
                Available = true
            }
        ]);

    public ValueTask<VirtualCardIssueResult> IssueVirtualCardAsync(
        VirtualCardRequest request,
        PaymentExecutionContext context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sequence = Interlocked.Increment(ref _sequence);
        var handleId = $"pvh_mock_{sequence:D6}";
        var spendId = $"mock_spend_{sequence:D6}";
        var validUntil = request.ValidUntilUtc ?? DateTimeOffset.UtcNow.AddMinutes(30);
        var pan = "4242424242424242";
        var secret = new PaymentSecret(
            handleId,
            ProviderId,
            pan: pan,
            cvv: "123",
            expMonth: "12",
            expYear: "2030",
            postalCode: "94107",
            expiresAtUtc: validUntil);
        var handle = new VirtualCardHandle
        {
            HandleId = handleId,
            ProviderId = ProviderId,
            Last4 = "4242",
            TargetMerchantName = request.MerchantName,
            IssuedAtUtc = DateTimeOffset.UtcNow,
            ValidUntilUtc = validUntil,
            SpendRequestId = spendId,
            Status = "issued",
            Environment = context.Environment
        };

        _statuses[handleId] = new PaymentStatus
        {
            PaymentId = handleId,
            ProviderId = ProviderId,
            Status = "issued",
            MerchantName = request.MerchantName,
            AmountMinor = request.AmountMinor,
            Currency = request.Currency,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            ProviderReference = spendId
        };

        return ValueTask.FromResult(new VirtualCardIssueResult
        {
            Handle = handle,
            Secret = secret
        });
    }

    public ValueTask<MachinePaymentProviderResult> ExecuteMachinePaymentAsync(
        MachinePaymentRequest request,
        PaymentExecutionContext context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sequence = Interlocked.Increment(ref _sequence);
        var paymentId = $"mp_mock_{sequence:D6}";
        var result = new MachinePaymentResult
        {
            PaymentId = paymentId,
            ProviderId = ProviderId,
            Status = "completed",
            MerchantName = request.Challenge.MerchantName,
            AmountMinor = request.Challenge.AmountMinor,
            Currency = request.Challenge.Currency,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ProviderReference = request.Challenge.ChallengeId ?? $"challenge_{sequence:D6}",
            SafeMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["protocol"] = request.Challenge.Protocol ?? "mock-402"
            }
        };
        _statuses[paymentId] = new PaymentStatus
        {
            PaymentId = paymentId,
            ProviderId = ProviderId,
            Status = result.Status,
            MerchantName = result.MerchantName,
            AmountMinor = result.AmountMinor,
            Currency = result.Currency,
            UpdatedAtUtc = result.CreatedAtUtc,
            ProviderReference = result.ProviderReference
        };

        var secret = new PaymentSecret(
            paymentId,
            ProviderId,
            authorizationToken: $"payment_mock_secret_token_{sequence:D6}",
            authorizationHeader: $"Payment payment_mock_secret_token_{sequence:D6}",
            expiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(5));

        return ValueTask.FromResult(new MachinePaymentProviderResult
        {
            Result = result,
            ScopedAuthorizationSecret = secret
        });
    }

    public ValueTask<PaymentStatus> GetPaymentStatusAsync(string paymentIdOrHandleId, PaymentExecutionContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_statuses.TryGetValue(paymentIdOrHandleId, out var status))
            return ValueTask.FromResult(status);

        return ValueTask.FromResult(new PaymentStatus
        {
            PaymentId = paymentIdOrHandleId,
            ProviderId = ProviderId,
            Status = "not_found",
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });
    }
}

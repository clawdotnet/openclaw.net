using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Payments.Abstractions;

public static class PaymentEnvironments
{
    public const string Test = "test";
    public const string Live = "live";
}

public static class PaymentActions
{
    public const string SetupStatus = "setup_status";
    public const string ListFundingSources = "list_funding_sources";
    public const string IssueVirtualCard = "issue_virtual_card";
    public const string ExecuteMachinePayment = "execute_machine_payment";
    public const string GetPaymentStatus = "get_payment_status";
    public const string BrowserSentinelFill = "browser_sentinel_fill";
}

public static class PaymentDecisionKinds
{
    public const string Allow = "allow";
    public const string Deny = "deny";
    public const string RequireApproval = "require_approval";
}

public static class PaymentApprovalSeverities
{
    public const string Critical = "critical";
}

public sealed record PaymentSetupRequirement
{
    public required string Name { get; init; }
    public bool Satisfied { get; init; }
    public string? Message { get; init; }
}

public sealed record PaymentSetupStatus
{
    public required string ProviderId { get; init; }
    public bool Enabled { get; init; }
    public bool Installed { get; init; }
    public string? Version { get; init; }
    public string Mode { get; init; } = PaymentEnvironments.Test;
    public string Status { get; init; } = "unknown";
    public string? Message { get; init; }
    public List<PaymentSetupRequirement> Requirements { get; init; } = [];
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.Ordinal);
}

public sealed record FundingSource
{
    public required string FundingSourceId { get; init; }
    public required string ProviderId { get; init; }
    public string DisplayName { get; init; } = "";
    public string Type { get; init; } = "";
    public string? Last4 { get; init; }
    public string? Currency { get; init; }
    public bool TestMode { get; init; } = true;
    public bool Available { get; init; } = true;
}

public sealed record BuyerProfile
{
    public string? BuyerId { get; init; }
    public string? DisplayName { get; init; }
    public string? EmailHash { get; init; }
    public string? Country { get; init; }
}

public sealed record VirtualCardRequest
{
    public string? ProviderId { get; init; }
    public string? FundingSourceId { get; init; }
    public required string MerchantName { get; init; }
    public string? MerchantUrl { get; init; }
    public long AmountMinor { get; init; }
    public required string Currency { get; init; }
    public string? Purpose { get; init; }
    public DateTimeOffset? ValidUntilUtc { get; init; }
    public BuyerProfile? BuyerProfile { get; init; }
    public string Environment { get; init; } = PaymentEnvironments.Test;
}

public sealed record VirtualCardHandle
{
    public required string HandleId { get; init; }
    public required string ProviderId { get; init; }
    public string? Last4 { get; init; }
    public string? TargetMerchantName { get; init; }
    public DateTimeOffset IssuedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ValidUntilUtc { get; init; }
    public string? SpendRequestId { get; init; }
    public string Status { get; init; } = "issued";
    public string Environment { get; init; } = PaymentEnvironments.Test;
}

public sealed record VirtualCardIssueResult
{
    public required VirtualCardHandle Handle { get; init; }
    [JsonIgnore]
    public PaymentSecret? Secret { get; init; }
}

public sealed record MachinePaymentChallenge
{
    public string? ChallengeId { get; init; }
    public string? Protocol { get; init; }
    public string? ResourceUrl { get; init; }
    public string? MerchantName { get; init; }
    public long AmountMinor { get; init; }
    public string Currency { get; init; } = "USD";
    public string? ProviderId { get; init; }
    public Dictionary<string, string> SafeMetadata { get; init; } = new(StringComparer.Ordinal);
}

public sealed record MachinePaymentRequest
{
    public string? ProviderId { get; init; }
    public required MachinePaymentChallenge Challenge { get; init; }
    public string? FundingSourceId { get; init; }
    public string Environment { get; init; } = PaymentEnvironments.Test;
    public string? IdempotencyKey { get; init; }
}

public sealed record MachinePaymentResult
{
    public required string PaymentId { get; init; }
    public required string ProviderId { get; init; }
    public string Status { get; init; } = "unknown";
    public string? MerchantName { get; init; }
    public long AmountMinor { get; init; }
    public string Currency { get; init; } = "USD";
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? ProviderReference { get; init; }
    public Dictionary<string, string> SafeMetadata { get; init; } = new(StringComparer.Ordinal);
}

public sealed record MachinePaymentProviderResult
{
    public required MachinePaymentResult Result { get; init; }
    [JsonIgnore]
    public PaymentSecret? ScopedAuthorizationSecret { get; init; }
}

public sealed record PaymentStatus
{
    public required string PaymentId { get; init; }
    public required string ProviderId { get; init; }
    public string Status { get; init; } = "unknown";
    public string? MerchantName { get; init; }
    public long? AmountMinor { get; init; }
    public string? Currency { get; init; }
    public DateTimeOffset? UpdatedAtUtc { get; init; }
    public string? ProviderReference { get; init; }
}

public sealed record PaymentPolicyDecision
{
    public required string Decision { get; init; }
    public string Reason { get; init; } = "";
    public string Severity { get; init; } = PaymentApprovalSeverities.Critical;
    public bool RequiresApproval => string.Equals(Decision, PaymentDecisionKinds.RequireApproval, StringComparison.OrdinalIgnoreCase);
    public bool Allowed => string.Equals(Decision, PaymentDecisionKinds.Allow, StringComparison.OrdinalIgnoreCase);
    public bool Denied => string.Equals(Decision, PaymentDecisionKinds.Deny, StringComparison.OrdinalIgnoreCase);
}

public sealed record ApprovalRequest
{
    public required string Action { get; init; }
    public required string Summary { get; init; }
    public string Severity { get; init; } = PaymentApprovalSeverities.Critical;
    public string? MerchantName { get; init; }
    public long? AmountMinor { get; init; }
    public string? Currency { get; init; }
    public string? FundingSourceDisplay { get; init; }
    public string? ProviderId { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }
    public string Environment { get; init; } = PaymentEnvironments.Test;
    public string? AgentId { get; init; }
    public string? WorkspaceId { get; init; }
    public string? SessionId { get; init; }
    public string? ChannelId { get; init; }
    public string? SenderId { get; init; }
    public bool CliConfirmed { get; init; }
}

public sealed record ApprovalResult
{
    public bool Approved { get; init; }
    public string Source { get; init; } = "unknown";
    public string? Reason { get; init; }
    public DateTimeOffset DecidedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record PaymentExecutionContext
{
    public string? SessionId { get; init; }
    public string? ChannelId { get; init; }
    public string? SenderId { get; init; }
    public string? CorrelationId { get; init; }
    public string? WorkspaceId { get; init; }
    public string Environment { get; init; } = PaymentEnvironments.Test;
    public bool CliConfirmed { get; init; }
    public bool AllowTestModeWithoutApproval { get; init; } = true;
}

public sealed record PaymentAuditEvent
{
    public required string EventType { get; init; }
    public required string ProviderId { get; init; }
    public string? HandleId { get; init; }
    public string? PaymentId { get; init; }
    public string? Last4 { get; init; }
    public string? MerchantName { get; init; }
    public long? AmountMinor { get; init; }
    public string? Currency { get; init; }
    public DateTimeOffset? IssuedAtUtc { get; init; }
    public DateTimeOffset? ValidUntilUtc { get; init; }
    public string? Status { get; init; }
    public string? Decision { get; init; }
    public string? Reason { get; init; }
    public string Environment { get; init; } = PaymentEnvironments.Test;
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
}

public enum PaymentSecretField
{
    Pan,
    Cvv,
    ExpMonth,
    ExpYear,
    ExpMonthYearShort,
    ExpMonthYearLong,
    PostalCode,
    AuthorizationHeader,
    AuthorizationToken
}

[JsonConverter(typeof(PaymentSecretJsonConverter))]
public sealed class PaymentSecret
{
    private string? _pan;
    private string? _cvv;
    private string? _expMonth;
    private string? _expYear;
    private string? _postalCode;
    private string? _authorizationToken;
    private string? _authorizationHeader;

    public PaymentSecret(
        string handleId,
        string providerId,
        string? pan = null,
        string? cvv = null,
        string? expMonth = null,
        string? expYear = null,
        string? postalCode = null,
        string? authorizationToken = null,
        string? authorizationHeader = null,
        DateTimeOffset? expiresAtUtc = null)
    {
        HandleId = handleId;
        ProviderId = providerId;
        Last4 = pan is { Length: >= 4 } ? pan[^4..] : null;
        ExpiresAtUtc = expiresAtUtc;
        _pan = pan;
        _cvv = cvv;
        _expMonth = NormalizeMonth(expMonth);
        _expYear = NormalizeYear(expYear);
        _postalCode = postalCode;
        _authorizationToken = authorizationToken;
        _authorizationHeader = authorizationHeader;
    }

    public string HandleId { get; }
    public string ProviderId { get; }
    public string? Last4 { get; }
    public DateTimeOffset? ExpiresAtUtc { get; }

    public string? Resolve(PaymentSecretField field)
        => field switch
        {
            PaymentSecretField.Pan => _pan,
            PaymentSecretField.Cvv => _cvv,
            PaymentSecretField.ExpMonth => _expMonth,
            PaymentSecretField.ExpYear => _expYear,
            PaymentSecretField.ExpMonthYearShort when _expMonth is not null && _expYear is { Length: >= 2 } => $"{_expMonth}/{_expYear[^2..]}",
            PaymentSecretField.ExpMonthYearLong when _expMonth is not null && _expYear is not null => $"{_expMonth}/{_expYear}",
            PaymentSecretField.PostalCode => _postalCode,
            PaymentSecretField.AuthorizationToken => _authorizationToken,
            PaymentSecretField.AuthorizationHeader => _authorizationHeader,
            _ => null
        };

    public void Clear()
    {
        _pan = null;
        _cvv = null;
        _expMonth = null;
        _expYear = null;
        _postalCode = null;
        _authorizationToken = null;
        _authorizationHeader = null;
    }

    private static string? NormalizeMonth(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return int.TryParse(value, out var month) ? month.ToString("D2") : value;
    }

    private static string? NormalizeYear(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return value.Length == 2 ? $"20{value}" : value;
    }
}

public sealed class PaymentSecretJsonConverter : JsonConverter<PaymentSecret>
{
    public override PaymentSecret Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new JsonException("PaymentSecret cannot be deserialized.");

    public override void Write(Utf8JsonWriter writer, PaymentSecret value, JsonSerializerOptions options)
        => throw new JsonException("PaymentSecret cannot be serialized.");
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(PaymentSetupRequirement))]
[JsonSerializable(typeof(List<PaymentSetupRequirement>))]
[JsonSerializable(typeof(PaymentSetupStatus))]
[JsonSerializable(typeof(FundingSource))]
[JsonSerializable(typeof(List<FundingSource>))]
[JsonSerializable(typeof(BuyerProfile))]
[JsonSerializable(typeof(VirtualCardRequest))]
[JsonSerializable(typeof(VirtualCardHandle))]
[JsonSerializable(typeof(VirtualCardIssueResult))]
[JsonSerializable(typeof(MachinePaymentChallenge))]
[JsonSerializable(typeof(MachinePaymentRequest))]
[JsonSerializable(typeof(MachinePaymentResult))]
[JsonSerializable(typeof(MachinePaymentProviderResult))]
[JsonSerializable(typeof(PaymentStatus))]
[JsonSerializable(typeof(PaymentPolicyDecision))]
[JsonSerializable(typeof(ApprovalRequest))]
[JsonSerializable(typeof(ApprovalResult))]
[JsonSerializable(typeof(PaymentExecutionContext))]
[JsonSerializable(typeof(PaymentAuditEvent))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public partial class PaymentJsonContext : JsonSerializerContext
{
}

using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Payments.Abstractions;
using OpenClaw.Payments.Core;

namespace OpenClaw.Plugins.Payment;

public sealed class PaymentTool : IToolWithContext
{
    private readonly PaymentRuntimeService _runtime;
    private readonly string _defaultProviderId;
    private readonly string _environment;

    public PaymentTool(PaymentRuntimeService runtime, string defaultProviderId = "mock", string environment = PaymentEnvironments.Test)
    {
        _runtime = runtime;
        _defaultProviderId = string.IsNullOrWhiteSpace(defaultProviderId) ? "mock" : defaultProviderId;
        _environment = string.IsNullOrWhiteSpace(environment) ? PaymentEnvironments.Test : environment;
    }

    public string Name => "payment";

    public string Description =>
        "Native OpenClaw payment capability for setup status, funding source listing, virtual card handles, machine payments, and payment status. Results never include raw card or authorization secrets.";

    public string ParameterSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["setup_status", "list_funding_sources", "issue_virtual_card", "execute_machine_payment", "get_payment_status"]
            },
            "provider": { "type": "string" },
            "environment": { "type": "string", "enum": ["test", "live"] },
            "funding_source_id": { "type": "string" },
            "merchant": { "type": "string" },
            "merchant_url": { "type": "string" },
            "amount_minor": { "type": "integer" },
            "currency": { "type": "string" },
            "purpose": { "type": "string" },
            "valid_minutes": { "type": "integer" },
            "payment_id": { "type": "string" },
            "resource_url": { "type": "string" },
            "challenge_id": { "type": "string" },
            "protocol": { "type": "string" }
          },
          "required": ["action"]
        }
        """;

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => ExecuteCoreAsync(argumentsJson, null, ct);

    public ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
        => ExecuteCoreAsync(argumentsJson, context, ct);

    private async ValueTask<string> ExecuteCoreAsync(string argumentsJson, ToolExecutionContext? context, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(argumentsJson);
        var root = doc.RootElement;
        var action = ReadString(root, "action");
        if (string.IsNullOrWhiteSpace(action))
            return Error("action is required.");

        var provider = ReadString(root, "provider") ?? _defaultProviderId;
        var executionContext = new PaymentExecutionContext
        {
            SessionId = context?.Session.Id,
            ChannelId = context?.Session.ChannelId,
            SenderId = context?.Session.SenderId,
            CorrelationId = context?.TurnContext.CorrelationId,
            Environment = ReadString(root, "environment") ?? _environment
        };

        try
        {
            return action switch
            {
                PaymentActions.SetupStatus => Serialize(await _runtime.GetSetupStatusAsync(provider, ct), PaymentJsonContext.Default.PaymentSetupStatus),
                PaymentActions.ListFundingSources => Serialize(new List<FundingSource>(await _runtime.ListFundingSourcesAsync(provider, executionContext, ct)), PaymentJsonContext.Default.ListFundingSource),
                PaymentActions.IssueVirtualCard => Serialize(await _runtime.IssueVirtualCardAsync(BuildVirtualCardRequest(root, provider, executionContext.Environment), executionContext, ct), PaymentJsonContext.Default.VirtualCardHandle),
                PaymentActions.ExecuteMachinePayment => Serialize(await _runtime.ExecuteMachinePaymentAsync(BuildMachinePaymentRequest(root, provider, executionContext.Environment), executionContext, ct), PaymentJsonContext.Default.MachinePaymentResult),
                PaymentActions.GetPaymentStatus => Serialize(await _runtime.GetPaymentStatusAsync(ReadRequiredString(root, "payment_id"), provider, executionContext, ct), PaymentJsonContext.Default.PaymentStatus),
                _ => Error($"Unsupported payment action '{action}'.")
            };
        }
        catch (PaymentPolicyDeniedException ex)
        {
            return Error(ex.Message, "payment_policy_denied");
        }
        catch (Exception ex)
        {
            return Error(ex.Message, "payment_error");
        }
    }

    private static VirtualCardRequest BuildVirtualCardRequest(JsonElement root, string provider, string environment)
        => new()
        {
            ProviderId = provider,
            FundingSourceId = ReadString(root, "funding_source_id"),
            MerchantName = ReadRequiredString(root, "merchant"),
            MerchantUrl = ReadString(root, "merchant_url"),
            AmountMinor = ReadLong(root, "amount_minor") ?? 0,
            Currency = ReadString(root, "currency") ?? "USD",
            Purpose = ReadString(root, "purpose"),
            ValidUntilUtc = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp((int)(ReadLong(root, "valid_minutes") ?? 30), 1, 1440)),
            Environment = environment
        };

    private static MachinePaymentRequest BuildMachinePaymentRequest(JsonElement root, string provider, string environment)
    {
        var merchant = ReadString(root, "merchant");
        return new MachinePaymentRequest
        {
            ProviderId = provider,
            Environment = environment,
            FundingSourceId = ReadString(root, "funding_source_id"),
            Challenge = new MachinePaymentChallenge
            {
                ChallengeId = ReadString(root, "challenge_id"),
                Protocol = ReadString(root, "protocol") ?? "http-402",
                ResourceUrl = ReadString(root, "resource_url"),
                MerchantName = merchant,
                AmountMinor = ReadLong(root, "amount_minor") ?? 0,
                Currency = ReadString(root, "currency") ?? "USD",
                ProviderId = provider
            }
        };
    }

    private static string Serialize<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        => JsonSerializer.Serialize(value, typeInfo);

    private static string Error(string message, string code = "invalid_request")
        => JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["status"] = "error",
            ["code"] = code,
            ["message"] = message
        }, PaymentJsonContext.Default.DictionaryStringString);

    private static string ReadRequiredString(JsonElement element, string property)
        => ReadString(element, property) ?? throw new InvalidOperationException($"{property} is required.");

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ReadLong(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed))
            return parsed;
        return null;
    }
}

using System.Text.Json;
using System.Text.RegularExpressions;
using OpenClaw.Core.Security;
using OpenClaw.Payments.Abstractions;

namespace OpenClaw.Payments.Core;

public sealed class PaymentSentinelSubstitutionService : ISentinelSubstitutionService
{
    private static readonly Regex PaymentSentinelRegex = new(
        @"\{\{payment\.vcard:([A-Za-z0-9_.:-]+):(pan|cvv|exp_month|exp_year|exp_mm_yy|exp_mm_yyyy|postal_code)\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly PaymentRuntimeService _runtime;

    public PaymentSentinelSubstitutionService(PaymentRuntimeService runtime)
    {
        _runtime = runtime;
    }

    public async ValueTask<SentinelSubstitutionResult> SubstituteAsync(SentinelSubstitutionContext context, CancellationToken ct)
    {
        if (!string.Equals(context.ToolName, "browser", StringComparison.Ordinal) ||
            !PaymentSentinelRegex.IsMatch(context.ArgumentsJson))
        {
            return new SentinelSubstitutionResult
            {
                ExecutionArgumentsJson = context.ArgumentsJson,
                PersistedArgumentsJson = context.ArgumentsJson,
                Substituted = false
            };
        }

        using var doc = JsonDocument.Parse(context.ArgumentsJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("action", out var actionEl) ||
            !string.Equals(actionEl.GetString(), "fill", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Payment sentinels may only be resolved inside browser fill execution.");
        }

        if (!root.TryGetProperty("value", out var valueEl) || valueEl.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Payment sentinel fill requires a string value.");
        }

        var value = valueEl.GetString() ?? "";
        var matches = PaymentSentinelRegex.Matches(value);
        if (matches.Count == 0)
        {
            return new SentinelSubstitutionResult
            {
                ExecutionArgumentsJson = context.ArgumentsJson,
                PersistedArgumentsJson = context.ArgumentsJson,
                Substituted = false
            };
        }

        var executionValue = value;
        foreach (Match match in matches)
        {
            var handleId = match.Groups[1].Value;
            var field = ParseField(match.Groups[2].Value);
            var approval = new ApprovalRequest
            {
                Action = PaymentActions.BrowserSentinelFill,
                Summary = $"Fill browser checkout field using payment handle {handleId}.",
                ProviderId = "payment",
                Environment = PaymentEnvironments.Live,
                SessionId = context.SessionId,
                ChannelId = context.ChannelId,
                SenderId = context.SenderId,
                Severity = PaymentApprovalSeverities.Critical
            };
            var raw = await _runtime.ResolveSecretFieldForApprovedBoundaryAsync(handleId, field, approval, ct);
            executionValue = executionValue.Replace(match.Value, raw, StringComparison.Ordinal);
        }

        var executionJson = ReplaceRootStringProperty(root, "value", executionValue);
        return new SentinelSubstitutionResult
        {
            ExecutionArgumentsJson = executionJson,
            PersistedArgumentsJson = context.ArgumentsJson,
            Substituted = true
        };
    }

    private static PaymentSecretField ParseField(string field)
        => field switch
        {
            "pan" => PaymentSecretField.Pan,
            "cvv" => PaymentSecretField.Cvv,
            "exp_month" => PaymentSecretField.ExpMonth,
            "exp_year" => PaymentSecretField.ExpYear,
            "exp_mm_yy" => PaymentSecretField.ExpMonthYearShort,
            "exp_mm_yyyy" => PaymentSecretField.ExpMonthYearLong,
            "postal_code" => PaymentSecretField.PostalCode,
            _ => throw new InvalidOperationException($"Unsupported payment sentinel field '{field}'.")
        };

    private static string ReplaceRootStringProperty(JsonElement root, string propertyName, string value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.Ordinal))
                    writer.WriteString(property.Name, value);
                else
                    property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}

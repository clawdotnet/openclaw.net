using System.Globalization;

namespace OpenClaw.Agent.Actions;

internal sealed class ActionApprovalRecord
{
    public string? Approver { get; init; }
    public string? DecisionAt { get; init; }
    public string? DecisionReason { get; init; }
    public string? TicketRef { get; init; }
    public string? DecisionType { get; init; }
}

internal static class ActionApprovalRecordValidator
{
    internal static bool TryValidate(ActionApprovalRecord record, out string errorCode)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (string.IsNullOrWhiteSpace(record.Approver)
            || string.IsNullOrWhiteSpace(record.DecisionAt)
            || string.IsNullOrWhiteSpace(record.DecisionReason)
            || string.IsNullOrWhiteSpace(record.TicketRef))
        {
            errorCode = "approval_denied";
            return false;
        }

        if (!IsValidUtcIso8601(record.DecisionAt))
        {
            errorCode = "approval_denied";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(record.DecisionType)
            && !record.DecisionType.Equals("approve", StringComparison.OrdinalIgnoreCase)
            && !record.DecisionType.Equals("reject", StringComparison.OrdinalIgnoreCase))
        {
            errorCode = "approval_denied";
            return false;
        }

        errorCode = string.Empty;
        return true;
    }

    private static bool IsValidUtcIso8601(string decisionAt)
    {
        if (!DateTimeOffset.TryParse(
                decisionAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var timestamp))
        {
            return false;
        }

        if (timestamp.Offset != TimeSpan.Zero)
            return false;

        return decisionAt.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
               || decisionAt.EndsWith("+00:00", StringComparison.Ordinal)
               || decisionAt.EndsWith("-00:00", StringComparison.Ordinal);
    }
}

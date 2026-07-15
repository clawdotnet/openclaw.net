using System.Globalization;

namespace OpenClaw.Core.Models;

internal static class ConnectorActionContractCatalog
{
    internal const string Proceed = "proceed";
    internal const string RequireApproval = "require_approval";
    internal const string Reject = "reject";
    internal const string Escalate = "escalate";

    internal static readonly string[] SupportedDecisions =
    [
        Proceed,
        RequireApproval,
        Reject,
        Escalate
    ];
}

public sealed class ConnectorActionExecuteRequest
{
    public required ActionProposal Proposal { get; init; }
    public required string Decision { get; init; }
    public string? RiskLevel { get; init; }
    public ConnectorApprovalPayload? Approval { get; init; }
}

public sealed class ConnectorActionExecuteResponse
{
    public bool Success { get; init; }
    public string? Decision { get; init; }
    public string? RiskLevel { get; init; }
    public ConnectorApprovalPayload? Approval { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class ConnectorApprovalPayload
{
    public string? Approver { get; init; }
    public string? DecisionAt { get; init; }
    public string? DecisionReason { get; init; }
    public string? TicketRef { get; init; }
    public string? DecisionType { get; init; }
}

public sealed class IntegrationConnectorActionExecuteRequest
{
    public required ActionProposal Proposal { get; init; }
    public required string Decision { get; init; }
    public string? RiskLevel { get; init; }
    public ConnectorApprovalPayload? Approval { get; init; }
}

public sealed class IntegrationConnectorActionExecuteResponse
{
    public bool Success { get; init; }
    public string? Decision { get; init; }
    public string? RiskLevel { get; init; }
    public ConnectorApprovalPayload? Approval { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public static class ConnectorActionContractValidator
{
    public static (bool Success, string? ErrorCode, string? ErrorMessage) ValidateForExecution(ConnectorActionExecuteRequest request)
    {
        if (request is null)
            return (false, "invalid_request", "Request is required.");

        if (string.IsNullOrWhiteSpace(request.Decision))
            return (false, "invalid_request", "Decision is required.");

        if (!IsSupportedDecision(request.Decision))
            return (false, "unsupported_decision", $"Unsupported decision '{request.Decision}'.");

        if (!request.Decision.Equals(ConnectorActionContractCatalog.RequireApproval, StringComparison.Ordinal))
            return (true, null, null);

        var approval = request.Approval;
        if (approval is null
            || string.IsNullOrWhiteSpace(approval.Approver)
            || string.IsNullOrWhiteSpace(approval.DecisionAt)
            || string.IsNullOrWhiteSpace(approval.DecisionReason)
            || string.IsNullOrWhiteSpace(approval.TicketRef))
        {
            return (false, "approval_denied", "Approval payload is incomplete.");
        }

        if (!IsUtcIso8601(approval.DecisionAt))
            return (false, "approval_denied", "Approval decision time must be UTC ISO-8601.");

        return (true, null, null);
    }

    internal static IReadOnlyList<string> GetSupportedDecisions()
        => ConnectorActionContractCatalog.SupportedDecisions;

    private static bool IsSupportedDecision(string decision)
        => Array.IndexOf(ConnectorActionContractCatalog.SupportedDecisions, decision) >= 0;

    private static bool IsUtcIso8601(string value)
    {
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            return false;

        return parsed.Offset == TimeSpan.Zero
               && (value.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
                   || value.EndsWith("+00:00", StringComparison.Ordinal)
                   || value.EndsWith("-00:00", StringComparison.Ordinal));
    }
}

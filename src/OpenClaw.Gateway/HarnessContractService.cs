using System.Linq;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal sealed class HarnessContractService
{
    private readonly IHarnessContractStore _store;
    private readonly RuntimeEventStore _runtimeEvents;
    private readonly ILogger<HarnessContractService> _logger;

    public HarnessContractService(
        IHarnessContractStore store,
        RuntimeEventStore runtimeEvents,
        ILogger<HarnessContractService> logger)
    {
        _store = store;
        _runtimeEvents = runtimeEvents;
        _logger = logger;
    }

    public async ValueTask<HarnessContract> CreateAsync(HarnessContract contract, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contract);

        var now = DateTimeOffset.UtcNow;
        var normalized = Normalize(contract, now, isNew: true);
        await _store.SaveAsync(normalized, ct);
        AppendEvent(
            normalized,
            action: "contract_created",
            severity: "info",
            summary: $"Created harness contract '{normalized.Id}'.");
        return normalized;
    }

    public async ValueTask<HarnessContract> SaveAsync(HarnessContract contract, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contract);

        var normalized = Normalize(contract, DateTimeOffset.UtcNow, isNew: false);
        await _store.SaveAsync(normalized, ct);
        AppendEvent(
            normalized,
            action: "contract_updated",
            severity: "info",
            summary: $"Updated harness contract '{normalized.Id}'.");
        return normalized;
    }

    public ValueTask<HarnessContract?> GetAsync(string id, CancellationToken ct)
        => _store.GetAsync(id, ct);

    public ValueTask<IReadOnlyList<HarnessContract>> ListAsync(HarnessContractListQuery query, CancellationToken ct)
        => _store.ListAsync(query, ct);

    public async ValueTask<HarnessContract?> MarkStatusAsync(string id, string status, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(status))
            throw new ArgumentException("Harness contract status is required.", nameof(status));

        var existing = await _store.GetAsync(id, ct);
        if (existing is null)
            return null;

        var normalizedStatus = NormalizeStatus(status);
        var now = DateTimeOffset.UtcNow;
        var updated = Copy(
            existing,
            id: existing.Id,
            status: normalizedStatus,
            riskLevel: existing.RiskLevel,
            createdAtUtc: existing.CreatedAtUtc == default ? now : existing.CreatedAtUtc,
            updatedAtUtc: now,
            approvedAtUtc: string.Equals(normalizedStatus, HarnessContractStatus.Approved, StringComparison.Ordinal)
                ? now
                : existing.ApprovedAtUtc,
            completedAtUtc: IsTerminalStatus(normalizedStatus) ? now : existing.CompletedAtUtc);

        await _store.SaveAsync(updated, ct);
        AppendEvent(
            updated,
            action: "contract_status_changed",
            severity: StatusSeverity(normalizedStatus),
            summary: $"Harness contract '{updated.Id}' changed to '{updated.Status}'.");
        return updated;
    }

    public string DeriveRiskLevel(HarnessContract contract)
    {
        if (!string.IsNullOrWhiteSpace(contract.RiskLevel))
            return NormalizeRisk(contract.RiskLevel);

        var risk = HarnessContractRiskLevels.Low;
        if ((contract.WriteSet ?? []).Count > 0)
            risk = MaxRisk(risk, HarnessContractRiskLevels.Medium);

        foreach (var action in (contract.PlannedActions ?? []).Where(static action => action is not null))
        {
            if (!string.IsNullOrWhiteSpace(action.RiskLevel))
            {
                risk = MaxRisk(risk, action.RiskLevel);
                continue;
            }

            var actionWriteSet = action.WriteSet ?? [];
            if (actionWriteSet.Count > 0 || action.RequiresApproval)
                risk = MaxRisk(risk, HarnessContractRiskLevels.Medium);

            var tool = action.ToolName ?? "";
            var actionType = action.ActionType ?? "";
            if (IsHighRiskToolOrAction(tool, actionType, actionWriteSet))
                risk = MaxRisk(risk, HarnessContractRiskLevels.High);
        }

        if ((contract.ToolsRequired ?? []).Any(static tool => tool is not null && IsHighRiskToolOrAction(tool.ToolName, "", [])))
            risk = MaxRisk(risk, HarnessContractRiskLevels.High);

        return risk;
    }

    private HarnessContract Normalize(HarnessContract contract, DateTimeOffset now, bool isNew)
    {
        var id = string.IsNullOrWhiteSpace(contract.Id)
            ? $"hctr_{Guid.NewGuid():N}"[..24]
            : contract.Id.Trim();

        var createdAt = contract.CreatedAtUtc == default || isNew ? now : contract.CreatedAtUtc;
        var status = string.IsNullOrWhiteSpace(contract.Status) ? HarnessContractStatus.Draft : NormalizeStatus(contract.Status);
        return Copy(
            contract,
            id,
            status,
            DeriveRiskLevel(contract),
            createdAt,
            updatedAtUtc: now,
            approvedAtUtc: contract.ApprovedAtUtc,
            completedAtUtc: contract.CompletedAtUtc);
    }

    private static HarnessContract Copy(
        HarnessContract source,
        string id,
        string status,
        string? riskLevel,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset? approvedAtUtc,
        DateTimeOffset? completedAtUtc)
        => new()
        {
            Id = id,
            Status = status,
            Goal = source.Goal,
            UserRequestSummary = source.UserRequestSummary,
            SourceSessionId = source.SourceSessionId,
            ActorId = source.ActorId,
            ChannelId = source.ChannelId,
            SenderId = source.SenderId,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = updatedAtUtc,
            ApprovedAtUtc = approvedAtUtc,
            CompletedAtUtc = completedAtUtc,
            RiskLevel = riskLevel,
            ApprovalRequired = source.ApprovalRequired,
            ApprovalReason = source.ApprovalReason,
            PlannedActions = NormalizeActions(source.PlannedActions),
            ReadSet = CleanList(source.ReadSet),
            WriteSet = CleanList(source.WriteSet),
            ToolsRequired = CleanList(source.ToolsRequired),
            Assumptions = CleanList(source.Assumptions),
            Constraints = CleanList(source.Constraints),
            VerificationPlan = CleanList(source.VerificationPlan),
            RollbackPlan = CleanList(source.RollbackPlan),
            SuccessCriteria = CleanStrings(source.SuccessCriteria),
            Tags = CleanStrings(source.Tags),
            Metadata = source.Metadata
        };

    private static IReadOnlyList<HarnessContractAction> NormalizeActions(IReadOnlyList<HarnessContractAction>? actions)
    {
        if (actions is null || actions.Count == 0)
            return [];

        var normalized = new List<HarnessContractAction>(actions.Count);
        for (var i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            if (action is null)
                continue;

            normalized.Add(new HarnessContractAction
            {
                Id = string.IsNullOrWhiteSpace(action.Id) ? $"action_{i + 1}" : action.Id.Trim(),
                Title = action.Title,
                Description = action.Description,
                ToolName = action.ToolName,
                ActionType = action.ActionType,
                RiskLevel = string.IsNullOrWhiteSpace(action.RiskLevel) ? null : NormalizeRisk(action.RiskLevel),
                RequiresApproval = action.RequiresApproval,
                ReadSet = CleanList(action.ReadSet),
                WriteSet = CleanList(action.WriteSet),
                ExpectedOutcome = action.ExpectedOutcome,
                Status = action.Status
            });
        }

        return normalized;
    }

    private static IReadOnlyList<T> CleanList<T>(IReadOnlyList<T>? items)
        where T : class
        => items?.Where(static item => item is not null).ToArray() ?? [];

    private static IReadOnlyList<string> CleanStrings(IReadOnlyList<string>? items)
        => items?
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item.Trim())
            .ToArray() ?? [];

    private void AppendEvent(HarnessContract contract, string action, string severity, string summary)
    {
        try
        {
            _runtimeEvents.Append(new RuntimeEventEntry
            {
                Id = $"evt_{Guid.NewGuid():N}"[..20],
                SessionId = contract.SourceSessionId,
                ChannelId = contract.ChannelId,
                SenderId = contract.SenderId,
                CorrelationId = contract.Id,
                Component = "harness",
                Action = action,
                Severity = severity,
                Summary = summary,
                Metadata = new Dictionary<string, string>
                {
                    ["contractId"] = contract.Id,
                    ["status"] = contract.Status,
                    ["riskLevel"] = contract.RiskLevel ?? HarnessContractRiskLevels.Medium
                }
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogWarning(ex, "Failed to append harness contract runtime event for {ContractId}.", contract.Id);
        }
    }

    private static bool IsHighRiskToolOrAction(
        string toolName,
        string actionType,
        IReadOnlyList<HarnessContractResourceRef> writeSet)
    {
        var text = $"{toolName} {actionType}".ToLowerInvariant();
        if (text.Contains("shell", StringComparison.Ordinal) ||
            text.Contains("process", StringComparison.Ordinal) ||
            text.Contains("external", StringComparison.Ordinal) ||
            text.Contains("deploy", StringComparison.Ordinal) ||
            text.Contains("security", StringComparison.Ordinal) ||
            text.Contains("public", StringComparison.Ordinal))
            return true;

        if (actionType.Contains("write", StringComparison.OrdinalIgnoreCase) ||
            actionType.Contains("mutat", StringComparison.OrdinalIgnoreCase) ||
            actionType.Contains("delete", StringComparison.OrdinalIgnoreCase))
        {
            return writeSet.Count == 0 || writeSet.Any(static item =>
                string.Equals(item.Kind, HarnessContractResourceKinds.ExternalApi, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Kind, HarnessContractResourceKinds.Database, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Kind, HarnessContractResourceKinds.Endpoint, StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    private static string MaxRisk(string left, string right)
        => RiskRank(right) > RiskRank(left) ? NormalizeRisk(right) : NormalizeRisk(left);

    private static int RiskRank(string risk)
        => NormalizeRisk(risk) switch
        {
            HarnessContractRiskLevels.Critical => 4,
            HarnessContractRiskLevels.High => 3,
            HarnessContractRiskLevels.Medium => 2,
            _ => 1
        };

    private static string NormalizeRisk(string risk)
        => risk.Trim().ToLowerInvariant() switch
        {
            HarnessContractRiskLevels.Critical => HarnessContractRiskLevels.Critical,
            HarnessContractRiskLevels.High => HarnessContractRiskLevels.High,
            HarnessContractRiskLevels.Medium => HarnessContractRiskLevels.Medium,
            HarnessContractRiskLevels.Low => HarnessContractRiskLevels.Low,
            _ => throw new ArgumentException($"Unsupported harness contract risk level '{risk}'.", nameof(risk))
        };

    private static string NormalizeStatus(string status)
        => status.Trim().ToLowerInvariant() switch
        {
            HarnessContractStatus.Draft => HarnessContractStatus.Draft,
            HarnessContractStatus.Proposed => HarnessContractStatus.Proposed,
            HarnessContractStatus.Approved => HarnessContractStatus.Approved,
            HarnessContractStatus.Executing => HarnessContractStatus.Executing,
            HarnessContractStatus.Verified => HarnessContractStatus.Verified,
            HarnessContractStatus.Failed => HarnessContractStatus.Failed,
            HarnessContractStatus.Rejected => HarnessContractStatus.Rejected,
            HarnessContractStatus.RolledBack => HarnessContractStatus.RolledBack,
            HarnessContractStatus.Cancelled => HarnessContractStatus.Cancelled,
            _ => throw new ArgumentException($"Unsupported harness contract status '{status}'.", nameof(status))
        };

    private static bool IsTerminalStatus(string status)
        => status.Trim().ToLowerInvariant() is HarnessContractStatus.Verified
            or HarnessContractStatus.Failed
            or HarnessContractStatus.Rejected
            or HarnessContractStatus.RolledBack
            or HarnessContractStatus.Cancelled;

    private static string StatusSeverity(string status)
        => status.Trim().ToLowerInvariant() is HarnessContractStatus.Failed
            or HarnessContractStatus.Rejected
            or HarnessContractStatus.RolledBack
            or HarnessContractStatus.Cancelled
            ? "warning"
            : "info";
}

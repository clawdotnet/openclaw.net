namespace OpenClaw.Core.Models;

using System.Text.Json;

public sealed class ActionProposal
{
    public required string ActionName { get; init; }
    public required ActionProposalSource Source { get; init; }
    public required ActionProposalTrigger Trigger { get; init; }
    public required ActionProposalTarget Target { get; init; }
    public IReadOnlyList<ActionCall> PreChecks { get; init; } = [];
    public required IReadOnlyList<ActionCall> Execution { get; init; }
    public IReadOnlyList<ActionCall> Rollback { get; init; } = [];
    public required string IdempotencyKey { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed class ActionProposalSource
{
    public required string MetaSkill { get; init; }
    public required string RunId { get; init; }
    public required string StepId { get; init; }
}

public sealed class ActionProposalTrigger
{
    public required string Condition { get; init; }
    public IReadOnlyList<string> EvidenceRefs { get; init; } = [];
}

public sealed class ActionProposalTarget
{
    public required string System { get; init; }
    public required string Operation { get; init; }
}

public sealed class ActionCall
{
    public required string Call { get; init; }
    public IReadOnlyDictionary<string, JsonElement> Args { get; init; } = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
}

public sealed class ActionProposalNormalizationResult
{
    public bool Success { get; init; }
    public ActionProposal? Proposal { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public static class ActionProposalValidation
{
    public static bool TryValidate(ActionProposal proposal, out string errorCode)
    {
        if (proposal.Execution.Count == 0)
        {
            errorCode = "invalid_proposal";
            return false;
        }

        errorCode = string.Empty;
        return true;
    }
}

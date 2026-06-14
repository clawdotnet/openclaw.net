namespace OpenClaw.Core.Models;

public static class MetaRunProposalPolicy
{
    public static bool CanMutate(string? operatorId)
        => !string.IsNullOrWhiteSpace(operatorId);

    public static bool IsAllowedTransition(string fromStatus, string toStatus)
        => (fromStatus, toStatus) switch
        {
            (LearningProposalStatus.Pending, LearningProposalStatus.Approved) => true,
            (LearningProposalStatus.Pending, LearningProposalStatus.Rejected) => true,
            (LearningProposalStatus.Approved, LearningProposalStatus.RolledBack) => true,
            (LearningProposalStatus.Rejected, LearningProposalStatus.RolledBack) => true,
            (LearningProposalStatus.RolledBack, LearningProposalStatus.Approved) => true,
            (LearningProposalStatus.RolledBack, LearningProposalStatus.Rejected) => true,
            _ => false
        };

    public static bool IsAllowedActionTransition(string action, string fromStatus, string toStatus)
    {
        if (!IsAllowedTransition(fromStatus, toStatus))
            return false;

        return action switch
        {
            MetaRunProposalActions.Accept => string.Equals(fromStatus, LearningProposalStatus.Pending, StringComparison.OrdinalIgnoreCase),
            MetaRunProposalActions.Dismiss => string.Equals(fromStatus, LearningProposalStatus.Pending, StringComparison.OrdinalIgnoreCase),
            MetaRunProposalActions.Rollback => string.Equals(toStatus, LearningProposalStatus.RolledBack, StringComparison.OrdinalIgnoreCase),
            MetaRunProposalActions.Change => string.Equals(fromStatus, LearningProposalStatus.RolledBack, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }
}

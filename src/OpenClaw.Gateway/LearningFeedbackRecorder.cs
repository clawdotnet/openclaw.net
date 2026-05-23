using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal sealed class LearningFeedbackRecorder(ILearningProposalStore proposalStore)
{
    public async ValueTask<LearningProposal?> RecordAsync(
        string proposalId,
        string action,
        IReadOnlyList<string> changedFields,
        int? beforeQualityScore,
        int? afterQualityScore,
        string summary,
        CancellationToken ct)
    {
        var proposal = await proposalStore.GetProposalAsync(proposalId, ct);
        if (proposal is null)
            return null;

        var feedbackEvent = new LearningProposalFeedbackEvent
        {
            Action = action,
            ChangedFields = changedFields.ToArray(),
            BeforeQualityScore = beforeQualityScore,
            AfterQualityScore = afterQualityScore,
            Summary = summary,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        var updated = CopyWithFeedback(proposal, proposal.FeedbackEvents.Append(feedbackEvent).ToArray());
        await proposalStore.SaveProposalAsync(updated, ct);
        return updated;
    }

    public static LearningProposal CopyWithFeedback(LearningProposal proposal, IReadOnlyList<LearningProposalFeedbackEvent> feedbackEvents)
        => new()
        {
            Id = proposal.Id,
            Kind = proposal.Kind,
            Status = proposal.Status,
            ActorId = proposal.ActorId,
            Title = proposal.Title,
            Summary = proposal.Summary,
            SkillName = proposal.SkillName,
            DraftContent = proposal.DraftContent,
            DraftContentHash = proposal.DraftContentHash,
            DraftPreview = proposal.DraftPreview,
            ProfileUpdate = proposal.ProfileUpdate,
            AppliedProfileBefore = proposal.AppliedProfileBefore,
            AutomationDraft = proposal.AutomationDraft,
            AutomationIntent = proposal.AutomationIntent,
            AutomationQuality = proposal.AutomationQuality,
            AutomationSuggestionPreview = proposal.AutomationSuggestionPreview,
            AppliedAutomationId = proposal.AppliedAutomationId,
            ManagedSkillPath = proposal.ManagedSkillPath,
            ManagedSkillMetadata = proposal.ManagedSkillMetadata,
            SourceSessionIds = proposal.SourceSessionIds,
            SourceTurnIds = proposal.SourceTurnIds,
            ToolNames = proposal.ToolNames,
            ToolSequence = proposal.ToolSequence,
            ToolObservations = proposal.ToolObservations,
            FeedbackEvents = feedbackEvents,
            RepeatedCount = proposal.RepeatedCount,
            ProposalFingerprint = proposal.ProposalFingerprint,
            RiskLevel = proposal.RiskLevel,
            Confidence = proposal.Confidence,
            CreatedReason = proposal.CreatedReason,
            ValidationStatus = proposal.ValidationStatus,
            ValidationWarnings = proposal.ValidationWarnings,
            ValidationErrors = proposal.ValidationErrors,
            CreatedAtUtc = proposal.CreatedAtUtc,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            ReviewedAtUtc = proposal.ReviewedAtUtc,
            ReviewNotes = proposal.ReviewNotes,
            RolledBack = proposal.RolledBack,
            RolledBackAtUtc = proposal.RolledBackAtUtc,
            RollbackReason = proposal.RollbackReason
        };
}

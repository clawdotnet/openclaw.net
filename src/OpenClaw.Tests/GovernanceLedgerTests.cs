using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Features;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Security;
using OpenClaw.Gateway;
using Xunit;

namespace OpenClaw.Tests;

public sealed class GovernanceLedgerTests
{
    [Fact]
    public void GovernanceLedgerEntry_RoundTrips_WithSourceGeneratedJson()
    {
        var original = BuildEntry("gov_roundtrip", GovernanceDecisions.Approved, "shell", "sess_roundtrip");

        var json = JsonSerializer.Serialize(original, CoreJsonContext.Default.GovernanceLedgerEntry);
        var restored = JsonSerializer.Deserialize(json, CoreJsonContext.Default.GovernanceLedgerEntry);

        Assert.NotNull(restored);
        Assert.Equal(original.Id, restored!.Id);
        Assert.Equal(GovernanceDecisions.Approved, restored.Decision);
        Assert.Equal(GovernanceDecisionStatuses.Active, restored.Status);
        Assert.Equal(GovernanceScopes.Session, restored.Scope);
        Assert.Equal("hctr_1", restored.HarnessContractId);
        Assert.Equal("evb_1", restored.EvidenceBundleId);
        Assert.Equal("review before allowing future reuse", restored.PolicyHint?.Notes);
    }

    [Fact]
    public async Task FileGovernanceLedgerStore_SavesLoadsAndFilters()
    {
        var root = CreateTempDir();
        var store = new FileGovernanceLedgerStore(root);
        await store.SaveAsync(BuildEntry("gov_approved", GovernanceDecisions.Approved, "shell", "sess_one"), CancellationToken.None);
        await store.SaveAsync(BuildEntry("gov_rejected", GovernanceDecisions.Rejected, "file_write", "sess_two"), CancellationToken.None);

        var loaded = await store.GetAsync("gov_approved", CancellationToken.None);
        var byDecision = await store.ListAsync(new GovernanceLedgerListQuery { Decision = GovernanceDecisions.Rejected }, CancellationToken.None);
        var byTool = await store.ListAsync(new GovernanceLedgerListQuery { ToolName = "shell" }, CancellationToken.None);
        var bySession = await store.ListAsync(new GovernanceLedgerListQuery { SessionId = "sess_two" }, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("gov_approved", loaded!.Id);
        Assert.Single(byDecision);
        Assert.Equal("gov_rejected", byDecision[0].Id);
        Assert.Single(byTool);
        Assert.Equal("gov_approved", byTool[0].Id);
        Assert.Single(bySession);
        Assert.Equal("gov_rejected", bySession[0].Id);
    }

    [Fact]
    public async Task FileGovernanceLedgerStore_RejectsUnsafeIds()
    {
        var store = new FileGovernanceLedgerStore(CreateTempDir());
        var unsafeEntry = BuildEntry("../escape", GovernanceDecisions.Approved, "shell", "sess");

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.SaveAsync(unsafeEntry, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.GetAsync("../escape", CancellationToken.None));
    }

    [Fact]
    public async Task FileGovernanceLedgerStore_RevokeMarksEntryWithoutDeleting()
    {
        var store = new FileGovernanceLedgerStore(CreateTempDir());
        await store.SaveAsync(BuildEntry("gov_revoke", GovernanceDecisions.Approved, "shell", "sess"), CancellationToken.None);

        var revoked = await store.RevokeAsync("gov_revoke", "operator", "scope changed", CancellationToken.None);
        var loaded = await store.GetAsync("gov_revoke", CancellationToken.None);

        Assert.NotNull(revoked);
        Assert.Equal(GovernanceDecisionStatuses.Revoked, revoked!.Status);
        Assert.Equal(GovernanceDecisions.Approved, revoked.Decision);
        Assert.Equal("operator", revoked.RevokedBy);
        Assert.Equal("scope changed", revoked.RevocationReason);
        Assert.NotNull(loaded);
    }

    [Fact]
    public async Task FileGovernanceLedgerStore_RevokeRejectsBlankActorOrReason()
    {
        var store = new FileGovernanceLedgerStore(CreateTempDir());
        await store.SaveAsync(BuildEntry("gov_revoke_blank", GovernanceDecisions.Approved, "shell", "sess"), CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.RevokeAsync("gov_revoke_blank", "", "scope changed", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.RevokeAsync("gov_revoke_blank", "operator", " ", CancellationToken.None));
    }

    [Fact]
    public async Task GovernanceLedgerService_RecordsDecisionsAndRuntimeEvents()
    {
        var root = CreateTempDir();
        var service = CreateService(root);
        var approval = BuildApprovalRequest("apr_approved", "shell", isMutation: true);
        var rejection = BuildApprovalRequest("apr_rejected", "file_write", isMutation: true);
        var expired = BuildApprovalRequest("apr_expired", "web_fetch", isMutation: false);
        var grant = BuildApprovalRequest("grant_1", "file_read", isMutation: false);

        var approved = await service.RecordApprovalAsync(approval, GovernanceLedgerSources.ToolApproval, "operator", "web", "operator", CancellationToken.None);
        var rejected = await service.RecordRejectionAsync(rejection, GovernanceLedgerSources.ToolApproval, "operator", "web", "operator", CancellationToken.None);
        var timedOut = await service.RecordExpiredAsync(expired, GovernanceLedgerSources.ApprovalTimeout, "timeout", CancellationToken.None);
        var grantConsumed = await service.RecordApprovalAsync(grant, GovernanceLedgerSources.ApprovalGrantConsumed, "grant-admin", null, "grant-admin", CancellationToken.None);

        Assert.Equal(GovernanceDecisions.Approved, approved.Decision);
        Assert.Equal(GovernanceRiskLevels.High, approved.RiskLevel);
        Assert.Equal(GovernanceDecisions.Rejected, rejected.Decision);
        Assert.Equal(GovernanceDecisions.Expired, timedOut.Decision);
        Assert.Equal(GovernanceDecisionStatuses.Expired, timedOut.Status);
        Assert.Equal(GovernanceLedgerSources.ApprovalGrantConsumed, grantConsumed.Source);
        Assert.DoesNotContain("sk-testsecret123", approved.RedactedArguments ?? "");

        var events = new RuntimeEventStore(root, NullLogger<RuntimeEventStore>.Instance)
            .Query(new RuntimeEventQuery { Component = "harness", Action = "governance_ledger_entry_recorded", Limit = 10 });
        Assert.Equal(4, events.Count);
    }

    [Fact]
    public async Task GovernanceLedgerService_RedactsFreeformFieldsAndGrantScope()
    {
        var root = CreateTempDir();
        var service = CreateService(root);
        var redacted = await service.CreateAsync(new GovernanceLedgerEntry
        {
            Id = "gov_redacted",
            Decision = GovernanceDecisions.Approved,
            Status = GovernanceDecisionStatuses.Active,
            Source = GovernanceLedgerSources.Manual,
            ActionSummary = "approved sk-testsecret123",
            DecisionReason = "reason sk-testsecret123",
            RevocationReason = "revoked sk-testsecret123",
            RiskLevel = GovernanceRiskLevels.Medium,
            Scope = GovernanceScopes.Session,
            Tags = ["tag-sk-testsecret123"],
            PolicyHint = new GovernancePolicyHint
            {
                SuggestedFutureBehavior = "reuse sk-testsecret123",
                SuggestedScope = GovernanceScopes.Session,
                Notes = "note sk-testsecret123"
            },
            Metadata = new GovernanceLedgerMetadata
            {
                CreatedBy = "operator sk-testsecret123",
                CorrelationId = "corr_redacted",
                Properties = new Dictionary<string, string> { ["secret"] = "value sk-testsecret123" }
            }
        }, CancellationToken.None);

        Assert.DoesNotContain("sk-testsecret123", redacted.ActionSummary);
        Assert.DoesNotContain("sk-testsecret123", redacted.DecisionReason ?? "");
        Assert.DoesNotContain("sk-testsecret123", redacted.RevocationReason ?? "");
        Assert.DoesNotContain("sk-testsecret123", redacted.Tags[0]);
        Assert.DoesNotContain("sk-testsecret123", redacted.PolicyHint?.SuggestedFutureBehavior ?? "");
        Assert.DoesNotContain("sk-testsecret123", redacted.PolicyHint?.Notes ?? "");
        Assert.DoesNotContain("sk-testsecret123", redacted.Metadata?.CreatedBy ?? "");
        Assert.DoesNotContain("sk-testsecret123", redacted.Metadata?.Properties["secret"] ?? "");

        await service.TryRecordApprovalGrantConsumedAsync(
            new ToolApprovalGrant
            {
                Id = "grant_scope",
                Scope = "session",
                SessionId = "sess_grant",
                ToolName = "file_read",
                GrantedBy = "operator",
                GrantSource = "test",
                RemainingUses = 2
            },
            BuildApprovalRequest("grant_scope", "file_read", isMutation: false) with
            {
                SessionId = "sess_grant"
            },
            CancellationToken.None);

        var grantEntries = await service.ListAsync(new GovernanceLedgerListQuery { SessionId = "sess_grant", Limit = 0 }, CancellationToken.None);
        var grantEntry = Assert.Single(grantEntries);
        Assert.Equal(GovernanceLedgerSources.ApprovalGrantConsumed, grantEntry.Source);
        Assert.Equal(GovernanceScopes.Session, grantEntry.Scope);
        Assert.Equal("sess_grant", grantEntry.ScopeKey);
    }

    [Theory]
    [InlineData("decision")]
    [InlineData("status")]
    [InlineData("scope")]
    [InlineData("risk")]
    [InlineData("source")]
    public async Task GovernanceLedgerService_ValidatesSupportedConstants(string invalidField)
    {
        var service = CreateService(CreateTempDir());
        var entry = invalidField switch
        {
            "decision" => BuildEntry("gov_invalid", "maybe", "shell", "sess"),
            "status" => BuildEntry("gov_invalid", GovernanceDecisions.Approved, "shell", "sess", status: "stale"),
            "scope" => BuildEntry("gov_invalid", GovernanceDecisions.Approved, "shell", "sess", scope: "workspace"),
            "risk" => BuildEntry("gov_invalid", GovernanceDecisions.Approved, "shell", "sess", riskLevel: "severe"),
            "source" => BuildEntry("gov_invalid", GovernanceDecisions.Approved, "shell", "sess", source: "robot"),
            _ => BuildEntry("gov_invalid", GovernanceDecisions.Approved, "shell", "sess")
        };

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.CreateAsync(entry, CancellationToken.None));
    }

    private static GovernanceLedgerService CreateService(string root)
        => new(
            new FileGovernanceLedgerStore(root),
            new RuntimeEventStore(root, NullLogger<RuntimeEventStore>.Instance),
            new RedactionPipeline([new BaselineSecretRedactor()]),
            NullLogger<GovernanceLedgerService>.Instance);

    private static ToolApprovalRequest BuildApprovalRequest(string approvalId, string toolName, bool isMutation)
        => new()
        {
            ApprovalId = approvalId,
            SessionId = "sess_governance",
            ChannelId = "web",
            SenderId = "operator",
            ToolName = toolName,
            Arguments = """{"cmd":"echo sk-testsecret123"}""",
            Action = isMutation ? "write" : "read",
            IsMutation = isMutation,
            Summary = "Review tool execution."
        };

    private static GovernanceLedgerEntry BuildEntry(
        string id,
        string decision,
        string toolName,
        string sessionId,
        string status = GovernanceDecisionStatuses.Active,
        string source = GovernanceLedgerSources.Manual,
        string riskLevel = GovernanceRiskLevels.Medium,
        string scope = GovernanceScopes.Session)
        => new()
        {
            Id = id,
            Decision = decision,
            Status = status,
            Source = source,
            ActionType = "write",
            ToolName = toolName,
            ActionSummary = "Operator reviewed a governed action.",
            ArgumentSummary = """{"secret":"sk-testsecret123"}""",
            RedactedArguments = """{"secret":"sk-testsecret123"}""",
            RiskLevel = riskLevel,
            Scope = scope,
            ScopeKey = sessionId,
            SessionId = sessionId,
            HarnessContractId = "hctr_1",
            EvidenceBundleId = "evb_1",
            LearningProposalId = "learn_1",
            ApprovalId = "apr_1",
            ActorId = "actor",
            ChannelId = "web",
            SenderId = "operator",
            DecidedBy = "operator",
            DecisionReason = "acceptable risk",
            Tags = ["approval", "governance"],
            PolicyHint = new GovernancePolicyHint
            {
                SuggestedFutureBehavior = "consider_reusable_grant",
                SuggestedScope = GovernanceScopes.Session,
                Confidence = EvidenceConfidenceLevels.Medium,
                RequiresReview = true,
                Notes = "review before allowing future reuse"
            },
            Metadata = new GovernanceLedgerMetadata
            {
                CreatedBy = "test",
                CorrelationId = "corr_1",
                Properties = new Dictionary<string, string> { ["suite"] = "governance" }
            }
        };

    private static string CreateTempDir()
    {
        var baseDir = Path.Join(Path.GetTempPath(), "openclaw-governance-ledger-tests");
        var leaf = Guid.NewGuid().ToString("N");
        var safeLeaf = Path.GetFileName(leaf);
        if (!string.Equals(leaf, safeLeaf, StringComparison.Ordinal))
            throw new InvalidOperationException("Generated temp directory leaf was not a file-name-only value.");

        var path = Path.Join(baseDir, safeLeaf);
        Directory.CreateDirectory(path);
        return path;
    }
}

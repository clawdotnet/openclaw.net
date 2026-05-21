using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Features;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using Xunit;

namespace OpenClaw.Tests;

public sealed class EvidenceBundleTests
{
    [Fact]
    public void EvidenceBundle_RoundTrips_WithSourceGeneratedJson()
    {
        var original = BuildBundle("evb_roundtrip");

        var json = JsonSerializer.Serialize(original, CoreJsonContext.Default.EvidenceBundle);
        var restored = JsonSerializer.Deserialize(json, CoreJsonContext.Default.EvidenceBundle);

        Assert.NotNull(restored);
        Assert.Equal(original.Id, restored!.Id);
        Assert.Equal(EvidenceConfidenceLevels.High, restored.Confidence);
        Assert.Single(restored.Items);
        Assert.Single(restored.Checks);
        Assert.Single(restored.Risks);
        Assert.Single(restored.HumanReviews);
    }

    [Fact]
    public async Task FileEvidenceBundleStore_SavesLoadsAndFiltersBySessionAndContract()
    {
        var root = CreateTempDir();
        var store = new FileEvidenceBundleStore(root);
        await store.SaveAsync(BuildBundle("evb_one", sessionId: "sess_one", contractId: "hctr_one"), CancellationToken.None);
        await store.SaveAsync(BuildBundle("evb_two", sessionId: "sess_two", contractId: "hctr_two"), CancellationToken.None);

        var loaded = await store.GetAsync("evb_one", CancellationToken.None);
        var bySession = await store.ListAsync(new EvidenceBundleListQuery { SourceSessionId = "sess_one" }, CancellationToken.None);
        var byContract = await store.ListAsync(new EvidenceBundleListQuery { HarnessContractId = "hctr_two" }, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("evb_one", loaded!.Id);
        Assert.Single(bySession);
        Assert.Equal("evb_one", bySession[0].Id);
        Assert.Single(byContract);
        Assert.Equal("evb_two", byContract[0].Id);
    }

    [Fact]
    public async Task FileEvidenceBundleStore_RejectsUnsafeIds()
    {
        var store = new FileEvidenceBundleStore(CreateTempDir());
        var unsafeBundle = BuildBundle("../escape");

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.SaveAsync(unsafeBundle, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.GetAsync("../escape", CancellationToken.None));
    }

    [Fact]
    public async Task EvidenceBundleService_CreatesBundleWithTimestampsAndRuntimeEvent()
    {
        var root = CreateTempDir();
        var service = CreateService(root);
        var created = await service.CreateAsync(new EvidenceBundle
        {
            Title = "Run evidence",
            Summary = "Manual evidence for a session.",
            SourceSessionId = "sess_create",
            Confidence = EvidenceConfidenceLevels.Medium,
            Tags = ["evidence"]
        }, CancellationToken.None);

        Assert.StartsWith("evb_", created.Id, StringComparison.Ordinal);
        Assert.True(created.CreatedAtUtc > DateTimeOffset.MinValue);
        Assert.True(created.UpdatedAtUtc >= created.CreatedAtUtc);
        Assert.Equal(EvidenceConfidenceLevels.Medium, created.Confidence);

        var events = new RuntimeEventStore(root, NullLogger<RuntimeEventStore>.Instance)
            .Query(new RuntimeEventQuery { Component = "harness", Action = "evidence_bundle_created" });
        Assert.Single(events);
        Assert.Equal(created.Id, events[0].CorrelationId);
    }

    [Fact]
    public async Task EvidenceBundleService_AppendsContentAndUpdatesTimestamp()
    {
        var root = CreateTempDir();
        var service = CreateService(root);
        var created = await service.CreateAsync(BuildBundle("evb_append"), CancellationToken.None);

        var withItem = await service.AddItemAsync(created.Id, new EvidenceItem
        {
            Kind = EvidenceItemKinds.Note,
            Title = "Operator note",
            Summary = "The operator checked the output."
        }, CancellationToken.None);
        var withCheck = await service.AddCheckAsync(created.Id, new EvidenceCheck
        {
            Name = "Focused tests",
            Status = EvidenceCheckStatuses.Passed,
            Summary = "Tests passed."
        }, CancellationToken.None);
        var withRisk = await service.AddRiskAsync(created.Id, new EvidenceRisk
        {
            RiskLevel = EvidenceRiskLevels.Low,
            Description = "Residual manual-review risk."
        }, CancellationToken.None);
        var withReview = await service.AddHumanReviewAsync(created.Id, new EvidenceHumanReview
        {
            Reviewer = "operator",
            Decision = "accepted",
            Notes = "Looks consistent."
        }, CancellationToken.None);

        Assert.NotNull(withItem);
        Assert.Equal(2, withItem!.Items.Count);
        Assert.NotNull(withCheck);
        Assert.Equal(2, withCheck!.Checks.Count);
        Assert.NotNull(withRisk);
        Assert.Equal(2, withRisk!.Risks.Count);
        Assert.NotNull(withReview);
        Assert.Equal(2, withReview!.HumanReviews.Count);
        Assert.True(withReview.UpdatedAtUtc >= created.UpdatedAtUtc);

        var events = new RuntimeEventStore(root, NullLogger<RuntimeEventStore>.Instance)
            .Query(new RuntimeEventQuery { Component = "harness", Action = "evidence_bundle_updated" });
        Assert.Equal(4, events.Count);
    }

    [Theory]
    [InlineData("confidence")]
    [InlineData("kind")]
    [InlineData("status")]
    [InlineData("risk")]
    public async Task EvidenceBundleService_ValidatesSupportedConstants(string invalidField)
    {
        var service = CreateService(CreateTempDir());
        var bundle = invalidField switch
        {
            "confidence" => new EvidenceBundle { Id = "evb_invalid", Confidence = "certain" },
            "kind" => new EvidenceBundle { Id = "evb_invalid", Items = [new EvidenceItem { Kind = "screenshot", Title = "bad" }] },
            "status" => new EvidenceBundle { Id = "evb_invalid", Checks = [new EvidenceCheck { Name = "bad", Status = "ok" }] },
            "risk" => new EvidenceBundle { Id = "evb_invalid", Risks = [new EvidenceRisk { RiskLevel = "severe", Description = "bad" }] },
            _ => BuildBundle("evb_invalid")
        };

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.CreateAsync(bundle, CancellationToken.None));
    }

    [Fact]
    public void EvidenceBundleService_BuildsCommonEvidenceItems()
    {
        var toolItem = EvidenceBundleService.FromToolInvocation(new ToolInvocation
        {
            CallId = "call_1",
            ToolName = "shell",
            Arguments = "dotnet test",
            Result = "Passed",
            ResultStatus = "passed",
            Duration = TimeSpan.FromMilliseconds(42)
        });
        var runtimeItem = EvidenceBundleService.FromRuntimeEvent(new RuntimeEventEntry
        {
            Id = "evt_1",
            Component = "harness",
            Action = "evidence_bundle_created",
            Severity = "info",
            Summary = "Created."
        });
        var approvalItem = EvidenceBundleService.FromApprovalHistoryEntry(new ApprovalHistoryEntry
        {
            EventType = "decision",
            ApprovalId = "approval_1",
            SessionId = "sess",
            ChannelId = "web",
            SenderId = "user",
            ToolName = "shell",
            ArgumentsPreview = "dotnet test",
            Approved = true,
            TimestampUtc = DateTimeOffset.UtcNow
        });
        var doctorItem = EvidenceBundleService.FromDoctorReport(new DoctorReportResponse
        {
            OverallStatus = SetupCheckStates.Pass,
            RecommendedNextActions = ["No action"]
        });
        var postureItem = EvidenceBundleService.FromPostureCheck(new SecurityPostureResponse
        {
            ToolApprovalRequired = true,
            RiskFlags = ["public_bind"],
            Recommendations = ["Require auth"]
        });

        Assert.Equal(EvidenceItemKinds.ToolCall, toolItem.Kind);
        Assert.Equal(EvidenceItemKinds.RuntimeEvent, runtimeItem.Kind);
        Assert.Equal(EvidenceItemKinds.Approval, approvalItem.Kind);
        Assert.Equal("approved", approvalItem.Status);
        Assert.Equal(EvidenceItemKinds.DoctorReport, doctorItem.Kind);
        Assert.Equal(EvidenceItemKinds.PostureCheck, postureItem.Kind);
        Assert.Equal(EvidenceCheckStatuses.Warning, postureItem.Status);
    }

    private static EvidenceBundleService CreateService(string root)
        => new(
            new FileEvidenceBundleStore(root),
            new RuntimeEventStore(root, NullLogger<RuntimeEventStore>.Instance),
            NullLogger<EvidenceBundleService>.Instance);

    private static EvidenceBundle BuildBundle(
        string id,
        string sessionId = "sess_evidence",
        string contractId = "hctr_evidence")
        => new()
        {
            Id = id,
            Title = "Evidence bundle",
            Summary = "Structured evidence for a governed action.",
            SourceSessionId = sessionId,
            HarnessContractId = contractId,
            ActorId = "operator",
            ChannelId = "web",
            SenderId = "user",
            Confidence = EvidenceConfidenceLevels.High,
            Items =
            [
                new EvidenceItem
                {
                    Id = "item_1",
                    Kind = EvidenceItemKinds.ToolCall,
                    Title = "Tool result",
                    Summary = "Tool completed.",
                    ToolName = "shell",
                    ToolCallId = "call_1",
                    Status = "passed"
                }
            ],
            Checks =
            [
                new EvidenceCheck
                {
                    Id = "check_1",
                    Name = "Tests",
                    Status = EvidenceCheckStatuses.Passed,
                    Summary = "Focused tests passed."
                }
            ],
            Risks =
            [
                new EvidenceRisk
                {
                    RiskLevel = EvidenceRiskLevels.Low,
                    Description = "Residual review risk.",
                    Mitigation = "Operator inspected details."
                }
            ],
            Assumptions =
            [
                new EvidenceAssumption
                {
                    Id = "assumption_1",
                    Text = "The test environment is representative.",
                    Verified = false
                }
            ],
            UntestedAreas =
            [
                new EvidenceUntestedArea
                {
                    Id = "untested_1",
                    Description = "Long-running integration path.",
                    RiskLevel = EvidenceRiskLevels.Medium
                }
            ],
            HumanReviews =
            [
                new EvidenceHumanReview
                {
                    Reviewer = "operator",
                    Decision = "accepted",
                    Notes = "Reviewed."
                }
            ],
            Tags = ["evidence", "harness"],
            Metadata = new EvidenceBundleMetadata
            {
                CreatedBy = "test",
                Source = "unit_test",
                Properties = new Dictionary<string, string>
                {
                    ["suite"] = "evidence"
                }
            }
        };

    private static string CreateTempDir()
    {
        var baseDir = Path.Join(Path.GetTempPath(), "openclaw-evidence-bundle-tests");
        var leaf = Guid.NewGuid().ToString("N");
        var safeLeaf = Path.GetFileName(leaf);
        if (!string.Equals(leaf, safeLeaf, StringComparison.Ordinal))
            throw new InvalidOperationException("Generated temp directory leaf was not a file-name-only value.");

        var path = Path.Join(baseDir, safeLeaf);
        Directory.CreateDirectory(path);
        return path;
    }
}

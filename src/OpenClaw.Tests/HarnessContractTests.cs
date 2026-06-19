using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Features;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using Xunit;

namespace OpenClaw.Tests;

public sealed class HarnessContractTests
{
    [Fact]
    public void HarnessContract_RoundTrips_WithSourceGeneratedJson()
    {
        var original = BuildContract(
            id: "hctr_roundtrip",
            status: HarnessContractStatus.Proposed,
            riskLevel: HarnessContractRiskLevels.High);

        var json = JsonSerializer.Serialize(original, CoreJsonContext.Default.HarnessContract);
        var restored = JsonSerializer.Deserialize(json, CoreJsonContext.Default.HarnessContract);

        Assert.NotNull(restored);
        Assert.Equal(original.Id, restored!.Id);
        Assert.Equal(HarnessContractStatus.Proposed, restored.Status);
        Assert.Equal(HarnessContractRiskLevels.High, restored.RiskLevel);
        Assert.Single(restored.PlannedActions);
        Assert.Single(restored.VerificationPlan);
        Assert.Single(restored.RollbackPlan);
    }

    [Fact]
    public async Task FileHarnessContractStore_SavesLoadsAndFiltersByStatus()
    {
        var root = CreateTempDir();
        var store = new FileHarnessContractStore(root);
        await store.SaveAsync(BuildContract("hctr_draft", HarnessContractStatus.Draft), TestContext.Current.CancellationToken);
        await store.SaveAsync(BuildContract("hctr_verified", HarnessContractStatus.Verified), TestContext.Current.CancellationToken);

        var loaded = await store.GetAsync("hctr_draft", TestContext.Current.CancellationToken);
        var filtered = await store.ListAsync(new HarnessContractListQuery { Status = HarnessContractStatus.Verified }, TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal("hctr_draft", loaded!.Id);
        Assert.Single(filtered);
        Assert.Equal("hctr_verified", filtered[0].Id);
    }

    [Fact]
    public async Task FileHarnessContractStore_RejectsUnsafeIds()
    {
        var store = new FileHarnessContractStore(CreateTempDir());
        var unsafeContract = BuildContract("../escape", HarnessContractStatus.Draft);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.SaveAsync(unsafeContract, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.GetAsync("../escape", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HarnessContractService_CreatesContractWithTimestampsAndRuntimeEvent()
    {
        var root = CreateTempDir();
        var service = CreateService(root);
        var created = await service.CreateAsync(new HarnessContract
        {
            Goal = "Inspect a file",
            PlannedActions =
            [
                new HarnessContractAction
                {
                    Id = "read",
                    Title = "Read file",
                    ToolName = "file_read",
                    ActionType = "read"
                }
            ]
        }, TestContext.Current.CancellationToken);

        Assert.StartsWith("hctr_", created.Id, StringComparison.Ordinal);
        Assert.Equal(HarnessContractRiskLevels.Low, created.RiskLevel);
        Assert.True(created.CreatedAtUtc > DateTimeOffset.MinValue);
        Assert.True(created.UpdatedAtUtc >= created.CreatedAtUtc);

        var events = new RuntimeEventStore(root, NullLogger<RuntimeEventStore>.Instance)
            .Query(new RuntimeEventQuery { Component = "harness", Action = "contract_created" });
        Assert.Single(events);
        Assert.Equal(created.Id, events[0].CorrelationId);
    }

    [Fact]
    public async Task HarnessContractService_MarkStatusUpdatesTimestampsAndEvent()
    {
        var root = CreateTempDir();
        var service = CreateService(root);
        var created = await service.CreateAsync(BuildContract("hctr_status", HarnessContractStatus.Proposed), TestContext.Current.CancellationToken);

        var updated = await service.MarkStatusAsync(created.Id, $" {HarnessContractStatus.Verified} ", TestContext.Current.CancellationToken);

        Assert.NotNull(updated);
        Assert.Equal(HarnessContractStatus.Verified, updated!.Status);
        Assert.NotNull(updated.CompletedAtUtc);

        var events = new RuntimeEventStore(root, NullLogger<RuntimeEventStore>.Instance)
            .Query(new RuntimeEventQuery { Component = "harness", Action = "contract_status_changed" });
        Assert.Single(events);
        Assert.Equal(created.Id, events[0].CorrelationId);
    }

    [Fact]
    public async Task HarnessContractService_RejectsUnknownStatus()
    {
        var service = CreateService(CreateTempDir());
        var created = await service.CreateAsync(BuildContract("hctr_invalid_status", HarnessContractStatus.Proposed), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.MarkStatusAsync(created.Id, "unknown_status", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HarnessContractService_NormalizesNullCollectionsFromJson()
    {
        var service = CreateService(CreateTempDir());
        const string json = """
            {
              "id": "hctr_null_collections",
              "status": "draft",
              "goal": "Handle nullable JSON payloads",
              "plannedActions": null,
              "readSet": null,
              "writeSet": null,
              "toolsRequired": null,
              "successCriteria": null,
              "tags": null
            }
            """;
        var contract = JsonSerializer.Deserialize(json, CoreJsonContext.Default.HarnessContract);

        var created = await service.CreateAsync(contract!, TestContext.Current.CancellationToken);

        Assert.Empty(created.PlannedActions);
        Assert.Empty(created.WriteSet);
        Assert.Empty(created.ToolsRequired);
        Assert.Empty(created.Tags);
        Assert.Equal(HarnessContractRiskLevels.Low, created.RiskLevel);
    }

    [Fact]
    public async Task HarnessContractService_RejectsUnknownRiskLevel()
    {
        var service = CreateService(CreateTempDir());

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.CreateAsync(BuildContract("hctr_invalid_risk", HarnessContractStatus.Draft, "severe"), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("file_read", "read", false, HarnessContractRiskLevels.Low)]
    [InlineData("file_write", "write", true, HarnessContractRiskLevels.Medium)]
    [InlineData("shell", "execute", true, HarnessContractRiskLevels.High)]
    [InlineData("http", "external_api_write", true, HarnessContractRiskLevels.High)]
    [InlineData("custom_tool", "mutate", false, HarnessContractRiskLevels.High)]
    public void HarnessContractService_DerivesRiskLevel(string toolName, string actionType, bool includeWriteSet, string expected)
    {
        var service = CreateService(CreateTempDir());
        var contract = new HarnessContract
        {
            Id = "hctr_risk",
            Goal = "risk",
            PlannedActions =
            [
                new HarnessContractAction
                {
                    Id = "action",
                    ToolName = toolName,
                    ActionType = actionType,
                    WriteSet = includeWriteSet
                        ? [new HarnessContractResourceRef { Kind = HarnessContractResourceKinds.File, Path = "/tmp/file" }]
                        : []
                }
            ]
        };

        Assert.Equal(expected, service.DeriveRiskLevel(contract));
    }

    private static HarnessContractService CreateService(string root)
        => new(
            new FileHarnessContractStore(root),
            new RuntimeEventStore(root, NullLogger<RuntimeEventStore>.Instance),
            NullLogger<HarnessContractService>.Instance);

    private static HarnessContract BuildContract(
        string id,
        string status,
        string? riskLevel = null)
        => new()
        {
            Id = id,
            Status = status,
            Goal = "Ship a passive harness contract foundation",
            UserRequestSummary = "Create inspectable plan records.",
            RiskLevel = riskLevel,
            PlannedActions =
            [
                new HarnessContractAction
                {
                    Id = "inspect",
                    Title = "Inspect current patterns",
                    ToolName = "file_read",
                    ActionType = "read",
                    ExpectedOutcome = "Implementation matches the codebase."
                }
            ],
            ReadSet = [new HarnessContractResourceRef { Kind = HarnessContractResourceKinds.File, Path = "src" }],
            VerificationPlan =
            [
                new HarnessContractVerificationStep
                {
                    Id = "tests",
                    Title = "Run tests",
                    Kind = "command",
                    Command = "dotnet test",
                    ExpectedSignal = "pass"
                }
            ],
            RollbackPlan =
            [
                new HarnessContractRollbackStep
                {
                    Id = "revert",
                    Title = "Revert branch",
                    Description = "Revert the feature commit if needed."
                }
            ],
            SuccessCriteria = ["Existing behavior is unchanged."],
            Tags = ["harness"]
        };

    private static string CreateTempDir()
    {
        var baseDir = Path.Join(Path.GetTempPath(), "openclaw-harness-contract-tests");
        var leaf = Guid.NewGuid().ToString("N");
        var safeLeaf = Path.GetFileName(leaf);
        if (!string.Equals(leaf, safeLeaf, StringComparison.Ordinal))
            throw new InvalidOperationException("Generated temp directory leaf was not a file-name-only value.");

        var path = Path.Join(baseDir, safeLeaf);
        Directory.CreateDirectory(path);
        return path;
    }
}

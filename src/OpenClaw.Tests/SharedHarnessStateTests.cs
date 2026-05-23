using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Features;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using Xunit;

namespace OpenClaw.Tests;

public sealed class SharedHarnessStateTests
{
    [Fact]
    public void SharedHarnessState_SerializesAndDeserializes()
    {
        var state = new SharedHarnessState
        {
            Id = "shs_roundtrip",
            SessionId = "session-a",
            Goal = "Coordinate delegated docs work",
            Participants =
            [
                new HarnessParticipant
                {
                    Id = "writer",
                    Role = HarnessParticipantRoles.DocsWriter,
                    SessionId = "session-writer"
                }
            ],
            Actions =
            [
                new HarnessStateAction
                {
                    Id = "write-docs",
                    ParticipantId = "writer",
                    Title = "Write docs",
                    WriteSet = [new HarnessResourceRef { Kind = HarnessContractResourceKinds.File, Path = "docs/SHARED_HARNESS_STATE.md" }]
                }
            ]
        };

        var json = JsonSerializer.Serialize(state, CoreJsonContext.Default.SharedHarnessState);
        var restored = JsonSerializer.Deserialize(json, CoreJsonContext.Default.SharedHarnessState);

        Assert.NotNull(restored);
        Assert.Equal("shs_roundtrip", restored!.Id);
        Assert.Single(restored.Participants);
        Assert.Single(restored.Actions);
        Assert.Equal("docs/SHARED_HARNESS_STATE.md", restored.Actions[0].WriteSet[0].Path);
    }

    [Fact]
    public async Task FileStore_SavesLoadsAndGetsBySession()
    {
        var tempDir = CreateTempDir();
        var store = new FileSharedHarnessStateStore(tempDir);
        var state = new SharedHarnessState
        {
            Id = "shs_store",
            SessionId = "session-store",
            Goal = "Persist shared state"
        };

        await store.SaveAsync(state, CancellationToken.None);

        var loaded = await store.GetAsync("shs_store", CancellationToken.None);
        var bySession = await store.GetBySessionAsync("session-store", CancellationToken.None);
        var listed = await store.ListAsync(new SharedHarnessStateListQuery { SessionId = "session-store" }, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.NotNull(bySession);
        Assert.Single(listed);
        Assert.Equal("shs_store", bySession!.Id);
    }

    [Fact]
    public async Task Service_AddParticipantAndAction_Works()
    {
        var service = CreateService(out _);
        var created = await service.CreateForSessionAsync("session-service", "Coordinate work", null, null, CancellationToken.None);

        var withParticipant = await service.AddParticipantAsync(created.Id, new HarnessParticipant
        {
            Id = "coder",
            Role = HarnessParticipantRoles.Coder,
            SessionId = "session-coder"
        }, CancellationToken.None);

        var withAction = await service.AddActionAsync(created.Id, new HarnessStateAction
        {
            Id = "edit-code",
            ParticipantId = "coder",
            Title = "Edit code",
            ToolName = "file_write",
            WriteSet = [new HarnessResourceRef { Kind = HarnessContractResourceKinds.File, Path = "src/OpenClaw.Core/Models/SharedHarnessStateModels.cs" }]
        }, CancellationToken.None);

        Assert.NotNull(withParticipant);
        Assert.Single(withParticipant!.Participants);
        Assert.NotNull(withAction);
        Assert.Single(withAction!.Actions);
    }

    [Fact]
    public async Task Service_CreateAsync_RejectsDuplicateCallerSuppliedId()
    {
        var service = CreateService(out _);
        await service.CreateAsync(new SharedHarnessState { Id = "shs_duplicate", SessionId = "session-a" }, CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.CreateAsync(new SharedHarnessState { Id = "shs_duplicate", SessionId = "session-b" }, CancellationToken.None));
    }

    [Fact]
    public async Task Service_UpdateActionStatus_RejectsMissingAction()
    {
        var service = CreateService(out _);
        var created = await service.CreateAsync(new SharedHarnessState
        {
            Id = "shs_missing_action",
            Actions = [new HarnessStateAction { Id = "existing" }]
        }, CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.UpdateActionStatusAsync(created.Id, "missing", HarnessStateStatuses.Completed, CancellationToken.None));
    }

    [Fact]
    public async Task Service_BlankParticipantAndActionIds_UseFirstUnusedGeneratedId()
    {
        var service = CreateService(out _);
        var created = await service.CreateAsync(new SharedHarnessState
        {
            Id = "shs_sparse_ids",
            Participants = [new HarnessParticipant { Id = "participant_1" }, new HarnessParticipant()],
            Actions = [new HarnessStateAction { Id = "action_1" }, new HarnessStateAction()]
        }, CancellationToken.None);

        Assert.Contains(created.Participants, participant => participant.Id == "participant_1");
        Assert.Contains(created.Participants, participant => participant.Id == "participant_2");
        Assert.Contains(created.Actions, action => action.Id == "action_1");
        Assert.Contains(created.Actions, action => action.Id == "action_2");

        var withParticipant = await service.AddParticipantAsync(created.Id, new HarnessParticipant(), CancellationToken.None);
        var withAction = await service.AddActionAsync(created.Id, new HarnessStateAction(), CancellationToken.None);

        Assert.NotNull(withParticipant);
        Assert.Contains(withParticipant!.Participants, participant => participant.Id == "participant_1");
        Assert.Contains(withParticipant.Participants, participant => participant.Id == "participant_3");
        Assert.NotNull(withAction);
        Assert.Contains(withAction!.Actions, action => action.Id == "action_1");
        Assert.Contains(withAction.Actions, action => action.Id == "action_3");
    }

    [Fact]
    public async Task Service_CreateAsync_RejectsDuplicateParticipantAndActionIds()
    {
        var service = CreateService(out _);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.CreateAsync(new SharedHarnessState
            {
                Id = "shs_duplicate_participants",
                Participants =
                [
                    new HarnessParticipant { Id = "participant" },
                    new HarnessParticipant { Id = "participant" }
                ]
            }, CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.CreateAsync(new SharedHarnessState
            {
                Id = "shs_duplicate_actions",
                Actions =
                [
                    new HarnessStateAction { Id = "action" },
                    new HarnessStateAction { Id = "action" }
                ]
            }, CancellationToken.None));
    }

    [Fact]
    public void ConflictDetection_DetectsWriteWriteConflict()
    {
        var service = CreateService(out _);
        var conflicts = service.DetectConflicts(new SharedHarnessState
        {
            Id = "shs_conflict",
            Actions =
            [
                new HarnessStateAction
                {
                    Id = "left",
                    ParticipantId = "a",
                    WriteSet = [new HarnessResourceRef { Kind = HarnessContractResourceKinds.File, Path = "README.md" }]
                },
                new HarnessStateAction
                {
                    Id = "right",
                    ParticipantId = "b",
                    WriteSet = [new HarnessResourceRef { Kind = HarnessContractResourceKinds.File, Path = "README.md" }]
                }
            ]
        });

        var conflict = Assert.Single(conflicts);
        Assert.Equal(HarnessConflictTypes.WriteWrite, conflict.Type);
        Assert.Equal(HarnessConflictPolicies.Warn, conflict.Policy);
    }

    [Fact]
    public void ConflictDetection_DetectsReadWriteConflictWithVersionDependency()
    {
        var service = CreateService(out _);
        var conflicts = service.DetectConflicts(new SharedHarnessState
        {
            Id = "shs_read_write",
            Actions =
            [
                new HarnessStateAction
                {
                    Id = "reader",
                    ParticipantId = "reviewer",
                    ReadSet = [new HarnessResourceRef { Kind = HarnessContractResourceKinds.File, Path = "docs/README.md" }],
                    VersionDependencies =
                    [
                        new HarnessVersionDependency
                        {
                            Id = "docs-version",
                            Resource = new HarnessResourceRef { Kind = HarnessContractResourceKinds.File, Path = "docs/README.md" },
                            Version = "main@abc123"
                        }
                    ]
                },
                new HarnessStateAction
                {
                    Id = "writer",
                    ParticipantId = "docs",
                    WriteSet = [new HarnessResourceRef { Kind = HarnessContractResourceKinds.File, Path = "docs/README.md" }]
                }
            ]
        });

        var conflict = Assert.Single(conflicts);
        Assert.Equal(HarnessConflictTypes.ReadWrite, conflict.Type);
    }

    [Fact]
    public void ConflictDetection_DetectsDirectoryFileConflict()
    {
        var service = CreateService(out _);
        var conflicts = service.DetectConflicts(new SharedHarnessState
        {
            Id = "shs_directory_file",
            Actions =
            [
                new HarnessStateAction
                {
                    Id = "directory-owner",
                    WriteSet = [new HarnessResourceRef { Kind = HarnessContractResourceKinds.Directory, Path = "docs" }]
                },
                new HarnessStateAction
                {
                    Id = "file-writer",
                    WriteSet = [new HarnessResourceRef { Kind = HarnessContractResourceKinds.File, Path = "docs/SHARED_HARNESS_STATE.md" }]
                }
            ]
        });

        var conflict = Assert.Single(conflicts);
        Assert.Equal(HarnessConflictTypes.WriteWrite, conflict.Type);
    }

    [Fact]
    public void ConflictDetection_DetectsAssumptionConflict()
    {
        var service = CreateService(out _);
        var conflicts = service.DetectConflicts(new SharedHarnessState
        {
            Id = "shs_assumptions",
            Assumptions =
            [
                new HarnessAssumption { Id = "a", Key = "target_branch", Value = "main" },
                new HarnessAssumption { Id = "b", Key = "target_branch", Value = "release" }
            ]
        });

        var conflict = Assert.Single(conflicts);
        Assert.Equal(HarnessConflictTypes.Assumption, conflict.Type);
    }

    [Fact]
    public void ConflictDetection_IgnoresNullJsonEntries()
    {
        var service = CreateService(out _);
        const string json = """
        {
          "id": "shs_null_entries",
          "assumptions": [null, { "key": "branch", "value": "main" }],
          "verifierObligations": [null],
          "actions": [
            null,
            {
              "id": "writer",
              "riskLevel": "high",
              "writeSet": [null, { "kind": "file", "path": "README.md" }],
              "assumptions": [null, { "key": "branch", "value": "release" }],
              "verifierObligations": [null]
            }
          ]
        }
        """;
        var state = JsonSerializer.Deserialize(json, CoreJsonContext.Default.SharedHarnessState);

        var conflicts = service.DetectConflicts(state!);

        Assert.Contains(conflicts, conflict => conflict.Type == HarnessConflictTypes.Assumption);
        Assert.Contains(conflicts, conflict => conflict.Type == HarnessConflictTypes.VerifierObligation);
    }

    [Fact]
    public void ConflictDetection_HighRiskWriteWithoutVerifier_Escalates()
    {
        var service = CreateService(out _);
        var conflicts = service.DetectConflicts(new SharedHarnessState
        {
            Id = "shs_verifier",
            Actions =
            [
                new HarnessStateAction
                {
                    Id = "deploy",
                    RiskLevel = HarnessContractRiskLevels.High,
                    WriteSet = [new HarnessResourceRef { Kind = HarnessContractResourceKinds.Endpoint, Uri = "https://prod.example" }]
                }
            ]
        });

        var conflict = Assert.Single(conflicts);
        Assert.Equal(HarnessConflictTypes.VerifierObligation, conflict.Type);
        Assert.Equal(HarnessConflictPolicies.Escalate, conflict.Policy);
    }

    [Fact]
    public void ConflictDetection_NoConflictForDisjointResources()
    {
        var service = CreateService(out _);
        var conflicts = service.DetectConflicts(new SharedHarnessState
        {
            Id = "shs_disjoint",
            Actions =
            [
                new HarnessStateAction
                {
                    Id = "left",
                    WriteSet = [new HarnessResourceRef { Kind = HarnessContractResourceKinds.File, Path = "docs/A.md" }]
                },
                new HarnessStateAction
                {
                    Id = "right",
                    ReadSet = [new HarnessResourceRef { Kind = HarnessContractResourceKinds.File, Path = "docs/B.md" }]
                }
            ]
        });

        Assert.Empty(conflicts);
    }

    private static SharedHarnessStateService CreateService(out string tempDir)
    {
        tempDir = CreateTempDir();
        return new SharedHarnessStateService(
            new FileSharedHarnessStateStore(tempDir),
            new RuntimeEventStore(tempDir, NullLogger<RuntimeEventStore>.Instance),
            NullLogger<SharedHarnessStateService>.Instance);
    }

    private static string CreateTempDir()
    {
        var folderName = Path.GetFileName($"openclaw-shared-state-test-{Guid.NewGuid():N}");
        if (string.IsNullOrWhiteSpace(folderName) || Path.IsPathRooted(folderName))
            throw new InvalidOperationException("Generated shared harness state test directory name must be relative.");

        var tempDir = Path.GetFullPath(Path.Join(Path.GetTempPath(), folderName));
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }
}

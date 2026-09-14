using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent;
using OpenClaw.Core.Skills;
using OpenClaw.MicrosoftAgentFrameworkAdapter;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// Verifies that both AgentRuntime (native) and MafAgentRuntime (MAF adapter)
/// produce identical behavior for ArtifactContract + Projection resolution.
/// </summary>
public sealed class AgentRuntimeProjectionTests
{
    // ────────────────────────────────────────────────────────
    // Test helpers
    // ────────────────────────────────────────────────────────

    private static SkillDefinition CreateSkill(
        string name,
        string instructions = "Base instructions.",
        IReadOnlyList<SkillProjectionContractSet>? projectionContracts = null,
        SkillArtifactContract? artifactContract = null,
        bool disableModelInvocation = false)
        => new()
        {
            Name = name,
            Description = $"Description of {name}",
            Instructions = instructions,
            Location = $"/skills/{name}",
            DisableModelInvocation = disableModelInvocation,
            ProjectionContracts = projectionContracts ?? [],
            ArtifactContract = artifactContract,
            Resources = []
        };

    private static SkillProjectionContractSet CreateProjectionContract(
        string tempDir,
        string topicSlug = "task-execution",
        string defaultTargetView = "prompt-constraint",
        IReadOnlyList<string>? primaryIntentSignals = null,
        IReadOnlyList<string>? explicitArtifactSignals = null,
        IReadOnlyList<string>? allowedTerms = null,
        int producerPriority = 0)
    {
        var relativePath = $"{topicSlug}/{topicSlug}.{defaultTargetView}.projection.json";
        var projectionDir = Path.Combine(tempDir, topicSlug);
        Directory.CreateDirectory(projectionDir);

        File.WriteAllText(
            Path.Combine(tempDir, relativePath),
            $$"""
            {
              "mapping_policy": {
                "unresolved_item_policy": "allow_unmapped_terms"
              },
              "prompt_projection": {
                "allowed_terms": {{(allowedTerms is not null ? System.Text.Json.JsonSerializer.Serialize(allowedTerms) : "[]")}}
              },
              "delivery_artifacts": [],
              "dropped_items": [],
              "open_questions": []
            }
            """);

        return new SkillProjectionContractSet
        {
            ProducerName = "test-producer",
            ProducerPriority = producerPriority,
            RootPath = tempDir,
            Index = new ProjectionContractIndex
            {
                ProducerSkill = "test-producer",
                ProducerPriority = producerPriority,
                DefaultSelectionPolicy = new ProjectionSelectionPolicy
                {
                    FallbackOrderByTargetView = [defaultTargetView]
                },
                Topics =
                [
                    new ProjectionTopicRecord
                    {
                        DomainSlug = topicSlug,
                        DefaultTargetView = defaultTargetView,
                        Views =
                        [
                            new ProjectionViewRecord
                            {
                                TargetView = defaultTargetView,
                                Status = "READY",
                                Path = relativePath.Replace('\\', '/')
                            }
                        ]
                    }
                ],
                TopicScoring = new ProjectionTopicScoring
                {
                    Topics =
                    [
                        new ProjectionTopicSignals
                        {
                            DomainSlug = topicSlug,
                            PrimaryIntentSignals = primaryIntentSignals ?? ["task execution"],
                            SupportingSignals = [],
                            ExplicitArtifactSignals = explicitArtifactSignals ?? [],
                            DemoteWhenCompetingTopicSignals = []
                        }
                    ]
                },
                TargetViewScoring = new ProjectionTargetViewScoring
                {
                    Views =
                    [
                        new ProjectionViewSignals
                        {
                            TargetView = defaultTargetView,
                            ExplicitOutputSignals = explicitArtifactSignals ?? [],
                            StrongSignals = [],
                            SupportingSignals = [],
                            DemoteWhenCompetingViewSignals = []
                        }
                    ]
                }
            }
        };
    }

    // ────────────────────────────────────────────────────────
    // CloneSkill tests — verify both runtimes produce identical clones
    // ────────────────────────────────────────────────────────

    [Fact]
    public void CloneSkill_BothRuntimes_PreserveAll20Fields()
    {
        var source = new SkillDefinition
        {
            Name = "source-skill",
            Description = "A test skill",
            Instructions = "Original instructions.",
            Location = "/skills/source-skill",
            Source = SkillSource.Workspace,
            Metadata = new SkillMetadata { Emoji = "🧪", Always = true },
            Kind = SkillKind.Standard,
            Triggers = ["test", "sample"],
            MetaPriority = 5,
            FinalTextMode = "raw",
            Composition = null,
            UserInvocable = true,
            DisableModelInvocation = false,
            CommandDispatch = "tool",
            CommandTool = "emit_artifact",
            CommandArgMode = "json",
            Resources = [new SkillResource { Name = "ref.md", RelativePath = "references/ref.md", AbsolutePath = "/abs/ref.md", Kind = SkillResourceKind.Reference }],
            ProjectionContracts = [],
            ProjectionDiscovery = null,
            ArtifactContract = new SkillArtifactContract { SchemaVersion = 1, Stages = [] }
        };

        var nativeClone = AgentRuntime.CloneSkill(source, "patched instructions.", disableModelInvocation: true);
        var mafClone = OpenClaw.MicrosoftAgentFrameworkAdapter.MafAgentRuntime.CloneSkill(source, "patched instructions.", disableModelInvocation: true);

        // Verify both clones are independent of source
        Assert.NotSame(source, nativeClone);
        Assert.NotSame(source, mafClone);

        // Verify instructions are patched
        Assert.Equal("patched instructions.", nativeClone.Instructions);
        Assert.Equal("patched instructions.", mafClone.Instructions);

        // Verify disableModelInvocation is applied
        Assert.True(nativeClone.DisableModelInvocation);
        Assert.True(mafClone.DisableModelInvocation);

        // Verify all 20 fields match between the two runtimes
        Assert.Equal(source.Name, nativeClone.Name);
        Assert.Equal(source.Name, mafClone.Name);
        Assert.Equal(source.Description, nativeClone.Description);
        Assert.Equal(source.Description, mafClone.Description);
        Assert.Equal(source.Location, nativeClone.Location);
        Assert.Equal(source.Location, mafClone.Location);
        Assert.Equal(source.Source, nativeClone.Source);
        Assert.Equal(source.Source, mafClone.Source);
        Assert.Equal(source.Metadata.Emoji, nativeClone.Metadata.Emoji);
        Assert.Equal(source.Metadata.Emoji, mafClone.Metadata.Emoji);
        Assert.Equal(source.Metadata.Always, nativeClone.Metadata.Always);
        Assert.Equal(source.Metadata.Always, mafClone.Metadata.Always);
        Assert.Equal(source.Kind, nativeClone.Kind);
        Assert.Equal(source.Kind, mafClone.Kind);
        Assert.Equal(source.Triggers, nativeClone.Triggers);
        Assert.Equal(source.Triggers, mafClone.Triggers);
        Assert.Equal(source.MetaPriority, nativeClone.MetaPriority);
        Assert.Equal(source.MetaPriority, mafClone.MetaPriority);
        Assert.Equal(source.FinalTextMode, nativeClone.FinalTextMode);
        Assert.Equal(source.FinalTextMode, mafClone.FinalTextMode);
        Assert.Equal(source.UserInvocable, nativeClone.UserInvocable);
        Assert.Equal(source.UserInvocable, mafClone.UserInvocable);
        Assert.Equal(source.CommandDispatch, nativeClone.CommandDispatch);
        Assert.Equal(source.CommandDispatch, mafClone.CommandDispatch);
        Assert.Equal(source.CommandTool, nativeClone.CommandTool);
        Assert.Equal(source.CommandTool, mafClone.CommandTool);
        Assert.Equal(source.CommandArgMode, nativeClone.CommandArgMode);
        Assert.Equal(source.CommandArgMode, mafClone.CommandArgMode);
        Assert.Equal(source.Resources, nativeClone.Resources);
        Assert.Equal(source.Resources, mafClone.Resources);
        Assert.Equal(source.ProjectionContracts, nativeClone.ProjectionContracts);
        Assert.Equal(source.ProjectionContracts, mafClone.ProjectionContracts);
        Assert.Null(nativeClone.ProjectionDiscovery);
        Assert.Null(mafClone.ProjectionDiscovery);
        Assert.NotNull(nativeClone.ArtifactContract);
        Assert.NotNull(mafClone.ArtifactContract);
        Assert.Equal(source.ArtifactContract!.SchemaVersion, nativeClone.ArtifactContract!.SchemaVersion);
        Assert.Equal(source.ArtifactContract!.SchemaVersion, mafClone.ArtifactContract!.SchemaVersion);
    }

    [Fact]
    public void CloneSkill_BothRuntimes_PreserveCapabilityRefComposition()
    {
        // #231: capability slots live inside the composition. Both runtimes share
        // the composition by reference; pin that the projection path carries the
        // slots untouched so a future deep-copy cannot silently drop them.
        var source = new SkillDefinition
        {
            Name = "capref-skill",
            Description = "Description of capref-skill",
            Instructions = "Base instructions.",
            Location = "/skills/capref-skill",
            ProjectionContracts = [],
            Resources = [],
            Composition = new MetaSkillComposition
            {
                Steps =
                [
                    new MetaSkillStepDefinition
                    {
                        Id = "pinned",
                        Kind = "tool_call",
                        CapabilityRef = new MetaCapabilityRefDefinition
                        {
                            Binding = "static",
                            Static = new MetaCapabilityStaticBinding { McpServerName = "weather-mcp", ToolName = "get_weather" },
                            Fallback = "fb"
                        },
                        OnFailure = "fb"
                    },
                    new MetaSkillStepDefinition
                    {
                        Id = "resolved",
                        Kind = "tool_call",
                        CapabilityRef = new MetaCapabilityRefDefinition
                        {
                            Binding = "dynamic",
                            Intent = new MetaCapabilityIntent { Type = "cap:WeatherQuery", TaskDescription = "查询天气", Keywords = ["天气"] },
                            SelectionPolicy = "exact_name"
                        }
                    },
                    new MetaSkillStepDefinition { Id = "fb", Kind = "tool_call", Tool = "emit_text" }
                ]
            }
        };

        var nativeClone = AgentRuntime.CloneSkill(source, "patched instructions.", disableModelInvocation: true);
        var mafClone = MafAgentRuntime.CloneSkill(source, "patched instructions.", disableModelInvocation: true);

        // Composition is deliberately shared by reference in both runtimes.
        Assert.Same(source.Composition, nativeClone.Composition);
        Assert.Same(source.Composition, mafClone.Composition);

        foreach (var clone in new[] { nativeClone, mafClone })
        {
            var steps = clone.Composition!.Steps;
            Assert.Equal(3, steps.Count);

            var pinned = steps[0];
            Assert.NotNull(pinned.CapabilityRef);
            Assert.Equal("static", pinned.CapabilityRef.Binding);
            Assert.Equal("weather-mcp", pinned.CapabilityRef.Static!.McpServerName);
            Assert.Equal("get_weather", pinned.CapabilityRef.Static.ToolName);
            Assert.Equal("fb", pinned.CapabilityRef.Fallback);
            Assert.Equal("fb", pinned.OnFailure);

            var resolved = steps[1];
            Assert.NotNull(resolved.CapabilityRef);
            Assert.Equal("dynamic", resolved.CapabilityRef.Binding);
            Assert.Equal("cap:WeatherQuery", resolved.CapabilityRef.Intent!.Type);
            Assert.Equal("查询天气", resolved.CapabilityRef.Intent.TaskDescription);
            Assert.Equal("exact_name", resolved.CapabilityRef.SelectionPolicy);
        }
    }

    [Fact]
    public void CloneSkill_BothRuntimes_PatchedInstructionsMatch()
    {
        var source = CreateSkill("test", instructions: "Original.");

        var nativeClone = AgentRuntime.CloneSkill(source, "Patched.", disableModelInvocation: false);
        var mafClone = MafAgentRuntime.CloneSkill(source, "Patched.", disableModelInvocation: false);

        Assert.Equal("Patched.", nativeClone.Instructions);
        Assert.Equal("Patched.", mafClone.Instructions);
    }

    [Fact]
    public void CloneSkill_BothRuntimes_DisableModelInvocationMatch()
    {
        var source = CreateSkill("test");

        var nativeEnabled = AgentRuntime.CloneSkill(source, "Body.", disableModelInvocation: false);
        var mafEnabled = MafAgentRuntime.CloneSkill(source, "Body.", disableModelInvocation: false);
        var nativeDisabled = AgentRuntime.CloneSkill(source, "Body.", disableModelInvocation: true);
        var mafDisabled = MafAgentRuntime.CloneSkill(source, "Body.", disableModelInvocation: true);

        Assert.False(nativeEnabled.DisableModelInvocation);
        Assert.False(mafEnabled.DisableModelInvocation);
        Assert.True(nativeDisabled.DisableModelInvocation);
        Assert.True(mafDisabled.DisableModelInvocation);
    }

    // ────────────────────────────────────────────────────────
    // ResolveSkillsForTurn tests — verify both runtimes produce identical results
    // ────────────────────────────────────────────────────────

    [Fact]
    public void ResolveSkillsForTurn_NoProjectionSkills_BothRuntimes_ReturnOriginalSkills()
    {
        var skills = new List<SkillDefinition>
        {
            CreateSkill("skill-a"),
            CreateSkill("skill-b")
        };

        var nativeResult = AgentRuntime.ResolveSkillsForTurn(skills, "anything", out var nativeBlocked);
        var mafResult = MafAgentRuntime.ResolveSkillsForTurn(skills, "anything", out var mafBlocked);

        Assert.Equal(2, nativeResult.Length);
        Assert.Equal(2, mafResult.Length);
        Assert.Equal("", nativeBlocked);
        Assert.Equal("", mafBlocked);
        Assert.Equal("Base instructions.", nativeResult[0].Instructions);
        Assert.Equal("Base instructions.", mafResult[0].Instructions);
    }

    [Fact]
    public void ResolveSkillsForTurn_WithProjectionMatch_BothRuntimes_PatchIdentically()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-agent-proj-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var contract = CreateProjectionContract(tempDir,
                primaryIntentSignals: ["task execution"],
                allowedTerms: ["approved term"]);

            var skills = new List<SkillDefinition>
            {
                CreateSkill("developer", projectionContracts: [contract])
            };

            var nativeResult = AgentRuntime.ResolveSkillsForTurn(skills, "task execution", out var nativeBlocked, out var nativePatches);
            var mafResult = MafAgentRuntime.ResolveSkillsForTurn(skills, "task execution", out var mafBlocked, out var mafPatches);

            Assert.Single(nativeResult);
            Assert.Single(mafResult);
            Assert.Equal("", nativeBlocked);
            Assert.Equal("", mafBlocked);

            // Both should have patched instructions
            Assert.Contains("[Projection Route]", nativeResult[0].Instructions);
            Assert.Contains("[Projection Route]", mafResult[0].Instructions);
            Assert.Contains("Base instructions.", nativeResult[0].Instructions);
            Assert.Contains("Base instructions.", mafResult[0].Instructions);
            Assert.Contains("approved term", nativeResult[0].Instructions);
            Assert.Contains("approved term", mafResult[0].Instructions);
            Assert.Contains("## developer", nativePatches);
            Assert.Contains("[Projection Route]", nativePatches);
            Assert.Contains("approved term", nativePatches);
            Assert.Equal(nativePatches, mafPatches);

            // Patch output should be identical
            Assert.Equal(nativeResult[0].Instructions, mafResult[0].Instructions);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ResolveSkillsForTurn_ProjectionBlocked_BothRuntimes_BlockIdentically()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-agent-proj-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var contract = CreateProjectionContract(tempDir,
                primaryIntentSignals: ["task execution"],
                explicitArtifactSignals: ["prompt policy"]);

            var skills = new List<SkillDefinition>
            {
                CreateSkill("developer", projectionContracts: [contract])
            };

            // Request matches no signals — falls back to default view (not blocked).
            // To test blocking, we need a scenario that actually blocks.
            // Use a contract with BlockOnOpenQuestions + open_questions in the projection file.
            var relativePathDir = Path.Combine(tempDir, "task-execution");
            var projectionPath = Path.Combine(relativePathDir,
                $"task-execution.{SkillProjectionViewKeys.PromptConstraint}.projection.json");
            File.WriteAllText(projectionPath, """
            {
              "mapping_policy": { "unresolved_item_policy": "block_or_escalate" },
              "prompt_projection": { "allowed_terms": [] },
              "delivery_artifacts": [],
              "dropped_items": [],
              "open_questions": ["Needs clarification."]
            }
            """);

            var nativeResult = AgentRuntime.ResolveSkillsForTurn(skills, "task execution", out var nativeBlocked);
            var mafResult = MafAgentRuntime.ResolveSkillsForTurn(skills, "task execution", out var mafBlocked);

            Assert.Single(nativeResult);
            Assert.Single(mafResult);

            // BlockOnOpenQuestions not set by default, but block_or_escalate + open_questions triggers block
            Assert.True(nativeResult[0].DisableModelInvocation);
            Assert.True(mafResult[0].DisableModelInvocation);

            Assert.Contains("developer", nativeBlocked);
            Assert.Contains("developer", mafBlocked);
            Assert.Equal(nativeBlocked, mafBlocked);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ResolveSkillsForTurn_MixedSkills_BothRuntimes_IdenticalOutput()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-agent-proj-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var patchedContract = CreateProjectionContract(tempDir,
                primaryIntentSignals: ["task execution"],
                allowedTerms: ["approved term"]);

            var skills = new List<SkillDefinition>
            {
                CreateSkill("no-projection"),  // No projection → pass through
                CreateSkill("patched", projectionContracts: [patchedContract]),  // Has projection → patched
                CreateSkill("also-no-projection")  // No projection → pass through
            };

            var nativeResult = AgentRuntime.ResolveSkillsForTurn(skills, "task execution", out var nativeBlocked);
            var mafResult = MafAgentRuntime.ResolveSkillsForTurn(skills, "task execution", out var mafBlocked);

            Assert.Equal(3, nativeResult.Length);
            Assert.Equal(3, mafResult.Length);
            Assert.Equal("", nativeBlocked);
            Assert.Equal("", mafBlocked);

            // Skill 0: no projection, unchanged
            Assert.Equal("Base instructions.", nativeResult[0].Instructions);
            Assert.Equal("Base instructions.", mafResult[0].Instructions);

            // Skill 1: patched
            Assert.Contains("[Projection Route]", nativeResult[1].Instructions);
            Assert.Contains("[Projection Route]", mafResult[1].Instructions);
            Assert.Equal(nativeResult[1].Instructions, mafResult[1].Instructions);

            // Skill 2: no projection, unchanged
            Assert.Equal("Base instructions.", nativeResult[2].Instructions);
            Assert.Equal("Base instructions.", mafResult[2].Instructions);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    // ────────────────────────────────────────────────────────
    // End-to-end consistency: ResolveSkillsForTurn + BuildPromptPatch integration
    // ────────────────────────────────────────────────────────

    [Fact]
    public void ResolveSkillsForTurn_BothRuntimes_SameSkillsProduceIdenticalBlockedRoutes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-agent-block-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var relativePathDir = Path.Combine(tempDir, "task-execution");
            Directory.CreateDirectory(relativePathDir);
            var projectionPath = Path.Combine(relativePathDir,
                $"task-execution.{SkillProjectionViewKeys.PromptConstraint}.projection.json");

            File.WriteAllText(projectionPath, """
            {
              "mapping_policy": { "unresolved_item_policy": "block_or_escalate" },
              "prompt_projection": { "allowed_terms": [] },
              "delivery_artifacts": [],
              "dropped_items": [],
              "open_questions": ["Requires clarification."]
            }
            """);

            var blockedContract = new SkillProjectionContractSet
            {
                ProducerName = "blocker",
                ProducerPriority = 0,
                RootPath = tempDir,
                Index = new ProjectionContractIndex
                {
                    DefaultSelectionPolicy = new ProjectionSelectionPolicy
                    {
                        FallbackOrderByTargetView = [SkillProjectionViewKeys.PromptConstraint]
                    },
                    Topics =
                    [
                        new ProjectionTopicRecord
                        {
                            DomainSlug = "task-execution",
                            DefaultTargetView = SkillProjectionViewKeys.PromptConstraint,
                            Views =
                            [
                                new ProjectionViewRecord
                                {
                                    TargetView = SkillProjectionViewKeys.PromptConstraint,
                                    Status = "READY",
                                    Path = $"task-execution/task-execution.{SkillProjectionViewKeys.PromptConstraint}.projection.json"
                                }
                            ]
                        }
                    ],
                    TopicScoring = new ProjectionTopicScoring
                    {
                        Topics =
                        [
                            new ProjectionTopicSignals
                            {
                                DomainSlug = "task-execution",
                                PrimaryIntentSignals = ["task execution"],
                                SupportingSignals = [],
                                ExplicitArtifactSignals = [],
                                DemoteWhenCompetingTopicSignals = []
                            }
                        ]
                    },
                    TargetViewScoring = new ProjectionTargetViewScoring
                    {
                        Views =
                        [
                            new ProjectionViewSignals
                            {
                                TargetView = SkillProjectionViewKeys.PromptConstraint,
                                ExplicitOutputSignals = [],
                                StrongSignals = [],
                                SupportingSignals = [],
                                DemoteWhenCompetingViewSignals = []
                            }
                        ]
                    }
                }
            };

            var skills = new List<SkillDefinition>
            {
                CreateSkill("blocked-skill", projectionContracts: [blockedContract])
            };

            var nativeResult = AgentRuntime.ResolveSkillsForTurn(skills, "task execution", out var nativeBlocked);
            var mafResult = MafAgentRuntime.ResolveSkillsForTurn(skills, "task execution", out var mafBlocked);

            // Both should block the skill
            Assert.True(nativeResult[0].DisableModelInvocation);
            Assert.True(mafResult[0].DisableModelInvocation);

            // Blocked routes text should be identical
            Assert.Equal(nativeBlocked, mafBlocked);
            Assert.Contains("requires escalation", nativeBlocked);
            Assert.Contains("requires escalation", mafBlocked);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}

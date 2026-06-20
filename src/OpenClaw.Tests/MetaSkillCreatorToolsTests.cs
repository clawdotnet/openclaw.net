using System.Text.Json;
using OpenClaw.Agent.Tools;
using Xunit;

namespace OpenClaw.Tests;

public sealed class MetaSkillCreatorToolsTests
{
    [Fact]
    public async Task EmitTextTool_ReturnsProvidedText()
    {
        var tool = new EmitTextTool();

        var result = await tool.ExecuteAsync("{\"text\":\"hello\"}", TestContext.Current.CancellationToken);

        Assert.Equal("hello", result);
    }

    [Fact]
    public async Task MetaSkillAssembleTool_UnknownPattern_ReturnsErrorJson()
    {
        var tool = new MetaSkillAssembleTool();

        var result = await tool.ExecuteAsync("{\"pattern_id\":\"unknown\",\"slots_json\":\"{}\"}", TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(result);
        Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("unknown_pattern_id", document.RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task MetaSkillFillSlotsTool_UnknownPattern_ReturnsErrorJson()
    {
        var tool = new MetaSkillFillSlotsTool();

        var result = await tool.ExecuteAsync(
            "{\"pattern_id\":\"unknown\",\"history_summary\":\"h\",\"user_intent\":\"u\"}",
            TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(result);
        Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("unknown_pattern_id", document.RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task MetaSkillFillSlotsTool_MissingHistorySummary_ReturnsErrorJson()
    {
        var tool = new MetaSkillFillSlotsTool();

        var result = await tool.ExecuteAsync(
            "{\"pattern_id\":\"p1_sequential\",\"history_summary\":\"\",\"user_intent\":\"need report\"}",
            TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(result);
        Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("invalid_arguments", document.RootElement.GetProperty("errorCode").GetString());
    }

        [Fact]
        public async Task MetaSkillFillSlotsTool_ExtractsRequiredTriggersFromIntent()
        {
                var tool = new MetaSkillFillSlotsTool();

                var input = """
                        {
                            "pattern_id": "p1_sequential",
                            "history_summary": "recent chain observed",
                            "user_intent": "请创建一个 meta 技能，触发词包含: 编写周报, 生成日报"
                        }
                        """;

                var result = await tool.ExecuteAsync(input, TestContext.Current.CancellationToken);

                using var document = JsonDocument.Parse(result);
                var triggers = document.RootElement.GetProperty("triggers");
                Assert.Equal(JsonValueKind.Array, triggers.ValueKind);
                Assert.Contains(triggers.EnumerateArray().Select(static item => item.GetString()), v => v == "编写周报");
                Assert.Contains(triggers.EnumerateArray().Select(static item => item.GetString()), v => v == "生成日报");
        }

        [Fact]
        public async Task MetaSkillAssembleTool_P1Slots_ProducesMetaFrontmatterAndComposition()
        {
                var tool = new MetaSkillAssembleTool();

                const string slotsJson = """
                        {
                            "name": "meta-p1-weekly-report",
                            "description": "This generated meta skill composes history and summarize for reporting.",
                            "meta_priority": 50,
                            "triggers": ["编写周报"],
                            "steps": [
                                {"id":"gather","skill":"history-explorer","task":"Collect context","with_keys":{}},
                                {"id":"synthesize","skill":"summarize","task":"Build report","with_keys":{}}
                            ]
                        }
                        """;

                var argsJson = JsonSerializer.Serialize(new
                {
                        pattern_id = "p1_sequential",
                        slots_json = slotsJson
                });

                var result = await tool.ExecuteAsync(argsJson, TestContext.Current.CancellationToken);

                Assert.Contains("kind: meta", result, StringComparison.Ordinal);
                Assert.Contains("composition:", result, StringComparison.Ordinal);
                Assert.Contains("- id: gather", result, StringComparison.Ordinal);
                Assert.Contains("- id: synthesize", result, StringComparison.Ordinal);
        }

                [Fact]
                public async Task MetaSkillAssembleTool_InvalidSlotsJson_ReturnsErrorJson()
                {
                    var tool = new MetaSkillAssembleTool();

                    var result = await tool.ExecuteAsync(
                        "{\"pattern_id\":\"p1_sequential\",\"slots_json\":\"{not-json}\"}",
                        TestContext.Current.CancellationToken);

                    using var document = JsonDocument.Parse(result);
                    Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
                    Assert.Equal("invalid_slots_json", document.RootElement.GetProperty("errorCode").GetString());
                }

        [Fact]
        public async Task MetaSkillLintRunTool_InvalidDependency_FailsG2()
        {
                var tool = new MetaSkillLintRunTool();
                const string skillMd = """
                        ---
                        name: "meta-invalid-dep"
                        description: "A generated meta skill with invalid dependency reference for lint coverage."
                        kind: meta
                        composition:
                            steps:
                                - id: first
                                    skill: "summarize"
                                    with:
                                        task: "a"
                                - id: second
                                    skill: "summarize"
                                    depends_on: [missing_step]
                                    with:
                                        task: "b"
                        ---
                        body
                        """;

                var argsJson = JsonSerializer.Serialize(new { skill_md = skillMd, gates = "G1,G2" });
                var result = await tool.ExecuteAsync(argsJson, TestContext.Current.CancellationToken);

                using var document = JsonDocument.Parse(result);
                Assert.False(document.RootElement.GetProperty("passed").GetBoolean());
                Assert.Contains(document.RootElement.GetProperty("failedGates").EnumerateArray().Select(static item => item.GetString()), v => v == "G2");
        }

        [Fact]
        public async Task MetaSkillSmokeRunTool_ReturnsGateObjectsAndDegradedFlag()
        {
                var tool = new MetaSkillSmokeRunTool();
                const string skillMd = """
                        ---
                        name: "meta-smoke"
                        description: "Meta skill for smoke gate verification and trigger matching coverage."
                        kind: meta
                        triggers:
                            - "create a meta-skill"
                        composition:
                            steps:
                                - id: s1
                                    skill: "summarize"
                                    with:
                                        task: "task"
                        ---
                        body
                        """;

                var argsJson = JsonSerializer.Serialize(new { skill_md = skillMd, classifier_model = "stub" });
                var result = await tool.ExecuteAsync(argsJson, TestContext.Current.CancellationToken);

                using var document = JsonDocument.Parse(result);
                Assert.True(document.RootElement.TryGetProperty("G3", out var g3));
                Assert.True(document.RootElement.TryGetProperty("G4", out var g4));
                Assert.True(g3.TryGetProperty("passed", out _));
                Assert.True(g4.TryGetProperty("passed", out _));
                Assert.True(document.RootElement.GetProperty("degraded").GetBoolean());
        }

        [Fact]
        public async Task MetaSkillRuntimeE2ERunTool_WithoutContext_ReturnsUnavailable()
        {
                var tool = new MetaSkillRuntimeE2ERunTool();
                var argsJson = JsonSerializer.Serialize(new { skill_md = "---\nname: n\nkind: meta\n---\n" });

                var result = await tool.ExecuteAsync(argsJson, TestContext.Current.CancellationToken);

                using var document = JsonDocument.Parse(result);
                Assert.Equal("unavailable", document.RootElement.GetProperty("status").GetString());
                Assert.False(document.RootElement.GetProperty("passed").GetBoolean());
        }

            [Fact]
            public async Task MetaSkillRuntimeE2ERunTool_WithContext_MetaWins_ReturnsPassed()
            {
                var tool = new MetaSkillRuntimeE2ERunTool();
                var argsJson = JsonSerializer.Serialize(new
                {
                    skill_md = "---\nname: n\nkind: meta\ntriggers:\n  - \"run n\"\n---\n",
                    eval_prompts = "[\"please run n\"]",
                    baseline_model = "gpt-test"
                });

                static ValueTask<IReadOnlyDictionary<string, string>> Runner(string mode, string prompt, string skillMd, string baselineModel, CancellationToken ct)
                {
                    IReadOnlyDictionary<string, string> payload = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["text"] = mode == "meta" ? "meta answer" : "baseline answer"
                    };
                    return ValueTask.FromResult(payload);
                }

                static ValueTask<IReadOnlyDictionary<string, string>> Judge(string prompt, IReadOnlyDictionary<string, string> meta, IReadOnlyDictionary<string, string> baseline, CancellationToken ct)
                {
                    IReadOnlyDictionary<string, string> payload = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["winner"] = "orchestrated",
                        ["reason"] = "meta is better",
                        ["regression"] = string.Empty
                    };
                    return ValueTask.FromResult(payload);
                }

                using var ctx = MetaSkillRuntimeE2ERunTool.PushContext(new MetaSkillRuntimeE2EContext(Runner, Judge));
                var result = await tool.ExecuteAsync(argsJson, TestContext.Current.CancellationToken);

                using var document = JsonDocument.Parse(result);
                Assert.Equal("ok", document.RootElement.GetProperty("status").GetString());
                Assert.True(document.RootElement.GetProperty("passed").GetBoolean());
                Assert.Equal("meta", document.RootElement.GetProperty("winner").GetString());
                Assert.Equal("gpt-test", document.RootElement.GetProperty("baseline_model").GetString());
            }

        [Fact]
        public async Task MetaSkillPersistProposalTool_FullGatedWithoutRuntimePass_NotAutoEnableEligible()
        {
                var tool = new MetaSkillPersistProposalTool();
                var lint = "{\"passed\":true}";
                var smoke = "{\"G3\":{\"passed\":true},\"G4\":{\"passed\":true}}";
                var runtime = "{\"passed\":false}";
                var home = Path.Combine(Path.GetTempPath(), "openclaw-meta-creator-tests", Guid.NewGuid().ToString("N"));

                var argsJson = JsonSerializer.Serialize(new
                {
                        skill_md = "---\nname: demo\nkind: meta\n---\n",
                        lint_result = lint,
                        smoke_result = smoke,
                        creator_mode = "FULL_GATED",
                        runtime_e2e_result = runtime,
                        home
                });

                try
                {
                        var result = await tool.ExecuteAsync(argsJson, TestContext.Current.CancellationToken);

                        using var document = JsonDocument.Parse(result);
                        Assert.Equal("ok", document.RootElement.GetProperty("status").GetString());
                    Assert.Equal(document.RootElement.GetProperty("proposal_id").GetString(), document.RootElement.GetProperty("proposalId").GetString());
                        Assert.False(document.RootElement.GetProperty("auto_enable_eligible").GetBoolean());
                        var proposalPath = document.RootElement.GetProperty("path").GetString();
                        Assert.NotNull(proposalPath);
                        Assert.True(File.Exists(Path.Combine(proposalPath!, "SKILL.md")));
                        Assert.True(File.Exists(Path.Combine(proposalPath!, "gates.json")));

                    using var gatesDocument = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(proposalPath!, "gates.json")));
                    Assert.Equal(document.RootElement.GetProperty("proposal_id").GetString(), gatesDocument.RootElement.GetProperty("proposal_id").GetString());
                    Assert.Equal("FULL_GATED", gatesDocument.RootElement.GetProperty("creator_mode").GetString());
                    Assert.True(gatesDocument.RootElement.TryGetProperty("lint", out _));
                    Assert.True(gatesDocument.RootElement.TryGetProperty("smoke", out _));
                    Assert.True(gatesDocument.RootElement.TryGetProperty("runtime_e2e", out _));
                }
                finally
                {
                        if (Directory.Exists(home))
                                Directory.Delete(home, recursive: true);
                }
        }

    [Fact]
    public async Task MetaSkillPersistProposalTool_MinimalPayload_ReturnsProposalEnvelope()
    {
        var tool = new MetaSkillPersistProposalTool();

        var result = await tool.ExecuteAsync(
            "{\"skill_md\":\"---\\nname: demo\\n---\\n\",\"lint_result\":\"{}\",\"smoke_result\":\"{}\"}",
            TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(result);
        Assert.Equal("ok", document.RootElement.GetProperty("status").GetString());
        Assert.True(document.RootElement.TryGetProperty("proposalId", out _));
    }

    [Fact]
    public async Task MetaSkillPersistProposalTool_MissingRequiredArgs_ReturnsErrorJson()
    {
        var tool = new MetaSkillPersistProposalTool();

        var result = await tool.ExecuteAsync("{\"skill_md\":\"---\\nname: demo\\n---\\n\"}", TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(result);
        Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("invalid_arguments", document.RootElement.GetProperty("errorCode").GetString());
    }

    // ── Model contract: field-name stability & structure ──

    [Fact]
    public async Task MetaSkillPersistProposalTool_ProposalIdSnakeAndCamel_AreEqual()
    {
        var tool = new MetaSkillPersistProposalTool();
        var home = Path.Combine(Path.GetTempPath(), "openclaw-meta-creator-tests", Guid.NewGuid().ToString("N"));

        var argsJson = JsonSerializer.Serialize(new
        {
            skill_md = "---\nname: demo\nkind: meta\n---\n",
            lint_result = "{}",
            smoke_result = "{\"G3\":{\"passed\":true},\"G4\":{\"passed\":true}}",
            home
        });

        try
        {
            var result = await tool.ExecuteAsync(argsJson, TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(result);

            // snake_case and camelCase proposal-id fields must contain the same value
            Assert.True(document.RootElement.TryGetProperty("proposal_id", out var snake));
            Assert.True(document.RootElement.TryGetProperty("proposalId", out var camel));
            Assert.Equal(snake.GetString(), camel.GetString());
            Assert.False(string.IsNullOrWhiteSpace(snake.GetString()));
        }
        finally
        {
            if (Directory.Exists(home))
                Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task MetaSkillSmokeRunTool_G3AndG4_HaveAllExpectedFields()
    {
        var tool = new MetaSkillSmokeRunTool();
        const string skillMd = """
                ---
                name: "meta-smoke-contract"
                description: "Meta skill for smoke gate contract verification and trigger matching."
                kind: meta
                triggers:
                    - "create a meta-skill"
                composition:
                    steps:
                        - id: s1
                          skill: "summarize"
                          with:
                              task: "task"
                ---
                body
                """;

        var argsJson = JsonSerializer.Serialize(new { skill_md = skillMd });
        var result = await tool.ExecuteAsync(argsJson, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(result);

        // G3 fields
        var g3 = document.RootElement.GetProperty("G3");
        Assert.True(g3.TryGetProperty("passed", out _));
        Assert.True(g3.TryGetProperty("degraded", out _));
        // G4 fields
        var g4 = document.RootElement.GetProperty("G4");
        Assert.True(g4.TryGetProperty("passed", out _));
        Assert.True(g4.TryGetProperty("degraded", out _));
        // top-level degraded flag
        Assert.True(document.RootElement.TryGetProperty("degraded", out _));
    }

    [Fact]
    public async Task MetaSkillLintRunTool_NoFailedGates_ReturnsEmptyFailedGatesArray()
    {
        var tool = new MetaSkillLintRunTool();
        const string skillMd = """
                ---
                name: "meta-valid"
                description: "A meta skill with valid dependency reference for positive lint coverage."
                kind: meta
                composition:
                    steps:
                        - id: first
                          skill: "summarize"
                          with:
                              task: "a"
                        - id: second
                          skill: "summarize"
                          depends_on: [first]
                          with:
                              task: "b"
                ---
                body
                """;

        var argsJson = JsonSerializer.Serialize(new { skill_md = skillMd, gates = "G1,G2" });
        var result = await tool.ExecuteAsync(argsJson, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(result);

        Assert.Equal("ok", document.RootElement.GetProperty("status").GetString());
        Assert.True(document.RootElement.GetProperty("passed").GetBoolean());
        var failedGates = document.RootElement.GetProperty("failedGates");
        Assert.Equal(JsonValueKind.Array, failedGates.ValueKind);
        Assert.Equal(0, failedGates.GetArrayLength());
    }

    [Fact]
    public async Task MetaSkillRuntimeE2ERunTool_WithContext_ReturnsCasesArrayWithExpectedFields()
    {
        var tool = new MetaSkillRuntimeE2ERunTool();
        var argsJson = JsonSerializer.Serialize(new
        {
            skill_md = "---\nname: n\nkind: meta\ntriggers:\n  - \"run n\"\n---\n",
            eval_prompts = "[\"please run n\",\"run n now\"]",
            baseline_model = "gpt-test"
        });

        static ValueTask<IReadOnlyDictionary<string, string>> Runner(string mode, string prompt, string skillMd, string baselineModel, CancellationToken ct)
        {
            IReadOnlyDictionary<string, string> payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["text"] = mode == "meta" ? "meta answer" : "baseline answer"
            };
            return ValueTask.FromResult(payload);
        }

        static ValueTask<IReadOnlyDictionary<string, string>> Judge(string prompt, IReadOnlyDictionary<string, string> meta, IReadOnlyDictionary<string, string> baseline, CancellationToken ct)
        {
            IReadOnlyDictionary<string, string> payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["winner"] = "orchestrated",
                ["reason"] = "meta is better",
                ["regression"] = string.Empty
            };
            return ValueTask.FromResult(payload);
        }

        using var ctx = MetaSkillRuntimeE2ERunTool.PushContext(new MetaSkillRuntimeE2EContext(Runner, Judge));
        var result = await tool.ExecuteAsync(argsJson, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(result);

        Assert.Equal("ok", document.RootElement.GetProperty("status").GetString());
        var cases = document.RootElement.GetProperty("cases");
        Assert.Equal(JsonValueKind.Array, cases.ValueKind);
        Assert.Equal(2, cases.GetArrayLength());

        foreach (var c in cases.EnumerateArray())
        {
            Assert.True(c.TryGetProperty("prompt", out _));
            Assert.True(c.TryGetProperty("winner", out _));
            Assert.True(c.TryGetProperty("regression", out _));
            Assert.True(c.TryGetProperty("reason", out _));
        }
    }

    [Fact]
    public async Task MetaSkillFillSlotsTool_InvalidArguments_MissingUserIntent_ReturnsErrorJson()
    {
        var tool = new MetaSkillFillSlotsTool();

        var result = await tool.ExecuteAsync(
            "{\"pattern_id\":\"p1_sequential\",\"history_summary\":\"h\",\"user_intent\":\"\"}",
            TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(result);
        Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("invalid_arguments", document.RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task MetaSkillFillSlotsTool_InvalidArguments_MissingPatternId_ReturnsErrorJson()
    {
        var tool = new MetaSkillFillSlotsTool();

        var result = await tool.ExecuteAsync(
            "{\"pattern_id\":\"\",\"history_summary\":\"h\",\"user_intent\":\"u\"}",
            TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(result);
        Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("unknown_pattern_id", document.RootElement.GetProperty("errorCode").GetString());
    }
}

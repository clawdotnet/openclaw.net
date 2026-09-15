using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Skills;
using Xunit;

namespace OpenClaw.Tests;

public class SkillLoaderTests
{
    [Fact]
    public void ParseSkillContent_ValidFrontmatter_ReturnsSkill()
    {
        var content = """
            ---
            name: test-skill
            description: A test skill for unit testing
            ---
            Use the test tool to run tests.
            Always validate output before returning.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/test-skill", SkillSource.Workspace);

        Assert.NotNull(skill);
        Assert.Equal("test-skill", skill!.Name);
        Assert.Equal("A test skill for unit testing", skill.Description);
        Assert.Contains("test tool", skill.Instructions);
        Assert.Equal("/skills/test-skill", skill.Location);
        Assert.Equal(SkillSource.Workspace, skill.Source);
    }

    [Fact]
    public void ParseSkillContent_MissingFrontmatter_ReturnsNull()
    {
        var content = "Just some markdown without frontmatter.";

        var skill = SkillLoader.ParseSkillContent(content, "/skills/bad", SkillSource.Workspace);

        Assert.Null(skill);
    }

    [Fact]
    public void ParseSkillContent_MissingName_ReturnsNull()
    {
        var content = """
            ---
            description: No name here
            ---
            Instructions body.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/noname", SkillSource.Workspace);

        Assert.Null(skill);
    }

    [Fact]
    public void ParseSkillContent_WithMetadata_ParsesRequirements()
    {
        var content = """
            ---
            name: gemini-skill
            description: Use Gemini for coding
            metadata: {"openclaw": {"requires": {"bins": ["gemini"], "env": ["GEMINI_API_KEY"]}, "primaryEnv": "GEMINI_API_KEY", "emoji": "♊️"}}
            ---
            Use the gemini CLI tool.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/gemini", SkillSource.Managed);

        Assert.NotNull(skill);
        Assert.Equal("gemini-skill", skill!.Name);
        Assert.Single(skill.Metadata.RequireBins);
        Assert.Equal("gemini", skill.Metadata.RequireBins[0]);
        Assert.Single(skill.Metadata.RequireEnv);
        Assert.Equal("GEMINI_API_KEY", skill.Metadata.RequireEnv[0]);
        Assert.Equal("GEMINI_API_KEY", skill.Metadata.PrimaryEnv);
        Assert.Equal("♊️", skill.Metadata.Emoji);
    }

    [Fact]
    public void ParseSkillContent_WithMetadata_ParsesRiskAndCapabilities()
    {
        var content = """
            ---
            name: secure-meta-skill
            description: Metadata governance sample
            metadata: {"openclaw": {"risk": "high", "capabilities": ["tool:allowed_tool", "network-read"]}}
            ---
            Secure skill instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/secure-meta", SkillSource.Managed);

        Assert.NotNull(skill);
        Assert.Equal("high", skill!.Metadata.Risk);
        Assert.Equal(["tool:allowed_tool", "network-read"], skill.Metadata.Capabilities);
    }

    [Fact]
    public void ParseMetadata_OpenSquillaAlias_ParsesRiskAndCapabilities()
    {
        var metadata = SkillLoader.ParseMetadata("""{"opensquilla":{"risk":"medium","capabilities":["tool:search"]}}""");

        Assert.Equal("medium", metadata.Risk);
        Assert.Equal(["tool:search"], metadata.Capabilities);
    }

    [Fact]
    public void ParseSkillContent_UserInvocableFalse_SetsProperly()
    {
        var content = """
            ---
            name: internal-skill
            description: Not user-invocable
            user-invocable: false
            ---
            Internal instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/internal", SkillSource.Bundled);

        Assert.NotNull(skill);
        Assert.False(skill!.UserInvocable);
    }

    [Fact]
    public void ParseSkillContent_DisableModelInvocation_SetsProperly()
    {
        var content = """
            ---
            name: slash-only
            description: Slash command only
            disable-model-invocation: true
            ---
            Only via slash command.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/slash", SkillSource.Bundled);

        Assert.NotNull(skill);
        Assert.True(skill!.DisableModelInvocation);
    }

    [Fact]
    public void ParseSkillContent_CommandDispatch_SetsProperly()
    {
        var content = """
            ---
            name: summarize
            description: Summarize content
            command-dispatch: tool
            command-tool: summarize_tool
            command-arg-mode: raw
            ---
            Summarization instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/summarize", SkillSource.Workspace);

        Assert.NotNull(skill);
        Assert.Equal("tool", skill!.CommandDispatch);
        Assert.Equal("summarize_tool", skill.CommandTool);
        Assert.Equal("raw", skill.CommandArgMode);
    }

    [Fact]
    public void ParseSkillContent_KindMeta_SetsKind()
    {
        var content = """
            ---
            name: meta-skill
            description: Meta orchestrator skill
            kind: meta
            composition: {"steps":[{"id":"s1","kind":"agent","skill":"web-research"}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta", SkillSource.Workspace);

        Assert.NotNull(skill);
        Assert.Equal(SkillKind.Meta, skill!.Kind);
    }

    [Fact]
    public void ParseSkillContent_KindMissing_UsesStandardDefault()
    {
        var content = """
            ---
            name: standard-skill
            description: Standard skill
            ---
            Standard instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/standard", SkillSource.Workspace);

        Assert.NotNull(skill);
        Assert.Equal(SkillKind.Standard, skill!.Kind);
    }

    [Fact]
    public void ParseSkillContent_InvalidKind_ReturnsNull()
    {
        var content = """
            ---
            name: invalid-kind
            description: Should fail
            kind: unknown
            ---
            Instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/invalid", SkillSource.Workspace);

        Assert.Null(skill);
    }

    [Fact]
    public void ParseSkillContent_MetaProtocolFields_ParsesSuccessfully()
    {
        var content = """
            ---
            name: meta-protocol
            description: Meta protocol fields
            kind: meta
            triggers: ["帮我调研并给出建议", "生成决策备忘录"]
            meta_priority: 50
            final_text_mode: auto
            composition: {"steps":[{"id":"research","kind":"agent","skill":"web-research","with":{"input":"{{input}}"}},{"id":"decision","type":"llm_chat","depends_on":["research"],"with":{"prompt":"{{outputs.research}}"}}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-protocol", SkillSource.Workspace);

        Assert.NotNull(skill);
        Assert.Equal(SkillKind.Meta, skill!.Kind);
        Assert.Equal(2, skill.Triggers.Count);
        Assert.Equal(50, skill.MetaPriority);
        Assert.Equal("auto", skill.FinalTextMode);
        Assert.NotNull(skill.Composition);
        Assert.Equal(2, skill.Composition!.Steps.Count);
        Assert.Equal("research", skill.Composition.Steps[0].Id);
        Assert.Equal("agent", skill.Composition.Steps[0].Kind);
        Assert.Equal("{\"input\":\"{{input}}\"}", skill.Composition.Steps[0].WithJson);
        Assert.Equal("decision", skill.Composition.Steps[1].Id);
        Assert.Equal("llm_chat", skill.Composition.Steps[1].Kind);
        Assert.Equal("{\"prompt\":\"{{outputs.research}}\"}", skill.Composition.Steps[1].WithJson);
        Assert.Single(skill.Composition.Steps[1].DependsOn);
        Assert.Equal("research", skill.Composition.Steps[1].DependsOn[0]);
    }

    [Fact]
    public void ParseSkillContent_YamlTriggersQuotedScalars_RemainStrings()
    {
        var content = """
            ---
            name: quoted-triggers
            description: Quoted scalars must serialize as JSON strings
            triggers:
              - "123"
              - "true"
              - "null"
              - "~"
            ---
            Instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/quoted-triggers", SkillSource.Workspace);

        Assert.NotNull(skill);
        Assert.Equal(["123", "true", "null", "~"], skill!.Triggers);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("true")]
    [InlineData("~")]
    public void ParseSkillContent_YamlTriggersPlainInferredScalar_FailsAsNonString(string plainValue)
    {
        // Plain scalars keep their boolean/number/null-like inference, so a
        // plain 123/true/~ converts to a non-string JSON value and cannot be a trigger.
        var content = $"""
            ---
            name: plain-trigger
            description: Plain inferred scalar trigger
            triggers:
              - {plainValue}
            ---
            Instructions.
            """;

        var ok = SkillLoader.TryParseSkillContent(content, "/skills/plain-trigger", SkillSource.Workspace, out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void ParseSkillContent_YamlCompositionQuotedScalars_RemainStrings()
    {
        var content = """
            ---
            name: quoted-composition
            description: Quoted scalars in a YAML composition block
            kind: meta
            composition:
              steps:
                - id: s1
                  kind: llm_chat
                  with:
                    prompt: "123"
                    truthy: "true"
                    nothing: "null"
            ---
            Instructions.
            """;

        var ok = SkillLoader.TryParseSkillContent(content, "/skills/quoted-composition", SkillSource.Workspace, out var skill, out var errorCode);

        Assert.True(ok, errorCode);
        Assert.NotNull(skill);
        var step = Assert.Single(skill!.Composition!.Steps);
        using var withDoc = JsonDocument.Parse(step.WithJson!);
        Assert.Equal(JsonValueKind.String, withDoc.RootElement.GetProperty("prompt").ValueKind);
        Assert.Equal("123", withDoc.RootElement.GetProperty("prompt").GetString());
        Assert.Equal("true", withDoc.RootElement.GetProperty("truthy").GetString());
        Assert.Equal("null", withDoc.RootElement.GetProperty("nothing").GetString());
    }

    [Fact]
    public void ParseSkillContent_YamlCompositionLegacySkillExecFields_AreNormalized()
    {
        var content = """
            ---
            name: legacy-skill-exec-fields
            description: Legacy skill_exec field aliases remain supported
            kind: meta
            composition:
              steps:
                - id: delegate
                  kind: skill_exec
                  skill: web-research
                  skill_exec_entrypoint: report
                  skill_exec_args: ["--format", "json"]
                  skill_exec_stdin: "{{ input }}"
                  skill_exec_cwd: artifacts/meta
                  skill_exec_parse_mode: json
            ---
            Instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/legacy-skill-exec-fields", SkillSource.Workspace);

        Assert.NotNull(skill);
        var step = Assert.Single(skill!.Composition!.Steps);
        Assert.Equal("report", step.SkillExecEntrypoint);
        Assert.Equal(["--format", "json"], step.SkillExecArgs);
        Assert.Equal("{{ input }}", step.SkillExecStdin);
        Assert.Equal("artifacts/meta", step.SkillExecCwd);
        Assert.Equal("json", step.SkillExecParseMode);
    }

    [Fact]
    public void ParseSkillContent_YamlCompositionLegacyNamedWithProperty_IsPreserved()
    {
        var content = """
            ---
            name: legacy-named-payload
            description: Arbitrary payload keys are preserved
            kind: meta
            composition:
              steps:
                - id: delegate
                  kind: llm_chat
                  with:
                    skill_exec_args: payload-value
            ---
            Instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/legacy-named-payload", SkillSource.Workspace);

        Assert.NotNull(skill);
        var step = Assert.Single(skill!.Composition!.Steps);
        using var withDoc = JsonDocument.Parse(step.WithJson!);
        Assert.Equal("payload-value", withDoc.RootElement.GetProperty("skill_exec_args").GetString());
        Assert.False(withDoc.RootElement.TryGetProperty("args", out _));
    }

    [Fact]
    public void TryParseSkillContent_YamlCompositionDuplicateSkillExecAlias_IsRejected()
    {
        var content = """
            ---
            name: duplicate-skill-exec-field
            description: Duplicate skill_exec field spellings are rejected
            kind: meta
            composition:
              steps:
                - id: delegate
                  kind: skill_exec
                  skill: web-research
                  args: ["canonical"]
                  skill_exec_args: ["legacy"]
            ---
            Instructions.
            """;

        var ok = SkillLoader.TryParseSkillContent(content, "/skills/duplicate-skill-exec-field", SkillSource.Workspace, out _, out var errorCode);

        Assert.False(ok);
        Assert.Equal("invalid_skill_exec", errorCode);
    }

    [Fact]
    public void ParseSkillContent_YamlCompositionUppercaseNull_PreservesLegacyInference()
    {
        var content = """
            ---
            name: uppercase-null
            description: Uppercase null keeps legacy scalar inference
            kind: meta
            composition:
              steps:
                - id: s1
                  kind: llm_chat
                  with:
                    value: NULL
            ---
            Instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/uppercase-null", SkillSource.Workspace);

        Assert.NotNull(skill);
        var step = Assert.Single(skill!.Composition!.Steps);
        using var withDoc = JsonDocument.Parse(step.WithJson!);
        Assert.Equal(JsonValueKind.Null, withDoc.RootElement.GetProperty("value").ValueKind);
    }

    [Fact]
    public void ParseSkillContent_YamlBlockBeyondDepthLimit_FailsGracefully()
    {
        var nesting = 80; // deeper than the conversion depth limit
        var nestedLines = Enumerable.Range(0, nesting)
            .Select(i => new string(' ', i * 2) + "n:")
            .Append(new string(' ', nesting * 2) + "leaf");
        var block = string.Join('\n', nestedLines.Select(line => "        " + line));

        var content = $"""
            ---
            name: deep-yaml
            description: YAML nested deeper than the conversion limit
            kind: meta
            composition:
            {block}
            ---
            Instructions.
            """;

        var ok = SkillLoader.TryParseSkillContent(content, "/skills/deep-yaml", SkillSource.Workspace, out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void ParseSkillContent_YamlSelfReferencingAnchor_FailsGracefully()
    {
        var content = """
            ---
            name: cyclic-yaml
            description: Self-referencing YAML anchor
            kind: meta
            composition:
              steps: &steps
                - id: cycle
                  kind: llm_chat
                  with:
                    again: *steps
            ---
            Instructions.
            """;

        var ok = SkillLoader.TryParseSkillContent(content, "/skills/cyclic-yaml", SkillSource.Workspace, out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void ParseSkillContent_MetaOpenSquillaDslFields_ParsesSuccessfully()
    {
        var content = """
            ---
            name: meta-opensquilla-native
            description: Native DSL fields
            kind: meta
            composition: {"tool_args":{"trace_id":"{{ input }}","format":"json"},"steps":[{"id":"collect","kind":"user_input","clarify":{"mode":"form","extract_natural_language":true,"fields":[{"name":"topic","type":"string","required":true,"min_length":3,"max_length":32,"default":"bugs"},{"name":"size","type":"integer","min":1,"max":5,"default":3},{"name":"priority","type":"enum","options":["low","medium","high"],"default":"medium"}],"cancel_words":["cancel"],"timeout_seconds":30},"route":[{"when":"outputs.collect == 'bug'","to":"bug_branch"},{"to":"default_branch"}]},{"id":"bug_branch","kind":"tool_call","tool":"dispatch_bug","tool_allowlist":["dispatch_bug"],"tool_args":{"ticket":"{{ outputs.collect }}"},"output_choices":["accepted","rejected"]},{"id":"default_branch","kind":"llm_chat","when":"outputs.collect != ''"}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-opensquilla-native", SkillSource.Workspace);

        Assert.NotNull(skill);
        Assert.Equal("{\"trace_id\":\"{{ input }}\",\"format\":\"json\"}", skill!.Composition!.ToolArgsJson);

        var collect = skill.Composition.Steps[0];
        Assert.Equal("form", collect.Clarify!.Mode);
        Assert.True(collect.Clarify.ExtractNaturalLanguage);
        Assert.Equal(["cancel"], collect.Clarify.CancelWords);
        Assert.Equal(30, collect.Clarify.TimeoutSeconds);
        Assert.Equal(3, collect.Clarify.Fields.Count);
        Assert.Equal("topic", collect.Clarify.Fields[0].Name);
        Assert.Equal("string", collect.Clarify.Fields[0].Type);
        Assert.True(collect.Clarify.Fields[0].Required);
        Assert.Equal(3, collect.Clarify.Fields[0].MinLength);
        Assert.Equal(32, collect.Clarify.Fields[0].MaxLength);
        Assert.Equal("size", collect.Clarify.Fields[1].Name);
        Assert.Equal("integer", collect.Clarify.Fields[1].Type);
        Assert.Equal(1, collect.Clarify.Fields[1].Min);
        Assert.Equal(5, collect.Clarify.Fields[1].Max);
        Assert.Equal(3, collect.Clarify.Fields[1].DefaultValue?.GetInt32());
        Assert.Equal("priority", collect.Clarify.Fields[2].Name);
        Assert.Equal("enum", collect.Clarify.Fields[2].Type);
        Assert.Equal(["low", "medium", "high"], collect.Clarify.Fields[2].Options);
        Assert.Equal("medium", collect.Clarify.Fields[2].DefaultValue?.GetString());
        Assert.True(collect.Routes.Count == 2);
        Assert.Equal("outputs.collect == 'bug'", collect.Routes[0].When);
        Assert.Equal("bug_branch", collect.Routes[0].To);
        Assert.Null(collect.Routes[1].When);
        Assert.Equal("default_branch", collect.Routes[1].To);

        var bugBranch = skill.Composition.Steps[1];
        Assert.Equal("{\"ticket\":\"{{ outputs.collect }}\"}", bugBranch.ToolArgsJson);
        Assert.Equal(["dispatch_bug"], bugBranch.ToolAllowlist);
        Assert.Equal(["accepted", "rejected"], bugBranch.OutputChoices);

        Assert.Equal("outputs.collect != ''", skill.Composition.Steps[2].When);
    }

    [Fact]
    public void ParseSkillContent_MetaClarifySkipIf_ParsesSuccessfully()
    {
        var content = """
            ---
            name: meta-clarify-skip-if
            description: Clarify skip_if support
            kind: meta
            composition: {"steps":[{"id":"collect","kind":"user_input","clarify":{"mode":"chat","skip_if":"inputs.user_message == ''"}}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-clarify-skip-if", SkillSource.Workspace);

        Assert.NotNull(skill);
        var clarify = skill!.Composition!.Steps[0].Clarify;
        Assert.NotNull(clarify);
        Assert.Equal("inputs.user_message == ''", clarify!.SkipIf);
    }

    [Fact]
    public void ParseSkillContent_MetaWithClarifyCompatibility_ParsesSuccessfully()
    {
        var content = """
            ---
            name: meta-with-clarify-compat
            description: Legacy with.clarify remains supported
            kind: meta
            composition: {"steps":[{"id":"collect","kind":"user_input","with":{"clarify":{"mode":"form","fields":[{"name":"topic","type":"string","required":true}]}}}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-with-clarify-compat", SkillSource.Workspace);

        Assert.NotNull(skill);
        var clarify = skill!.Composition!.Steps[0].Clarify;
        Assert.NotNull(clarify);
        Assert.Equal("form", clarify!.Mode);
        Assert.Single(clarify.Fields);
        Assert.Equal("topic", clarify.Fields[0].Name);
    }

    [Fact]
    public void ParseSkillContent_MetaStepClarifyOverridesWithClarify_ParsesSuccessfully()
    {
        var content = """
            ---
            name: meta-step-clarify-wins
            description: Step clarify wins over with.clarify
            kind: meta
            composition: {"steps":[{"id":"collect","kind":"user_input","clarify":{"mode":"chat"},"with":{"clarify":{"mode":"form","fields":[{"name":"topic","type":"string"}]}}}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-step-clarify-wins", SkillSource.Workspace);

        Assert.NotNull(skill);
        var clarify = skill!.Composition!.Steps[0].Clarify;
        Assert.NotNull(clarify);
        Assert.Equal("chat", clarify!.Mode);
        Assert.Empty(clarify.Fields);
    }

    [Fact]
    public void ParseSkillContent_MetaRouteArrayWithMissingTarget_ReturnsNull()
    {
        var content = """
            ---
            name: meta-route-missing-target
            description: Invalid route array target
            kind: meta
            composition: {"steps":[{"id":"collect","kind":"user_input","route":[{"to":"missing"}]},{"id":"next","kind":"llm_chat"}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-route-missing-target", SkillSource.Workspace);

        Assert.Null(skill);
    }

    [Fact]
    public void TryParseSkillContent_MetaToolCallWithSkill_ReturnsDiagnosticCode()
    {
        var content = """
            ---
            name: meta-tool-call-invalid-diag
            description: Invalid tool_call step
            kind: meta
            composition: {"steps":[{"id":"t1","kind":"tool_call","tool":"search","skill":"web-research"}]}
            ---
            Meta instructions.
            """;

        var ok = SkillLoader.TryParseSkillContent(content, "/skills/meta-tool-call-invalid-diag", SkillSource.Workspace, out var skill, out var errorCode);

        Assert.False(ok);
        Assert.Null(skill);
        Assert.Equal("invalid_step_kind_fields", errorCode);
    }

    [Fact]
    public void TryParseSkillContent_MetaInvalidFinalTextMode_ReturnsDiagnosticCode()
    {
        var content = """
            ---
            name: meta-invalid-final-mode-diag
            description: Invalid final text mode
            kind: meta
            final_text_mode: latest
            composition: {"steps":[{"id":"s1","kind":"agent","skill":"web-research"}]}
            ---
            Meta instructions.
            """;

        var ok = SkillLoader.TryParseSkillContent(content, "/skills/meta-invalid-final-mode-diag", SkillSource.Workspace, out var skill, out var errorCode);

        Assert.False(ok);
        Assert.Null(skill);
        Assert.Equal("invalid_final_text_mode", errorCode);
    }

    [Fact]
    public void TryParseSkillContent_MetaDependencyCycle_ReturnsDiagnosticCode()
    {
        var content = """
            ---
            name: meta-cycle-diag
            description: Invalid cycle
            kind: meta
            composition: {"steps":[{"id":"a","kind":"agent","skill":"one","depends_on":["b"]},{"id":"b","kind":"agent","skill":"two","depends_on":["a"]}]}
            ---
            Meta instructions.
            """;

        var ok = SkillLoader.TryParseSkillContent(content, "/skills/meta-cycle-diag", SkillSource.Workspace, out var skill, out var errorCode);

        Assert.False(ok);
        Assert.Null(skill);
        Assert.Equal("dependency_cycle", errorCode);
    }

    [Fact]
    public void TryParseSkillContent_MetaStepWithNonObject_ReturnsDiagnosticCode()
    {
        var content = """
            ---
            name: meta-with-non-object-diag
            description: Invalid step with payload
            kind: meta
            composition: {"steps":[{"id":"chat","kind":"llm_chat","with":"plain text"}]}
            ---
            Meta instructions.
            """;

        var ok = SkillLoader.TryParseSkillContent(content, "/skills/meta-with-non-object-diag", SkillSource.Workspace, out var skill, out var errorCode);

        Assert.False(ok);
        Assert.Null(skill);
        Assert.Equal("invalid_with_payload", errorCode);
    }

    [Theory]
    [InlineData("{\"tool_args\":[],\"steps\":[{\"id\":\"call\",\"kind\":\"tool_call\",\"tool\":\"search\"}]}", "invalid_tool_args")]
    [InlineData("{\"steps\":[{\"id\":\"call\",\"kind\":\"tool_call\",\"tool\":\"search\",\"tool_args\":[]}]} ", "invalid_tool_args")]
    [InlineData("{\"steps\":[{\"id\":\"chat\",\"kind\":\"llm_chat\",\"tool_args\":{\"q\":\"x\"}}]}", "invalid_tool_args")]
    [InlineData("{\"steps\":[{\"id\":\"call\",\"kind\":\"tool_call\",\"tool\":\"search\",\"tool_allowlist\":\"search\"}]}", "invalid_tool_allowlist")]
    [InlineData("{\"steps\":[{\"id\":\"call\",\"kind\":\"tool_call\",\"tool\":\"search\",\"tool_allowlist\":[]}]} ", "invalid_tool_allowlist")]
    [InlineData("{\"steps\":[{\"id\":\"chat\",\"kind\":\"llm_chat\",\"tool_allowlist\":[\"search\"]}]}", "invalid_tool_allowlist")]
    [InlineData("{\"steps\":[{\"id\":\"call\",\"kind\":\"tool_call\",\"tool\":\"search\",\"output_choices\":\"accepted\"}]}", "invalid_output_choices")]
    [InlineData("{\"steps\":[{\"id\":\"call\",\"kind\":\"tool_call\",\"tool\":\"search\",\"output_choices\":[]}]} ", "invalid_output_choices")]
    [InlineData("{\"steps\":[{\"id\":\"ask\",\"kind\":\"user_input\",\"output_choices\":[\"accepted\"]}]}", "invalid_output_choices")]
    [InlineData("{\"steps\":[{\"id\":\"chat\",\"kind\":\"llm_chat\",\"when\":1}]}", "invalid_when_expression")]
    [InlineData("{\"steps\":[{\"id\":\"chat\",\"kind\":\"llm_chat\",\"when\":\"   \"}]}", "invalid_when_expression")]
    [InlineData("{\"steps\":[{\"id\":\"ask\",\"kind\":\"user_input\",\"clarify\":[]}]} ", "invalid_clarify_schema")]
    [InlineData("{\"steps\":[{\"id\":\"ask\",\"kind\":\"user_input\",\"clarify\":{\"mode\":\"modal\"}}]}", "invalid_clarify_schema")]
    [InlineData("{\"steps\":[{\"id\":\"ask\",\"kind\":\"user_input\",\"clarify\":{\"extract_natural_language\":\"yes\"}}]}", "invalid_clarify_schema")]
    [InlineData("{\"steps\":[{\"id\":\"ask\",\"kind\":\"user_input\",\"clarify\":{\"mode\":\"form\",\"fields\":[]}}]}", "invalid_clarify_schema")]
    [InlineData("{\"steps\":[{\"id\":\"ask\",\"kind\":\"user_input\",\"clarify\":{\"mode\":\"form\",\"fields\":\"topic\"}}]}", "invalid_clarify_schema")]
    [InlineData("{\"steps\":[{\"id\":\"ask\",\"kind\":\"user_input\",\"clarify\":{\"fields\":[{\"name\":\"topic\",\"type\":\"bogus\"}]}}]}", "invalid_clarify_schema")]
    [InlineData("{\"steps\":[{\"id\":\"ask\",\"kind\":\"user_input\",\"clarify\":{\"fields\":[{\"name\":\"topic\",\"type\":\"string\",\"required\":\"yes\"}]}}]}", "invalid_clarify_schema")]
    [InlineData("{\"steps\":[{\"id\":\"ask\",\"kind\":\"user_input\",\"clarify\":{\"fields\":[{\"name\":\"topic\",\"type\":\"string\",\"min_length\":10,\"max_length\":3}]}}]}", "invalid_clarify_schema")]
    [InlineData("{\"steps\":[{\"id\":\"ask\",\"kind\":\"user_input\",\"clarify\":{\"fields\":[{\"name\":\"topic\",\"type\":\"string\"},{\"name\":\"topic\",\"type\":\"string\"}]}}]}", "invalid_clarify_schema")]
    [InlineData("{\"steps\":[{\"id\":\"ask\",\"kind\":\"user_input\",\"clarify\":{\"fields\":[{\"name\":\"priority\",\"type\":\"enum\",\"options\":[\"low\",\"medium\"],\"default\":\"high\"}]}}]}", "invalid_clarify_schema")]
    [InlineData("{\"steps\":[{\"id\":\"ask\",\"kind\":\"user_input\",\"clarify\":{\"fields\":[{\"name\":\"count\",\"type\":\"integer\",\"default\":\"1\"}]}}]}", "invalid_clarify_schema")]
    [InlineData("{\"steps\":[{\"id\":\"ask\",\"kind\":\"user_input\",\"clarify\":{\"fields\":[{\"name\":\"count\",\"type\":\"integer\",\"min_length\":3}]}}]}", "invalid_clarify_schema")]
    [InlineData("{\"steps\":[{\"id\":\"ask\",\"kind\":\"user_input\",\"clarify\":{\"fields\":[{\"name\":\"topic\",\"type\":\"string\",\"min\":1}]}}]}", "invalid_clarify_schema")]
    [InlineData("{\"steps\":[{\"id\":\"route\",\"kind\":\"agent\",\"skill\":\"triage\",\"route\":{\"to\":\"next\"}},{\"id\":\"next\",\"kind\":\"llm_chat\"}]}", "invalid_route")]
    [InlineData("{\"steps\":[{\"id\":\"route\",\"kind\":\"agent\",\"skill\":\"triage\",\"route\":[]},{\"id\":\"next\",\"kind\":\"llm_chat\"}]}", "invalid_route")]
    [InlineData("{\"steps\":[{\"id\":\"route\",\"kind\":\"agent\",\"skill\":\"triage\",\"route\":[{\"when\":1,\"to\":\"next\"}]},{\"id\":\"next\",\"kind\":\"llm_chat\"}]}", "invalid_when_expression")]
    [InlineData("{\"steps\":[{\"id\":\"route\",\"kind\":\"agent\",\"skill\":\"triage\",\"route\":[{\"when\":\"   \",\"to\":\"next\"}]},{\"id\":\"next\",\"kind\":\"llm_chat\"}]}", "invalid_when_expression")]
    [InlineData("{\"steps\":[{\"id\":\"route\",\"kind\":\"agent\",\"skill\":\"triage\",\"route\":[{\"to\":\"missing\"}]},{\"id\":\"next\",\"kind\":\"llm_chat\"}]}", "invalid_route_target")]
    [InlineData("{\"steps\":[{\"id\":\"route\",\"kind\":\"agent\",\"skill\":\"triage\",\"route\":[{\"to\":\"route\"}]},{\"id\":\"next\",\"kind\":\"llm_chat\"}]}", "invalid_route_scope")]
    [InlineData("{\"steps\":[{\"id\":\"route\",\"kind\":\"agent\",\"skill\":\"triage\",\"route\":[{\"to\":\"first\"},{\"to\":\"second\"}]},{\"id\":\"first\",\"kind\":\"llm_chat\"},{\"id\":\"second\",\"kind\":\"llm_chat\"}]}", "invalid_route_fallback")]
    [InlineData("{\"steps\":[{\"id\":\"route\",\"kind\":\"agent\",\"skill\":\"triage\",\"route\":[{\"to\":\"default\"},{\"when\":\"input == 'x'\",\"to\":\"specific\"}]},{\"id\":\"default\",\"kind\":\"llm_chat\"},{\"id\":\"specific\",\"kind\":\"llm_chat\"}]}", "invalid_route_fallback")]
    [InlineData("{\"steps\":[{\"id\":\"classify\",\"kind\":\"llm_classify\",\"with\":{\"options\":[\"ok\"],\"route\":{\"ok\":\"next\"}},\"route\":[{\"to\":\"next\"}]},{\"id\":\"next\",\"kind\":\"llm_chat\"}]}", "invalid_route")]
    public void TryParseSkillContent_MetaNativeDslDiagnostics_ReturnsExpectedCode(string compositionJson, string expectedError)
    {
        var content = $$"""
            ---
            name: meta-native-diagnostics
            description: Invalid native fields
            kind: meta
            composition: {{compositionJson}}
            ---
            Meta instructions.
            """;

        var ok = SkillLoader.TryParseSkillContent(content, "/skills/meta-native-diagnostics", SkillSource.Workspace, out var skill, out var errorCode);

        Assert.False(ok);
        Assert.Null(skill);
        Assert.Equal(expectedError, errorCode);
    }

    [Theory]
    [InlineData("{\"steps\":[{\"id\":\"call\",\"kind\":\"tool_call\",\"tool\":\"unstable\",\"timeout_seconds\":0}]}", "invalid_step_timeout")]
    [InlineData("{\"steps\":[{\"id\":\"call\",\"kind\":\"tool_call\",\"tool\":\"unstable\",\"retry\":{\"max_attempts\":0}}]}", "invalid_step_retry")]
    [InlineData("{\"steps\":[{\"id\":\"call\",\"kind\":\"tool_call\",\"tool\":\"unstable\",\"retry\":{\"max_attempt\":3}}]}", "invalid_step_retry")]
    [InlineData("{\"steps\":[{\"id\":\"draft\",\"kind\":\"llm_chat\",\"output_contract\":{\"format\":\"xml\"}}]}", "invalid_output_contract")]
    [InlineData("{\"steps\":[{\"id\":\"draft\",\"kind\":\"llm_chat\",\"output_contract\":{\"required_properties\":[\"answer\"]}}]}", "invalid_output_contract")]
    [InlineData("{\"steps\":[{\"id\":\"draft\",\"kind\":\"llm_chat\",\"output_contract\":{\"formats\":\"json\"}}]}", "invalid_output_contract")]
    [InlineData("{\"steps\":[{\"id\":\"primary\",\"kind\":\"tool_call\",\"tool\":\"unstable\",\"on_failure\":\"   \"}]}", "invalid_on_failure")]
    [InlineData("{\"steps\":[{\"id\":\"primary\",\"kind\":\"tool_call\",\"tool\":\"unstable\",\"on_failure\":\"fallback\"},{\"id\":\"fallback\",\"kind\":\"tool_call\",\"tool\":\"safe\",\"depends_on\":[\"primary\"]}]}", "invalid_on_failure")]
    [InlineData("{\"steps\":[{\"id\":\"primary\",\"kind\":\"tool_call\",\"tool\":\"unstable\",\"on_failure\":\"fallback\"},{\"id\":\"fallback\",\"kind\":\"tool_call\",\"tool\":\"safe\"},{\"id\":\"router\",\"kind\":\"agent\",\"skill\":\"triage\",\"route\":[{\"to\":\"fallback\"}]}]}", "invalid_on_failure")]
    [InlineData("{\"steps\":[{\"id\":\"primary\",\"kind\":\"tool_call\",\"tool\":\"unstable\",\"on_failure\":\"fallback\"},{\"id\":\"fallback\",\"kind\":\"tool_call\",\"tool\":\"safe\"},{\"id\":\"classify\",\"kind\":\"llm_classify\",\"with\":{\"options\":[\"ok\"],\"route\":{\"ok\":\"fallback\"}}}]}", "invalid_on_failure")]
    // Rule 1: on_failure target must exist
    [InlineData("{\"steps\":[{\"id\":\"primary\",\"kind\":\"tool_call\",\"tool\":\"unstable\",\"on_failure\":\"ghost\"}]}", "invalid_on_failure")]
    // Rule 2: step cannot self-reference via on_failure
    [InlineData("{\"steps\":[{\"id\":\"primary\",\"kind\":\"tool_call\",\"tool\":\"unstable\",\"on_failure\":\"primary\"}]}", "invalid_on_failure")]
    // Rule 3: substitute step cannot have on_failure (no chain)
    [InlineData("{\"steps\":[{\"id\":\"a\",\"kind\":\"tool_call\",\"tool\":\"unstable\",\"on_failure\":\"b\"},{\"id\":\"b\",\"kind\":\"tool_call\",\"tool\":\"unstable\",\"on_failure\":\"c\"},{\"id\":\"c\",\"kind\":\"tool_call\",\"tool\":\"safe\"}]}", "invalid_on_failure")]
    // Rule 4: fallback step cannot be shared by multiple primaries
    [InlineData("{\"steps\":[{\"id\":\"a\",\"kind\":\"tool_call\",\"tool\":\"unstable\",\"on_failure\":\"fallback\"},{\"id\":\"b\",\"kind\":\"tool_call\",\"tool\":\"unstable\",\"on_failure\":\"fallback\"},{\"id\":\"fallback\",\"kind\":\"tool_call\",\"tool\":\"safe\"}]}", "invalid_on_failure")]
    [InlineData("{\"steps\":[{\"id\":\"route\",\"kind\":\"agent\",\"skill\":\"triage\",\"route\":[{\"target\":\"next\",\"to\":\"next\"}]},{\"id\":\"next\",\"kind\":\"llm_chat\"}]}", "invalid_route")]
    public void TryParseSkillContent_MetaStepValidationDiagnostics_ReturnExpectedCode(string compositionJson, string expectedError)
    {
        var content = $$"""
            ---
            name: meta-step-validation-diagnostics
            description: Invalid step validation fields
            kind: meta
            composition: {{compositionJson}}
            ---
            Meta instructions.
            """;

        var ok = SkillLoader.TryParseSkillContent(content, "/skills/meta-step-validation-diagnostics", SkillSource.Workspace, out var skill, out var errorCode);

        Assert.False(ok);
        Assert.Null(skill);
        Assert.Equal(expectedError, errorCode);
    }

    [Theory]
    [InlineData("{\"steps\":[{\"id\":\"q\",\"kind\":\"tool_call\",\"tool\":\"u\",\"capability_ref\":\"static\"}]}", "invalid_capability_ref")]
    [InlineData("{\"steps\":[{\"id\":\"q\",\"kind\":\"tool_call\",\"tool\":\"u\",\"capability_ref\":{\"binding\":\"quantum\"}}]}", "invalid_capability_ref")]
    [InlineData("{\"steps\":[{\"id\":\"q\",\"kind\":\"tool_call\",\"tool\":\"u\",\"capability_ref\":{\"binding\":\"static\"}}]}", "invalid_capability_ref")]
    [InlineData("{\"steps\":[{\"id\":\"q\",\"kind\":\"tool_call\",\"tool\":\"u\",\"capability_ref\":{\"binding\":\"static\",\"static\":{\"tool_name\":\"get_weather\"}}}]}", "invalid_capability_ref")]
    [InlineData("{\"steps\":[{\"id\":\"q\",\"kind\":\"tool_call\",\"tool\":\"u\",\"capability_ref\":{\"binding\":\"static\",\"static\":{\"mcp_server_name\":\"weather-mcp\"}}}]}", "invalid_capability_ref")]
    [InlineData("{\"steps\":[{\"id\":\"q\",\"kind\":\"tool_call\",\"tool\":\"u\",\"capability_ref\":{\"binding\":\"dynamic\"}}]}", "invalid_capability_ref")]
    [InlineData("{\"steps\":[{\"id\":\"q\",\"kind\":\"tool_call\",\"tool\":\"u\",\"capability_ref\":{\"binding\":\"dynamic\",\"intent\":{\"keywords\":[\"weather\"]}}}]}", "invalid_capability_ref")]
    [InlineData("{\"steps\":[{\"id\":\"q\",\"kind\":\"tool_call\",\"tool\":\"u\",\"capability_ref\":{\"binding\":\"dynamic\",\"intent\":{\"task_description\":\"weather\"},\"selection_policy\":\"nearest\"}}]}", "invalid_capability_ref")]
    [InlineData("{\"steps\":[{\"id\":\"q\",\"kind\":\"tool_call\",\"tool\":\"u\",\"capability_ref\":{\"binding\":\"static\",\"static\":{\"mcp_server_name\":\"w\",\"tool_name\":\"t\"},\"top_k\":5}}]}", "capabilityref_reserved_field")]
    [InlineData("{\"steps\":[{\"id\":\"q\",\"kind\":\"tool_call\",\"tool\":\"u\",\"capability_ref\":{\"binding\":\"static\",\"static\":{\"mcp_server_name\":\"w\",\"tool_name\":\"t\"},\"prefer_version\":\">=1.0.0\"}}]}", "capabilityref_reserved_field")]
    // capability_ref must not coexist with tool:
    [InlineData("{\"steps\":[{\"id\":\"q\",\"kind\":\"tool_call\",\"tool\":\"u\",\"capability_ref\":{\"binding\":\"static\",\"static\":{\"mcp_server_name\":\"w\",\"tool_name\":\"t\"}}}]}", "invalid_capability_ref")]
    // capability_ref is only legal on tool_call steps:
    [InlineData("{\"steps\":[{\"id\":\"q\",\"kind\":\"llm_chat\",\"capability_ref\":{\"binding\":\"static\",\"static\":{\"mcp_server_name\":\"w\",\"tool_name\":\"t\"}}}]}", "invalid_capability_ref")]
    // fallback must not coexist with step on_failure:
    [InlineData("{\"steps\":[{\"id\":\"q\",\"kind\":\"tool_call\",\"tool\":\"u\",\"on_failure\":\"fb\",\"capability_ref\":{\"binding\":\"static\",\"static\":{\"mcp_server_name\":\"w\",\"tool_name\":\"t\"},\"fallback\":\"fb\"}},{\"id\":\"fb\",\"kind\":\"tool_call\",\"tool\":\"safe\"}]}", "invalid_capability_ref")]
    // capability_ref.fallback obeys the on_failure five constraints (ghost target):
    [InlineData("{\"steps\":[{\"id\":\"q\",\"kind\":\"tool_call\",\"capability_ref\":{\"binding\":\"static\",\"static\":{\"mcp_server_name\":\"w\",\"tool_name\":\"t\"},\"fallback\":\"ghost\"}}]}", "invalid_on_failure")]
    public void TryParseSkillContent_CapabilityRefValidation_ReturnsExpectedCode(string compositionJson, string expectedError)
    {
        var content = $$"""
            ---
            name: capability-ref-validation
            description: Capability slot validation
            kind: meta
            composition: {{compositionJson}}
            ---
            Meta instructions.
            """;

        var ok = SkillLoader.TryParseSkillContent(content, "/skills/capability-ref-validation", SkillSource.Workspace, out var skill, out var errorCode);

        Assert.False(ok);
        Assert.Null(skill);
        Assert.Equal(expectedError, errorCode);
    }

    [Fact]
    public void TryParseSkillContent_CapabilityRefStatic_ParsesPinnedBinding()
    {
        var content = """
            ---
            name: capability-ref-static
            description: Static capability slot
            kind: meta
            composition: {"steps":[{"id":"q","kind":"tool_call","capability_ref":{"binding":"static","static":{"mcp_server_name":"weather-mcp","tool_name":"get_weather"}},"tool_args":{"city":"{{ input }}"}}]}
            ---
            Meta instructions.
            """;

        var ok = SkillLoader.TryParseSkillContent(content, "/skills/capability-ref-static", SkillSource.Workspace, out var skill, out var errorCode);

        Assert.True(ok, errorCode);
        var slot = skill!.Composition!.Steps[0];
        Assert.Null(slot.Tool);
        Assert.NotNull(slot.CapabilityRef);
        Assert.Equal("static", slot.CapabilityRef.Binding);
        Assert.Equal("weather-mcp", slot.CapabilityRef.Static!.Target);
        Assert.Equal("get_weather", slot.CapabilityRef.Static.ToolName);
        Assert.Equal("first", slot.CapabilityRef.SelectionPolicy);
    }

    [Fact]
    public void TryParseSkillContent_CapabilityRefDynamic_ParsesIntentAndPolicy()
    {
        var content = """
            ---
            name: capability-ref-dynamic
            description: Dynamic capability slot
            kind: meta
            composition: {"steps":[{"id":"q","kind":"tool_call","capability_ref":{"binding":"dynamic","intent":{"type":"cap:WeatherQuery","task_description":"查询指定城市的天气","keywords":["天气","city"]},"selection_policy":"exact_name"}}]}
            ---
            Meta instructions.
            """;

        var ok = SkillLoader.TryParseSkillContent(content, "/skills/capability-ref-dynamic", SkillSource.Workspace, out var skill, out var errorCode);

        Assert.True(ok, errorCode);
        var slot = skill!.Composition!.Steps[0];
        Assert.NotNull(slot.CapabilityRef);
        Assert.Equal("dynamic", slot.CapabilityRef.Binding);
        Assert.Null(slot.CapabilityRef.Static);
        Assert.Equal("cap:WeatherQuery", slot.CapabilityRef.Intent!.Type);
        Assert.Equal("查询指定城市的天气", slot.CapabilityRef.Intent.TaskDescription);
        Assert.Equal(new[] { "天气", "city" }, slot.CapabilityRef.Intent.Keywords);
        Assert.Equal("exact_name", slot.CapabilityRef.SelectionPolicy);
    }

    [Fact]
    public void TryParseSkillContent_CapabilityRefFallback_FoldsIntoOnFailure()
    {
        var content = """
            ---
            name: capability-ref-fallback
            description: Capability slot with fallback
            kind: meta
            composition: {"steps":[{"id":"q","kind":"tool_call","capability_ref":{"binding":"static","static":{"mcp_server_name":"weather-mcp","tool_name":"get_weather"},"fallback":"fb"}},{"id":"fb","kind":"tool_call","tool":"emit_text","tool_args":{"text":"unavailable"}}]}
            ---
            Meta instructions.
            """;

        var ok = SkillLoader.TryParseSkillContent(content, "/skills/capability-ref-fallback", SkillSource.Workspace, out var skill, out var errorCode);

        Assert.True(ok, errorCode);
        var slot = skill!.Composition!.Steps[0];
        Assert.Equal("fb", slot.CapabilityRef!.Fallback);
        // Folded into OnFailure so the existing failure-branch machinery validates
        // and routes it (same five constraints as on_failure).
        Assert.Equal("fb", slot.OnFailure);
    }

    [Fact]
    public void ParseSkillContent_MetaClassifyLegacyRouteObjectMap_ParsesSuccessfully()
    {
        var content = """
            ---
            name: meta-classify-route-map
            description: Legacy classify route object map remains supported
            kind: meta
            composition: {"steps":[{"id":"classify","kind":"llm_classify","with":{"options":["ok","retry"],"route":{"ok":"next","retry":["fallback"]}}},{"id":"next","kind":"llm_chat"},{"id":"fallback","kind":"llm_chat"}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-classify-route-map", SkillSource.Workspace);

        Assert.NotNull(skill);
        Assert.Empty(skill!.Composition!.Steps[0].Routes);
        Assert.Contains("\"route\":{\"ok\":\"next\",\"retry\":[\"fallback\"]}", skill.Composition.Steps[0].WithJson);
    }

    [Fact]
    public void ParseSkillContent_MetaWithoutComposition_ReturnsNull()
    {
        var content = """
            ---
            name: meta-missing-composition
            description: Invalid meta
            kind: meta
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-missing", SkillSource.Workspace);

        Assert.Null(skill);
    }

    [Fact]
    public void ParseSkillContent_MetaClassifyWithoutOptions_ReturnsNull()
    {
        var content = """
            ---
            name: meta-invalid-classify
            description: Invalid classify step
            kind: meta
            composition: {"steps":[{"id":"classify","kind":"llm_classify","with":{"route":{"ok":"next"}}},{"id":"next","kind":"agent","skill":"web-research","depends_on":["classify"]}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-invalid-classify", SkillSource.Workspace);

        Assert.Null(skill);
    }

    [Fact]
    public void ParseSkillContent_MetaStepWithNonObject_ReturnsNull()
    {
        var content = """
            ---
            name: meta-with-non-object
            description: Invalid step with payload
            kind: meta
            composition: {"steps":[{"id":"chat","kind":"llm_chat","with":"plain text"}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-with-non-object", SkillSource.Workspace);

        Assert.Null(skill);
    }

    [Fact]
    public void ParseSkillContent_MetaStepWithObject_ParsesSuccessfully()
    {
        var content = """
            ---
            name: meta-with-object
            description: Valid step with payload
            kind: meta
            composition: {"steps":[{"id":"chat","kind":"llm_chat","with":{"prompt":"hello"}}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-with-object", SkillSource.Workspace);

        Assert.NotNull(skill);
        Assert.Equal("{\"prompt\":\"hello\"}", skill!.Composition!.Steps[0].WithJson);
    }

    [Fact]
    public void ParseSkillContent_MetaSkillExecWithoutSkill_ReturnsNull()
    {
        var content = """
            ---
            name: meta-skill-exec-missing
            description: Invalid skill_exec step
            kind: meta
            composition: {"steps":[{"id":"delegate","kind":"skill_exec"}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-skill-exec-missing", SkillSource.Workspace);

        Assert.Null(skill);
    }

    [Fact]
    public void ParseSkillContent_MetaSkillExecWithoutEntrypoint_ReturnsNull()
    {
        var content = """
            ---
            name: meta-skill-exec-missing-entrypoint
            description: Invalid skill_exec step without entrypoint
            kind: meta
            composition: {"steps":[{"id":"delegate","kind":"skill_exec","skill":"web-research"}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-skill-exec-missing-entrypoint", SkillSource.Workspace);

        Assert.Null(skill);
    }

    [Fact]
    public void ParseSkillContent_MetaSkillExecWithSkill_ParsesSuccessfully()
    {
        var content = """
            ---
            name: meta-skill-exec-valid
            description: Valid skill_exec step
            kind: meta
            composition: {"steps":[{"id":"delegate","kind":"skill_exec","skill":"web-research","entrypoint":"report","args":["--format","json"],"stdin":"{{ input }}","cwd":"artifacts/meta","parse_mode":"json"}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-skill-exec-valid", SkillSource.Workspace);

        Assert.NotNull(skill);
        Assert.Equal("web-research", skill!.Composition!.Steps[0].Skill);
        Assert.Equal("report", skill.Composition.Steps[0].SkillExecEntrypoint);
        Assert.Equal(["--format", "json"], skill.Composition.Steps[0].SkillExecArgs);
        Assert.Equal("{{ input }}", skill.Composition.Steps[0].SkillExecStdin);
        Assert.Equal("artifacts/meta", skill.Composition.Steps[0].SkillExecCwd);
        Assert.Equal("json", skill.Composition.Steps[0].SkillExecParseMode);
    }

    [Fact]
    public void ParseSkillContent_MetaToolCallWithSkill_ReturnsNull()
    {
        var content = """
            ---
            name: meta-tool-call-invalid
            description: Invalid tool_call step
            kind: meta
            composition: {"steps":[{"id":"t1","kind":"tool_call","tool":"search","skill":"web-research"}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-tool-call-invalid", SkillSource.Workspace);

        Assert.Null(skill);
    }

    [Fact]
    public void ParseSkillContent_MetaToolCallWithoutSkill_ParsesSuccessfully()
    {
        var content = """
            ---
            name: meta-tool-call-valid
            description: Valid tool_call step
            kind: meta
            composition: {"steps":[{"id":"t1","kind":"tool_call","tool":"search"}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-tool-call-valid", SkillSource.Workspace);

        Assert.NotNull(skill);
        Assert.Equal("search", skill!.Composition!.Steps[0].Tool);
    }

    [Fact]
    public void ParseSkillContent_MetaSkillExecWithTool_ReturnsNull()
    {
        var content = """
            ---
            name: meta-skill-exec-with-tool
            description: Invalid skill_exec step with tool
            kind: meta
            composition: {"steps":[{"id":"delegate","kind":"skill_exec","skill":"web-research","tool":"search"}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-skill-exec-with-tool", SkillSource.Workspace);

        Assert.Null(skill);
    }

    [Fact]
    public void ParseSkillContent_MetaSkillExecWithoutTool_ParsesSuccessfully()
    {
        var content = """
            ---
            name: meta-skill-exec-without-tool
            description: Valid skill_exec step without tool
            kind: meta
            composition: {"steps":[{"id":"delegate","kind":"skill_exec","skill":"web-research","entrypoint":"report"}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-skill-exec-without-tool", SkillSource.Workspace);

        Assert.NotNull(skill);
        Assert.Equal("web-research", skill!.Composition!.Steps[0].Skill);
        Assert.Equal("report", skill.Composition.Steps[0].SkillExecEntrypoint);
    }

    [Fact]
    public void ParseSkillContent_MetaLlmChatWithTool_ReturnsNull()
    {
        var content = """
            ---
            name: meta-llm-chat-with-tool
            description: Invalid llm_chat step with tool
            kind: meta
            composition: {"steps":[{"id":"chat","kind":"llm_chat","tool":"search","with":{"prompt":"hello"}}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-llm-chat-with-tool", SkillSource.Workspace);

        Assert.Null(skill);
    }

    [Fact]
    public void ParseSkillContent_MetaUserInputWithSkill_ReturnsNull()
    {
        var content = """
            ---
            name: meta-user-input-with-skill
            description: Invalid user_input step with skill
            kind: meta
            composition: {"steps":[{"id":"ask","kind":"user_input","skill":"web-research","with":{"prompt":"confirm"}}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-user-input-with-skill", SkillSource.Workspace);

        Assert.Null(skill);
    }

    [Fact]
    public void ParseSkillContent_MetaAgentWithTool_ReturnsNull()
    {
        var content = """
            ---
            name: meta-agent-with-tool
            description: Invalid agent step with tool
            kind: meta
            composition: {"steps":[{"id":"delegate","kind":"agent","tool":"search","skill":"web-research"}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-agent-with-tool", SkillSource.Workspace);

        Assert.Null(skill);
    }

    [Fact]
    public void ParseSkillContent_MetaClassifyRouteTargetMissing_ReturnsNull()
    {
        var content = """
            ---
            name: meta-classify-missing-target
            description: Invalid classify route target
            kind: meta
            composition: {"steps":[{"id":"classify","kind":"llm_classify","with":{"options":["ok","fallback"],"route":{"ok":"next","fallback":"missing"}}},{"id":"next","kind":"agent","skill":"web-research","depends_on":["classify"]}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-classify-missing-target", SkillSource.Workspace);

        Assert.Null(skill);
    }

    [Fact]
    public void ParseSkillContent_MetaClassifyRouteLabelNotInOptions_ReturnsNull()
    {
        var content = """
            ---
            name: meta-classify-invalid-label
            description: Invalid classify route label
            kind: meta
            composition: {"steps":[{"id":"classify","kind":"llm_classify","with":{"options":["ok","fallback"],"route":{"ok":"next","other":"next"}}},{"id":"next","kind":"agent","skill":"web-research","depends_on":["classify"]}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-classify-invalid-label", SkillSource.Workspace);

        Assert.Null(skill);
    }

    [Fact]
    public void ParseSkillContent_MetaClassifyRouteTargetsExist_ParsesSuccessfully()
    {
        var content = """
            ---
            name: meta-classify-valid-route
            description: Valid classify route
            kind: meta
            composition: {"steps":[{"id":"classify","kind":"llm_classify","with":{"options":["ok","fallback"],"route":{"ok":"next","fallback":["next","audit"]}}},{"id":"next","kind":"agent","skill":"web-research","depends_on":["classify"]},{"id":"audit","kind":"llm_chat","depends_on":["classify"],"with":{"prompt":"audit"}}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-classify-valid-route", SkillSource.Workspace);

        Assert.NotNull(skill);
        Assert.Equal(3, skill!.Composition!.Steps.Count);
    }

    [Fact]
    public void ParseSkillContent_MetaOnFailureTarget_ParsesSuccessfully()
    {
        var content = """
            ---
            name: meta-on-failure-valid
            description: Valid failure branch
            kind: meta
            composition: {"steps":[{"id":"primary","kind":"tool_call","tool":"unstable","on_failure":"fallback"},{"id":"fallback","kind":"tool_call","tool":"safe"}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-on-failure-valid", SkillSource.Workspace);

        Assert.NotNull(skill);
        Assert.Equal("fallback", skill!.Composition!.Steps[0].OnFailure);
    }

    [Fact]
    public void ParseSkillContent_MetaOnFailureTargetWithDependency_ReturnsNull()
    {
        var content = """
            ---
            name: meta-on-failure-invalid-dependency
            description: Invalid failure branch
            kind: meta
            composition: {"steps":[{"id":"primary","kind":"tool_call","tool":"unstable","on_failure":"fallback"},{"id":"fallback","kind":"tool_call","tool":"safe","depends_on":["primary"]}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-on-failure-invalid-dependency", SkillSource.Workspace);

        Assert.Null(skill);
    }

    [Fact]
    public void ParseSkillContent_MetaOnFailureTargetUsedAsDependency_ReturnsNull()
    {
        var content = """
            ---
            name: meta-on-failure-invalid-dependent
            description: Invalid direct dependency on fallback branch
            kind: meta
            composition: {"steps":[{"id":"primary","kind":"tool_call","tool":"unstable","on_failure":"fallback"},{"id":"fallback","kind":"tool_call","tool":"safe"},{"id":"after","kind":"tool_call","tool":"next","depends_on":["fallback"]}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-on-failure-invalid-dependent", SkillSource.Workspace);

        Assert.Null(skill);
    }

    [Fact]
    public void ParseSkillContent_MetaStepRetryTimeoutPolicy_ParsesSuccessfully()
    {
        var content = """
            ---
            name: meta-step-policy-valid
            description: Valid retry and timeout policy
            kind: meta
            composition: {"steps":[{"id":"call","kind":"tool_call","tool":"unstable","timeout_seconds":2,"retry":{"max_attempts":3,"backoff_ms":10}}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-step-policy-valid", SkillSource.Workspace);

        Assert.NotNull(skill);
        var step = skill!.Composition!.Steps[0];
        Assert.Equal(2, step.TimeoutSeconds);
        Assert.Equal(3, step.Retry.MaxAttempts);
        Assert.Equal(10, step.Retry.BackoffMs);
    }

    [Fact]
    public void ParseSkillContent_MetaStepRetryWithInvalidAttempts_ReturnsNull()
    {
        var content = """
            ---
            name: meta-step-policy-invalid
            description: Invalid retry policy
            kind: meta
            composition: {"steps":[{"id":"call","kind":"tool_call","tool":"unstable","retry":{"max_attempts":0}}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-step-policy-invalid", SkillSource.Workspace);

        Assert.Null(skill);
    }

    [Fact]
    public void ParseSkillContent_MetaOutputContract_ParsesSuccessfully()
    {
        var content = """
            ---
            name: meta-output-contract-valid
            description: Valid output contract
            kind: meta
            composition: {"steps":[{"id":"draft","kind":"llm_chat","with":{"input":"hello"},"output_contract":{"format":"json","required_properties":["answer"]}}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-output-contract-valid", SkillSource.Workspace);

        Assert.NotNull(skill);
        var contract = skill!.Composition!.Steps[0].OutputContract;
        Assert.Equal("json", contract.Format);
        Assert.Equal(["answer"], contract.RequiredProperties);
    }

    [Fact]
    public void ParseSkillContent_MetaOutputContractWithUnsupportedFormat_ReturnsNull()
    {
        var content = """
            ---
            name: meta-output-contract-invalid
            description: Invalid output contract
            kind: meta
            composition: {"steps":[{"id":"draft","kind":"llm_chat","with":{"input":"hello"},"output_contract":{"format":"xml"}}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-output-contract-invalid", SkillSource.Workspace);

        Assert.Null(skill);
    }

    [Fact]
    public void ParseSkillContent_MetaCompositionWithDependencyCycle_ReturnsNull()
    {
        var content = """
            ---
            name: meta-cycle
            description: Invalid cycle
            kind: meta
            composition: {"steps":[{"id":"a","kind":"agent","skill":"one","depends_on":["b"]},{"id":"b","kind":"agent","skill":"two","depends_on":["a"]}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-cycle", SkillSource.Workspace);

        Assert.Null(skill);
    }

    [Fact]
    public void ParseSkillContent_MetaFinalTextModeStepTargetExists_ParsesSuccessfully()
    {
        var content = """
            ---
            name: meta-final-step
            description: Valid final_text_mode step target
            kind: meta
            final_text_mode: step:decision
            composition: {"steps":[{"id":"research","kind":"agent","skill":"web-research"},{"id":"decision","kind":"llm_chat","depends_on":["research"],"with":{"prompt":"{{outputs.research}}"}}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-final-step", SkillSource.Workspace);

        Assert.NotNull(skill);
        Assert.Equal("step:decision", skill!.FinalTextMode);
    }

    [Fact]
    public void ParseSkillContent_MetaFinalTextModeStepTargetMissing_ReturnsNull()
    {
        var content = """
            ---
            name: meta-final-step-missing
            description: Invalid final_text_mode step target
            kind: meta
            final_text_mode: step:missing
            composition: {"steps":[{"id":"research","kind":"agent","skill":"web-research"}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-final-step-missing", SkillSource.Workspace);

        Assert.Null(skill);
    }

    [Fact]
    public void ParseSkillContent_MetaFinalTextModeUnsupportedValue_ReturnsNull()
    {
        var content = """
            ---
            name: meta-final-unsupported
            description: Invalid final_text_mode
            kind: meta
            final_text_mode: latest
            composition: {"steps":[{"id":"research","kind":"agent","skill":"web-research"}]}
            ---
            Meta instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/meta-final-unsupported", SkillSource.Workspace);

        Assert.Null(skill);
    }

    [Theory]
    [InlineData("community-research-insight", 8)]
    [InlineData("meta-skill-creator", 19)]
    public void ParseSkillContent_BundledMetaSkillYamlComposition_ParsesSuccessfully(string skillName, int minStepCount)
    {
        var repositoryRoot = FindRepositoryRoot();
        var skillDir = Path.Combine(repositoryRoot, "src", "OpenClaw.Gateway", "skills", skillName);
        var skillPath = Path.Combine(skillDir, "SKILL.md");
        var content = File.ReadAllText(skillPath);

        var ok = SkillLoader.TryParseSkillContent(content, skillDir, SkillSource.Bundled, out var skill, out var errorCode);

        Assert.True(ok, errorCode);
        Assert.NotNull(skill);
        Assert.Equal(SkillKind.Meta, skill!.Kind);
        Assert.NotNull(skill.Composition);
        Assert.True(skill.Composition!.Steps.Count >= minStepCount);
    }

    [Fact]
    public void ParseSkillContent_CommunityResearchInsight_MatchesExampleWorkflow()
    {
        var repositoryRoot = FindRepositoryRoot();
        var skillDir = Path.Combine(repositoryRoot, "src", "OpenClaw.Gateway", "skills", "community-research-insight");
        var content = File.ReadAllText(Path.Combine(skillDir, "SKILL.md"));

        var ok = SkillLoader.TryParseSkillContent(content, skillDir, SkillSource.Bundled, out var skill, out var errorCode);

        Assert.True(ok, errorCode);
        Assert.NotNull(skill);
        Assert.Equal("step:final_response", skill!.FinalTextMode);

        var steps = skill.Composition!.Steps.ToDictionary(static step => step.Id, StringComparer.OrdinalIgnoreCase);
        var analyze = steps["analyze"];
        Assert.Equal("llm_chat", analyze.Kind);
        Assert.Null(analyze.Skill);
        Assert.Contains("collect", analyze.DependsOn);
        Assert.Equal("json", analyze.OutputContract.Format);
        Assert.Contains("key_pain_points", analyze.OutputContract.RequiredProperties);
        Assert.Contains("stakeholder_needs", analyze.OutputContract.RequiredProperties);
        Assert.Contains("opportunities", analyze.OutputContract.RequiredProperties);
        AssertRuntimeInputKey(analyze);

        var validate = steps["validate"];
        Assert.Equal("llm_chat", validate.Kind);
        Assert.Equal(["PASS", "REVISE"], validate.OutputChoices);
        Assert.Contains(validate.Routes, route => route.To == "preview" && route.When?.Contains("outputs.validate == 'PASS'", StringComparison.Ordinal) == true);
        Assert.Contains(validate.Routes, route => route.To == "validation_revise");

        var preview = steps["preview"];
        Assert.Contains("draft", preview.DependsOn);
        Assert.Contains("validate", preview.DependsOn);

        var review = steps["review"];
        Assert.Equal("chat", review.Clarify!.Mode);
        using (var reviewWith = JsonDocument.Parse(review.WithJson!))
        {
            var root = reviewWith.RootElement;
            Assert.True(root.TryGetProperty("prompt", out var prompt));
            Assert.Contains("outputs.preview", prompt.GetString(), StringComparison.Ordinal);
        }

        var finalResponse = steps["final_response"];
        Assert.Contains("review", finalResponse.DependsOn);
        Assert.Contains("preview", finalResponse.DependsOn);

        foreach (var step in steps.Values.Where(static step => step.Kind.Equals("llm_chat", StringComparison.OrdinalIgnoreCase)))
        {
            AssertRuntimeInputKey(step);
            using var withDoc = JsonDocument.Parse(step.WithJson!);
            Assert.True(withDoc.RootElement.TryGetProperty("system_prompt", out _), $"Step '{step.Id}' should use runtime key 'system_prompt'.");
            Assert.False(withDoc.RootElement.TryGetProperty("system", out _), $"Step '{step.Id}' uses ignored key 'system'.");
            Assert.False(withDoc.RootElement.TryGetProperty("task", out _), $"Step '{step.Id}' uses ignored key 'task'.");
        }
    }

    [Fact]
    public void ParseSkillContent_ReplacesBaseDir()
    {
        var content = """
            ---
            name: my-skill
            description: Uses baseDir
            ---
            Run the script at {baseDir}/run.sh
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/home/user/skills/my-skill", SkillSource.Workspace);

        Assert.NotNull(skill);
        Assert.Contains("/home/user/skills/my-skill/run.sh", skill!.Instructions);
        Assert.DoesNotContain("{baseDir}", skill.Instructions);
    }

    [Fact]
    public void ParseSkillContent_WithOsGate_ParsesOsList()
    {
        var content = """
            ---
            name: mac-only
            description: macOS only skill
            metadata: {"openclaw": {"os": ["darwin"]}}
            ---
            macOS instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/mac", SkillSource.Bundled);

        Assert.NotNull(skill);
        Assert.Single(skill!.Metadata.Os);
        Assert.Equal("darwin", skill.Metadata.Os[0]);
    }

    [Fact]
    public void ParseSkillContent_AlwaysTrue_SetsFlag()
    {
        var content = """
            ---
            name: core-skill
            description: Always loaded
            metadata: {"openclaw": {"always": true}}
            ---
            Core instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/core", SkillSource.Bundled);

        Assert.NotNull(skill);
        Assert.True(skill!.Metadata.Always);
    }

    [Fact]
    public void ParseMetadata_Null_ReturnsDefaults()
    {
        var meta = SkillLoader.ParseMetadata(null);
        Assert.False(meta.Always);
        Assert.Empty(meta.Os);
        Assert.Empty(meta.RequireBins);
        Assert.Empty(meta.RequireEnv);
    }

    private static void AssertRuntimeInputKey(MetaSkillStepDefinition step)
    {
        Assert.False(string.IsNullOrWhiteSpace(step.WithJson), $"Step '{step.Id}' should declare a with payload.");
        using var withDoc = JsonDocument.Parse(step.WithJson!);
        Assert.True(withDoc.RootElement.TryGetProperty("input", out _), $"Step '{step.Id}' should use runtime key 'input'.");
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        var sourceDirectory = string.IsNullOrWhiteSpace(sourceFilePath)
            ? null
            : Path.GetDirectoryName(sourceFilePath);
        foreach (var startDirectory in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory(), sourceDirectory })
        {
            if (string.IsNullOrWhiteSpace(startDirectory))
                continue;

            var current = new DirectoryInfo(startDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "OpenClaw.Net.slnx")) &&
                    Directory.Exists(Path.Combine(current.FullName, "src")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }

        throw new InvalidOperationException("Could not locate repository root from test output directory.");
    }

    [Fact]
    public void ParseMetadata_InvalidJson_ReturnsDefaults()
    {
        var meta = SkillLoader.ParseMetadata("not json at all");
        Assert.False(meta.Always);
    }

    [Fact]
    public void ParseMetadata_NoOpenclawKey_ReturnsDefaults()
    {
        var meta = SkillLoader.ParseMetadata("""{"other": true}""");
        Assert.False(meta.Always);
    }

    [Fact]
    public void LoadAll_Disabled_ReturnsEmpty()
    {
        var config = new SkillsConfig { Enabled = false };
        var logger = new TestLogger();

        var skills = SkillLoader.LoadAll(config, null, logger);

        Assert.Empty(skills);
    }

    [Fact]
    public void LoadAll_NoDirectories_ReturnsEmpty()
    {
        var config = new SkillsConfig
        {
            Enabled = true,
            Load = new SkillLoadConfig { IncludeBundled = false, IncludeManaged = false }
        };
        var logger = new TestLogger();

        var skills = SkillLoader.LoadAll(config, "/nonexistent/workspace", logger);

        Assert.Empty(skills);
    }

    [Fact]
    public void LoadAll_WithSkillFiles_LoadsAndFilters()
    {
        // Create temp skill structure: <workspace>/skills/<skill-name>/SKILL.md
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-test-skills-{Guid.NewGuid():N}");
        var skillDir = Path.Combine(tempDir, "skills", "test-skill");
        Directory.CreateDirectory(skillDir);

        try
        {
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), """
                ---
                name: test-skill
                description: A test skill
                ---
                Test instructions here.
                """);

            var config = new SkillsConfig
            {
                Enabled = true,
                Load = new SkillLoadConfig { IncludeBundled = false, IncludeManaged = false }
            };
            var logger = new TestLogger();

            // Use tempDir as workspace skills
            var skills = SkillLoader.LoadAll(config, tempDir, logger);

            Assert.Single(skills);
            Assert.Equal("test-skill", skills[0].Name);
            Assert.Equal(SkillSource.Workspace, skills[0].Source);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void LoadAll_DefaultScanSubdirectories_DoesNotLoadNestedSkills()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-test-skills-shallow-{Guid.NewGuid():N}");
        var rootSkillDir = Path.Combine(tempDir, "skills", "meta-skill");
        var nestedSkillDir = Path.Combine(rootSkillDir, "subskills", "reporter");
        Directory.CreateDirectory(nestedSkillDir);

        try
        {
            File.WriteAllText(Path.Combine(rootSkillDir, "SKILL.md"), """
                ---
                name: meta-skill
                description: Root meta skill
                kind: meta
                composition: {"steps":[{"id":"collect","kind":"user_input","with":{"prompt":"ok?"}}]}
                ---
                Root instructions.
                """);

            File.WriteAllText(Path.Combine(nestedSkillDir, "SKILL.md"), """
                ---
                name: reporter
                description: Nested reporter skill
                ---
                Reporter instructions.
                """);

            var config = new SkillsConfig
            {
                Enabled = true,
                Load = new SkillLoadConfig { IncludeBundled = false, IncludeManaged = false }
            };
            var logger = new TestLogger();

            var skills = SkillLoader.LoadAll(config, tempDir, logger);

            Assert.Contains(skills, skill => skill.Name == "meta-skill");
            Assert.DoesNotContain(skills, skill => skill.Name == "reporter");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void LoadAll_DefaultScanSubdirectories_LoadsOwnerQualifiedSkills()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"openclaw-test-skills-owner-qualified-{Guid.NewGuid():N}");
        var skillDir = Path.Join(tempDir, "skills", "@steipete", "peekaboo");
        var nestedSkillDir = Path.Join(skillDir, "nested");
        Directory.CreateDirectory(skillDir);
        Directory.CreateDirectory(nestedSkillDir);

        try
        {
            File.WriteAllText(Path.Join(skillDir, "SKILL.md"), """
                ---
                name: peekaboo
                description: Owner-qualified ClawHub skill
                ---
                Peekaboo instructions.
                """);
            File.WriteAllText(Path.Join(nestedSkillDir, "SKILL.md"), """
                ---
                name: nested-owner-skill
                description: Must remain excluded from a shallow scan
                ---
                Nested instructions.
                """);

            var config = new SkillsConfig
            {
                Enabled = true,
                Load = new SkillLoadConfig { IncludeBundled = false, IncludeManaged = false }
            };
            var logger = new TestLogger();

            var skills = SkillLoader.LoadAll(config, tempDir, logger);

            var skill = Assert.Single(skills);
            Assert.Equal("peekaboo", skill.Name);
            Assert.Equal(SkillSource.Workspace, skill.Source);
            Assert.Equal(skillDir, skill.Location);
            Assert.DoesNotContain(skills, item => item.Name == "nested-owner-skill");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void LoadAll_DuplicateOwnerQualifiedSlugs_AreRejectedDeterministically()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"openclaw-test-skills-owner-collision-{Guid.NewGuid():N}");
        var firstSkillDir = Path.Join(tempDir, "skills", "@owner-a", "peekaboo");
        var secondSkillDir = Path.Join(tempDir, "skills", "@owner-b", "peekaboo");
        var unrelatedSkillDir = Path.Join(tempDir, "skills", "unrelated");
        Directory.CreateDirectory(firstSkillDir);
        Directory.CreateDirectory(secondSkillDir);
        Directory.CreateDirectory(unrelatedSkillDir);

        try
        {
            const string peekabooSkill = """
                ---
                name: peekaboo
                description: Duplicate owner-qualified skill
                ---
                Peekaboo instructions.
                """;
            File.WriteAllText(Path.Join(firstSkillDir, "SKILL.md"), peekabooSkill);
            File.WriteAllText(Path.Join(secondSkillDir, "SKILL.md"), peekabooSkill);
            File.WriteAllText(Path.Join(unrelatedSkillDir, "SKILL.md"), """
                ---
                name: unrelated
                description: Adjacent skill
                ---
                Unrelated instructions.
                """);

            var config = new SkillsConfig
            {
                Enabled = true,
                Load = new SkillLoadConfig { IncludeBundled = false, IncludeManaged = false }
            };
            var logger = new CapturingTestLogger();

            var skills = SkillLoader.LoadAll(config, tempDir, logger);

            var skill = Assert.Single(skills);
            Assert.Equal("unrelated", skill.Name);
            Assert.Contains(logger.WarningMessages, message =>
                message.Contains("duplicate owner-qualified skill slug 'peekaboo'", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void LoadAll_ScanSubdirectories_LoadsNestedWorkspaceSkills()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-test-skills-recursive-{Guid.NewGuid():N}");
        var rootSkillDir = Path.Combine(tempDir, "skills", "meta-skill");
        var nestedSkillDir = Path.Combine(rootSkillDir, "subskills", "reporter");
        Directory.CreateDirectory(nestedSkillDir);

        try
        {
            File.WriteAllText(Path.Combine(rootSkillDir, "SKILL.md"), """
                ---
                name: meta-skill
                description: Root meta skill
                kind: meta
                composition: {"steps":[{"id":"collect","kind":"user_input","with":{"prompt":"ok?"}}]}
                ---
                Root instructions.
                """);

            File.WriteAllText(Path.Combine(nestedSkillDir, "SKILL.md"), """
                ---
                name: reporter
                description: Nested reporter skill
                ---
                Reporter instructions.
                """);

            var config = new SkillsConfig
            {
                Enabled = true,
                Load = new SkillLoadConfig
                {
                    IncludeBundled = false,
                    IncludeManaged = false,
                    ScanSubdirectories = true
                }
            };
            var logger = new TestLogger();

            var skills = SkillLoader.LoadAll(config, tempDir, logger);

            Assert.Contains(skills, skill => skill.Name == "meta-skill");
            var nestedSkill = Assert.Single(skills, skill => skill.Name == "reporter");
            Assert.Equal(SkillSource.Workspace, nestedSkill.Source);
            Assert.Equal(nestedSkillDir, nestedSkill.Location);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void LoadAll_MetaPolicy_FiltersRiskAndCapabilities()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-test-skills-meta-policy-{Guid.NewGuid():N}");
        var riskSkillDir = Path.Combine(tempDir, "skills", "meta-risk");
        var capabilitySkillDir = Path.Combine(tempDir, "skills", "meta-capability");
        var standardSkillDir = Path.Combine(tempDir, "skills", "standard-skill");
        Directory.CreateDirectory(riskSkillDir);
        Directory.CreateDirectory(capabilitySkillDir);
        Directory.CreateDirectory(standardSkillDir);

        try
        {
            File.WriteAllText(Path.Combine(riskSkillDir, "SKILL.md"), """
                ---
                name: meta-risk
                description: High risk meta skill
                kind: meta
                metadata: {"opensquilla":{"risk":"high","capabilities":["tool:search","meta:invoke"]}}
                composition: {"steps":[{"id":"collect","kind":"user_input","with":{"prompt":"ok?"}}]}
                ---
                Instructions.
                """);

            File.WriteAllText(Path.Combine(capabilitySkillDir, "SKILL.md"), """
                ---
                name: meta-capability
                description: Missing required capability
                kind: meta
                metadata: {"opensquilla":{"risk":"low","capabilities":["tool:search"]}}
                composition: {"steps":[{"id":"collect","kind":"user_input","with":{"prompt":"ok?"}}]}
                ---
                Instructions.
                """);

            File.WriteAllText(Path.Combine(standardSkillDir, "SKILL.md"), """
                ---
                name: standard-skill
                description: Standard skill
                ---
                Instructions.
                """);

            var config = new SkillsConfig
            {
                Enabled = true,
                Load = new SkillLoadConfig { IncludeBundled = false, IncludeManaged = false }
            };
            config.MetaSkill.AllowedRiskLevels = ["low"];
            config.MetaSkill.RequiredCapabilities = ["tool:search", "meta:invoke"];

            var logger = new TestLogger();
            var skills = SkillLoader.LoadAll(config, tempDir, logger);

            Assert.Single(skills);
            Assert.Equal("standard-skill", skills[0].Name);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void LoadAll_DisabledByEntry_Excluded()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-test-skills-{Guid.NewGuid():N}");
        var skillDir = Path.Combine(tempDir, "skills", "disabled-skill");
        Directory.CreateDirectory(skillDir);

        try
        {
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), """
                ---
                name: disabled-skill
                description: Should be filtered out
                ---
                Instructions.
                """);

            var config = new SkillsConfig
            {
                Enabled = true,
                Load = new SkillLoadConfig { IncludeBundled = false, IncludeManaged = false },
                Entries = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["disabled-skill"] = new SkillEntryConfig { Enabled = false }
                }
            };
            var logger = new TestLogger();

            var skills = SkillLoader.LoadAll(config, tempDir, logger);

            Assert.Empty(skills);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void LoadAll_WorkspaceOverridesManaged_HigherPrecedenceWins()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-test-skills-{Guid.NewGuid():N}");
        var extraDir = Path.Combine(tempDir, "extra");
        var wsDir = Path.Combine(tempDir, "workspace");

        var extraSkillDir = Path.Combine(extraDir, "my-skill");
        var wsSkillDir = Path.Combine(wsDir, "skills", "my-skill");
        Directory.CreateDirectory(extraSkillDir);
        Directory.CreateDirectory(wsSkillDir);

        try
        {
            File.WriteAllText(Path.Combine(extraSkillDir, "SKILL.md"), """
                ---
                name: my-skill
                description: Extra version
                ---
                Extra instructions.
                """);

            File.WriteAllText(Path.Combine(wsSkillDir, "SKILL.md"), """
                ---
                name: my-skill
                description: Workspace version
                ---
                Workspace instructions.
                """);

            var config = new SkillsConfig
            {
                Enabled = true,
                Load = new SkillLoadConfig { ExtraDirs = [extraDir], IncludeBundled = false, IncludeManaged = false }
            };
            var logger = new TestLogger();

            var skills = SkillLoader.LoadAll(config, wsDir, logger);

            Assert.Single(skills);
            Assert.Equal("my-skill", skills[0].Name);
            Assert.Equal("Workspace version", skills[0].Description);
            Assert.Equal(SkillSource.Workspace, skills[0].Source);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void LoadAll_ManagedSkill_IsDiscoveredFromDotOpenclaw()
    {
        var managedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".openclaw",
            "skills",
            $"managed-skill-{Guid.NewGuid():N}");
        Directory.CreateDirectory(managedRoot);

        try
        {
            File.WriteAllText(Path.Combine(managedRoot, "SKILL.md"), """
                ---
                name: managed-skill
                description: Managed skill
                ---
                Managed instructions.
                """);

            var config = new SkillsConfig
            {
                Enabled = true,
                Load = new SkillLoadConfig { IncludeBundled = false, IncludeWorkspace = false }
            };
            var logger = new TestLogger();

            var skills = SkillLoader.LoadAll(config, null, logger);

            var skill = Assert.Single(skills, s => s.Name == "managed-skill");
            Assert.Equal(SkillSource.Managed, skill.Source);
        }
        finally
        {
            Directory.Delete(managedRoot, true);
        }
    }

    [Fact]
    public void LoadAll_ManagedRoot_TildePrefix_IsExpandedToUserHome()
    {
        var suffix = Path.Combine(".openclaw", "skills", $"managed-tilde-{Guid.NewGuid():N}");
        var managedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), suffix);
        Directory.CreateDirectory(managedRoot);

        try
        {
            File.WriteAllText(Path.Combine(managedRoot, "SKILL.md"), """
                ---
                name: managed-tilde-skill
                description: Managed tilde skill
                ---
                Managed tilde instructions.
                """);

            var config = new SkillsConfig
            {
                Enabled = true,
                Load = new SkillLoadConfig
                {
                    IncludeBundled = false,
                    IncludeWorkspace = false,
                    ManagedRoot = $"~/{suffix.Replace('\\', '/')}"
                }
            };
            var logger = new TestLogger();

            var skills = SkillLoader.LoadAll(config, null, logger);

            var skill = Assert.Single(skills, s => s.Name == "managed-tilde-skill");
            Assert.Equal(SkillSource.Managed, skill.Source);
        }
        finally
        {
            Directory.Delete(managedRoot, true);
        }
    }

    [Fact]
    public void LoadAll_ManagedRoot_RelativePath_IsResolvedFromCurrentDirectory()
    {
        var originalCwd = Directory.GetCurrentDirectory();
        var tempDir = Path.Combine(Path.GetTempPath(), $"skill-loader-relative-{Guid.NewGuid():N}");
        var relativeManagedRoot = "managed-relative";
        var absoluteManagedRoot = Path.Combine(tempDir, relativeManagedRoot);
        Directory.CreateDirectory(absoluteManagedRoot);

        try
        {
            File.WriteAllText(Path.Combine(absoluteManagedRoot, "SKILL.md"), """
                ---
                name: managed-relative-skill
                description: Managed relative skill
                ---
                Managed relative instructions.
                """);

            Directory.SetCurrentDirectory(tempDir);

            var config = new SkillsConfig
            {
                Enabled = true,
                Load = new SkillLoadConfig
                {
                    IncludeBundled = false,
                    IncludeWorkspace = false,
                    ManagedRoot = relativeManagedRoot
                }
            };
            var logger = new TestLogger();

            var skills = SkillLoader.LoadAll(config, null, logger);

            var skill = Assert.Single(skills, s => s.Name == "managed-relative-skill");
            Assert.Equal(SkillSource.Managed, skill.Source);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void LoadAll_ManagedRoot_InvalidPath_DoesNotThrow()
    {
        var config = new SkillsConfig
        {
            Enabled = true,
            Load = new SkillLoadConfig
            {
                IncludeBundled = false,
                IncludeWorkspace = false,
                ManagedRoot = "invalid\0managed-root"
            }
        };
        var logger = new TestLogger();

        var skills = SkillLoader.LoadAll(config, null, logger);

        Assert.Empty(skills);
    }

    [Fact]
    public void LoadAll_InvalidSkill_LogsDiagnosticErrorCode()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-test-skills-{Guid.NewGuid():N}");
        var badSkillDir = Path.Combine(tempDir, "bad-meta");
        Directory.CreateDirectory(badSkillDir);

        try
        {
            File.WriteAllText(Path.Combine(badSkillDir, "SKILL.md"), """
                ---
                name: bad-meta
                description: invalid meta skill
                kind: meta
                composition: {"steps":[{"id":"t1","kind":"tool_call","tool":"search","skill":"web-research"}]}
                ---
                Invalid.
                """);

            var config = new SkillsConfig
            {
                Enabled = true,
                Load = new SkillLoadConfig
                {
                    IncludeBundled = false,
                    IncludeManaged = false,
                    IncludeWorkspace = false,
                    ExtraDirs = [tempDir]
                }
            };

            var logger = new CapturingTestLogger();
            var skills = SkillLoader.LoadAll(config, null, logger);

            Assert.Empty(skills);
            Assert.Contains(logger.WarningMessages, message =>
                message.Contains("error_code=invalid_step_kind_fields", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void LoadAll_InvalidRootSkill_LogsDiagnosticErrorCode()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-test-skills-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "SKILL.md"), """
                ---
                name: bad-root-meta
                description: invalid root meta skill
                kind: meta
                composition: {"steps":[{"id":"t1","kind":"tool_call","tool":"search","skill":"web-research"}]}
                ---
                Invalid root skill.
                """);

            var config = new SkillsConfig
            {
                Enabled = true,
                Load = new SkillLoadConfig
                {
                    IncludeBundled = false,
                    IncludeManaged = false,
                    IncludeWorkspace = false,
                    ExtraDirs = [tempDir]
                }
            };

            var logger = new CapturingTestLogger();
            var skills = SkillLoader.LoadAll(config, null, logger);

            Assert.Empty(skills);
            Assert.Contains(logger.WarningMessages, message =>
                message.Contains("error_code=invalid_step_kind_fields", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void LoadAll_BundledMetaYamlSkills_LoadWithoutParseWarnings()
    {
        var config = new SkillsConfig
        {
            Enabled = true,
            Load = new SkillLoadConfig
            {
                IncludeBundled = true,
                IncludeManaged = false,
                IncludeWorkspace = false
            }
        };
        var logger = new CapturingTestLogger();

        var skills = SkillLoader.LoadAll(config, null, logger);

        Assert.Contains(skills, skill => skill.Name == "community-research-insight" && skill.Kind == SkillKind.Meta);
        Assert.Contains(skills, skill => skill.Name == "meta-skill-creator" && skill.Kind == SkillKind.Meta);
        Assert.DoesNotContain(logger.WarningMessages, message =>
            message.Contains("Failed to parse skill", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParseSkillContent_NoResourceDirs_ReturnsEmptyResources()
    {
        var content = """
            ---
            name: bare-skill
            description: No references or scripts
            ---
            Body.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/bare-skill", SkillSource.Workspace);

        Assert.NotNull(skill);
        Assert.Empty(skill!.Resources);
    }

    [Fact]
    public void ParseSkillFile_WithReferencesAndScripts_PopulatesResources()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-skill-resources-{Guid.NewGuid():N}");
        var referencesDir = Path.Combine(tempDir, "references");
        var scriptsDir = Path.Combine(tempDir, "scripts");
        var nestedDir = Path.Combine(referencesDir, "nested");
        Directory.CreateDirectory(referencesDir);
        Directory.CreateDirectory(scriptsDir);
        Directory.CreateDirectory(nestedDir);

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "SKILL.md"), """
                ---
                name: rich-skill
                description: Has resources
                ---
                Body.
                """);
            File.WriteAllText(Path.Combine(referencesDir, "lookup.md"), "ref content");
            File.WriteAllText(Path.Combine(nestedDir, "deep.md"), "deep");
            File.WriteAllText(Path.Combine(scriptsDir, "run.sh"), "#!/bin/sh\n");

            var skill = SkillLoader.ParseSkillFile(
                Path.Combine(tempDir, "SKILL.md"),
                tempDir,
                SkillSource.Workspace);

            Assert.NotNull(skill);
            Assert.Equal(3, skill!.Resources.Count);

            var byPath = skill.Resources.ToDictionary(r => r.RelativePath, r => r);
            Assert.True(byPath.ContainsKey("references/lookup.md"));
            Assert.True(byPath.ContainsKey("references/nested/deep.md"));
            Assert.True(byPath.ContainsKey("scripts/run.sh"));

            Assert.Equal(SkillResourceKind.Reference, byPath["references/lookup.md"].Kind);
            Assert.Equal(SkillResourceKind.Reference, byPath["references/nested/deep.md"].Kind);
            Assert.Equal(SkillResourceKind.Script, byPath["scripts/run.sh"].Kind);

            Assert.Equal("lookup.md", byPath["references/lookup.md"].Name);
            Assert.True(File.Exists(byPath["references/lookup.md"].AbsolutePath));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ParseSkillFile_WithArtifactContractAndProjections_LoadsBoth()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-skill-contracts-{Guid.NewGuid():N}");
        var contractsDir = Path.Combine(tempDir, "contracts");
        var projectionsDir = Path.Combine(contractsDir, "projections", "producer-one");
        Directory.CreateDirectory(projectionsDir);

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "SKILL.md"), """
                ---
                name: pipeline-skill
                description: A skill with contracts
                ---
                Body.
                """);

            // Add a references/ dir so ScanSkillResources returns at least 1 resource.
            var referencesDir = Path.Combine(tempDir, "references");
            Directory.CreateDirectory(referencesDir);
            File.WriteAllText(Path.Combine(referencesDir, "note.md"), "ref content");

            File.WriteAllText(Path.Combine(contractsDir, "artifacts.json"), """
                {
                  "schemaVersion": 2,
                  "stages": [
                    {
                      "name": "design",
                      "label": "Design Phase",
                      "artifacts": [
                        {
                          "type": "requirements",
                          "label": "Requirements Doc",
                          "display": "badge",
                          "terminal": true
                        }
                      ]
                    }
                  ]
                }
                """);

            File.WriteAllText(Path.Combine(projectionsDir, "contract-index.json"), """
                {
                  "producer_skill": "producer-one",
                  "producer_priority": 42,
                  "default_selection_policy": {
                    "prefer_ready_only": true,
                    "block_on_open_questions": true,
                    "fallback_order_by_target_view": ["prompt-constraint"]
                  },
                  "topic_scoring": {
                    "score_dimensions": [
                      { "dimension": "primary_intent_match", "score": 5 }
                    ],
                    "topics": [
                      {
                        "domain_slug": "skill-loading",
                        "primary_intent_signals": ["load skill"],
                        "supporting_signals": ["skill"],
                        "explicit_artifact_signals": [],
                        "demote_when_competing_topic_signals": []
                      }
                    ]
                  },
                  "target_view_scoring": {
                    "score_dimensions": [
                      { "dimension": "explicit_output_match", "score": 5 }
                    ],
                    "views": [
                      {
                        "target_view": "prompt-constraint",
                        "explicit_output_signals": ["prompt policy"],
                        "strong_signals": [],
                        "supporting_signals": [],
                        "demote_when_competing_view_signals": []
                      }
                    ],
                    "within_topic_overrides": [
                      {
                        "domain_slug": "skill-loading",
                        "bonuses": [
                          {
                            "target_view": "prompt-constraint",
                            "when_request_signals": ["policy"],
                            "score": 3
                          }
                        ]
                      }
                    ]
                  },
                  "topics": [
                    {
                      "domain_slug": "skill-loading",
                      "default_target_view": "prompt-constraint",
                      "views": [
                        {
                          "target_view": "prompt-constraint",
                          "status": "READY",
                          "path": "producer-one/skill-loading.prompt-constraint.projection.json"
                        }
                      ]
                    }
                  ]
                }
                """);

            var skill = SkillLoader.ParseSkillFile(
                Path.Combine(tempDir, "SKILL.md"),
                tempDir,
                SkillSource.Workspace);

            Assert.NotNull(skill);
            Assert.Single(skill!.Resources);
            Assert.NotNull(skill.ArtifactContract);
            Assert.Equal(2, skill.ArtifactContract!.SchemaVersion);
            var stage = Assert.Single(skill.ArtifactContract.Stages);
            Assert.Equal("design", stage.Name);
            Assert.Equal("requirements", Assert.Single(stage.Artifacts).Type);

            var contract = Assert.Single(skill.ProjectionContracts);
            Assert.Equal("producer-one", contract.ProducerName);
            Assert.Equal(42, contract.ProducerPriority);
            Assert.Equal(Path.GetFullPath(projectionsDir), Path.GetFullPath(contract.RootPath));
            Assert.True(contract.Index.DefaultSelectionPolicy.PreferReadyOnly);
            Assert.True(contract.Index.DefaultSelectionPolicy.BlockOnOpenQuestions);
            Assert.Equal(["prompt-constraint"], contract.Index.DefaultSelectionPolicy.FallbackOrderByTargetView);
            Assert.Equal("skill-loading", Assert.Single(contract.Index.Topics).DomainSlug);
            Assert.Equal("prompt-constraint", Assert.Single(Assert.Single(contract.Index.Topics).Views).TargetView);
            Assert.Equal("skill-loading", Assert.Single(contract.Index.TopicScoring!.Topics).DomainSlug);
            Assert.Equal("prompt-constraint", Assert.Single(contract.Index.TargetViewScoring!.Views).TargetView);
            Assert.Equal("prompt-constraint", Assert.Single(Assert.Single(contract.Index.TargetViewScoring.WithinTopicOverrides).Bonuses).TargetView);

            Assert.NotNull(skill.ProjectionDiscovery);
            Assert.Equal("bound", skill.ProjectionDiscovery!.Status);
            Assert.Equal(1, skill.ProjectionDiscovery.IndexCount);
            Assert.Equal(1, skill.ProjectionDiscovery.BoundCount);
            Assert.Equal([Path.Combine(projectionsDir, "contract-index.json")], skill.ProjectionDiscovery.IndexPaths);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ParseSkillFile_WithEmptyProjectionIndex_DoesNotBindProjectionContract()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-skill-empty-projections-{Guid.NewGuid():N}");
        var projectionsDir = Path.Combine(tempDir, "contracts", "projections", "producer-one");
        Directory.CreateDirectory(projectionsDir);

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "SKILL.md"), """
                ---
                name: empty-projection-skill
                description: A skill with an empty projection contract
                ---
                Body.
                """);

            File.WriteAllText(Path.Combine(projectionsDir, "contract-index.json"), """
                {
                  "producer_skill": "producer-one",
                  "topics": []
                }
                """);

            var skill = SkillLoader.ParseSkillFile(
                Path.Combine(tempDir, "SKILL.md"),
                tempDir,
                SkillSource.Workspace);

            Assert.NotNull(skill);
            Assert.Empty(skill!.ProjectionContracts);
            Assert.NotNull(skill.ProjectionDiscovery);
            Assert.Equal("parse-failed", skill.ProjectionDiscovery!.Status);
            Assert.Equal(1, skill.ProjectionDiscovery.IndexCount);
            Assert.Equal(0, skill.ProjectionDiscovery.BoundCount);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ParseSkillFile_WithProjectionIndexMissingRoutingSurface_DoesNotBindProjectionContract()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-skill-useless-projections-{Guid.NewGuid():N}");
        var projectionsDir = Path.Combine(tempDir, "contracts", "projections", "producer-one");
        Directory.CreateDirectory(projectionsDir);

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "SKILL.md"), """
                ---
                name: unroutable-projection-skill
                description: A skill with an unroutable projection contract
                ---
                Body.
                """);

            File.WriteAllText(Path.Combine(projectionsDir, "contract-index.json"), """
                {
                  "producer_skill": "producer-one",
                  "default_selection_policy": {
                    "fallback_order_by_target_view": []
                  },
                  "topics": [
                    {
                      "domain_slug": "skill-loading",
                      "default_target_view": "prompt-constraint",
                      "views": [
                        {
                          "target_view": "prompt-constraint",
                          "status": "READY",
                          "path": "producer-one/skill-loading.prompt-constraint.projection.json"
                        }
                      ]
                    }
                  ]
                }
                """);

            var skill = SkillLoader.ParseSkillFile(
                Path.Combine(tempDir, "SKILL.md"),
                tempDir,
                SkillSource.Workspace);

            Assert.NotNull(skill);
            Assert.Empty(skill!.ProjectionContracts);
            Assert.NotNull(skill.ProjectionDiscovery);
            Assert.Equal("parse-failed", skill.ProjectionDiscovery!.Status);
            Assert.Equal(1, skill.ProjectionDiscovery.IndexCount);
            Assert.Equal(0, skill.ProjectionDiscovery.BoundCount);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}

public class SkillPromptBuilderTests
{
    [Fact]
    public void Build_NoSkills_ReturnsEmpty()
    {
        var result = SkillPromptBuilder.Build([]);
        Assert.Equal("", result);
    }

    [Fact]
    public void Build_WithSkills_GeneratesXml()
    {
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "web-search",
                Description = "Search the web",
                Instructions = "Use the web_search tool to find information.",
                Location = "/skills/web-search"
            }
        };

        var result = SkillPromptBuilder.Build(skills);

        Assert.Contains("<available-skills>", result);
        Assert.Contains("<name>web-search</name>", result);
        Assert.Contains("<description>Search the web</description>", result);
        Assert.Contains("<location>/skills/web-search</location>", result);
        Assert.Contains("</available-skills>", result);
        Assert.Contains("<skill-instructions>", result);
        Assert.Contains("## Skill: web-search", result);
        Assert.Contains("Use the web_search tool", result);
    }

    [Fact]
    public void Build_DisableModelInvocation_ExcludesSkill()
    {
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "visible",
                Description = "Visible skill",
                Instructions = "Visible instructions.",
                Location = "/skills/visible"
            },
            new()
            {
                Name = "hidden",
                Description = "Hidden skill",
                Instructions = "Hidden instructions.",
                Location = "/skills/hidden",
                DisableModelInvocation = true
            }
        };

        var result = SkillPromptBuilder.Build(skills);

        Assert.Contains("visible", result);
        Assert.DoesNotContain("<name>hidden</name>", result);
    }

    [Fact]
    public void Build_EscapesXmlChars()
    {
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "test & <demo>",
                Description = "A \"test\" skill",
                Instructions = "Instructions here.",
                Location = "/skills/test"
            }
        };

        var result = SkillPromptBuilder.Build(skills);

        Assert.Contains("test &amp; &lt;demo&gt;", result);
        Assert.Contains("A &quot;test&quot; skill", result);
    }

    [Fact]
    public void BuildSummary_NoSkills_ReturnsMessage()
    {
        var result = SkillPromptBuilder.BuildSummary([]);
        Assert.Equal("No skills loaded.", result);
    }

    [Fact]
    public void BuildSummary_WithSkills_ListsThem()
    {
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "search",
                Description = "Web search",
                Instructions = "...",
                Location = "/skills/search",
                Source = SkillSource.Workspace
            },
            new()
            {
                Name = "internal",
                Description = "Internal only",
                Instructions = "...",
                Location = "/skills/internal",
                Source = SkillSource.Bundled,
                DisableModelInvocation = true
            }
        };

        var result = SkillPromptBuilder.BuildSummary(skills);

        Assert.Contains("Loaded skills (2)", result);
        Assert.Contains("search: Web search", result);
        Assert.Contains("(Workspace)", result);
        Assert.Contains("internal: Internal only", result);
        Assert.Contains("[no-model]", result);
        Assert.Contains("(Bundled)", result);
    }

    [Fact]
    public void BuildSummary_MetaSkill_IncludesMetaFlags()
    {
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "meta-brief",
                Kind = SkillKind.Meta,
                Description = "Meta brief",
                Instructions = "...",
                Location = "/skills/meta-brief",
                MetaPriority = 40,
                Source = SkillSource.Workspace
            }
        };

        var result = SkillPromptBuilder.BuildSummary(skills);

        Assert.Contains("kind:meta", result);
        Assert.Contains("meta-priority:40", result);
    }

    [Fact]
    public void EstimateCharacterCost_NoSkills_ReturnsZero()
    {
        Assert.Equal(0, SkillPromptBuilder.EstimateCharacterCost([]));
    }

    [Fact]
    public void EstimateCharacterCost_WithSkills_ReturnsPositive()
    {
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "test",
                Description = "Test skill",
                Instructions = "Do the thing.",
                Location = "/skills/test"
            }
        };

        var cost = SkillPromptBuilder.EstimateCharacterCost(skills);
        Assert.True(cost > 195); // base + per-skill
    }

    [Fact]
    public void EstimateCharacterCost_ExcludesDisabledModelSkills()
    {
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "hidden",
                Description = "Hidden",
                Instructions = "...",
                Location = "/skills/hidden",
                DisableModelInvocation = true
            }
        };

        Assert.Equal(0, SkillPromptBuilder.EstimateCharacterCost(skills));
    }

    [Fact]
    public void BuildIndex_NoSkills_ReturnsEmpty()
    {
        Assert.Equal("", SkillPromptBuilder.BuildIndex([]));
    }

    [Fact]
    public void BuildIndex_OmitsInstructions_AndPointsAtLoadSkillTool()
    {
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "web-search",
                Description = "Search the web",
                Instructions = "Use the web_search tool to find information.",
                Location = "/skills/web-search"
            }
        };

        var index = SkillPromptBuilder.BuildIndex(skills);

        Assert.Contains("<available-skills>", index);
        Assert.Contains("<name>web-search</name>", index);
        Assert.Contains("<description>Search the web</description>", index);
        Assert.Contains("`load_skill`", index);
        // Critical: instructions body must NOT leak into the index.
        Assert.DoesNotContain("<skill-instructions>", index);
        Assert.DoesNotContain("Use the web_search tool", index);
    }

    [Fact]
    public void BuildIndex_IncludesResourceManifest()
    {
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "rich",
                Description = "Has resources",
                Instructions = "...",
                Location = "/skills/rich",
                Resources =
                [
                    new SkillResource
                    {
                        Name = "lookup.md",
                        RelativePath = "references/lookup.md",
                        AbsolutePath = "/skills/rich/references/lookup.md",
                        Kind = SkillResourceKind.Reference
                    },
                    new SkillResource
                    {
                        Name = "run.sh",
                        RelativePath = "scripts/run.sh",
                        AbsolutePath = "/skills/rich/scripts/run.sh",
                        Kind = SkillResourceKind.Script
                    }
                ]
            }
        };

        var index = SkillPromptBuilder.BuildIndex(skills);

        Assert.Contains("<resources>", index);
        Assert.Contains("kind=\"reference\"", index);
        Assert.Contains("path=\"references/lookup.md\"", index);
        Assert.Contains("kind=\"script\"", index);
        Assert.Contains("path=\"scripts/run.sh\"", index);
    }

    [Fact]
    public void BuildIndex_MetaSkill_IncludesKindTriggersAndPriority()
    {
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "meta-research",
                Kind = SkillKind.Meta,
                Description = "Meta research flow",
                Instructions = "...",
                Location = "/skills/meta-research",
                MetaPriority = 80,
                Triggers = ["帮我调研", "生成决策建议"]
            }
        };

        var index = SkillPromptBuilder.BuildIndex(skills);

        Assert.Contains("<kind>meta</kind>", index);
        Assert.Contains("<meta-priority>80</meta-priority>", index);
        Assert.Contains("<triggers>", index);
        Assert.Contains("<trigger>帮我调研</trigger>", index);
    }

    [Fact]
    public void BuildIndex_ExcludesDisableModelInvocation()
    {
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "hidden",
                Description = "Hidden",
                Instructions = "...",
                Location = "/skills/hidden",
                DisableModelInvocation = true
            }
        };

        Assert.Equal("", SkillPromptBuilder.BuildIndex(skills));
    }

    [Fact]
    public void BuildSkillBody_ReturnsInstructionsFragment()
    {
        var skill = new SkillDefinition
        {
            Name = "search",
            Description = "Search",
            Instructions = "Do the thing.",
            Location = "/skills/search"
        };

        var body = SkillPromptBuilder.BuildSkillBody(skill);

        Assert.Contains("<skill-instructions>", body);
        Assert.Contains("## Skill: search", body);
        Assert.Contains("Do the thing.", body);
        Assert.Contains("</skill-instructions>", body);
    }

    [Fact]
    public void BuildSkillBody_DisableModelInvocation_ReturnsEmpty()
    {
        var skill = new SkillDefinition
        {
            Name = "hidden",
            Description = "Hidden",
            Instructions = "Hidden body.",
            Location = "/skills/hidden",
            DisableModelInvocation = true
        };

        Assert.Equal("", SkillPromptBuilder.BuildSkillBody(skill));
    }

    [Fact]
    public void BuildIndex_CustomTemplate_ReplacesSkillsPlaceholder()
    {
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "alpha",
                Description = "Alpha skill",
                Instructions = "...",
                Location = "/skills/alpha"
            }
        };

        var template = "<skills-section>\n{skills}\n</skills-section>";

        var index = SkillPromptBuilder.BuildIndex(skills, template);

        Assert.Contains("<skills-section>", index);
        Assert.Contains("</skills-section>", index);
        Assert.Contains("<name>alpha</name>", index);
        // The default envelope must NOT leak in when a custom template is supplied.
        Assert.DoesNotContain("<available-skills>", index);
        Assert.DoesNotContain("Only load what is needed", index);
    }

    [Fact]
    public void BuildIndex_CustomTemplate_ReplacesLoadAndResourceInstructions()
    {
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "rich",
                Description = "Has resources",
                Instructions = "...",
                Location = "/skills/rich",
                Resources =
                [
                    new SkillResource
                    {
                        Name = "ref.md",
                        RelativePath = "references/ref.md",
                        AbsolutePath = "/skills/rich/references/ref.md",
                        Kind = SkillResourceKind.Reference
                    }
                ]
            }
        };

        const string template = "PRELUDE\n{load_instruction}{resource_instruction}---\n{skills}\nEND";

        var index = SkillPromptBuilder.BuildIndex(skills, template);

        Assert.Contains("PRELUDE", index);
        Assert.Contains("END", index);
        Assert.Contains("`load_skill`", index);
        Assert.Contains("`read_skill_resource`", index);
        Assert.Contains("<name>rich</name>", index);
    }

    [Fact]
    public void BuildIndex_CustomTemplate_OmitsResourceInstructionWhenNoResources()
    {
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "plain",
                Description = "No resources",
                Instructions = "...",
                Location = "/skills/plain"
            }
        };

        const string template = "{load_instruction}{resource_instruction}---{skills}";

        var index = SkillPromptBuilder.BuildIndex(skills, template);

        Assert.Contains("`load_skill`", index);
        Assert.DoesNotContain("`read_skill_resource`", index);
    }

    [Fact]
    public void BuildIndex_CustomTemplate_MissingSkillsPlaceholder_Throws()
    {
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "alpha",
                Description = "Alpha",
                Instructions = "...",
                Location = "/skills/alpha"
            }
        };

        var ex = Assert.Throws<ArgumentException>(
            () => SkillPromptBuilder.BuildIndex(skills, "no placeholder here"));

        Assert.Contains("{skills}", ex.Message);
    }

    [Fact]
    public void BuildIndex_NullOrWhitespaceTemplate_FallsBackToDefault()
    {
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "alpha",
                Description = "Alpha",
                Instructions = "...",
                Location = "/skills/alpha"
            }
        };

        var fromNull = SkillPromptBuilder.BuildIndex(skills, template: null);
        var fromBlank = SkillPromptBuilder.BuildIndex(skills, template: "   ");
        var defaultIndex = SkillPromptBuilder.BuildIndex(skills);

        Assert.Equal(defaultIndex, fromNull);
        Assert.Equal(defaultIndex, fromBlank);
        Assert.Contains("<available-skills>", defaultIndex);
    }

    [Fact]
    public void BuildIndex_NoEligibleSkills_IgnoresCustomTemplate()
    {
        // When all skills are excluded from the model, BuildIndex must return an empty
        // string regardless of any custom template supplied — including ones missing
        // the {skills} placeholder, which would otherwise throw.
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "hidden",
                Description = "Hidden",
                Instructions = "...",
                Location = "/skills/hidden",
                DisableModelInvocation = true
            }
        };

        Assert.Equal("", SkillPromptBuilder.BuildIndex(skills, "broken template"));
    }
}

public class LoadSkillToolTests
{
    private static SkillDefinition Skill(string name, string body = "Body.", bool disableModel = false,
        IReadOnlyList<SkillResource>? resources = null,
        SkillArtifactContract? artifactContract = null) =>
        new()
        {
            Name = name,
            Description = $"Description of {name}",
            Instructions = body,
            Location = $"/skills/{name}",
            DisableModelInvocation = disableModel,
            Resources = resources ?? [],
            ArtifactContract = artifactContract
        };

    [Fact]
    public async Task ExecuteAsync_ReturnsSkillBody_ForKnownSkill()
    {
        var tool = new LoadSkillTool([Skill("search", body: "Search instructions.")]);

        var result = await tool.ExecuteAsync("""{"skill":"search"}""", default);

        Assert.Contains("<skill-instructions>", result);
        Assert.Contains("## Skill: search", result);
        Assert.Contains("Search instructions.", result);
    }

    [Fact]
    public async Task ExecuteAsync_IsCaseInsensitive()
    {
        var tool = new LoadSkillTool([Skill("Search")]);

        var result = await tool.ExecuteAsync("""{"skill":"SEARCH"}""", default);

        Assert.Contains("## Skill: Search", result);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownSkill_ReturnsErrorListingAvailable()
    {
        var tool = new LoadSkillTool([Skill("alpha"), Skill("beta")]);

        var result = await tool.ExecuteAsync("""{"skill":"gamma"}""", default);

        Assert.Contains("not found", result);
        Assert.Contains("alpha", result);
        Assert.Contains("beta", result);
    }

    [Fact]
    public async Task ExecuteAsync_MissingArgument_ReturnsError()
    {
        var tool = new LoadSkillTool([Skill("alpha")]);

        var result = await tool.ExecuteAsync("{}", default);

        Assert.StartsWith("Error:", result);
        Assert.Contains("missing required argument", result);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidJson_ReturnsError()
    {
        var tool = new LoadSkillTool([Skill("alpha")]);

        var result = await tool.ExecuteAsync("not-json", default);

        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public async Task ExecuteAsync_DisabledForModel_RejectsLoad()
    {
        var tool = new LoadSkillTool([Skill("hidden", disableModel: true)]);

        var result = await tool.ExecuteAsync("""{"skill":"hidden"}""", default);

        Assert.StartsWith("Error:", result);
        Assert.Contains("not available for model invocation", result);
    }

    [Fact]
    public async Task ExecuteAsync_AppendsResourceManifest_WhenPresent()
    {
        var resources = new List<SkillResource>
        {
            new()
            {
                Name = "guide.md",
                RelativePath = "references/guide.md",
                AbsolutePath = "/skills/rich/references/guide.md",
                Kind = SkillResourceKind.Reference
            }
        };
        var tool = new LoadSkillTool([Skill("rich", resources: resources)]);

        var result = await tool.ExecuteAsync("""{"skill":"rich"}""", default);

        Assert.Contains("<skill-instructions>", result);
        Assert.Contains("<skill-resources>", result);
        Assert.Contains("path=\"references/guide.md\"", result);
    }

    [Fact]
    public async Task ExecuteAsync_EscapesResourceManifestPath()
    {
        var resources = new List<SkillResource>
        {
            new()
            {
                Name = "bad.md",
                RelativePath = "references/bad\"<>&'.md",
                AbsolutePath = "/skills/rich/references/bad.md",
                Kind = SkillResourceKind.Reference
            }
        };
        var tool = new LoadSkillTool([Skill("rich", resources: resources)]);

        var result = await tool.ExecuteAsync("""{"skill":"rich"}""", default);

        Assert.Contains("path=\"references/bad&quot;&lt;&gt;&amp;&apos;.md\"", result);
        Assert.DoesNotContain("bad\"<>&'.md", result);
    }

    [Fact]
    public async Task ExecuteAsync_AppendsArtifactContract_WhenPresent()
    {
        var contract = new SkillArtifactContract
        {
            SchemaVersion = 2,
            Stages =
            [
                new SkillArtifactStageContract
                {
                    Name = "design",
                    Label = "Design Phase",
                    Artifacts =
                    [
                        new SkillArtifactTypeContract
                        {
                            Type = "requirements",
                            Label = "Requirements Doc",
                            Display = "badge",
                            Terminal = true
                        }
                    ]
                },
                new SkillArtifactStageContract
                {
                    Name = "review",
                    Label = "Review Phase",
                    Gate = new SkillArtifactStageGateContract { RequiresStage = "design" },
                    Artifacts =
                    [
                        new SkillArtifactTypeContract
                        {
                            Type = "review_checklist",
                            Label = "Review Checklist",
                            Display = "tree"
                            // Terminal omitted (defaults to null) — not written in JSON.
                        }
                    ]
                }
            ]
        };
        var tool = new LoadSkillTool([Skill("pipeline", artifactContract: contract)]);

        var result = await tool.ExecuteAsync("""{"skill":"pipeline"}""", default);

        Assert.Contains("<skill-instructions>", result);
        Assert.Contains("<skill-artifact-contract>", result);
        Assert.Contains("\"schema_version\": 2", result);
        Assert.Contains("\"name\": \"design\"", result);
        Assert.Contains("\"label\": \"Design Phase\"", result);
        Assert.Contains("\"type\": \"requirements\"", result);
        Assert.Contains("\"display\": \"badge\"", result);
        Assert.Contains("\"terminal\": true", result);
        Assert.Contains("\"name\": \"review\"", result);
        Assert.Contains("\"requires_stage\": \"design\"", result);
        Assert.Contains("\"type\": \"review_checklist\"", result);
        Assert.Contains("\"display\": \"tree\"", result);
        Assert.DoesNotContain("\"terminal\": false", result);
    }

    [Fact]
    public async Task ExecuteAsync_AppendsArtifactContract_BeforeResourceManifest()
    {
        var resources = new List<SkillResource>
        {
            new()
            {
                Name = "spec.md",
                RelativePath = "references/spec.md",
                AbsolutePath = "/skills/dsgn/references/spec.md",
                Kind = SkillResourceKind.Reference
            }
        };
        var contract = new SkillArtifactContract
        {
            Stages =
            [
                new SkillArtifactStageContract
                {
                    Name = "plan",
                    Artifacts = [new SkillArtifactTypeContract { Type = "requirements" }]
                }
            ]
        };
        var tool = new LoadSkillTool([Skill("dsgn", resources: resources, artifactContract: contract)]);

        var result = await tool.ExecuteAsync("""{"skill":"dsgn"}""", default);

        var contractIndex = result.IndexOf("<skill-artifact-contract>", StringComparison.Ordinal);
        var resourcesIndex = result.IndexOf("<skill-resources>", StringComparison.Ordinal);
        Assert.True(contractIndex >= 0);
        Assert.True(resourcesIndex >= 0);
        Assert.True(contractIndex < resourcesIndex, "Artifact contract must appear before resource manifest");
    }

    [Fact]
    public async Task ExecuteAsync_NoArtifactContract_DoesNotInjectBlock()
    {
        var tool = new LoadSkillTool([Skill("lean")]);

        var result = await tool.ExecuteAsync("""{"skill":"lean"}""", default);

        Assert.Contains("<skill-instructions>", result);
        Assert.DoesNotContain("<skill-artifact-contract>", result);
        Assert.DoesNotContain("schema_version", result);
    }

    [Fact]
    public async Task ExecuteAsync_AcceptsSkillNameAlias()
    {
        var tool = new LoadSkillTool([Skill("alpha")]);

        var result = await tool.ExecuteAsync("""{"skill_name":"alpha"}""", default);

        Assert.Contains("## Skill: alpha", result);
    }

    [Fact]
    public async Task ExecuteAsync_ResolvesViaSkillKeyAlias()
    {
        var skill = new SkillDefinition
        {
            Name = "real-name",
            Description = "d",
            Instructions = "Body.",
            Location = "/skills/real-name",
            Metadata = new SkillMetadata { SkillKey = "alias" }
        };
        var tool = new LoadSkillTool([skill]);

        var result = await tool.ExecuteAsync("""{"skill":"alias"}""", default);

        Assert.Contains("## Skill: real-name", result);
    }

    [Fact]
    public async Task ExecuteAsync_ProviderIsReevaluatedPerCall()
    {
        var skills = new List<SkillDefinition> { Skill("first") };
        var tool = new LoadSkillTool(() => skills);

        var first = await tool.ExecuteAsync("""{"skill":"first"}""", default);
        Assert.Contains("## Skill: first", first);

        // Hot reload simulation: swap the loaded set.
        skills.Clear();
        skills.Add(Skill("second"));

        var second = await tool.ExecuteAsync("""{"skill":"second"}""", default);
        Assert.Contains("## Skill: second", second);

        var missing = await tool.ExecuteAsync("""{"skill":"first"}""", default);
        Assert.Contains("not found", missing);
    }
}

public class MetaSkillResolverTests
{
    [Fact]
    public void TryResolve_NoSkills_ReturnsFalse()
    {
        var resolved = MetaSkillResolver.TryResolve([], "帮我调研", out var skill);

        Assert.False(resolved);
        Assert.Null(skill);
    }

    [Fact]
    public void TryResolve_MatchesByTrigger_ReturnsMetaSkill()
    {
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "meta-research",
                Kind = SkillKind.Meta,
                Description = "Meta research",
                Instructions = "...",
                Location = "/skills/meta-research",
                Triggers = ["帮我调研"]
            }
        };

        var resolved = MetaSkillResolver.TryResolve(skills, "请帮我调研这个市场", out var skill);

        Assert.True(resolved);
        Assert.NotNull(skill);
        Assert.Equal("meta-research", skill!.Name);
    }

    [Fact]
    public void TryResolve_HigherMetaPriority_Wins()
    {
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "meta-low",
                Kind = SkillKind.Meta,
                Description = "Low priority",
                Instructions = "...",
                Location = "/skills/meta-low",
                MetaPriority = 10,
                Triggers = ["调研"]
            },
            new()
            {
                Name = "meta-high",
                Kind = SkillKind.Meta,
                Description = "High priority",
                Instructions = "...",
                Location = "/skills/meta-high",
                MetaPriority = 80,
                Triggers = ["调研"]
            }
        };

        var resolved = MetaSkillResolver.TryResolve(skills, "调研这个问题", out var skill);

        Assert.True(resolved);
        Assert.NotNull(skill);
        Assert.Equal("meta-high", skill!.Name);
    }

    [Fact]
    public void TryResolve_NonMetaSkills_AreIgnored()
    {
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "plain",
                Kind = SkillKind.Standard,
                Description = "Plain skill",
                Instructions = "...",
                Location = "/skills/plain",
                Triggers = ["调研"]
            }
        };

        var resolved = MetaSkillResolver.TryResolve(skills, "调研这个问题", out var skill);

        Assert.False(resolved);
        Assert.Null(skill);
    }
}

public class MetaInvokeToolTests
{
    [Fact]
    public async Task ExecuteAsync_WithValidMetaSkill_ReturnsIntentPayload()
    {
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "meta-research",
                Kind = SkillKind.Meta,
                Description = "Meta research flow",
                Instructions = "...",
                Location = "/skills/meta-research",
                FinalTextMode = "auto",
                MetaPriority = 60,
                Composition = new MetaSkillComposition
                {
                    Steps =
                    [
                        new MetaSkillStepDefinition { Id = "s1", Kind = "agent" },
                        new MetaSkillStepDefinition { Id = "s2", Kind = "llm_chat", DependsOn = ["s1"] }
                    ]
                }
            }
        };

        var tool = new MetaInvokeTool(() => skills);
        var result = await tool.ExecuteAsync("""{"skill":"meta-research","input":"请调研并给建议"}""", default);

        Assert.Contains("\"skill\":\"meta-research\"", result);
        Assert.Contains("\"finalTextMode\":\"auto\"", result);
        Assert.Contains("\"metaPriority\":60", result);
        Assert.Contains("\"id\":\"s1\"", result);
    }

    [Fact]
    public async Task ExecuteAsync_MissingSkill_ReturnsError()
    {
        var tool = new MetaInvokeTool(() => []);

        var result = await tool.ExecuteAsync("{}", default);

        Assert.Contains("missing required argument 'skill'", result);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownMetaSkill_ReturnsAvailableList()
    {
        var tool = new MetaInvokeTool(() =>
        [
            new SkillDefinition
            {
                Name = "meta-a",
                Kind = SkillKind.Meta,
                Description = "Meta A",
                Instructions = "...",
                Location = "/skills/meta-a"
            }
        ]);

        var result = await tool.ExecuteAsync("""{"skill":"meta-b"}""", default);

        Assert.Contains("not found", result);
        Assert.Contains("meta-a", result);
    }
}

public class ReadSkillResourceToolTests : IDisposable
{
    private readonly string _root;

    public ReadSkillResourceToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "openclaw-readskill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore cleanup races */ }
    }

    private SkillDefinition WriteSkillWithResource(string skillName, string relativePath, string content,
        SkillResourceKind kind = SkillResourceKind.Reference, bool disableModel = false)
    {
        var skillDir = Path.Combine(_root, skillName);
        var fullPath = Path.Combine(skillDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);

        return new SkillDefinition
        {
            Name = skillName,
            Description = $"Description of {skillName}",
            Instructions = "Body.",
            Location = skillDir,
            DisableModelInvocation = disableModel,
            Resources =
            [
                new SkillResource
                {
                    Name = Path.GetFileName(fullPath),
                    RelativePath = relativePath,
                    AbsolutePath = fullPath,
                    Kind = kind
                }
            ]
        };
    }

    [Fact]
    public async Task ExecuteAsync_ReadsResourceByRelativePath()
    {
        var skill = WriteSkillWithResource("alpha", "references/guide.md", "Hello, world.");
        var tool = new ReadSkillResourceTool([skill]);

        var result = await tool.ExecuteAsync("""{"skill":"alpha","resource":"references/guide.md"}""", default);

        Assert.Equal("Hello, world.", result);
    }

    [Fact]
    public async Task ExecuteAsync_ReadsResourceByBareFileName()
    {
        var skill = WriteSkillWithResource("alpha", "references/guide.md", "Hi.");
        var tool = new ReadSkillResourceTool([skill]);

        var result = await tool.ExecuteAsync("""{"skill":"alpha","resource":"guide.md"}""", default);

        Assert.Equal("Hi.", result);
    }

    [Fact]
    public async Task ExecuteAsync_AcceptsBackslashSeparator()
    {
        var skill = WriteSkillWithResource("alpha", "references/guide.md", "Body.");
        var tool = new ReadSkillResourceTool([skill]);

        var result = await tool.ExecuteAsync("""{"skill":"alpha","resource":"references\\guide.md"}""", default);

        Assert.Equal("Body.", result);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownSkill_ReturnsError()
    {
        var skill = WriteSkillWithResource("alpha", "references/guide.md", "Body.");
        var tool = new ReadSkillResourceTool([skill]);

        var result = await tool.ExecuteAsync("""{"skill":"missing","resource":"guide.md"}""", default);

        Assert.StartsWith("Error:", result);
        Assert.Contains("not found", result);
        Assert.Contains("alpha", result);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownResource_ListsAvailable()
    {
        var skill = WriteSkillWithResource("alpha", "references/guide.md", "Body.");
        var tool = new ReadSkillResourceTool([skill]);

        var result = await tool.ExecuteAsync("""{"skill":"alpha","resource":"missing.md"}""", default);

        Assert.StartsWith("Error:", result);
        Assert.Contains("references/guide.md", result);
    }

    [Fact]
    public async Task ExecuteAsync_MissingArguments_ReturnError()
    {
        var tool = new ReadSkillResourceTool([WriteSkillWithResource("alpha", "references/guide.md", "x")]);

        var missingResource = await tool.ExecuteAsync("""{"skill":"alpha"}""", default);
        Assert.Contains("'resource'", missingResource);

        var missingSkill = await tool.ExecuteAsync("""{"resource":"guide.md"}""", default);
        Assert.Contains("'skill'", missingSkill);

        var emptyArgs = await tool.ExecuteAsync("", default);
        Assert.StartsWith("Error:", emptyArgs);
    }

    [Fact]
    public async Task ExecuteAsync_DisabledSkill_RejectsRead()
    {
        var skill = WriteSkillWithResource("hidden", "references/g.md", "x", disableModel: true);
        var tool = new ReadSkillResourceTool([skill]);

        var result = await tool.ExecuteAsync("""{"skill":"hidden","resource":"g.md"}""", default);

        Assert.StartsWith("Error:", result);
        Assert.Contains("not available for model invocation", result);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsResourceOutsideSkillRoot()
    {
        // Build a skill whose Resources entry points OUTSIDE the skill's Location — simulates a
        // post-discovery symlink or hand-crafted SkillDefinition.
        var skillDir = Path.Combine(_root, "alpha");
        Directory.CreateDirectory(skillDir);
        var outsidePath = Path.Combine(_root, "evil.md");
        File.WriteAllText(outsidePath, "secret");

        var skill = new SkillDefinition
        {
            Name = "alpha",
            Description = "d",
            Instructions = "b",
            Location = skillDir,
            Resources =
            [
                new SkillResource
                {
                    Name = "evil.md",
                    RelativePath = "../evil.md",
                    AbsolutePath = outsidePath,
                    Kind = SkillResourceKind.Reference
                }
            ]
        };
        var tool = new ReadSkillResourceTool([skill]);

        var result = await tool.ExecuteAsync("""{"skill":"alpha","resource":"../evil.md"}""", default);

        Assert.StartsWith("Error:", result);
        Assert.Contains("outside skill root", result);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsSymlinkResourceEscapingSkillRoot()
    {
        var skillDir = Path.Combine(_root, "alpha");
        var referencesDir = Path.Combine(skillDir, "references");
        Directory.CreateDirectory(referencesDir);
        var outsidePath = Path.Combine(_root, "secret.md");
        File.WriteAllText(outsidePath, "secret");
        var linkPath = Path.Combine(referencesDir, "guide.md");

        try
        {
            File.CreateSymbolicLink(linkPath, outsidePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var skill = new SkillDefinition
        {
            Name = "alpha",
            Description = "d",
            Instructions = "b",
            Location = skillDir,
            Resources =
            [
                new SkillResource
                {
                    Name = "guide.md",
                    RelativePath = "references/guide.md",
                    AbsolutePath = linkPath,
                    Kind = SkillResourceKind.Reference
                }
            ]
        };
        var tool = new ReadSkillResourceTool([skill]);

        var result = await tool.ExecuteAsync("""{"skill":"alpha","resource":"references/guide.md"}""", default);

        Assert.StartsWith("Error:", result);
        Assert.Contains("symlink", result);
    }

    [Fact]
    public async Task ExecuteAsync_ProviderIsReevaluatedPerCall()
    {
        var skill = WriteSkillWithResource("alpha", "references/g.md", "first");
        var skills = new List<SkillDefinition> { skill };
        var tool = new ReadSkillResourceTool(() => skills);

        var first = await tool.ExecuteAsync("""{"skill":"alpha","resource":"g.md"}""", default);
        Assert.Equal("first", first);

        // Simulate hot reload pointing to a different file.
        var skill2 = WriteSkillWithResource("alpha", "references/g.md", "second");
        skills.Clear();
        skills.Add(skill2);

        var second = await tool.ExecuteAsync("""{"skill":"alpha","resource":"g.md"}""", default);
        Assert.Equal("second", second);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsResourceExceedingCustomByteLimit()
    {
        var skill = WriteSkillWithResource("alpha", "references/big.md", new string('x', 1024));
        // Cap below file size to force the size-limit branch.
        var tool = new ReadSkillResourceTool([skill], maxResourceBytes: 256);

        var result = await tool.ExecuteAsync("""{"skill":"alpha","resource":"big.md"}""", default);

        Assert.StartsWith("Error:", result);
        Assert.Contains("256", result);
        Assert.Contains("workspace file tools", result);
    }

    [Fact]
    public async Task ExecuteAsync_NonPositiveByteLimit_FallsBackToDefault()
    {
        // 256 KB default cap allows a 1 KB body even when caller passes 0/-1.
        var skill = WriteSkillWithResource("alpha", "references/g.md", new string('y', 1024));
        var toolFromZero = new ReadSkillResourceTool([skill], maxResourceBytes: 0);
        var toolFromNegative = new ReadSkillResourceTool([skill], maxResourceBytes: -42);

        var fromZero = await toolFromZero.ExecuteAsync("""{"skill":"alpha","resource":"g.md"}""", default);
        var fromNegative = await toolFromNegative.ExecuteAsync("""{"skill":"alpha","resource":"g.md"}""", default);

        Assert.DoesNotContain("Error:", fromZero);
        Assert.Equal(1024, fromZero.Length);
        Assert.DoesNotContain("Error:", fromNegative);
        Assert.Equal(1024, fromNegative.Length);
    }

    [Fact]
    public async Task ExecuteAsync_CustomByteLimit_AcceptsResourceUnderCap()
    {
        var skill = WriteSkillWithResource("alpha", "references/small.md", "short");
        var tool = new ReadSkillResourceTool([skill], maxResourceBytes: 1024);

        var result = await tool.ExecuteAsync("""{"skill":"alpha","resource":"small.md"}""", default);

        Assert.Equal("short", result);
    }

    [Fact]
    public void DefaultMaxResourceBytes_MatchesPublicConstant()
    {
        // Lock the public default so SkillsConfig.MaxResourceReadBytes can rely on it.
        Assert.Equal(256 * 1024, ReadSkillResourceTool.DefaultMaxResourceBytes);
    }
}

public class SkillProjectionResolverTests
{
    private static SkillDefinition SkillWithProjections(
        string name,
        string tempDir,
        string relativePath,
        IReadOnlyList<string>? explicitArtifactSignals = null,
        IReadOnlyList<string>? allowedTermsSignals = null,
        int producerPriority = 0,
        string? unresolvedItemPolicy = null)
    {
        var projectionDir = Path.Combine(tempDir, Path.GetDirectoryName(relativePath)!);
        Directory.CreateDirectory(projectionDir);

        File.WriteAllText(
            Path.Combine(tempDir, relativePath),
            $$"""
            {
              "mapping_policy": {
                "unresolved_item_policy": "{{unresolvedItemPolicy ?? "allow_unmapped_terms"}}"
              },
              "prompt_projection": {
                "allowed_terms": {{(allowedTermsSignals is not null ? JsonSerializer.Serialize(allowedTermsSignals) : "[]")}}
              },
              "delivery_artifacts": [],
              "dropped_items": [],
              "open_questions": []
            }
            """);

        return new SkillDefinition
        {
            Name = name,
            Description = $"Description of {name}",
            Instructions = "Base skill instructions.",
            Location = $"/skills/{name}",
            ProjectionContracts =
            [
                new SkillProjectionContractSet
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
                                    DomainSlug = "task-execution",
                                    PrimaryIntentSignals = ["task execution"],
                                    SupportingSignals = ["run"],
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
                                    TargetView = SkillProjectionViewKeys.PromptConstraint,
                                    ExplicitOutputSignals = explicitArtifactSignals ?? [],
                                    StrongSignals = [],
                                    SupportingSignals = [],
                                    DemoteWhenCompetingViewSignals = []
                                }
                            ]
                        }
                    }
                }
            ]
        };
    }

    [Fact]
    public void ResolveForRequest_NoProjectionContracts_ReturnsNoContracts()
    {
        var skill = new SkillDefinition
        {
            Name = "bare",
            Description = "No projections",
            Instructions = "Body.",
            Location = "/skills/bare"
        };

        var result = SkillProjectionResolver.ResolveForRequest(skill, "task execution",
            NullLogger.Instance);

        Assert.Equal("bare", result.SkillName);
        Assert.False(result.HasContracts);
        Assert.False(result.IsBlocked);
    }

    [Fact]
    public void ResolveForRequest_MatchingRequest_PatchesInstructionsWithProjection()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-proj-resolve-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var relativePath = Path.Combine("task-execution",
                $"task-execution.{SkillProjectionViewKeys.PromptConstraint}.projection.json").Replace('\\', '/');
            var skill = SkillWithProjections("software-developer", tempDir, relativePath,
                explicitArtifactSignals: ["prompt policy"],
                allowedTermsSignals: ["review guidance", "prompt policy"]);

            var result = SkillProjectionResolver.ResolveForRequest(skill, "task execution with prompt policy",
                NullLogger.Instance);

            Assert.Equal("software-developer", result.SkillName);
            Assert.True(result.HasContracts);
            Assert.False(result.IsBlocked);
            Assert.Equal("task-execution", result.SelectedTopic);
            Assert.Equal("prompt-constraint", result.SelectedTargetView);
            Assert.NotNull(result.Projection);
            Assert.Contains("review guidance", result.Projection!.PromptProjection.AllowedTerms);

            var patch = SkillProjectionResolver.BuildPromptPatch(result);
            Assert.Contains("[Projection Route]", patch);
            Assert.Contains("Selected topic: task-execution", patch);
            Assert.Contains("Selected target view: prompt-constraint", patch);
            Assert.Contains("review guidance", patch);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ResolveForRequest_DuplicateScoringKeys_DoesNotThrow()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-proj-dup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var relativePath = Path.Combine("task-execution",
                $"task-execution.{SkillProjectionViewKeys.PromptConstraint}.projection.json").Replace('\\', '/');
            var skill = SkillWithProjections("software-developer", tempDir, relativePath,
                allowedTermsSignals: ["review guidance"]);
            var contract = skill.ProjectionContracts[0];
            var topicSignal = contract.Index.TopicScoring!.Topics[0];
            var viewSignal = contract.Index.TargetViewScoring!.Views[0];

            skill = new SkillDefinition
            {
                Name = skill.Name,
                Description = skill.Description,
                Instructions = skill.Instructions,
                Location = skill.Location,
                ProjectionContracts =
                [
                    new SkillProjectionContractSet
                    {
                        ProducerName = contract.ProducerName,
                        ProducerPriority = contract.ProducerPriority,
                        RootPath = contract.RootPath,
                        Index = new ProjectionContractIndex
                        {
                            ProducerSkill = contract.Index.ProducerSkill,
                            ProducerPriority = contract.Index.ProducerPriority,
                            DefaultSelectionPolicy = contract.Index.DefaultSelectionPolicy,
                            Topics = contract.Index.Topics,
                            TopicScoring = new ProjectionTopicScoring
                            {
                                ClarifyWhenScoreGapBelow = contract.Index.TopicScoring.ClarifyWhenScoreGapBelow,
                                ScoreDimensions = contract.Index.TopicScoring.ScoreDimensions,
                                Topics = [topicSignal, topicSignal]
                            },
                            TargetViewScoring = new ProjectionTargetViewScoring
                            {
                                ClarifyWhenScoreGapBelow = contract.Index.TargetViewScoring.ClarifyWhenScoreGapBelow,
                                PreferExplicitUserArtifactRequests = contract.Index.TargetViewScoring.PreferExplicitUserArtifactRequests,
                                ScoreDimensions = contract.Index.TargetViewScoring.ScoreDimensions,
                                Views = [viewSignal, viewSignal],
                                WithinTopicOverrides = contract.Index.TargetViewScoring.WithinTopicOverrides
                            }
                        }
                    }
                ]
            };

            var result = SkillProjectionResolver.ResolveForRequest(skill, "task execution",
                NullLogger.Instance);

            Assert.False(result.IsBlocked);
            Assert.Equal("task-execution", result.SelectedTopic);
            Assert.Contains("review guidance", result.Projection!.PromptProjection.AllowedTerms);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ResolveForRequest_PathEscapingContractRoot_ReturnsBlocked()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), $"openclaw-proj-root-{Guid.NewGuid():N}");
        var outsideDir = Path.Combine(Path.GetTempPath(), $"openclaw-proj-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootDir);
        Directory.CreateDirectory(outsideDir);
        try
        {
            var outsidePath = Path.Combine(outsideDir, "projection.json");
            File.WriteAllText(outsidePath,
                """
                {
                  "mapping_policy": {
                    "unresolved_item_policy": "allow_unmapped_terms"
                  },
                  "prompt_projection": {
                    "allowed_terms": ["outside"]
                  },
                  "delivery_artifacts": [],
                  "dropped_items": [],
                  "open_questions": []
                }
                """);

            var escapingRelativePath = Path.GetRelativePath(rootDir, outsidePath).Replace('\\', '/');
            var skill = new SkillDefinition
            {
                Name = "software-developer",
                Description = "Description",
                Instructions = "Base skill instructions.",
                Location = rootDir,
                ProjectionContracts =
                [
                    new SkillProjectionContractSet
                    {
                        ProducerName = "test-producer",
                        RootPath = rootDir,
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
                                            Path = escapingRelativePath
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
                            }
                        }
                    }
                ]
            };

            var result = SkillProjectionResolver.ResolveForRequest(skill, "task execution",
                NullLogger.Instance);

            Assert.True(result.IsBlocked);
            Assert.Contains("outside the projection contract root", result.BlockReason);
        }
        finally
        {
            Directory.Delete(rootDir, true);
            Directory.Delete(outsideDir, true);
        }
    }

    [Fact]
    public void ResolveForRequest_BlockOnOpenQuestions_ReturnsBlocked()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-proj-block-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var relativePath = Path.Combine("task-execution",
                $"task-execution.{SkillProjectionViewKeys.PromptConstraint}.projection.json").Replace('\\', '/');
            var skill = SkillWithProjections("software-developer", tempDir, relativePath,
                explicitArtifactSignals: ["prompt policy"]);

            // Overwrite projection file to include open_questions.
            File.WriteAllText(Path.Combine(tempDir, relativePath),
                """
                {
                  "mapping_policy": {
                    "unresolved_item_policy": "block_or_escalate"
                  },
                  "prompt_projection": {
                    "allowed_terms": ["skills_config"]
                  },
                  "delivery_artifacts": [],
                  "dropped_items": [],
                  "open_questions": ["Needs clarification."]
                }
                """);

            // Override the default_selection_policy in the index to block on open questions.
            skill = new SkillDefinition
            {
                Name = skill.Name,
                Description = skill.Description,
                Instructions = skill.Instructions,
                Location = skill.Location,
                ProjectionContracts = skill.ProjectionContracts.Select(pc => new SkillProjectionContractSet
                {
                    ProducerName = pc.ProducerName,
                    ProducerPriority = pc.ProducerPriority,
                    RootPath = pc.RootPath,
                    Index = pc.Index is { } idx ? new ProjectionContractIndex
                    {
                        ProducerSkill = idx.ProducerSkill,
                        ProducerPriority = idx.ProducerPriority,
                        DefaultSelectionPolicy = new ProjectionSelectionPolicy
                        {
                            BlockOnOpenQuestions = true,
                            FallbackOrderByTargetView = idx.DefaultSelectionPolicy.FallbackOrderByTargetView
                        },
                        Topics = idx.Topics,
                        TopicScoring = idx.TopicScoring,
                        TargetViewScoring = idx.TargetViewScoring
                    } : pc.Index
                }).ToList()
            };

            var result = SkillProjectionResolver.ResolveForRequest(skill, "task execution with prompt policy",
                NullLogger.Instance);

            Assert.True(result.IsBlocked);
            Assert.Contains("open questions", result.BlockReason);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ResolveForRequest_NoMatchingSignals_FallsBackToOrderedTargetView()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-proj-fallback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var relativePath = Path.Combine("task-execution",
                $"task-execution.{SkillProjectionViewKeys.PromptConstraint}.projection.json").Replace('\\', '/');
            var skill = SkillWithProjections("software-developer", tempDir, relativePath,
                explicitArtifactSignals: ["prompt policy"]);

            // Request text matches no signals; fallback ordering kicks in.
            var result = SkillProjectionResolver.ResolveForRequest(skill, "completely unrelated topic",
                NullLogger.Instance);

            // Fallback resolved successfully, not blocked.
            Assert.False(result.IsBlocked);
            Assert.Equal("task-execution", result.SelectedTopic);
            Assert.Equal("prompt-constraint", result.SelectedTargetView);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BuildPromptPatch_EmptyResolution_ReturnsEmptyString()
    {
        var resolution = new SkillProjectionResolution
        {
            SkillName = "test",
            HasContracts = true,
            IsBlocked = true,
            BlockReason = "No matching view"
        };

        var patch = SkillProjectionResolver.BuildPromptPatch(resolution);
        Assert.Equal("", patch);
    }

    [Fact]
    public void BuildPromptPatch_WithPromptAssumptionPolicy_FormatsConstraint()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-proj-patch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var relativePath = Path.Combine("task-execution",
                $"task-execution.{SkillProjectionViewKeys.PromptConstraint}.projection.json").Replace('\\', '/');
            var skill = SkillWithProjections("software-developer", tempDir, relativePath,
                explicitArtifactSignals: ["prompt policy"],
                allowedTermsSignals: ["approved term"],
                unresolvedItemPolicy: null);

            // Overwrite the projection file to include prompt_assumption_policy.
            File.WriteAllText(Path.Combine(tempDir, relativePath),
                """
                {
                  "mapping_policy": {
                    "unresolved_item_policy": "allow_unmapped_terms",
                    "prompt_assumption_policy": "disallow_unmapped_terms"
                  },
                  "prompt_projection": {
                    "allowed_terms": ["approved term"]
                  },
                  "delivery_artifacts": [],
                  "dropped_items": [],
                  "open_questions": []
                }
                """);

            var result = SkillProjectionResolver.ResolveForRequest(skill, "task execution with prompt policy",
                NullLogger.Instance);

            Assert.False(result.IsBlocked);
            var patch = SkillProjectionResolver.BuildPromptPatch(result);
            Assert.Contains("Do not use unmapped terms", patch);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void LoadAll_PluginCommandRoot_MapsMarkdownCommandsIntoSkills()
    {
        var root = Path.Combine(Path.GetTempPath(), $"openclaw-bundle-commands-{Guid.NewGuid():N}");
        var commands = Path.Combine(root, "commands");
        Directory.CreateDirectory(commands);
        File.WriteAllText(
            Path.Combine(commands, "review.md"),
            "---\ndescription: Review a proposed change\n---\nReview the change and report concrete risks.");

        try
        {
            var skills = SkillLoader.LoadAll(
                new SkillsConfig
                {
                    Enabled = true,
                    Load = new SkillLoadConfig
                    {
                        IncludeBundled = false,
                        IncludeManaged = false,
                        IncludeWorkspace = false
                    }
                },
                null,
                NullLogger.Instance,
                [commands]);

            var command = Assert.Single(skills);
            Assert.Equal("review", command.Name);
            Assert.Equal("Review a proposed change", command.Description);
            Assert.Contains("concrete risks", command.Instructions, StringComparison.Ordinal);
            Assert.True(command.UserInvocable);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LoadAll_PluginCommandRoot_IgnoresNestedFrontmatterName()
    {
        var root = Path.Combine(Path.GetTempPath(), $"openclaw-bundle-command-name-{Guid.NewGuid():N}");
        var commands = Path.Combine(root, "commands");
        Directory.CreateDirectory(commands);
        File.WriteAllText(
            Path.Combine(commands, "review.md"),
            "---\nmetadata:\n  name: nested-value\ndescription: Review a proposed change\n---\nReview the change.");

        try
        {
            var skills = SkillLoader.LoadAll(
                new SkillsConfig
                {
                    Enabled = true,
                    Load = new SkillLoadConfig
                    {
                        IncludeBundled = false,
                        IncludeManaged = false,
                        IncludeWorkspace = false
                    }
                },
                null,
                NullLogger.Instance,
                [commands]);

            Assert.Equal("review", Assert.Single(skills).Name);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

/// <summary>Minimal ILogger for tests.</summary>
file sealed class TestLogger : Microsoft.Extensions.Logging.ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) { }
}

file sealed class CapturingTestLogger : Microsoft.Extensions.Logging.ILogger
{
    public List<string> WarningMessages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel)
        => logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel != Microsoft.Extensions.Logging.LogLevel.Warning)
            return;

        WarningMessages.Add(formatter(state, exception));
    }
}

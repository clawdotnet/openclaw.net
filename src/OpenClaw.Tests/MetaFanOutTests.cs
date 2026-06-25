using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Skills;
using Xunit;

namespace OpenClaw.Tests;

public sealed class MetaFanOutTests
{
    // ── ListToolsTool ──

    [Fact]
    public async Task ListToolsTool_ReturnsAllDescriptors_WhenNoFilter()
    {
        var descriptors = new List<ToolDescriptor>
        {
            new("web_search", "Search the web", """{"type":"object"}"""),
            new("file_read", "Read a file", """{"type":"object"}"""),
            new("shell_exec", "Execute a shell command", """{"type":"object"}"""),
        };

        var tool = new ListToolsTool(() => descriptors);
        var result = await tool.ExecuteAsync("{}", CancellationToken.None);

        Assert.NotNull(result);
        var parsed = JsonSerializer.Deserialize<List<ToolDescriptor>>(result)!;
        Assert.Equal(3, parsed.Count);
        Assert.Contains(parsed, d => d.Name == "web_search");
        Assert.Contains(parsed, d => d.Name == "file_read");
        Assert.Contains(parsed, d => d.Name == "shell_exec");
    }

    [Fact]
    public async Task ListToolsTool_FiltersByName_WhenFilterProvided()
    {
        var descriptors = new List<ToolDescriptor>
        {
            new("web_search", "Search the web", """{"type":"object"}"""),
            new("web_fetch", "Fetch a URL", """{"type":"object"}"""),
            new("file_read", "Read a file", """{"type":"object"}"""),
        };

        var tool = new ListToolsTool(() => descriptors);
        var result = await tool.ExecuteAsync("""{"filter":"web"}""", CancellationToken.None);

        var parsed = JsonSerializer.Deserialize<List<ToolDescriptor>>(result)!;
        Assert.Equal(2, parsed.Count);
        Assert.All(parsed, d => Assert.Contains("web", d.Name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListToolsTool_ReturnsEmpty_WhenFilterHasNoMatch()
    {
        var descriptors = new List<ToolDescriptor>
        {
            new("web_search", "Search", """{"type":"object"}"""),
        };

        var tool = new ListToolsTool(() => descriptors);
        var result = await tool.ExecuteAsync("""{"filter":"nonexistent"}""", CancellationToken.None);

        var parsed = JsonSerializer.Deserialize<List<ToolDescriptor>>(result)!;
        Assert.Empty(parsed);
    }

    [Fact]
    public async Task ListToolsTool_ReturnsEmpty_WhenProviderReturnsEmpty()
    {
        var tool = new ListToolsTool(() => []);
        var result = await tool.ExecuteAsync("{}", CancellationToken.None);

        var parsed = JsonSerializer.Deserialize<List<ToolDescriptor>>(result)!;
        Assert.Empty(parsed);
    }

    [Fact]
    public async Task ListToolsTool_ReturnsAll_WhenInvalidJson()
    {
        var descriptors = new List<ToolDescriptor>
        {
            new("test", "desc", "{}"),
        };

        var tool = new ListToolsTool(() => descriptors);
        var result = await tool.ExecuteAsync("not valid json", CancellationToken.None);

        var parsed = JsonSerializer.Deserialize<List<ToolDescriptor>>(result)!;
        Assert.Single(parsed);
    }

    [Fact]
    public async Task ListToolsTool_RespectsCancellation()
    {
        var tool = new ListToolsTool(() => []);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => tool.ExecuteAsync("{}", cts.Token).AsTask());
    }

    // ── SkillLoader: fan_out validation ──

    [Fact]
    public void ParseSkillContent_ValidFanOutStep_ParsesSuccessfully()
    {
        var content = """
            ---
            name: fanout-test
            description: Tests fan_out parsing
            kind: meta
            composition:
              steps:
                - id: search_all
                  kind: fan_out
                  iterable: "{{ outputs.extract | from_json }}"
                  fan_out_max_concurrency: 3
                  fan_out_merge_mode: json_array
                  fan_out_template:
                    kind: tool_call
                    tool: web_search
            ---
            Instructions body.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/fanout-test", SkillSource.Workspace);

        Assert.NotNull(skill);
        Assert.Equal(SkillKind.Meta, skill!.Kind);
        Assert.NotNull(skill.Composition);
        Assert.Single(skill.Composition!.Steps);

        var step = skill.Composition.Steps[0];
        Assert.Equal("search_all", step.Id);
        Assert.Equal("fan_out", step.Kind);
        Assert.Equal("{{ outputs.extract | from_json }}", step.Iterable);
        Assert.Equal(3, step.FanOutMaxConcurrency);
        Assert.Equal("json_array", step.FanOutMergeMode);
        Assert.NotNull(step.FanOutTemplate);
        Assert.Equal("tool_call", step.FanOutTemplate!.Kind);
        Assert.Equal("web_search", step.FanOutTemplate.Tool);
    }

    [Fact]
    public void ParseSkillContent_FanOutMissingIterable_ReturnsNull()
    {
        var content = """
            ---
            name: bad-fanout
            description: Missing iterable
            kind: meta
            composition:
              steps:
                - id: bad_step
                  kind: fan_out
                  fan_out_template:
                    kind: tool_call
                    tool: web_search
            ---
            Body.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/bad-fanout", SkillSource.Workspace);
        Assert.Null(skill);
    }

    [Fact]
    public void ParseSkillContent_FanOutMissingTemplate_ReturnsNull()
    {
        var content = """
            ---
            name: bad-fanout2
            description: Missing template
            kind: meta
            composition:
              steps:
                - id: bad_step
                  kind: fan_out
                  iterable: "{{ outputs.list }}"
            ---
            Body.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/bad-fanout2", SkillSource.Workspace);
        Assert.Null(skill);
    }

    [Fact]
    public void ParseSkillContent_FanOutTemplateMissingKind_ReturnsNull()
    {
        var content = """
            ---
            name: bad-fanout3
            description: Template missing kind
            kind: meta
            composition:
              steps:
                - id: bad_step
                  kind: fan_out
                  iterable: "{{ outputs.list }}"
                  fan_out_template:
                    tool: web_search
            ---
            Body.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/bad-fanout3", SkillSource.Workspace);
        Assert.Null(skill);
    }

    [Fact]
    public void ParseSkillContent_FanOutWithDefaultMergeMode_DefaultsToConcat()
    {
        var content = """
            ---
            name: fanout-defaults
            description: Default merge mode
            kind: meta
            composition:
              steps:
                - id: search_all
                  kind: fan_out
                  iterable: "{{ outputs.list }}"
                  fan_out_template:
                    kind: tool_call
                    tool: search
            ---
            Body.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/fanout-defaults", SkillSource.Workspace);
        Assert.NotNull(skill);
        Assert.NotNull(skill!.Composition);
        var step = skill.Composition!.Steps[0];
        Assert.Equal("concat", step.FanOutMergeMode);
        Assert.Equal(4, step.FanOutMaxConcurrency);
    }

    // ── MetaExecutionContext: Tools ──

    [Fact]
    public void MetaExecutionContext_ToolsProperty_DefaultsToEmpty()
    {
        var ctx = new MetaExecutionContext("input");
        Assert.NotNull(ctx.Tools);
        Assert.Empty(ctx.Tools);
    }

    [Fact]
    public void MetaExecutionContext_ToolsProperty_PopulatedByRuntime()
    {
        var tools = new List<ToolDescriptor>
        {
            new("web_search", "desc", "{}"),
            new("file_read", "desc", "{}"),
        };

        var ctx = new MetaExecutionContext("input", tools: tools);
        Assert.Equal(2, ctx.Tools.Count);
        Assert.Equal("web_search", ctx.Tools[0].Name);
        Assert.Equal("file_read", ctx.Tools[1].Name);
    }

    [Fact]
    public void MetaExecutionContext_ToolsProperty_DoesNotAffectOtherProperties()
    {
        var outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["step1"] = "result"
        };

        var tools = new List<ToolDescriptor>
        {
            new("test", "desc", "{}"),
        };

        var ctx = new MetaExecutionContext("my input", outputs, tools: tools);
        Assert.Equal("my input", ctx.Input);
        Assert.Equal("result", ctx.Outputs["step1"]);
        Assert.Single(ctx.Tools);
    }

    // ── ToolDescriptor record ──

    [Fact]
    public void ToolDescriptor_Equality_WorksByValue()
    {
        var a = new ToolDescriptor("web_search", "Search the web", """{"type":"object"}""");
        var b = new ToolDescriptor("web_search", "Search the web", """{"type":"object"}""");
        var c = new ToolDescriptor("web_fetch", "Fetch URL", """{"type":"object"}""");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void ToolDescriptor_Serialization_RoundTrips()
    {
        var original = new ToolDescriptor("web_search", "Search desc", """{"type":"object","properties":{"q":{"type":"string"}}}""");
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<ToolDescriptor>(json);

        Assert.Equal(original.Name, deserialized!.Name);
        Assert.Equal(original.Description, deserialized.Description);
        Assert.Equal(original.ParameterSchema, deserialized.ParameterSchema);
    }

    // ── FanOut step with depends_on (routing context) ──

    [Fact]
    public void ParseSkillContent_FanOutStep_WithDependsOn_ParsesCorrectly()
    {
        var content = """
            ---
            name: fanout-chain
            description: Fan out with dependencies
            kind: meta
            composition:
              steps:
                - id: extract
                  kind: llm_chat
                  with:
                    system_prompt: "Extract topics as JSON array."
                - id: search_all
                  kind: fan_out
                  iterable: "{{ outputs.extract | from_json }}"
                  fan_out_template:
                    kind: tool_call
                    tool: web_search
                  depends_on:
                    - extract
                - id: summarize
                  kind: llm_chat
                  depends_on:
                    - search_all
                  with:
                    system_prompt: "Summarize results."
            ---
            Body.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/fanout-chain", SkillSource.Workspace);
        Assert.NotNull(skill);
        Assert.NotNull(skill!.Composition);
        Assert.Equal(3, skill.Composition!.Steps.Count);

        var fanOut = skill.Composition.Steps.First(s => s.Kind == "fan_out");
        Assert.Single(fanOut.DependsOn);
        Assert.Equal("extract", fanOut.DependsOn[0]);

        var summarize = skill.Composition.Steps.First(s => s.Id == "summarize");
        Assert.Contains("search_all", summarize.DependsOn);
    }

    // ── FanOut with when condition ──

    [Fact]
    public void ParseSkillContent_FanOutStep_WithWhenCondition_ParsesCorrectly()
    {
        var content = """
            ---
            name: fanout-conditional
            description: Conditional fan_out
            kind: meta
            composition:
              steps:
                - id: conditional_search
                  kind: fan_out
                  iterable: "{{ outputs.topics | from_json }}"
                  when: "outputs.topics | from_json | length > 0"
                  fan_out_template:
                    kind: tool_call
                    tool: web_search
            ---
            Body.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/fanout-conditional", SkillSource.Workspace);
        Assert.NotNull(skill);
        Assert.NotNull(skill!.Composition);
        var step = skill.Composition!.Steps[0];
        Assert.NotNull(step.When);
        Assert.Contains("length > 0", step.When);
    }

    // ── FanOut with on_failure ──

    [Fact]
    public void ParseSkillContent_FanOutStep_WithOnFailure_ParsesCorrectly()
    {
        var content = """
            ---
            name: fanout-fallback
            description: Fan_out with failure branch
            kind: meta
            composition:
              steps:
                - id: search_all
                  kind: fan_out
                  iterable: "{{ outputs.topics | from_json }}"
                  on_failure: fallback_search
                  fan_out_template:
                    kind: tool_call
                    tool: web_search
                - id: fallback_search
                  kind: tool_call
                  tool: web_search
            ---
            Body.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/fanout-fallback", SkillSource.Workspace);
        Assert.NotNull(skill);
        Assert.NotNull(skill!.Composition);
        var fanOut = skill.Composition!.Steps.First(s => s.Id == "search_all");
        Assert.Equal("fallback_search", fanOut.OnFailure);
    }

    // ── FanOut with llm_chat template ──

    [Fact]
    public void ParseSkillContent_FanOutTemplate_LlmChat_ParsesCorrectly()
    {
        var content = """
            ---
            name: fanout-llm
            description: LLM-based fan_out
            kind: meta
            composition:
              steps:
                - id: analyze_all
                  kind: fan_out
                  iterable: "{{ outputs.items | from_json }}"
                  fan_out_max_concurrency: 2
                  fan_out_merge_mode: concat
                  fan_out_template:
                    kind: llm_chat
                    with:
                      system_prompt: "Analyze the following item concisely."
            ---
            Body.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/fanout-llm", SkillSource.Workspace);
        Assert.NotNull(skill);
        Assert.NotNull(skill!.Composition);
        var step = skill.Composition!.Steps[0];
        Assert.Equal("fan_out", step.Kind);
        Assert.NotNull(step.FanOutTemplate);
        Assert.Equal("llm_chat", step.FanOutTemplate!.Kind);
        Assert.NotNull(step.FanOutTemplate.WithJson);
        Assert.Contains("Analyze", step.FanOutTemplate.WithJson);
        Assert.Equal("concat", step.FanOutMergeMode);
        Assert.Equal(2, step.FanOutMaxConcurrency);
    }

    [Fact]
    public async Task ExecuteFanOutStep_ChildFailureWithoutFailureBranch_BlocksDependents()
    {
        var fanOutStep = CreateFanOutStep();
        var dependentStep = new MetaSkillStepDefinition
        {
            Id = "summarize",
            Kind = "llm_chat",
            DependsOn = ["fan"]
        };
        var steps = new[] { fanOutStep, dependentStep };
        var stepById = steps.ToDictionary(static step => step.Id, StringComparer.OrdinalIgnoreCase);
        var dependentsByStep = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["fan"] = ["summarize"]
        };
        var pending = new HashSet<string>(["fan", "summarize"], StringComparer.OrdinalIgnoreCase);
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var failureAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var stepResults = new List<MetaStepExecutionResult>();

        var executed = await MetaFanOutExecutor.TryExecuteFanOutStepAsync(
            CreateSession(),
            CreateMetaSkill(),
            steps,
            stepById,
            dependentsByStep,
            pending,
            blocked,
            outputs,
            failureAliases,
            stepResults,
            "input",
            new TurnContext { SessionId = "sess-fan", ChannelId = "test" },
            new MetaTemplateRenderer(),
            new MetaConditionEvaluator(new MetaTemplateRenderer()),
            new MetaToolArgumentResolver(new MetaTemplateRenderer()),
            new MetaRoutePlanner(new MetaConditionEvaluator(new MetaTemplateRenderer())),
            FailFirstChildAsync,
            logger: null,
            TestContext.Current.CancellationToken);

        Assert.True(executed);
        Assert.DoesNotContain("fan", pending);
        Assert.DoesNotContain("summarize", pending);
        Assert.Contains("fan", blocked);
        Assert.Contains("summarize", blocked);
        Assert.Empty(outputs);

        var parentResult = Assert.Single(stepResults, static result => result.Id == "fan");
        Assert.Equal(ToolResultStatuses.Failed, parentResult.Status);
        Assert.Equal("child_step_failed", parentResult.FailureCode);
    }

    [Fact]
    public async Task ExecuteFanOutStep_ChildFailureWithFailureBranch_ActivatesFallback()
    {
        var fanOutStep = CreateFanOutStep(onFailure: "fallback");
        var fallbackStep = new MetaSkillStepDefinition
        {
            Id = "fallback",
            Kind = "tool_call",
            Tool = "search"
        };
        var steps = new[] { fanOutStep, fallbackStep };
        var stepById = steps.ToDictionary(static step => step.Id, StringComparer.OrdinalIgnoreCase);
        var dependentsByStep = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var pending = new HashSet<string>(["fan"], StringComparer.OrdinalIgnoreCase);
        var blocked = new HashSet<string>(["fallback"], StringComparer.OrdinalIgnoreCase);
        var outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var failureAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var stepResults = new List<MetaStepExecutionResult>();

        var executed = await MetaFanOutExecutor.TryExecuteFanOutStepAsync(
            CreateSession(),
            CreateMetaSkill(),
            steps,
            stepById,
            dependentsByStep,
            pending,
            blocked,
            outputs,
            failureAliases,
            stepResults,
            "input",
            new TurnContext { SessionId = "sess-fan", ChannelId = "test" },
            new MetaTemplateRenderer(),
            new MetaConditionEvaluator(new MetaTemplateRenderer()),
            new MetaToolArgumentResolver(new MetaTemplateRenderer()),
            new MetaRoutePlanner(new MetaConditionEvaluator(new MetaTemplateRenderer())),
            FailFirstChildAsync,
            logger: null,
            TestContext.Current.CancellationToken);

        Assert.True(executed);
        Assert.DoesNotContain("fan", pending);
        Assert.Contains("fallback", pending);
        Assert.DoesNotContain("fallback", blocked);
        Assert.Equal("fan", failureAliases["fallback"]);

        var parentResult = Assert.Single(stepResults, static result => result.Id == "fan");
        Assert.Equal(ToolResultStatuses.Failed, parentResult.Status);
    }

    private static MetaSkillStepDefinition CreateFanOutStep(string? onFailure = null) => new()
    {
        Id = "fan",
        Kind = "fan_out",
        Iterable = """["alpha","beta"]""",
        FanOutTemplate = new MetaSkillStepDefinition
        {
            Id = "template",
            Kind = "tool_call",
            Tool = "search"
        },
        OnFailure = onFailure
    };

    private static Session CreateSession() => new()
    {
        Id = "sess-fan",
        ChannelId = "test",
        SenderId = "user"
    };

    private static SkillDefinition CreateMetaSkill() => new()
    {
        Name = "fanout-test",
        Description = "Fan-out test skill",
        Instructions = "Test",
        Location = "/skills/fanout-test",
        Kind = SkillKind.Meta,
        Source = SkillSource.Workspace
    };

    private static Task<(string Output, string? FailureCode)> FailFirstChildAsync(
        SkillDefinition _,
        MetaSkillStepDefinition __,
        string ___,
        string childInput,
        MetaExecutionContext ____,
        Session _____,
        TurnContext ______,
        CancellationToken _______)
    {
        return Task.FromResult(
            string.Equals(childInput, "alpha", StringComparison.Ordinal)
                ? ("boom", (string?)"child_failed")
                : ("ok", null));
    }
}

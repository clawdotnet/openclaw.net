using System.Text.Json;
using OpenClaw.Core.Skills;
using Xunit;

namespace OpenClaw.Tests;

public sealed class MetaCoreServicesTests
{
    [Fact]
    public void MetaTemplateRenderer_RendersOutputsAndFilters()
    {
        var renderer = new MetaTemplateRenderer();
        var context = new MetaExecutionContext(
            input: "Need <xml>",
            outputs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["step-id"] = "Quarterly Report",
                ["summary"] = "This is a very long answer that should be shortened for prompt safety."
            },
            inputs: new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["request_id"] = "req-1"
            },
            steps: new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["payload"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["name"] = "alpha",
                    ["count"] = 2
                }
            });

        var rendered = renderer.Render("{{ input|xml_escape }} {{ outputs[\"step-id\"]|slugify }} {{ outputs.summary|truncate(12) }} {{ inputs.user_message }} {{ steps.payload|tojson }}", context);

        Assert.Equal("Need &lt;xml&gt; quarterly-report This is a... Need <xml> {\"name\":\"alpha\",\"count\":2}", rendered);
    }

    [Fact]
    public void MetaTemplateRenderer_TruncateDefaultsToEightyCharacters()
    {
        var renderer = new MetaTemplateRenderer();
        var input = new string('a', 100);

        var rendered = renderer.Render("{{ input|truncate }}", new MetaExecutionContext(input));

        Assert.Equal(80, rendered.Length);
        Assert.EndsWith("...", rendered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{{ outputs.classify == 'bug' }}", true)]
    [InlineData("outputs.classify == 'doc'", false)]
    [InlineData("steps.prepare == 'done'", true)]
    [InlineData("off", false)]
    [InlineData("1", true)]
    public void MetaConditionEvaluator_UsesSharedTruthiness(string expression, bool expected)
    {
        var context = new MetaExecutionContext(
            input: "hello",
            outputs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["classify"] = "bug"
            },
            steps: new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["prepare"] = "done"
            });

        var evaluator = new MetaConditionEvaluator(new MetaTemplateRenderer());

        Assert.Equal(expected, evaluator.Evaluate(expression, context));
    }

    [Fact]
    public void MetaConditionEvaluator_EvaluatesStatementTemplates()
    {
        var context = new MetaExecutionContext(
            input: "hello",
            outputs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["classify"] = "bug"
            });

        var evaluator = new MetaConditionEvaluator(new MetaTemplateRenderer());

        Assert.True(evaluator.Evaluate("{% if outputs.classify == 'bug' %}true{% endif %}", context));
    }

    [Fact]
    public void MetaToolArgumentResolver_MergesAndRendersJsonObject()
    {
        var resolver = new MetaToolArgumentResolver(new MetaTemplateRenderer());
        var context = new MetaExecutionContext(
            input: "incident-42",
            outputs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["prepare"] = "ready"
            });

        var result = resolver.Resolve(
            "{\"trace\":\"{{ input }}\",\"mode\":\"default\"}",
            "{\"mode\":\"with\",\"state\":\"{{ outputs.prepare }}\"}",
            "{\"mode\":\"step\"}",
            context);

        Assert.Equal("{\"trace\":\"incident-42\",\"mode\":\"step\",\"state\":\"ready\"}", result);
    }

    [Fact]
    public void MetaToolArgumentResolver_InvalidRenderedJson_Throws()
    {
        var resolver = new MetaToolArgumentResolver(new MetaTemplateRenderer());
        var context = new MetaExecutionContext("incident-42");

        var exception = Assert.Throws<InvalidOperationException>(() => resolver.Resolve(
            null,
            "{\"trace\":\"{{ input }\"}",
            null,
            context));

        Assert.Equal("invalid_tool_args", exception.Message);
    }

    [Fact]
    public void MetaClarifyValidator_NormalizesFormInputToCanonicalJson()
    {
        var schema = new MetaClarifySchema
        {
            Mode = "form",
            Fields =
            [
                new MetaClarifyField { Name = "topic", Type = "string", Required = true, MinLength = 3 },
                new MetaClarifyField { Name = "size", Type = "integer", Required = true, Min = 1, Max = 5 },
                new MetaClarifyField { Name = "confirmed", Type = "boolean", Required = true },
                new MetaClarifyField { Name = "priority", Type = "enum", Options = ["low", "medium", "high"], DefaultValue = JsonSerializer.SerializeToElement("medium") }
            ]
        };

        var validator = new MetaClarifyValidator();
        var result = validator.ValidateAndNormalize("{\"topic\":\"OpenSquilla\",\"size\":3,\"confirmed\":true}", schema);

        Assert.True(result.IsValid);
        Assert.Equal("{\"topic\":\"OpenSquilla\",\"size\":3,\"confirmed\":true,\"priority\":\"medium\"}", result.NormalizedOutput);
    }

    [Fact]
    public void MetaClarifyValidator_RejectsInvalidIntegerAndBooleanTypes()
    {
        var schema = new MetaClarifySchema
        {
            Mode = "form",
            Fields =
            [
                new MetaClarifyField { Name = "size", Type = "integer", Required = true, Min = 1, Max = 5 },
                new MetaClarifyField { Name = "confirmed", Type = "boolean", Required = true }
            ]
        };

        var validator = new MetaClarifyValidator();

        var integerResult = validator.ValidateAndNormalize("{\"size\":2.5,\"confirmed\":true}", schema);
        var booleanResult = validator.ValidateAndNormalize("{\"size\":2,\"confirmed\":\"yes\"}", schema);

        Assert.False(integerResult.IsValid);
        Assert.Equal("clarify_invalid_type", integerResult.FailureCode);
        Assert.False(booleanResult.IsValid);
        Assert.Equal("clarify_invalid_type", booleanResult.FailureCode);
    }

    [Fact]
    public void MetaClarifyValidator_ReturnsCancelledForCancelWord()
    {
        var schema = new MetaClarifySchema
        {
            Mode = "form",
            CancelWords = ["cancel"]
        };

        var validator = new MetaClarifyValidator();
        var result = validator.ValidateAndNormalize("cancel", schema);

        Assert.False(result.IsValid);
        Assert.Equal("user_input_cancelled", result.FailureCode);
    }

    [Fact]
    public void MetaRoutePlanner_SelectsFirstMatchingRouteOrFallback()
    {
        var planner = new MetaRoutePlanner(new MetaConditionEvaluator(new MetaTemplateRenderer()));
        var step = new MetaSkillStepDefinition
        {
            Id = "classify",
            Kind = "llm_classify",
            Routes =
            [
                new MetaRouteDefinition { When = "outputs.classify == 'bug'", To = "bug_branch" },
                new MetaRouteDefinition { To = "default_branch" }
            ]
        };

        var context = new MetaExecutionContext(
            input: "hello",
            outputs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["classify"] = "bug"
            });

        var selected = planner.SelectNextStep(step, context);

        Assert.Equal("bug_branch", selected);
    }

    [Fact]
    public void MetaRoutePlanner_ExcludesBranchTargetsUntilSourceCompletes()
    {
        var planner = new MetaRoutePlanner(new MetaConditionEvaluator(new MetaTemplateRenderer()));
        var steps = new[]
        {
            new MetaSkillStepDefinition
            {
                Id = "classify",
                Kind = "llm_classify",
                Routes =
                [
                    new MetaRouteDefinition { When = "outputs.classify == 'bug'", To = "bug_branch" },
                    new MetaRouteDefinition { To = "default_branch" }
                ]
            },
            new MetaSkillStepDefinition { Id = "bug_branch", Kind = "tool_call", Tool = "dispatch_bug" },
            new MetaSkillStepDefinition { Id = "default_branch", Kind = "llm_chat" }
        };

        var pending = new HashSet<string>(steps.Select(static step => step.Id), StringComparer.OrdinalIgnoreCase);
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        planner.ApplyInitialRoutingBlocks(steps, blocked, pending);

        Assert.Contains("classify", pending);
        Assert.DoesNotContain("bug_branch", pending);
        Assert.DoesNotContain("default_branch", pending);
    }

    [Fact]
    public void MetaRoutePlanner_ActivatesSelectedBranchOnCompletion()
    {
        var planner = new MetaRoutePlanner(new MetaConditionEvaluator(new MetaTemplateRenderer()));
        var step = new MetaSkillStepDefinition
        {
            Id = "classify",
            Kind = "llm_classify",
            Routes =
            [
                new MetaRouteDefinition { When = "outputs.classify == 'bug'", To = "bug_branch" },
                new MetaRouteDefinition { To = "default_branch" }
            ]
        };
        var stepById = new Dictionary<string, MetaSkillStepDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["classify"] = step,
            ["bug_branch"] = new MetaSkillStepDefinition { Id = "bug_branch", Kind = "tool_call", Tool = "dispatch_bug" },
            ["default_branch"] = new MetaSkillStepDefinition { Id = "default_branch", Kind = "llm_chat" }
        };
        var dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var context = new MetaExecutionContext(
            input: "hello",
            outputs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["classify"] = "bug"
            });

        planner.ApplyCompletionRouting(step, context, stepById, blocked, pending, dependents);

        Assert.Contains("bug_branch", pending);
        Assert.DoesNotContain("default_branch", pending);
        Assert.Contains("default_branch", blocked);
    }

    // ── Jinja2 sandbox: three lines of defense ──

    [Fact]
    public void MetaTemplateRenderer_Sandbox_BlocksClassicEscapes()
    {
        // Line 1: __class__, __bases__, __subclasses__, and .GetType() must not resolve.
        var renderer = new MetaTemplateRenderer();
        var context = new MetaExecutionContext(
            input: "safe",
            outputs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["key"] = "value"
            });

        // Classic Python-style escapes — these should render as undefined or empty
        var dunderClass = renderer.Render("{{ input.__class__ }}", context);
        Assert.DoesNotContain("String", dunderClass, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("System.", dunderClass, StringComparison.OrdinalIgnoreCase);

        var dunderBases = renderer.Render("{{ input.__bases__ }}", context);
        Assert.DoesNotContain("Object", dunderBases, StringComparison.OrdinalIgnoreCase);

        var dunderSubclasses = renderer.Render("{{ ''.__subclasses__ }}", context);
        Assert.DoesNotContain("System.", dunderSubclasses, StringComparison.OrdinalIgnoreCase);

        var dunderMro = renderer.Render("{{ input.__mro__ }}", context);
        Assert.DoesNotContain("System.", dunderMro, StringComparison.OrdinalIgnoreCase);

        // .NET-specific reflection escapes
        var getType = renderer.Render("{{ input.GetType }}", context);
        Assert.DoesNotContain("String", getType, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("System.", getType, StringComparison.OrdinalIgnoreCase);

        var getTypeMethod = renderer.Render("{{ input.GetType() }}", context);
        Assert.DoesNotContain("System.", getTypeMethod, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MetaTemplateRenderer_Sandbox_FilterAllowlist_OnlyRegisteredFiltersWork()
    {
        // Line 2: Only xml_escape, slugify, truncate, tojson are allowed.
        // HardenFilterAllowlist unregisters all Jinja2.NET built-in filters.
        var renderer = new MetaTemplateRenderer();
        var context = new MetaExecutionContext(input: "Hello World");

        // Registered filters must work
        var escaped = renderer.Render("{{ input|xml_escape }}", context);
        Assert.DoesNotContain("<", escaped, StringComparison.Ordinal);

        var slug = renderer.Render("{{ input|slugify }}", context);
        Assert.Contains("hello-world", slug, StringComparison.Ordinal);

        var truncated = renderer.Render("{{ input|truncate(5) }}", context);
        Assert.True(truncated.Length <= 5 + 3);

        var jsoned = renderer.Render("{{ input|tojson }}", context);
        Assert.Contains("\"Hello World\"", jsoned, StringComparison.Ordinal);

        // Built-in filters (upper, lower, capitalize, etc.) must be blocked.
        // Blocked filters pass through the original value unchanged.
        var upperAttempt = renderer.Render("{{ input|upper }}", context);
        Assert.Equal("Hello World", upperAttempt); // NOT uppercased

        var lowerAttempt = renderer.Render("{{ input|lower }}", context);
        Assert.Equal("Hello World", lowerAttempt); // NOT lowercased

        var capitalizeAttempt = renderer.Render("{{ input|capitalize }}", context);
        Assert.Equal("Hello World", capitalizeAttempt); // NOT capitalized

        var replaceAttempt = renderer.Render("{{ input|replace }}", context);
        Assert.Equal("Hello World", replaceAttempt); // NOT replaced
    }

    [Fact]
    public void MetaTemplateRenderer_Sandbox_GlobalsCleared_NoBuiltinsAccessible()
    {
        // Line 3: range() and dict() are blocked AND caught — no crash.
        var renderer = new MetaTemplateRenderer();
        var context = new MetaExecutionContext(input: "safe");

        // range() blocked: renders safe error string, no exception
        var rangeResult = renderer.Render("{{ range(10) }}", context);
        Assert.Contains("template render error", rangeResult, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("range", rangeResult, StringComparison.OrdinalIgnoreCase);

        // dict() blocked: safe error, no exception
        var dictResult = renderer.Render("{{ dict() }}", context);
        Assert.Contains("template render error", dictResult, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dict", dictResult, StringComparison.OrdinalIgnoreCase);

        // Undefined variable renders as empty (Jinja2.NET default)
        var undefinedResult = renderer.Render("{{ nonexistent_var }}", context);
        Assert.Equal(string.Empty, undefinedResult);
    }

    [Fact]
    public void MetaTemplateRenderer_Sandbox_ContextIsolation_NoCrossContextLeak()
    {
        // Verify that one render call does not leak state into another.
        var renderer = new MetaTemplateRenderer();

        var ctx1 = new MetaExecutionContext(
            input: "alpha",
            outputs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["k"] = "v1" });

        var ctx2 = new MetaExecutionContext(
            input: "beta",
            outputs: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["k"] = "v2" });

        var r1 = renderer.Render("{{ input }}:{{ outputs.k }}", ctx1);
        var r2 = renderer.Render("{{ input }}:{{ outputs.k }}", ctx2);

        Assert.Equal("alpha:v1", r1);
        Assert.Equal("beta:v2", r2);
    }
}
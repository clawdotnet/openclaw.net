using System.Text.Json;
using OpenClaw.Cli;
using OpenClaw.Testing;
using Xunit;

namespace OpenClaw.Tests;

public sealed class AgentScenarioHarnessTests
{
    [Fact]
    public async Task JsonScenarioLoader_LoadsCommittedSampleScenarios()
    {
        var root = FindRepositoryRoot();
        var scenarios = await new JsonScenarioLoader().LoadAsync(Path.Combine(root, "tests", "agent-scenarios"));

        Assert.Contains(scenarios, scenario => scenario.Id == "agent.tool.basic");
        Assert.Contains(scenarios, scenario => scenario.Id == "agent.approval.shell");
    }

    [Fact]
    public void QualityGate_DuplicateIds_Fails()
    {
        var scenario = BuildScenario("duplicate.id", ScenarioOracleTypes.ApprovalNotRequired);
        var result = new ScenarioQualityGate().Evaluate([scenario, scenario]);

        Assert.False(result.Passed);
        Assert.Contains(result.Issues, issue => issue.Message.Contains("Duplicate scenario id", StringComparison.Ordinal));
    }

    [Fact]
    public void QualityGate_HighRiskWithoutOracles_Fails()
    {
        var scenario = new AgentScenario
        {
            Id = "risk.no-oracles",
            Title = "High-risk scenario without oracles",
            Risk = ScenarioRisk.High,
            Expected = new ScenarioExpected { RequiresApproval = true },
            Tags = ["approval"]
        };

        var result = new ScenarioQualityGate().Evaluate([scenario]);

        Assert.False(result.Passed);
        Assert.Contains(result.Issues, issue => issue.Message.Contains("must declare at least one oracle", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(OracleCases))]
    public async Task ScriptedScenarioRunner_EvaluatesOraclePassAndFailCases(
        string oracleType,
        AgentScenario passingScenario,
        AgentScenario failingScenario)
    {
        var runner = new ScriptedScenarioRunner();

        var passing = await runner.RunAsync(passingScenario);
        var failing = await runner.RunAsync(failingScenario);

        Assert.True(passing.Passed, $"{oracleType} should pass: {passing.FailureSummary}");
        Assert.False(failing.Passed);
        Assert.Contains(failing.OracleResults, result => result.Name == oracleType && !result.Passed);
    }

    [Fact]
    public async Task JsonTraceWriter_WritesTraceResultsAndMarkdownReport()
    {
        var outputRoot = CreateTempDirectory();
        var scenario = BuildScenario("report.basic", ScenarioOracleTypes.ApprovalNotRequired);
        var report = await new ScenarioHarness().RunAsync([scenario]);

        var result = await new JsonTraceWriter().WriteAsync(report, outputRoot);

        Assert.True(File.Exists(result.ResultsPath));
        Assert.True(File.Exists(result.ReportPath));
        Assert.Single(result.TracePaths);
        Assert.Contains("Agent Scenario Test Report", await File.ReadAllTextAsync(result.ReportPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestingCommands_GatesAndRun_UseTextWriterOverloads()
    {
        var scenarioDirectory = CreateTempDirectory();
        var outputRoot = CreateTempDirectory();
        var scenario = BuildScenario("cli.basic", ScenarioOracleTypes.ApprovalNotRequired);
        await File.WriteAllTextAsync(
            Path.Combine(scenarioDirectory, "cli.basic.json"),
            JsonSerializer.Serialize(scenario, ScenarioJsonContext.Default.AgentScenario));

        using var gateOutput = new StringWriter();
        using var gateError = new StringWriter();
        var gates = await TestingCommands.RunAsync(["gates", "--scenarios", scenarioDirectory], gateOutput, gateError);

        using var runOutput = new StringWriter();
        using var runError = new StringWriter();
        var run = await TestingCommands.RunAsync(
            ["run", "--scenarios", scenarioDirectory, "--output", outputRoot, "--fail-on", "any"],
            runOutput,
            runError);

        Assert.Equal(0, gates);
        Assert.Equal(string.Empty, gateError.ToString());
        Assert.Equal(0, run);
        Assert.Equal(string.Empty, runError.ToString());
        Assert.Contains("summary=1/1 passed", runOutput.ToString(), StringComparison.Ordinal);
    }

    public static TheoryData<string, AgentScenario, AgentScenario> OracleCases => new()
    {
        {
            ScenarioOracleTypes.ToolCalled,
            BuildScenario("oracle.tool-called.pass", ScenarioOracleTypes.ToolCalled, toolCalls: ["web_search"]),
            BuildScenario("oracle.tool-called.fail", ScenarioOracleTypes.ToolCalled, toolCalls: [])
        },
        {
            ScenarioOracleTypes.ToolNotCalled,
            BuildScenario("oracle.tool-not-called.pass", ScenarioOracleTypes.ToolNotCalled, toolCalls: []),
            BuildScenario("oracle.tool-not-called.fail", ScenarioOracleTypes.ToolNotCalled, toolCalls: ["shell"])
        },
        {
            ScenarioOracleTypes.MaxToolCalls,
            BuildScenario("oracle.max-tool-calls.pass", ScenarioOracleTypes.MaxToolCalls, toolCalls: ["web_search"]),
            BuildScenario("oracle.max-tool-calls.fail", ScenarioOracleTypes.MaxToolCalls, toolCalls: ["web_search", "memory_search"])
        },
        {
            ScenarioOracleTypes.FinalAnswerContains,
            BuildScenario("oracle.final-answer-contains.pass", ScenarioOracleTypes.FinalAnswerContains, finalAnswer: "contains demo value"),
            BuildScenario("oracle.final-answer-contains.fail", ScenarioOracleTypes.FinalAnswerContains, finalAnswer: "missing value")
        },
        {
            ScenarioOracleTypes.FinalAnswerNotContains,
            BuildScenario("oracle.final-answer-not-contains.pass", ScenarioOracleTypes.FinalAnswerNotContains, finalAnswer: "safe answer"),
            BuildScenario("oracle.final-answer-not-contains.fail", ScenarioOracleTypes.FinalAnswerNotContains, finalAnswer: "unsafe secret answer")
        },
        {
            ScenarioOracleTypes.ApprovalRequired,
            BuildScenario("oracle.approval-required.pass", ScenarioOracleTypes.ApprovalRequired, approvals: ["shell"]),
            BuildScenario("oracle.approval-required.fail", ScenarioOracleTypes.ApprovalRequired)
        },
        {
            ScenarioOracleTypes.ApprovalNotRequired,
            BuildScenario("oracle.approval-not-required.pass", ScenarioOracleTypes.ApprovalNotRequired),
            BuildScenario("oracle.approval-not-required.fail", ScenarioOracleTypes.ApprovalNotRequired, approvals: ["shell"])
        },
        {
            ScenarioOracleTypes.NoUnsafeTool,
            BuildScenario("oracle.no-unsafe-tool.pass", ScenarioOracleTypes.NoUnsafeTool, toolCalls: ["shell"], approvals: ["shell"]),
            BuildScenario("oracle.no-unsafe-tool.fail", ScenarioOracleTypes.NoUnsafeTool, toolCalls: ["shell"])
        }
    };

    private static AgentScenario BuildScenario(
        string id,
        string oracleType,
        IReadOnlyList<string>? toolCalls = null,
        IReadOnlyList<string>? approvals = null,
        string finalAnswer = "demo answer")
    {
        toolCalls ??= [];
        approvals ??= [];

        var steps = new List<TraceStep>();
        foreach (var approval in approvals)
        {
            steps.Add(new TraceStep
            {
                Kind = TraceStepKinds.ApprovalRequest,
                ToolName = approval,
                ApprovalId = $"apr_{approval}"
            });
        }

        foreach (var toolCall in toolCalls)
        {
            steps.Add(new TraceStep
            {
                Kind = TraceStepKinds.ToolCall,
                ToolName = toolCall,
                ArgumentsJson = "{}"
            });
        }

        var isToolUse = oracleType is ScenarioOracleTypes.ToolCalled or ScenarioOracleTypes.ToolNotCalled or ScenarioOracleTypes.MaxToolCalls;

        return new AgentScenario
        {
            Id = id,
            Title = id,
            Risk = ScenarioRisk.Medium,
            Type = "agent",
            Tags = isToolUse ? ["tool-use"] : ["regression"],
            Input = new ScenarioInput { UserMessage = "test" },
            Expected = new ScenarioExpected
            {
                MustCallTools = oracleType == ScenarioOracleTypes.ToolCalled ? ["web_search"] : [],
                MustNotCallTools = oracleType == ScenarioOracleTypes.ToolNotCalled ? ["shell"] : [],
                FinalAnswerContains = oracleType == ScenarioOracleTypes.FinalAnswerContains ? ["demo"] : [],
                FinalAnswerMustNotContain = oracleType == ScenarioOracleTypes.FinalAnswerNotContains ? ["secret"] : [],
                MaxToolCalls = oracleType == ScenarioOracleTypes.MaxToolCalls ? 1 : null,
                RequiresApproval = oracleType is ScenarioOracleTypes.ApprovalRequired or ScenarioOracleTypes.NoUnsafeTool
            },
            Oracles =
            [
                BuildOracleDefinition(oracleType)
            ],
            ScriptedTrace = new ScriptedTrace
            {
                FinalAnswer = finalAnswer,
                Steps = steps
            }
        };
    }

    private static ScenarioOracleDefinition BuildOracleDefinition(string oracleType)
        => oracleType switch
        {
            ScenarioOracleTypes.ToolCalled => new ScenarioOracleDefinition { Type = oracleType, Tool = "web_search" },
            ScenarioOracleTypes.ToolNotCalled => new ScenarioOracleDefinition { Type = oracleType, Tool = "shell" },
            ScenarioOracleTypes.MaxToolCalls => new ScenarioOracleDefinition { Type = oracleType, Limit = 1 },
            ScenarioOracleTypes.FinalAnswerContains => new ScenarioOracleDefinition { Type = oracleType, Value = JsonString("demo") },
            ScenarioOracleTypes.FinalAnswerNotContains => new ScenarioOracleDefinition { Type = oracleType, Value = JsonString("secret") },
            ScenarioOracleTypes.ApprovalRequired => new ScenarioOracleDefinition { Type = oracleType, Tool = "shell" },
            _ => new ScenarioOracleDefinition { Type = oracleType }
        };

    private static JsonElement JsonString(string value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return document.RootElement.Clone();
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openclaw-scenario-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(Path.Combine(current, "OpenClaw.Net.slnx")))
                return current;

            var parent = Directory.GetParent(current);
            if (parent is null)
                break;
            current = parent.FullName;
        }

        throw new InvalidOperationException("Could not find OpenClaw.Net.slnx.");
    }
}

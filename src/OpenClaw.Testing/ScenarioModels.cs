using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Testing;

[JsonConverter(typeof(JsonStringEnumConverter<ScenarioRisk>))]
public enum ScenarioRisk
{
    Low,
    Medium,
    High,
    Critical
}

public sealed class AgentScenario
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public ScenarioRisk Risk { get; init; } = ScenarioRisk.Low;
    public string Type { get; init; } = "agent";
    public List<string> Tags { get; init; } = [];
    public ScenarioInput Input { get; init; } = new();
    public ScenarioExpected Expected { get; init; } = new();
    public List<ScenarioOracleDefinition> Oracles { get; init; } = [];
    public ScriptedTrace? ScriptedTrace { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ScenarioInput
{
    public string UserMessage { get; init; } = "";
    public List<string> Context { get; init; } = [];
    public Dictionary<string, string> Variables { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ScenarioExpected
{
    public List<string> MustCallTools { get; init; } = [];
    public List<string> MustNotCallTools { get; init; } = [];
    public List<string> FinalAnswerContains { get; init; } = [];
    public List<string> FinalAnswerMustNotContain { get; init; } = [];
    public int? MaxToolCalls { get; init; }
    public bool? RequiresApproval { get; init; }
    public Dictionary<string, string> ExpectedState { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> ExpectedEvents { get; init; } = [];
    public List<string> ForbiddenEvents { get; init; } = [];
}

public sealed class ScenarioOracleDefinition
{
    public string Type { get; init; } = "";
    public string? Tool { get; init; }
    public JsonElement? Value { get; init; }
    public int? Limit { get; init; }
    public List<string> Tools { get; init; } = [];
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ScriptedTrace
{
    public string? FinalAnswer { get; init; }
    public string Status { get; init; } = ScenarioRunStatuses.Completed;
    public List<TraceStep> Steps { get; init; } = [];
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AgentRunTrace
{
    public string RunId { get; init; } = "";
    public string ScenarioId { get; init; } = "";
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
    public List<TraceStep> Steps { get; init; } = [];
    public string? FinalAnswer { get; init; }
    public string Status { get; init; } = ScenarioRunStatuses.Unknown;
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class TraceStep
{
    public string Kind { get; init; } = TraceStepKinds.Event;
    public DateTimeOffset TimestampUtc { get; init; }
    public string? Message { get; init; }
    public string? ToolName { get; init; }
    public string? ArgumentsJson { get; init; }
    public string? Result { get; init; }
    public string? Error { get; init; }
    public string? StatePath { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }
    public string? ApprovalId { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class OracleResult
{
    public bool Passed { get; init; }
    public string Name { get; init; } = "";
    public string Message { get; init; } = "";
    public List<string> Evidence { get; init; } = [];
}

public sealed class ScenarioRunResult
{
    public AgentScenario Scenario { get; init; } = new();
    public AgentRunTrace Trace { get; init; } = new();
    public List<OracleResult> OracleResults { get; init; } = [];
    public bool Passed { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
    public string? FailureSummary { get; init; }
}

public sealed class ScenarioRunReport
{
    public string RunId { get; init; } = "";
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
    public ScenarioRunSummary Summary { get; init; } = new();
    public List<ScenarioRunResult> Results { get; init; } = [];
    public string? Markdown { get; init; }
}

public sealed class ScenarioRunSummary
{
    public int Total { get; init; }
    public int Passed { get; init; }
    public int Failed { get; init; }
    public int HighOrCriticalFailures { get; init; }
}

public sealed class TraceWriteResult
{
    public string RunId { get; init; } = "";
    public string DirectoryPath { get; init; } = "";
    public string ResultsPath { get; init; } = "";
    public string ReportPath { get; init; } = "";
    public List<string> TracePaths { get; init; } = [];
}

public static class TraceStepKinds
{
    public const string Reasoning = "reasoning";
    public const string ToolCall = "toolCall";
    public const string ToolResult = "toolResult";
    public const string StateChange = "stateChange";
    public const string Event = "event";
    public const string ApprovalRequest = "approvalRequest";
    public const string Error = "error";
}

public static class ScenarioRunStatuses
{
    public const string Unknown = "unknown";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

using System.Text.Json.Serialization;

namespace OpenClaw.Testing;

[JsonSerializable(typeof(AgentScenario))]
[JsonSerializable(typeof(List<AgentScenario>))]
[JsonSerializable(typeof(ScenarioInput))]
[JsonSerializable(typeof(ScenarioExpected))]
[JsonSerializable(typeof(ScenarioOracleDefinition))]
[JsonSerializable(typeof(ScriptedTrace))]
[JsonSerializable(typeof(AgentRunTrace))]
[JsonSerializable(typeof(TraceStep))]
[JsonSerializable(typeof(OracleResult))]
[JsonSerializable(typeof(ScenarioRunResult))]
[JsonSerializable(typeof(ScenarioRunReport))]
[JsonSerializable(typeof(ScenarioRunSummary))]
[JsonSerializable(typeof(TraceWriteResult))]
[JsonSerializable(typeof(List<TraceStep>))]
[JsonSerializable(typeof(List<OracleResult>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
public sealed partial class ScenarioJsonContext : JsonSerializerContext;

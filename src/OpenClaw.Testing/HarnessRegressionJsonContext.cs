using System.Text.Json.Serialization;
using OpenClaw.Client;

namespace OpenClaw.Testing;

[JsonSerializable(typeof(HarnessRegressionReport))]
[JsonSerializable(typeof(HarnessRegressionScenarioResult))]
[JsonSerializable(typeof(HarnessRegressionSummary))]
[JsonSerializable(typeof(HarnessRegressionRecommendation))]
[JsonSerializable(typeof(List<HarnessRegressionScenarioResult>))]
[JsonSerializable(typeof(List<HarnessRegressionRecommendation>))]
[JsonSerializable(typeof(McpJsonRpcRequest))]
[JsonSerializable(typeof(McpInitializeRequest))]
[JsonSerializable(typeof(McpClientCapabilities))]
[JsonSerializable(typeof(McpClientInfo))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
public sealed partial class HarnessRegressionJsonContext : JsonSerializerContext;

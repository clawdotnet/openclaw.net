using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Routing.Decisions;

public interface IDecisionClient
{
    Task<DecisionResponse> EvaluateAsync(DecisionRequest request, CancellationToken cancellationToken);
}

public sealed class DecisionRequest
{
    public required string Model { get; init; }
    [JsonIgnore]
    public string RubricVersion { get; init; } = DecisionTurnRoutingPolicy.RubricVersion;
    public required JsonElement State { get; init; }
    public required Dictionary<string, DecisionQuestion> Questions { get; init; }
}

public sealed class DecisionQuestion
{
    public required string Type { get; init; }
    public required string Instructions { get; init; }
    public JsonElement? Criteria { get; init; }
}

public sealed class DecisionResponse
{
    public required string Model { get; init; }
    public required Dictionary<string, DecisionAnswer> Answers { get; init; }
    public required DecisionUsage Usage { get; init; }
    public DecisionMetadata? Metadata { get; init; }
}

public sealed class DecisionMetadata
{
    public required string Checkpoint { get; init; }
    public required string Revision { get; init; }
    public required string CalibrationId { get; init; }
    public required string SchemaHash { get; init; }
    public required string RubricVersion { get; init; }
    public required string Device { get; init; }
    public required string SdkVersion { get; init; }
    public required bool Truncated { get; init; }
}

internal sealed record DecisionRubric(string RubricVersion, Dictionary<string, DecisionQuestion> Questions);

internal sealed record LayaWireRequest(string Model, JsonElement State,
    Dictionary<string, DecisionQuestion> Questions, string RubricVersion, string? Language);

public sealed class DecisionAnswer
{
    public required string Type { get; init; }
    public string? Choice { get; init; }
    public double? Score { get; init; }
    public double? Noul { get; init; }
    public double? Confidence { get; init; }
    public Dictionary<string, double>? Probabilities { get; init; }
    public Dictionary<string, string>? Legend { get; init; }
}

public sealed class DecisionUsage
{
    public required long InputTokens { get; init; }
    public required long OutputTokens { get; init; }
}

public sealed class DecisionException(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DecisionRequest))]
[JsonSerializable(typeof(DecisionResponse))]
[JsonSerializable(typeof(LayaWireRequest))]
[JsonSerializable(typeof(DecisionRubric))]
[JsonSerializable(typeof(DecisionRoutingState))]
[JsonSerializable(typeof(DecisionRoutingDiagnostic))]
[JsonSerializable(typeof(Dictionary<string, DecisionQuestion>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class DecisionJsonContext : JsonSerializerContext;

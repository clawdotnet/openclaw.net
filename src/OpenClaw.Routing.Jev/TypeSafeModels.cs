using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Routing.Jev;

public interface ITypeSafeDecisionClient
{
    Task<TypeSafeResponse> EvaluateAsync(TypeSafeRequest request, CancellationToken cancellationToken);
}

public sealed class TypeSafeRequest
{
    public required string Model { get; init; }
    public required JsonElement State { get; init; }
    public required Dictionary<string, TypeSafeQuestion> Questions { get; init; }
}

public sealed class TypeSafeQuestion
{
    public required string Type { get; init; }
    public required string Instructions { get; init; }
    public JsonElement? Criteria { get; init; }
}

public sealed class TypeSafeResponse
{
    public required string Model { get; init; }
    public required Dictionary<string, TypeSafeAnswer> Answers { get; init; }
    public required TypeSafeUsage Usage { get; init; }
}

public sealed class TypeSafeAnswer
{
    public required string Type { get; init; }
    public string? Choice { get; init; }
    public double? Score { get; init; }
    public double? Noul { get; init; }
    public double? Confidence { get; init; }
    public Dictionary<string, double>? Probabilities { get; init; }
    public Dictionary<string, string>? Legend { get; init; }
}

public sealed class TypeSafeUsage
{
    public required long InputTokens { get; init; }
    public required long OutputTokens { get; init; }
}

public sealed class TypeSafeException(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TypeSafeRequest))]
[JsonSerializable(typeof(TypeSafeResponse))]
[JsonSerializable(typeof(JevRoutingState))]
[JsonSerializable(typeof(JevRoutingDiagnostic))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class JevJsonContext : JsonSerializerContext;

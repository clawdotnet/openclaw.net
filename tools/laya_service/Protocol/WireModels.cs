using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.LayaService.Protocol;

public sealed record DecisionWireRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("state")]
    public required JsonElement State { get; init; }

    [JsonPropertyName("questions")]
    public required JsonElement Questions { get; init; }

    [JsonPropertyName("rubric_version")]
    public required string RubricVersion { get; init; }

    [JsonPropertyName("language")]
    public string? Language { get; init; }
}

public sealed class ProtocolRejectionException(string reasonCode) : Exception(reasonCode)
{
    public string ReasonCode { get; } = reasonCode;
}
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.LayaService.Evaluation;

public sealed record Observation(
    [property: JsonPropertyName("case_id")] string CaseId,
    [property: JsonPropertyName("case_fingerprint")] string CaseFingerprint,
    [property: JsonPropertyName("question_id")] string QuestionId,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("checkpoint")] string Checkpoint,
    [property: JsonPropertyName("schema_hash")] string SchemaHash,
    [property: JsonPropertyName("sdk_version")] string RuntimeVersion,
    [property: JsonPropertyName("answer")] JsonElement RawAnswer,
    [property: JsonPropertyName("label")] string Label)
{
    [JsonPropertyName("runtime")]
    public string Runtime { get; init; } = "NLaya";

    [JsonPropertyName("source_calibration")]
    public string SourceCalibration { get; init; } = "raw";
}
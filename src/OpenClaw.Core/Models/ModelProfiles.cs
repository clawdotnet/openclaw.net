namespace OpenClaw.Core.Models;

public sealed class ModelsConfig
{
    public string? DefaultProfile { get; set; }
    public List<ModelProfileConfig> Profiles { get; set; } = [];
}

public sealed class ModelProfileConfig
{
    public string Id { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public string[] Tags { get; set; } = [];
    public string[] FallbackProfileIds { get; set; } = [];
    public string[] FallbackModels { get; set; } = [];
    public ModelCapabilities? Capabilities { get; set; }
}

public sealed class ModelCapabilities
{
    public bool SupportsTools { get; set; }
    public bool SupportsVision { get; set; }
    public bool SupportsJsonSchema { get; set; }
    public bool SupportsStructuredOutputs { get; set; }
    public bool SupportsStreaming { get; set; } = true;
    public bool SupportsParallelToolCalls { get; set; }
    public bool SupportsReasoningEffort { get; set; }
    public bool SupportsSystemMessages { get; set; } = true;
    public bool SupportsImageInput { get; set; }
    public bool SupportsAudioInput { get; set; }
    public int MaxContextTokens { get; set; }
    public int MaxOutputTokens { get; set; }
}

public sealed class ModelSelectionRequirements
{
    public bool? SupportsTools { get; set; }
    public bool? SupportsVision { get; set; }
    public bool? SupportsJsonSchema { get; set; }
    public bool? SupportsStructuredOutputs { get; set; }
    public bool? SupportsStreaming { get; set; }
    public bool? SupportsParallelToolCalls { get; set; }
    public bool? SupportsReasoningEffort { get; set; }
    public bool? SupportsSystemMessages { get; set; }
    public bool? SupportsImageInput { get; set; }
    public bool? SupportsAudioInput { get; set; }
    public int? MinContextTokens { get; set; }
    public int? MinOutputTokens { get; set; }
}

public sealed class ModelProfile
{
    public required string Id { get; init; }
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
    public string? BaseUrl { get; init; }
    public string? ApiKey { get; init; }
    public string[] Tags { get; init; } = [];
    public string[] FallbackProfileIds { get; init; } = [];
    public string[] FallbackModels { get; init; } = [];
    public required ModelCapabilities Capabilities { get; init; }
    public bool IsImplicit { get; init; }
}

public sealed class ModelProfileStatus
{
    public required string Id { get; init; }
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
    public bool IsDefault { get; init; }
    public bool IsImplicit { get; init; }
    public bool IsAvailable { get; init; }
    public string[] Tags { get; init; } = [];
    public required ModelCapabilities Capabilities { get; init; }
    public string[] ValidationIssues { get; init; } = [];
    public string[] FallbackProfileIds { get; init; } = [];
    public string[] FallbackModels { get; init; } = [];
}

public sealed class ModelSelectionDescriptor
{
    public string? ProfileId { get; set; }
    public string[] PreferredTags { get; set; } = [];
    public string[] FallbackProfileIds { get; set; } = [];
    public ModelSelectionRequirements Requirements { get; set; } = new();
}

public sealed class ModelProfilesStatusResponse
{
    public string? DefaultProfileId { get; init; }
    public IReadOnlyList<ModelProfileStatus> Profiles { get; init; } = [];
}

public sealed class ModelSelectionDoctorResponse
{
    public string? DefaultProfileId { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<ModelProfileStatus> Profiles { get; init; } = [];
}

public sealed class ModelEvaluationRequest
{
    public string? ProfileId { get; init; }
    public string[] ProfileIds { get; init; } = [];
    public string[] ScenarioIds { get; init; } = [];
    public bool IncludeMarkdown { get; init; } = true;
}

public sealed class ModelEvaluationScenarioResult
{
    public required string ScenarioId { get; init; }
    public required string Name { get; init; }
    public string Status { get; init; } = "unknown";
    public string? Summary { get; init; }
    public long LatencyMs { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public bool MalformedJson { get; init; }
    public int ToolCalls { get; init; }
    public string? Error { get; init; }
}

public sealed class ModelEvaluationProfileReport
{
    public required string ProfileId { get; init; }
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
    public IReadOnlyList<ModelEvaluationScenarioResult> Scenarios { get; init; } = [];
}

public sealed class ModelEvaluationReport
{
    public required string RunId { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
    public IReadOnlyList<string> ScenarioIds { get; init; } = [];
    public IReadOnlyList<ModelEvaluationProfileReport> Profiles { get; init; } = [];
    public string? JsonPath { get; init; }
    public string? MarkdownPath { get; init; }
    public string? Markdown { get; init; }
}

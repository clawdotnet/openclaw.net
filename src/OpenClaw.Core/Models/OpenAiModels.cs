using System.Text.Json.Serialization;

namespace OpenClaw.Core.Models;

// ── Health endpoint (P0) ───────────────────────────────────────────────

/// <summary>
/// Typed response for /health — replaces anonymous object for NativeAOT safety.
/// </summary>
public sealed record HealthResponse
{
    public required string Status { get; init; }
    public long Uptime { get; init; }
}

// ── OpenAI Chat Completions (P1) ───────────────────────────────────────

/// <summary>
/// POST /v1/chat/completions request body.
/// Subset of the OpenAI spec — enough for SDK compatibility.
/// </summary>
public sealed class OpenAiChatCompletionRequest
{
    public string? Model { get; set; }
    public List<OpenAiMessage> Messages { get; set; } = [];
    public bool Stream { get; set; }
    public float? Temperature { get; set; }
    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }
}

public sealed class OpenAiMessage
{
    public required string Role { get; set; }
    public required string Content { get; set; }
}

/// <summary>
/// Non-streaming response for /v1/chat/completions.
/// </summary>
public sealed class OpenAiChatCompletionResponse
{
    public required string Id { get; init; }
    public string Object { get; init; } = "chat.completion";
    public long Created { get; init; }
    public required string Model { get; init; }
    public required List<OpenAiChoice> Choices { get; init; }
    public OpenAiUsage? Usage { get; init; }
}

public sealed class OpenAiChoice
{
    public int Index { get; init; }
    public required OpenAiResponseMessage Message { get; init; }
    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; init; }
}

public sealed class OpenAiResponseMessage
{
    public required string Role { get; init; }
    public required string Content { get; init; }
}

public sealed class OpenAiUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; init; }
    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; init; }
    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; init; }
}

// ── SSE Streaming ──────────────────────────────────────────────────────

/// <summary>
/// A single SSE chunk for streaming /v1/chat/completions responses.
/// </summary>
public sealed class OpenAiStreamChunk
{
    public required string Id { get; init; }
    public string Object { get; init; } = "chat.completion.chunk";
    public long Created { get; init; }
    public required string Model { get; init; }
    public required List<OpenAiStreamChoice> Choices { get; init; }
}

public sealed class OpenAiStreamChoice
{
    public int Index { get; init; }
    public required OpenAiDelta Delta { get; init; }
    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; init; }
}

public sealed class OpenAiDelta
{
    public string? Role { get; init; }
    public string? Content { get; init; }
}

// ── OpenAI Responses API (P1) ──────────────────────────────────────────

/// <summary>
/// POST /v1/responses request body.
/// Simplified input format per the Responses API spec.
/// </summary>
public sealed class OpenAiResponseRequest
{
    public string? Model { get; set; }
    /// <summary>String prompt or structured messages.</summary>
    public string? Input { get; set; }
    public float? Temperature { get; set; }
    [JsonPropertyName("max_output_tokens")]
    public int? MaxOutputTokens { get; set; }
}

/// <summary>
/// Response envelope for /v1/responses.
/// </summary>
public sealed class OpenAiResponseResponse
{
    public required string Id { get; init; }
    public string Object { get; init; } = "response";
    public required string Status { get; init; }
    public required List<OpenAiResponseOutput> Output { get; init; }
    public OpenAiUsage? Usage { get; init; }
}

public sealed class OpenAiResponseOutput
{
    public string Type { get; init; } = "message";
    public required string Id { get; init; }
    public required string Role { get; init; }
    public required List<OpenAiResponseContent> Content { get; init; }
}

public sealed class OpenAiResponseContent
{
    public string Type { get; init; } = "output_text";
    public required string Text { get; init; }
}

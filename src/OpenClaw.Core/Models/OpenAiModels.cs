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
    [JsonPropertyName("tool_calls")]
    public List<OpenAiToolCallDelta>? ToolCalls { get; init; }
    [JsonPropertyName("openclaw_tool_delta")]
    public OpenAiToolOutputDelta? ToolDelta { get; init; }
    [JsonPropertyName("openclaw_tool_result")]
    public OpenAiToolResultDelta? ToolResult { get; init; }
}

public sealed class OpenAiToolCallDelta
{
    public int Index { get; init; }
    public string? Id { get; init; }
    public string Type { get; init; } = "function";
    public OpenAiFunctionCallDelta? Function { get; init; }
}

public sealed class OpenAiFunctionCallDelta
{
    public string? Name { get; init; }
    public string? Arguments { get; init; }
}

public sealed class OpenAiToolResultDelta
{
    public string? CallId { get; init; }
    public required string ToolName { get; init; }
    public required string Content { get; init; }
    public string ResultStatus { get; init; } = ToolResultStatuses.Completed;
    public string? FailureCode { get; init; }
    public string? FailureMessage { get; init; }
    public string? NextStep { get; init; }
}

public sealed class OpenAiToolOutputDelta
{
    public string? CallId { get; init; }
    public required string ToolName { get; init; }
    public required string Content { get; init; }
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
    public bool Stream { get; set; }
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
    [JsonPropertyName("created_at")]
    public long? CreatedAt { get; init; }
    public string? Model { get; init; }
    public required string Status { get; init; }
    public required List<OpenAiResponseOutput> Output { get; init; }
    public OpenAiUsage? Usage { get; init; }
    public OpenAiResponseError? Error { get; init; }
}

public sealed class OpenAiResponseOutput
{
    public required string Id { get; init; }
    public string Type { get; init; } = "message";
    public string? Status { get; init; }
    public string? Role { get; init; }
    public List<OpenAiResponseContent>? Content { get; init; }
    [JsonPropertyName("call_id")]
    public string? CallId { get; init; }
    public string? Name { get; init; }
    public string? Arguments { get; init; }
    public string? Output { get; init; }
}

public sealed class OpenAiResponseContent
{
    public string Type { get; init; } = "output_text";
    public required string Text { get; init; }
}

/// <summary>
/// Streamed response object used by Responses API SSE lifecycle events.
/// </summary>
public sealed class OpenAiResponseStreamResponse
{
    public required string Id { get; init; }
    public string Object { get; init; } = "response";
    [JsonPropertyName("created_at")]
    public long CreatedAt { get; init; }
    public required string Model { get; init; }
    public required string Status { get; init; }
    public required List<OpenAiResponseStreamItem> Output { get; init; }
    public OpenAiUsage? Usage { get; init; }
    public OpenAiResponseError? Error { get; init; }
}

public sealed class OpenAiResponseStreamItem
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public string? Status { get; init; }
    public string? Role { get; init; }
    public List<OpenAiResponseContent>? Content { get; init; }
    [JsonPropertyName("call_id")]
    public string? CallId { get; init; }
    public string? Name { get; init; }
    public string? Arguments { get; init; }
    public string? Output { get; init; }
}

public sealed class OpenAiResponseCreatedEvent
{
    public string Type { get; init; } = "response.created";
    [JsonPropertyName("sequence_number")]
    public int SequenceNumber { get; init; }
    public required OpenAiResponseStreamResponse Response { get; init; }
}

public sealed class OpenAiResponseError
{
    public required string Code { get; init; }
    public required string Message { get; init; }
}

public sealed class OpenAiResponseInProgressEvent
{
    public string Type { get; init; } = "response.in_progress";
    [JsonPropertyName("sequence_number")]
    public int SequenceNumber { get; init; }
    public required OpenAiResponseStreamResponse Response { get; init; }
}

public sealed class OpenAiResponseCompletedEvent
{
    public string Type { get; init; } = "response.completed";
    [JsonPropertyName("sequence_number")]
    public int SequenceNumber { get; init; }
    public required OpenAiResponseStreamResponse Response { get; init; }
}

public sealed class OpenAiResponseFailedEvent
{
    public string Type { get; init; } = "response.failed";
    [JsonPropertyName("sequence_number")]
    public int SequenceNumber { get; init; }
    public required OpenAiResponseStreamResponse Response { get; init; }
}

public sealed class OpenAiResponseOutputItemAddedEvent
{
    public string Type { get; init; } = "response.output_item.added";
    [JsonPropertyName("sequence_number")]
    public int SequenceNumber { get; init; }
    [JsonPropertyName("response_id")]
    public required string ResponseId { get; init; }
    [JsonPropertyName("output_index")]
    public int OutputIndex { get; init; }
    public required OpenAiResponseStreamItem Item { get; init; }
}

public sealed class OpenAiResponseOutputItemDoneEvent
{
    public string Type { get; init; } = "response.output_item.done";
    [JsonPropertyName("sequence_number")]
    public int SequenceNumber { get; init; }
    [JsonPropertyName("response_id")]
    public required string ResponseId { get; init; }
    [JsonPropertyName("output_index")]
    public int OutputIndex { get; init; }
    public required OpenAiResponseStreamItem Item { get; init; }
}

public sealed class OpenAiResponseContentPartAddedEvent
{
    public string Type { get; init; } = "response.content_part.added";
    [JsonPropertyName("sequence_number")]
    public int SequenceNumber { get; init; }
    [JsonPropertyName("response_id")]
    public required string ResponseId { get; init; }
    [JsonPropertyName("output_index")]
    public int OutputIndex { get; init; }
    [JsonPropertyName("item_id")]
    public required string ItemId { get; init; }
    [JsonPropertyName("content_index")]
    public int ContentIndex { get; init; }
    public required OpenAiResponseContent Part { get; init; }
}

public sealed class OpenAiResponseContentPartDoneEvent
{
    public string Type { get; init; } = "response.content_part.done";
    [JsonPropertyName("sequence_number")]
    public int SequenceNumber { get; init; }
    [JsonPropertyName("response_id")]
    public required string ResponseId { get; init; }
    [JsonPropertyName("output_index")]
    public int OutputIndex { get; init; }
    [JsonPropertyName("item_id")]
    public required string ItemId { get; init; }
    [JsonPropertyName("content_index")]
    public int ContentIndex { get; init; }
    public required OpenAiResponseContent Part { get; init; }
}

public sealed class OpenAiResponseOutputTextDeltaEvent
{
    public string Type { get; init; } = "response.output_text.delta";
    [JsonPropertyName("sequence_number")]
    public int SequenceNumber { get; init; }
    [JsonPropertyName("response_id")]
    public required string ResponseId { get; init; }
    [JsonPropertyName("output_index")]
    public int OutputIndex { get; init; }
    [JsonPropertyName("item_id")]
    public required string ItemId { get; init; }
    [JsonPropertyName("content_index")]
    public int ContentIndex { get; init; }
    public required string Delta { get; init; }
}

public sealed class OpenAiResponseOutputTextDoneEvent
{
    public string Type { get; init; } = "response.output_text.done";
    [JsonPropertyName("sequence_number")]
    public int SequenceNumber { get; init; }
    [JsonPropertyName("response_id")]
    public required string ResponseId { get; init; }
    [JsonPropertyName("output_index")]
    public int OutputIndex { get; init; }
    [JsonPropertyName("item_id")]
    public required string ItemId { get; init; }
    [JsonPropertyName("content_index")]
    public int ContentIndex { get; init; }
    public required string Text { get; init; }
}

public sealed class OpenAiResponseFunctionCallArgumentsDeltaEvent
{
    public string Type { get; init; } = "response.function_call_arguments.delta";
    [JsonPropertyName("sequence_number")]
    public int SequenceNumber { get; init; }
    [JsonPropertyName("response_id")]
    public required string ResponseId { get; init; }
    [JsonPropertyName("output_index")]
    public int OutputIndex { get; init; }
    [JsonPropertyName("item_id")]
    public required string ItemId { get; init; }
    public required string Delta { get; init; }
}

public sealed class OpenAiResponseFunctionCallArgumentsDoneEvent
{
    public string Type { get; init; } = "response.function_call_arguments.done";
    [JsonPropertyName("sequence_number")]
    public int SequenceNumber { get; init; }
    [JsonPropertyName("response_id")]
    public required string ResponseId { get; init; }
    [JsonPropertyName("output_index")]
    public int OutputIndex { get; init; }
    [JsonPropertyName("item_id")]
    public required string ItemId { get; init; }
    [JsonPropertyName("call_id")]
    public string? CallId { get; init; }
    public string? Name { get; init; }
    public required string Arguments { get; init; }
}

public sealed class OpenAiResponseToolOutputDeltaEvent
{
    public string Type { get; init; } = "response.openclaw_tool_delta";
    [JsonPropertyName("sequence_number")]
    public int SequenceNumber { get; init; }
    [JsonPropertyName("response_id")]
    public required string ResponseId { get; init; }
    [JsonPropertyName("output_index")]
    public int OutputIndex { get; init; }
    [JsonPropertyName("item_id")]
    public required string ItemId { get; init; }
    [JsonPropertyName("call_id")]
    public string? CallId { get; init; }
    [JsonPropertyName("tool_name")]
    public required string ToolName { get; init; }
    public required string Delta { get; init; }
}

public sealed class OpenAiResponseToolResultEvent
{
    public string Type { get; init; } = "response.openclaw_tool_result";
    [JsonPropertyName("sequence_number")]
    public int SequenceNumber { get; init; }
    [JsonPropertyName("response_id")]
    public required string ResponseId { get; init; }
    [JsonPropertyName("output_index")]
    public int OutputIndex { get; init; }
    [JsonPropertyName("item_id")]
    public required string ItemId { get; init; }
    [JsonPropertyName("call_id")]
    public string? CallId { get; init; }
    [JsonPropertyName("tool_name")]
    public required string ToolName { get; init; }
    public required string Content { get; init; }
    [JsonPropertyName("result_status")]
    public string ResultStatus { get; init; } = ToolResultStatuses.Completed;
    [JsonPropertyName("failure_code")]
    public string? FailureCode { get; init; }
    [JsonPropertyName("failure_message")]
    public string? FailureMessage { get; init; }
    [JsonPropertyName("next_step")]
    public string? NextStep { get; init; }
}

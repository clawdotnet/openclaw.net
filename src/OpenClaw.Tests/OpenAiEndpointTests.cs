using System.Text.Json;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// Tests for OpenAI-compatible HTTP surface models (P1).
/// Validates serialization matches the OpenAI SDK wire format.
/// </summary>
public class OpenAiEndpointTests
{
    // ── Request Deserialization ─────────────────────────────────────────

    [Fact]
    public void ChatCompletionRequest_Deserializes_StandardOpenAiJson()
    {
        const string json = """
            {
                "model": "gpt-4o",
                "messages": [
                    {"role": "system", "content": "You are helpful."},
                    {"role": "user", "content": "Hello!"}
                ],
                "stream": false,
                "temperature": 0.7,
                "max_tokens": 1024
            }
            """;

        var req = JsonSerializer.Deserialize(json, CoreJsonContext.Default.OpenAiChatCompletionRequest);

        Assert.NotNull(req);
        Assert.Equal("gpt-4o", req.Model);
        Assert.Equal(2, req.Messages.Count);
        Assert.Equal("system", req.Messages[0].Role);
        Assert.Equal("You are helpful.", req.Messages[0].Content);
        Assert.Equal("user", req.Messages[1].Role);
        Assert.Equal("Hello!", req.Messages[1].Content);
        Assert.False(req.Stream);
        Assert.Equal(0.7f, req.Temperature);
        Assert.Equal(1024, req.MaxTokens);
    }

    [Fact]
    public void ChatCompletionRequest_Deserializes_StreamTrue()
    {
        const string json = """{"model":"gpt-4","messages":[{"role":"user","content":"hi"}],"stream":true}""";
        var req = JsonSerializer.Deserialize(json, CoreJsonContext.Default.OpenAiChatCompletionRequest);

        Assert.NotNull(req);
        Assert.True(req.Stream);
    }

    [Fact]
    public void ChatCompletionRequest_Deserializes_MinimalPayload()
    {
        const string json = """{"messages":[{"role":"user","content":"test"}]}""";
        var req = JsonSerializer.Deserialize(json, CoreJsonContext.Default.OpenAiChatCompletionRequest);

        Assert.NotNull(req);
        Assert.Null(req.Model);
        Assert.Single(req.Messages);
        Assert.False(req.Stream);
    }

    [Fact]
    public void ChatCompletionRequest_Deserializes_MultimodalContentPartsAsPromptMarkers()
    {
        const string json = """
            {
                "messages": [
                    {
                        "role": "user",
                        "content": [
                            { "type": "text", "text": "What is in this image?" },
                            { "type": "image_url", "image_url": { "url": "data:image/png;base64,AAAA" } }
                        ]
                    }
                ]
            }
            """;

        var req = JsonSerializer.Deserialize(json, CoreJsonContext.Default.OpenAiChatCompletionRequest);

        Assert.NotNull(req);
        var content = req.Messages[0].Content.ToPromptText();
        Assert.Contains("What is in this image?", content, StringComparison.Ordinal);
        Assert.Contains("[IMAGE_URL:data:image/png;base64,AAAA]", content, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatCompletionRequest_Deserializes_NullContentAsEmptyPromptText()
    {
        const string json = """{"messages":[{"role":"assistant","content":null}]}""";

        var req = JsonSerializer.Deserialize(json, CoreJsonContext.Default.OpenAiChatCompletionRequest);

        Assert.NotNull(req);
        Assert.Equal(string.Empty, req.Messages[0].Content.ToPromptText());
    }

    // ── Response Serialization ──────────────────────────────────────────

    [Fact]
    public void ChatCompletionResponse_Serializes_ToOpenAiShape()
    {
        var response = new OpenAiChatCompletionResponse
        {
            Id = "chatcmpl-abc123",
            Created = 1700000000,
            Model = "gpt-4o",
            Choices =
            [
                new OpenAiChoice
                {
                    Index = 0,
                    Message = new OpenAiResponseMessage { Role = "assistant", Content = "Hello!" },
                    FinishReason = "stop"
                }
            ],
            Usage = new OpenAiUsage
            {
                PromptTokens = 10,
                CompletionTokens = 5,
                TotalTokens = 15
            }
        };

        var json = JsonSerializer.Serialize(response, CoreJsonContext.Default.OpenAiChatCompletionResponse);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("chatcmpl-abc123", root.GetProperty("id").GetString());
        Assert.Equal("chat.completion", root.GetProperty("object").GetString());
        Assert.Equal(1700000000, root.GetProperty("created").GetInt64());
        Assert.Equal("gpt-4o", root.GetProperty("model").GetString());

        var choice = root.GetProperty("choices")[0];
        Assert.Equal(0, choice.GetProperty("index").GetInt32());
        Assert.Equal("assistant", choice.GetProperty("message").GetProperty("role").GetString());
        Assert.Equal("Hello!", choice.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());

        var usage = root.GetProperty("usage");
        Assert.Equal(10, usage.GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(5, usage.GetProperty("completion_tokens").GetInt32());
        Assert.Equal(15, usage.GetProperty("total_tokens").GetInt32());
    }

    // ── SSE Stream Chunk ────────────────────────────────────────────────

    [Fact]
    public void StreamChunk_Serializes_CorrectSseFormat()
    {
        var chunk = new OpenAiStreamChunk
        {
            Id = "chatcmpl-stream1",
            Created = 1700000000,
            Model = "gpt-4o",
            Choices =
            [
                new OpenAiStreamChoice
                {
                    Index = 0,
                    Delta = new OpenAiDelta { Content = "Hello" }
                }
            ]
        };

        var json = JsonSerializer.Serialize(chunk, CoreJsonContext.Default.OpenAiStreamChunk);
        var sseLine = $"data: {json}\n\n";

        Assert.StartsWith("data: ", sseLine);
        Assert.EndsWith("\n\n", sseLine);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("chat.completion.chunk", root.GetProperty("object").GetString());
        Assert.Equal("Hello", root.GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());
    }

    [Fact]
    public void StreamChunk_FinalChunk_HasFinishReason()
    {
        var chunk = new OpenAiStreamChunk
        {
            Id = "chatcmpl-done",
            Created = 1700000000,
            Model = "gpt-4o",
            Choices =
            [
                new OpenAiStreamChoice
                {
                    Index = 0,
                    Delta = new OpenAiDelta(),
                    FinishReason = "stop"
                }
            ]
        };

        var json = JsonSerializer.Serialize(chunk, CoreJsonContext.Default.OpenAiStreamChunk);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("stop", doc.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }

    [Fact]
    public void StreamChunk_RoleChunk_HasRoleOnly()
    {
        var chunk = new OpenAiStreamChunk
        {
            Id = "chatcmpl-role",
            Created = 1700000000,
            Model = "gpt-4",
            Choices =
            [
                new OpenAiStreamChoice
                {
                    Index = 0,
                    Delta = new OpenAiDelta { Role = "assistant" }
                }
            ]
        };

        var json = JsonSerializer.Serialize(chunk, CoreJsonContext.Default.OpenAiStreamChunk);
        using var doc = JsonDocument.Parse(json);
        var delta = doc.RootElement.GetProperty("choices")[0].GetProperty("delta");
        Assert.Equal("assistant", delta.GetProperty("role").GetString());
        // Content should be null/absent due to WhenWritingNull
        Assert.False(delta.TryGetProperty("content", out _));
    }

    [Fact]
    public void StreamChunk_ToolCallChunk_UsesOpenAiToolCallsShape()
    {
        var chunk = new OpenAiStreamChunk
        {
            Id = "chatcmpl-tool",
            Created = 1700000000,
            Model = "gpt-4",
            Choices =
            [
                new OpenAiStreamChoice
                {
                    Index = 0,
                    Delta = new OpenAiDelta
                    {
                        ToolCalls =
                        [
                            new OpenAiToolCallDelta
                            {
                                Index = 0,
                                Id = "call_openclaw_1",
                                Function = new OpenAiFunctionCallDelta
                                {
                                    Name = "stream_echo",
                                    Arguments = "{\"chunks\":[\"a\"]}"
                                }
                            }
                        ]
                    }
                }
            ]
        };

        var json = JsonSerializer.Serialize(chunk, CoreJsonContext.Default.OpenAiStreamChunk);
        using var doc = JsonDocument.Parse(json);
        var toolCall = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("delta")
            .GetProperty("tool_calls")[0];

        Assert.Equal(0, toolCall.GetProperty("index").GetInt32());
        Assert.Equal("call_openclaw_1", toolCall.GetProperty("id").GetString());
        Assert.Equal("stream_echo", toolCall.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("{\"chunks\":[\"a\"]}", toolCall.GetProperty("function").GetProperty("arguments").GetString());
    }

    [Fact]
    public void StreamChunk_ToolResultChunk_UsesOpenClawExtensionShape()
    {
        var chunk = new OpenAiStreamChunk
        {
            Id = "chatcmpl-tool-result",
            Created = 1700000000,
            Model = "gpt-4",
            Choices =
            [
                new OpenAiStreamChoice
                {
                    Index = 0,
                    Delta = new OpenAiDelta
                    {
                        ToolResult = new OpenAiToolResultDelta
                        {
                            CallId = "call_openclaw_1",
                            ToolName = "stream_echo",
                            Content = "abc",
                            ResultStatus = ToolResultStatuses.Blocked,
                            FailureCode = ToolFailureCodes.OperatorAuthRequired,
                            FailureMessage = "Operator authentication required.",
                            NextStep = "Authenticate and retry."
                        }
                    }
                }
            ]
        };

        var json = JsonSerializer.Serialize(chunk, CoreJsonContext.Default.OpenAiStreamChunk);
        using var doc = JsonDocument.Parse(json);
        var toolResult = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("delta")
            .GetProperty("openclaw_tool_result");

        Assert.Equal("call_openclaw_1", toolResult.GetProperty("callId").GetString());
        Assert.Equal("stream_echo", toolResult.GetProperty("toolName").GetString());
        Assert.Equal("abc", toolResult.GetProperty("content").GetString());
        Assert.Equal(ToolResultStatuses.Blocked, toolResult.GetProperty("resultStatus").GetString());
        Assert.Equal(ToolFailureCodes.OperatorAuthRequired, toolResult.GetProperty("failureCode").GetString());
        Assert.Equal("Operator authentication required.", toolResult.GetProperty("failureMessage").GetString());
        Assert.Equal("Authenticate and retry.", toolResult.GetProperty("nextStep").GetString());
    }

    [Fact]
    public void StreamChunk_ToolDeltaChunk_UsesOpenClawExtensionShape()
    {
        var chunk = new OpenAiStreamChunk
        {
            Id = "chatcmpl-tool-delta",
            Created = 1700000000,
            Model = "gpt-4",
            Choices =
            [
                new OpenAiStreamChoice
                {
                    Index = 0,
                    Delta = new OpenAiDelta
                    {
                        ToolDelta = new OpenAiToolOutputDelta
                        {
                            CallId = "call_openclaw_1",
                            ToolName = "stream_echo",
                            Content = "a"
                        }
                    }
                }
            ]
        };

        var json = JsonSerializer.Serialize(chunk, CoreJsonContext.Default.OpenAiStreamChunk);
        using var doc = JsonDocument.Parse(json);
        var toolDelta = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("delta")
            .GetProperty("openclaw_tool_delta");

        Assert.Equal("call_openclaw_1", toolDelta.GetProperty("callId").GetString());
        Assert.Equal("stream_echo", toolDelta.GetProperty("toolName").GetString());
        Assert.Equal("a", toolDelta.GetProperty("content").GetString());
    }

    // ── Responses API ───────────────────────────────────────────────────

    [Fact]
    public void ResponseRequest_Deserializes_Correctly()
    {
        const string json = """{"model":"gpt-4o","input":"Tell me a joke","temperature":0.5,"max_output_tokens":256}""";
        var req = JsonSerializer.Deserialize(json, CoreJsonContext.Default.OpenAiResponseRequest);

        Assert.NotNull(req);
        Assert.Equal("gpt-4o", req.Model);
        Assert.Equal("Tell me a joke", req.Input);
        Assert.Equal(0.5f, req.Temperature);
        Assert.Equal(256, req.MaxOutputTokens);
    }

    [Fact]
    public void ResponseRequest_Deserializes_StreamTrue()
    {
        const string json = """{"model":"gpt-4.1-mini","input":"stream this","stream":true}""";
        var req = JsonSerializer.Deserialize(json, CoreJsonContext.Default.OpenAiResponseRequest);

        Assert.NotNull(req);
        Assert.True(req.Stream);
    }

    [Fact]
    public void ResponseResponse_Serializes_ToExpectedShape()
    {
        var response = new OpenAiResponseResponse
        {
            Id = "resp-abc123",
            CreatedAt = 1700000000,
            Model = "gpt-4o",
            Status = "completed",
            Output =
            [
                new OpenAiResponseOutput
                {
                    Id = "msg-xyz789",
                    Role = "assistant",
                    Content = [new OpenAiResponseContent { Text = "Here's a joke!" }]
                }
            ],
            Usage = new OpenAiUsage
            {
                PromptTokens = 8,
                CompletionTokens = 12,
                TotalTokens = 20
            }
        };

        var json = JsonSerializer.Serialize(response, CoreJsonContext.Default.OpenAiResponseResponse);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("resp-abc123", root.GetProperty("id").GetString());
        Assert.Equal("response", root.GetProperty("object").GetString());
        Assert.Equal(1700000000, root.GetProperty("created_at").GetInt64());
        Assert.Equal("gpt-4o", root.GetProperty("model").GetString());
        Assert.Equal("completed", root.GetProperty("status").GetString());

        var output = root.GetProperty("output")[0];
        Assert.Equal("msg-xyz789", output.GetProperty("id").GetString());
        Assert.Equal("assistant", output.GetProperty("role").GetString());
        Assert.Equal("output_text", output.GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("Here's a joke!", output.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void ResponseResponse_Serializes_FunctionCallOutputItemShape()
    {
        var response = new OpenAiResponseResponse
        {
            Id = "resp-tool",
            CreatedAt = 1700000000,
            Model = "gpt-4.1-mini",
            Status = "completed",
            Output =
            [
                new OpenAiResponseOutput
                {
                    Id = "fc_1",
                    Type = "function_call",
                    Status = "completed",
                    CallId = "call_openclaw_1",
                    Name = "memory",
                    Arguments = "{\"action\":\"write\"}"
                },
                new OpenAiResponseOutput
                {
                    Id = "fco_1",
                    Type = "function_call_output",
                    Status = "completed",
                    CallId = "call_openclaw_1",
                    Output = "stored"
                }
            ]
        };

        var json = JsonSerializer.Serialize(response, CoreJsonContext.Default.OpenAiResponseResponse);
        using var doc = JsonDocument.Parse(json);
        var output = doc.RootElement.GetProperty("output");

        Assert.Equal("function_call", output[0].GetProperty("type").GetString());
        Assert.Equal("call_openclaw_1", output[0].GetProperty("call_id").GetString());
        Assert.Equal("memory", output[0].GetProperty("name").GetString());
        Assert.Equal("{\"action\":\"write\"}", output[0].GetProperty("arguments").GetString());

        Assert.Equal("function_call_output", output[1].GetProperty("type").GetString());
        Assert.Equal("call_openclaw_1", output[1].GetProperty("call_id").GetString());
        Assert.Equal("stored", output[1].GetProperty("output").GetString());
    }

    [Fact]
    public void ResponseResponse_Serializes_FailedShape()
    {
        var response = new OpenAiResponseResponse
        {
            Id = "resp-failed",
            CreatedAt = 1700000000,
            Model = "gpt-4.1-mini",
            Status = "failed",
            Output = [],
            Error = new OpenAiResponseError
            {
                Code = "provider_error",
                Message = "upstream failed"
            }
        };

        var json = JsonSerializer.Serialize(response, CoreJsonContext.Default.OpenAiResponseResponse);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("failed", root.GetProperty("status").GetString());
        Assert.Equal("provider_error", root.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("upstream failed", root.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public void ResponseCreatedEvent_Serializes_ToExpectedShape()
    {
        var createdEvent = new OpenAiResponseCreatedEvent
        {
            SequenceNumber = 1,
            Response = new OpenAiResponseStreamResponse
            {
                Id = "resp-created",
                CreatedAt = 1700000000,
                Model = "gpt-4.1-mini",
                Status = "in_progress",
                Output = []
            }
        };

        var json = JsonSerializer.Serialize(createdEvent, CoreJsonContext.Default.OpenAiResponseCreatedEvent);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("response.created", root.GetProperty("type").GetString());
        Assert.Equal(1, root.GetProperty("sequence_number").GetInt32());
        Assert.Equal("resp-created", root.GetProperty("response").GetProperty("id").GetString());
        Assert.Equal("in_progress", root.GetProperty("response").GetProperty("status").GetString());
        Assert.Equal("gpt-4.1-mini", root.GetProperty("response").GetProperty("model").GetString());
    }

    [Fact]
    public void ResponseInProgressEvent_Serializes_ToExpectedShape()
    {
        var inProgressEvent = new OpenAiResponseInProgressEvent
        {
            SequenceNumber = 2,
            Response = new OpenAiResponseStreamResponse
            {
                Id = "resp-created",
                CreatedAt = 1700000000,
                Model = "gpt-4.1-mini",
                Status = "in_progress",
                Output = []
            }
        };

        var json = JsonSerializer.Serialize(inProgressEvent, CoreJsonContext.Default.OpenAiResponseInProgressEvent);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("response.in_progress", root.GetProperty("type").GetString());
        Assert.Equal(2, root.GetProperty("sequence_number").GetInt32());
        Assert.Equal("resp-created", root.GetProperty("response").GetProperty("id").GetString());
    }

    [Fact]
    public void ResponseFunctionCallArgumentsDeltaEvent_Serializes_ToExpectedShape()
    {
        var deltaEvent = new OpenAiResponseFunctionCallArgumentsDeltaEvent
        {
            SequenceNumber = 3,
            ResponseId = "resp-123",
            OutputIndex = 0,
            ItemId = "fc_123",
            Delta = "{\"city\":\"San"
        };

        var json = JsonSerializer.Serialize(deltaEvent, CoreJsonContext.Default.OpenAiResponseFunctionCallArgumentsDeltaEvent);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("response.function_call_arguments.delta", root.GetProperty("type").GetString());
        Assert.Equal(3, root.GetProperty("sequence_number").GetInt32());
        Assert.Equal("resp-123", root.GetProperty("response_id").GetString());
        Assert.Equal(0, root.GetProperty("output_index").GetInt32());
        Assert.Equal("fc_123", root.GetProperty("item_id").GetString());
        Assert.Equal("{\"city\":\"San", root.GetProperty("delta").GetString());
    }

    [Fact]
    public void ResponseFunctionCallArgumentsDoneEvent_Serializes_CallMetadata()
    {
        var doneEvent = new OpenAiResponseFunctionCallArgumentsDoneEvent
        {
            SequenceNumber = 4,
            ResponseId = "resp-123",
            OutputIndex = 0,
            ItemId = "fc_123",
            CallId = "call_openclaw_1",
            Name = "memory",
            Arguments = "{\"action\":\"write\"}"
        };

        var json = JsonSerializer.Serialize(doneEvent, CoreJsonContext.Default.OpenAiResponseFunctionCallArgumentsDoneEvent);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("response.function_call_arguments.done", root.GetProperty("type").GetString());
        Assert.Equal(4, root.GetProperty("sequence_number").GetInt32());
        Assert.Equal("call_openclaw_1", root.GetProperty("call_id").GetString());
        Assert.Equal("memory", root.GetProperty("name").GetString());
        Assert.Equal("{\"action\":\"write\"}", root.GetProperty("arguments").GetString());
    }

    [Fact]
    public void ResponseOutputItemAddedEvent_Serializes_FunctionCallOutputShape()
    {
        var addedEvent = new OpenAiResponseOutputItemAddedEvent
        {
            SequenceNumber = 5,
            ResponseId = "resp-123",
            OutputIndex = 1,
            Item = new OpenAiResponseStreamItem
            {
                Id = "fco_1",
                Type = "function_call_output",
                Status = "in_progress",
                CallId = "call_openclaw_1",
                Output = "partial"
            }
        };

        var json = JsonSerializer.Serialize(addedEvent, CoreJsonContext.Default.OpenAiResponseOutputItemAddedEvent);
        using var doc = JsonDocument.Parse(json);
        var item = doc.RootElement.GetProperty("item");

        Assert.Equal("response.output_item.added", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(5, doc.RootElement.GetProperty("sequence_number").GetInt32());
        Assert.Equal("function_call_output", item.GetProperty("type").GetString());
        Assert.Equal("call_openclaw_1", item.GetProperty("call_id").GetString());
        Assert.Equal("partial", item.GetProperty("output").GetString());
    }

    [Fact]
    public void ResponseFailedEvent_Serializes_ToExpectedShape()
    {
        var failedEvent = new OpenAiResponseFailedEvent
        {
            SequenceNumber = 7,
            Response = new OpenAiResponseStreamResponse
            {
                Id = "resp-failed",
                CreatedAt = 1700000000,
                Model = "gpt-4.1-mini",
                Status = "failed",
                Output = [],
                Error = new OpenAiResponseError
                {
                    Code = "provider_error",
                    Message = "upstream failed"
                }
            }
        };

        var json = JsonSerializer.Serialize(failedEvent, CoreJsonContext.Default.OpenAiResponseFailedEvent);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("response.failed", root.GetProperty("type").GetString());
        Assert.Equal(7, root.GetProperty("sequence_number").GetInt32());
        Assert.Equal("failed", root.GetProperty("response").GetProperty("status").GetString());
        Assert.Equal("provider_error", root.GetProperty("response").GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void ResponseToolResultEvent_Serializes_ToOpenClawExtensionShape()
    {
        var resultEvent = new OpenAiResponseToolResultEvent
        {
            SequenceNumber = 6,
            ResponseId = "resp-123",
            OutputIndex = 1,
            ItemId = "fc_456",
            CallId = "call_openclaw_1",
            ToolName = "stream_echo",
            Content = "abc",
            ResultStatus = ToolResultStatuses.Blocked,
            FailureCode = ToolFailureCodes.OperatorAuthRequired,
            FailureMessage = "Operator authentication required.",
            NextStep = "Authenticate and retry."
        };

        var json = JsonSerializer.Serialize(resultEvent, CoreJsonContext.Default.OpenAiResponseToolResultEvent);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("response.openclaw_tool_result", root.GetProperty("type").GetString());
        Assert.Equal(6, root.GetProperty("sequence_number").GetInt32());
        Assert.Equal("call_openclaw_1", root.GetProperty("call_id").GetString());
        Assert.Equal("stream_echo", root.GetProperty("tool_name").GetString());
        Assert.Equal("abc", root.GetProperty("content").GetString());
        Assert.Equal(ToolResultStatuses.Blocked, root.GetProperty("result_status").GetString());
        Assert.Equal(ToolFailureCodes.OperatorAuthRequired, root.GetProperty("failure_code").GetString());
        Assert.Equal("Operator authentication required.", root.GetProperty("failure_message").GetString());
        Assert.Equal("Authenticate and retry.", root.GetProperty("next_step").GetString());
    }

    // ── Round-trip ──────────────────────────────────────────────────────

    [Fact]
    public void ChatCompletionRequest_RoundTrips_Via_SourceGen()
    {
        var original = new OpenAiChatCompletionRequest
        {
            Model = "gpt-4o-mini",
            Messages =
            [
                new OpenAiMessage { Role = "user", Content = "Hi" }
            ],
            Stream = true,
            Temperature = 0.8f,
            MaxTokens = 2048
        };

        var json = JsonSerializer.Serialize(original, CoreJsonContext.Default.OpenAiChatCompletionRequest);
        var roundTripped = JsonSerializer.Deserialize(json, CoreJsonContext.Default.OpenAiChatCompletionRequest);

        Assert.NotNull(roundTripped);
        Assert.Equal(original.Model, roundTripped.Model);
        Assert.Equal(original.Messages.Count, roundTripped.Messages.Count);
        Assert.Equal(original.Stream, roundTripped.Stream);
        Assert.Equal(original.Temperature, roundTripped.Temperature);
        Assert.Equal(original.MaxTokens, roundTripped.MaxTokens);
    }
}

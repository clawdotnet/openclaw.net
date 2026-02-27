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
    public void ResponseResponse_Serializes_ToExpectedShape()
    {
        var response = new OpenAiResponseResponse
        {
            Id = "resp-abc123",
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
        Assert.Equal("completed", root.GetProperty("status").GetString());

        var output = root.GetProperty("output")[0];
        Assert.Equal("msg-xyz789", output.GetProperty("id").GetString());
        Assert.Equal("assistant", output.GetProperty("role").GetString());
        Assert.Equal("output_text", output.GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("Here's a joke!", output.GetProperty("content")[0].GetProperty("text").GetString());
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

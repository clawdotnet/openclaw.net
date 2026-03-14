using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Deterministic streaming tool used only by the published-binary smoke harness.
/// It is not registered unless OPENCLAW_ENABLE_STREAMING_SMOKE_TOOL=1.
/// </summary>
public sealed class StreamingSmokeEchoTool : ITool, IStreamingTool
{
    public string Name => "stream_echo";

    public string Description
        => "Experimental smoke tool that streams the provided chunks and returns their concatenated text.";

    public string ParameterSchema
        => """
        {
          "type": "object",
          "properties": {
            "chunks": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Chunks to stream in order."
            }
          },
          "required": ["chunks"]
        }
        """;

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => new(string.Concat(ParseChunks(argumentsJson)));

    public async IAsyncEnumerable<string> ExecuteStreamingAsync(
        string argumentsJson,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var chunk in ParseChunks(argumentsJson))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return chunk;
        }
    }

    private static IReadOnlyList<string> ParseChunks(string argumentsJson)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        if (!document.RootElement.TryGetProperty("chunks", out var chunksElement) || chunksElement.ValueKind != JsonValueKind.Array)
            return ["missing-chunks"];

        var chunks = new List<string>();
        foreach (var item in chunksElement.EnumerateArray())
        {
            chunks.Add(item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : item.ToString());
        }

        return chunks.Count == 0 ? [string.Empty] : chunks;
    }
}

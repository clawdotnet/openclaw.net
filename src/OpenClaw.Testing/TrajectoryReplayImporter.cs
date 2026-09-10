using System.Text;
using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Testing;

/// <summary>Imports one complete user exchange from the gateway's v1 JSONL trajectory export.</summary>
public static class TrajectoryReplayImporter
{
    public const int MaxInputCharacters = 8 * 1024 * 1024;
    public const int MaxRecords = 10_000;

    public static async Task<TrajectoryReplayFixture> ImportAsync(TextReader reader, string sessionId,
        int promptTurnIndex, IRedactionPipeline redaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(redaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegative(promptTurnIndex);
        var input = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            if (input.Length + count > MaxInputCharacters)
                throw new InvalidDataException("Trajectory exceeds the import size limit.");
            input.Append(buffer, 0, count);
        }
        var records = new List<TrajectoryExportRecord>();
        using var lines = new StringReader(input.ToString());
        var recordCount = 0;
        while (lines.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (++recordCount > MaxRecords) throw new InvalidDataException("Trajectory has too many records.");
            TrajectoryExportRecord record;
            try { record = JsonSerializer.Deserialize(line, CoreJsonContext.Default.TrajectoryExportRecord) ?? throw new JsonException(); }
            catch (JsonException) { throw new InvalidDataException("Invalid trajectory JSON record."); }
            if (record.SchemaVersion != 1) throw new InvalidDataException("Unsupported trajectory schema version.");
            if (record.SessionId == sessionId && record.Type is not "evidence_bundle" and not "governance_ledger_entry") records.Add(record);
        }
        var start = records.FindIndex(r => r.Type == "prompt" && r.TurnIndex == promptTurnIndex);
        if (start < 0) throw new InvalidDataException("Selected user prompt was not found.");
        if (records.Count(r => r.Type == "prompt" && r.TurnIndex == promptTurnIndex) != 1)
            throw new InvalidDataException("Selected prompt is ambiguous.");
        var prompt = records[start];
        if (prompt.Role != "user" || prompt.Content is null)
            throw new InvalidDataException("Replay requires a user text prompt.");
        var cleanPrompt = Redact(prompt.Content, redaction);
        if (MediaMarkerProtocol.Extract(cleanPrompt).Markers.Count > 0)
            throw new InvalidDataException("This text fixture importer does not support media markers.");
        var fixture = new TrajectoryReplayFixture { Prompt = cleanPrompt };
        ReplayResponse? response = null;
        var turnIndex = promptTurnIndex;
        for (var i = start + 1; i < records.Count; i++)
        {
            var record = records[i];
            if (record.Type == "prompt") break;
            if (record.Type == "response")
            {
                if (record.Role != "assistant" || record.TurnIndex <= turnIndex || record.Content is null)
                    throw new InvalidDataException("Response order or role is invalid.");
                if (response is { ToolCalls.Count: 0 })
                    throw new InvalidDataException("Multiple final responses are not supported.");
                turnIndex = record.TurnIndex;
                response = new ReplayResponse { Text = Redact(record.Content, redaction) };
                fixture.Responses.Add(response);
            }
            else if (record.Type == "tool_call")
            {
                if (response is null || record.TurnIndex != turnIndex || ++i >= records.Count)
                    throw new InvalidDataException("Tool call is missing its response or result.");
                var result = records[i];
                if (result.Type != "tool_result" || result.TurnIndex != turnIndex || result.CallId != record.CallId || result.ToolName != record.ToolName || result.Result is null)
                    throw new InvalidDataException("Tool result does not match its call.");
                if (result.ResultStatus is not null and not "completed" || result.FailureCode is not null || result.FailureMessage is not null)
                    throw new InvalidDataException("Only completed tool results can be replayed by this fixture version.");
                if (string.IsNullOrWhiteSpace(record.ToolName) || record.ToolName.Length > 128 || record.ToolName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-' and not '.'))
                    throw new InvalidDataException("Invalid replay tool name.");
                string args;
                try
                {
                    using var json = JsonDocument.Parse(record.Arguments ?? "");
                    if (json.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
                    using var stream = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(stream)) WriteRedactedJson(writer, json.RootElement, redaction);
                    args = Encoding.UTF8.GetString(stream.ToArray());
                }
                catch (JsonException) { throw new InvalidDataException("Tool arguments must be a valid JSON object."); }
                response.ToolCalls.Add(new ReplayToolCall { ToolName = record.ToolName, ArgumentsJson = args, Result = RedactPayload(result.Result, redaction) });
            }
            else throw new InvalidDataException("Unexpected trajectory record in selected exchange.");
        }
        if (fixture.Responses.Count == 0 || fixture.Responses[^1].ToolCalls.Count != 0)
            throw new InvalidDataException("Selected exchange is incomplete: a final assistant response is required.");
        for (var i = 0; i < fixture.Responses.Count; i++)
        {
            var item = fixture.Responses[i];
            // Native session history stores this marker instead of provider text for tool batches.
            if (item.ToolCalls.Count > 0 && item.Text == "[tool_use]")
                fixture.Responses[i] = new ReplayResponse { ToolCalls = item.ToolCalls };
        }
        // Validate replay-only constraints (including ambiguous parallel calls) before returning a fixture.
        using var validation = new TrajectoryReplay(fixture);
        return fixture;
    }

    private static string RedactPayload(string text, IRedactionPipeline pipeline)
    {
        try
        {
            using var json = JsonDocument.Parse(text);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream)) WriteRedactedJson(writer, json.RootElement, pipeline);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException) { return Redact(text, pipeline); }
    }

    private static string Redact(string text, IRedactionPipeline pipeline)
        => new BaselineSecretRedactor().Redact(pipeline.Redact(text));

    private static void WriteRedactedJson(Utf8JsonWriter writer, JsonElement value, IRedactionPipeline pipeline)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    var key = Redact(property.Name, pipeline);
                    if (!keys.Add(key)) throw new InvalidDataException("Duplicate argument keys after redaction.");
                    writer.WritePropertyName(key);
                    var normalized = property.Name.Replace("_", "").Replace("-", "").ToLowerInvariant();
                    if (normalized is "password" or "secret" or "apikey" or "token" or "accesstoken" or "refreshtoken" or "authorization" or "cookie" or "clientsecret" or "privatekey" or "sessiontoken" or "xapikey")
                        writer.WriteStringValue("[REDACTED]");
                    else WriteRedactedJson(writer, property.Value, pipeline);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteRedactedJson(writer, item, pipeline);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String: writer.WriteStringValue(Redact(value.GetString()!, pipeline)); break;
            default:
                var raw = value.GetRawText();
                var clean = Redact(raw, pipeline);
                if (raw == clean) value.WriteTo(writer); else writer.WriteStringValue(clean);
                break;
        }
    }
}

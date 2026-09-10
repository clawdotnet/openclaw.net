using System.Text;
using System.Text.Json;
namespace OpenClaw.Core.Security;

public static class TrajectorySanitizer
{
    public static string RedactPayload(string text, IRedactionPipeline pipeline)
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

    public static string Redact(string text, IRedactionPipeline pipeline)
        => new BaselineSecretRedactor().Redact(pipeline.Redact(text));

    public static void WriteRedactedJson(Utf8JsonWriter writer, JsonElement value, IRedactionPipeline pipeline)
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
                    if (normalized is "password" or "secret" or "apikey" or "token" or "accesstoken" or "refreshtoken" or "authorization" or "cookie")
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
            default: value.WriteTo(writer); break;
        }
    }
}

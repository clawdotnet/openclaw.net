using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenClaw.LayaService.Protocol;

namespace OpenClaw.LayaService.Protocol;

public static class StrictJson
{
    private static readonly HashSet<string> RequestProperties =
        ["model", "state", "questions", "rubric_version", "language"];

    public static DecisionWireRequest ParseRequest(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ProtocolRejectionException("invalid_request");
            }

            RejectDuplicateProperties(document.RootElement);
            if (document.RootElement.EnumerateObject().Any(property => !RequestProperties.Contains(property.Name)))
            {
                throw new ProtocolRejectionException("invalid_request");
            }

            return document.RootElement.Deserialize<DecisionWireRequest>()
                ?? throw new ProtocolRejectionException("invalid_request");
        }
        catch (ProtocolRejectionException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new ProtocolRejectionException("invalid_json");
        }
        catch (DecoderFallbackException)
        {
            throw new ProtocolRejectionException("invalid_json");
        }
    }

    public static string Canonicalize(JsonElement value)
    {
        var output = new StringBuilder();
        AppendCanonical(value, output);
        return output.ToString();
    }

    public static string SchemaHash(JsonElement questions)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonicalize(questions))));

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new ProtocolRejectionException("invalid_json");
                }

                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }

    private static void AppendCanonical(JsonElement value, StringBuilder output)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                output.Append('{');
                var firstProperty = true;
                foreach (var property in value.EnumerateObject())
                {
                    if (!firstProperty) output.Append(',');
                    firstProperty = false;
                    AppendString(property.Name, output);
                    output.Append(':');
                    AppendCanonical(property.Value, output);
                }
                output.Append('}');
                break;
            case JsonValueKind.Array:
                output.Append('[');
                var firstItem = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!firstItem) output.Append(',');
                    firstItem = false;
                    AppendCanonical(item, output);
                }
                output.Append(']');
                break;
            case JsonValueKind.String:
                AppendString(value.GetString()!, output);
                break;
            default:
                output.Append(value.GetRawText());
                break;
        }
    }

    private static void AppendString(string value, StringBuilder output)
    {
        output.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\b': output.Append("\\b"); break;
                case '\f': output.Append("\\f"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                default:
                    if (character < 0x20) output.Append("\\u").Append(((int)character).ToString("x4"));
                    else output.Append(character);
                    break;
            }
        }
        output.Append('"');
    }
}
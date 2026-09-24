using System.Text;
using System.Text.Json;

namespace OpenClaw.Routing.Decisions;

// Matches protocol.canonical: ordered properties, compact separators, literal Unicode,
// and lowercase control escapes. JSON encoders may otherwise escape HTML or Unicode.
internal static class LayaCanonicalJson
{
    public static string Serialize(JsonElement value)
    {
        var output = new StringBuilder();
        Append(value, output);
        return output.ToString();
    }

    private static void Append(JsonElement value, StringBuilder output)
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
                    Append(property.Value, output);
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
                    Append(item, output);
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
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\b': output.Append("\\b"); break;
                case '\f': output.Append("\\f"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                default:
                    if (c < 0x20) output.Append("\\u").Append(((int)c).ToString("x4"));
                    else output.Append(c);
                    break;
            }
        }
        output.Append('"');
    }
}

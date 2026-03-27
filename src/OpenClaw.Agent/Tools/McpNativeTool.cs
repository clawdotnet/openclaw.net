using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenClaw.Core.Abstractions;

namespace OpenClaw.Agent.Tools;

public sealed class McpNativeTool(
    McpClient client,
    string localName,
    string remoteName,
    string description,
    string parameterSchema) : ITool
{
    public string Name => localName;
    public string Description => description;
    public string ParameterSchema => parameterSchema;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        try
        {
            using var argsDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var argsDict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in argsDoc.RootElement.EnumerateObject())
            {
                object? value = null;
                var v = prop.Value;
                switch (v.ValueKind)
                {
                    case JsonValueKind.String:
                        value = v.GetString();
                        break;
                    case JsonValueKind.Number:
                        value = v.TryGetInt64(out var l) ? l : v.GetDouble();
                        break;
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        value = v.GetBoolean();
                        break;
                    case JsonValueKind.Null:
                        value = null;
                        break;
                    default:
                        value = v.Clone();
                        break;
                }
                argsDict[prop.Name] = value;
            }
            var response = await client.CallToolAsync(remoteName, argsDict, progress: null, cancellationToken: ct);
            var parts = new List<string>();
            foreach (var item in response.Content)
            {
                if (item is TextContentBlock t)
                    parts.Add(t.Text ?? "");
            }
            var text = string.Join("\n\n", parts);
            var isError = response.IsError ?? false;
            return isError ? $"Error: {text}" : text;
        }
        catch (JsonException ex)
        {
            return $"Error: Invalid JSON arguments for MCP tool '{localName}': {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Error: MCP tool '{localName}' failed: {ex.Message}";
        }
    }
}

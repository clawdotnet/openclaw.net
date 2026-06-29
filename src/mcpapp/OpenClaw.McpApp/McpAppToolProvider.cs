using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenClaw.Core.Abstractions;
using OpenClaw.McpApp.Shared;

namespace OpenClaw.McpApp;

/// <summary>
/// Bridges an MCP App tool to the OpenClaw <see cref="ITool"/> interface.
/// Each instance represents a single tool from a single MCP App.
/// Registered into the <see cref="OpenClaw.Agent.Plugins.NativePluginRegistry"/>.
/// </summary>
public sealed class McpAppNativeTool : ITool
{
    private readonly McpClient _client;
    private readonly string _remoteName;
    private readonly IMcpAppInfoProvider _app;

    public McpAppNativeTool(
        McpClient client,
        string localName,
        string remoteName,
        string description,
        string parameterSchema,
        IMcpAppInfoProvider app)
    {
        _client = client;
        _remoteName = remoteName;
        _app = app;
        Name = localName;
        Description = description;
        ParameterSchema = parameterSchema;
    }

    public string Name { get; }
    public string Description { get; }
    public string ParameterSchema { get; }

    /// <summary>
    /// The MCP App this tool belongs to.
    /// </summary>
    public IMcpAppInfoProvider App => _app;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        try
        {
            using var argsDoc = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);

            if (argsDoc.RootElement.ValueKind != JsonValueKind.Object)
                return $"Error: Invalid JSON arguments for MCP App tool '{Name}': root must be an object.";

            var argsDict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in argsDoc.RootElement.EnumerateObject())
            {
                argsDict[prop.Name] = ConvertJsonElement(prop.Value);
            }

            var response = await _client.CallToolAsync(_remoteName, argsDict, progress: null, cancellationToken: ct);
            var text = FormatResponseContent(response);
            var isError = response.IsError ?? false;
            return isError ? $"Error: {text}" : text;
        }
        catch (JsonException ex)
        {
            return $"Error: Invalid JSON arguments for MCP App tool '{Name}': {ex.Message}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Error: MCP App tool '{Name}' from '{_app.AppId}' failed: {ex.Message}";
        }
    }

    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.Clone(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.Clone(),
        };
    }

    private static string FormatResponseContent(CallToolResult response)
    {
        var parts = new List<string>();

        foreach (var item in response.Content ?? [])
        {
            switch (item)
            {
                case TextContentBlock textBlock when !string.IsNullOrEmpty(textBlock.Text):
                    parts.Add(textBlock.Text);
                    break;
                case EmbeddedResourceBlock { Resource: TextResourceContents resource }
                    when !string.IsNullOrEmpty(resource.Text):
                    parts.Add(resource.Text);
                    break;
                default:
                    parts.Add(JsonSerializer.Serialize(item, McpAppToolSerializerContext.Default.ContentBlock));
                    break;
            }
        }

        if (response.StructuredContent is { } structured &&
            structured.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            parts.Add(structured.GetRawText());
        }

        return string.Join("\n\n", parts);
    }
}

[JsonSerializable(typeof(ContentBlock))]
[JsonSerializable(typeof(TextContentBlock))]
[JsonSerializable(typeof(ImageContentBlock))]
[JsonSerializable(typeof(AudioContentBlock))]
[JsonSerializable(typeof(EmbeddedResourceBlock))]
[JsonSerializable(typeof(ResourceLinkBlock))]
[JsonSerializable(typeof(ToolUseContentBlock))]
[JsonSerializable(typeof(ToolResultContentBlock))]
[JsonSerializable(typeof(ResourceContents))]
[JsonSerializable(typeof(TextResourceContents))]
[JsonSerializable(typeof(BlobResourceContents))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
internal sealed partial class McpAppToolSerializerContext : JsonSerializerContext;
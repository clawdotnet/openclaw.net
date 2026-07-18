using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenClaw.Core.Abstractions;

namespace OpenClaw.Agent.Tools;

public sealed class McpNativeTool(
    McpClient client,
    string localName,
    string remoteName,
    string description,
    string parameterSchema,
    bool suppressStructuredContent = false) : IToolWithContext
{
    public string Name => localName;
    public string Description => description;
    public string ParameterSchema => parameterSchema;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => await ExecuteCoreAsync(argumentsJson, context: null, ct);

    public async ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
        => await ExecuteCoreAsync(argumentsJson, context, ct);

    private async ValueTask<string> ExecuteCoreAsync(string argumentsJson, ToolExecutionContext? context, CancellationToken ct)
    {
        try
        {
            using var argsDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            if (argsDoc.RootElement.ValueKind != JsonValueKind.Object)
                return $"Error: Invalid JSON arguments for MCP tool '{localName}': JSON root must be an object.";

            var argsDict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var prop in argsDoc.RootElement.EnumerateObject())
                argsDict[prop.Name] = prop.Value.Clone();

            // Reads the current user identity from the AsyncLocal execution context and injects it into the MCP protocol¡¯s _meta field.
            // _meta is protocol-level metadata and does not pollute the tool's arguments.
            // Prefer the stable user ID obtained through OIDC authentication; if authentication is unavailable, fall back to the route-level SenderId.
            var session = context?.Session ?? AgentExecutionContextScope.TryGetCurrent()?.Session;
            JsonObject? meta = null;
            if (session is not null)
            {
                meta = new JsonObject
                {
                    ["userId"] = JsonValue.Create(session.AuthenticatedUserId ?? session.SenderId),
                    ["sessionId"] = JsonValue.Create(session.Id),
                };
            }

            var callParams = new CallToolRequestParams
            {
                Name      = remoteName,
                Arguments = argsDict,
                Meta      = meta,
            };

            var response = await client.SendRequestAsync<CallToolRequestParams, CallToolResult>(
                RequestMethods.ToolsCall,
                callParams,
                cancellationToken: ct);

            var text = FormatResponseContent(response, suppressStructuredContent);
            var isError = response.IsError ?? false;
            return isError ? $"Error: {text}" : text;
        }
        catch (JsonException ex)
        {
            return $"Error: Invalid JSON arguments for MCP tool '{localName}': {ex.Message}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Error: MCP tool '{localName}' failed: {ex.Message}";
        }
    }

    private static string FormatResponseContent(CallToolResult response, bool suppressStructuredContent)
    {
        var parts = new List<string>();

        foreach (var item in response.Content ?? [])
        {
            switch (item)
            {
                case TextContentBlock textBlock when !string.IsNullOrEmpty(textBlock.Text):
                    parts.Add(textBlock.Text);
                    break;
                case EmbeddedResourceBlock { Resource: TextResourceContents resource } when !string.IsNullOrEmpty(resource.Text):
                    parts.Add(resource.Text);
                    break;
                default:
                    parts.Add(JsonSerializer.Serialize(item, McpToolSerializerContext.Default.ContentBlock));
                    break;
            }
        }

        if (!suppressStructuredContent &&
            response.StructuredContent is { } structuredContent &&
            structuredContent.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            parts.Add(structuredContent.GetRawText());
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
internal sealed partial class McpToolSerializerContext : JsonSerializerContext;

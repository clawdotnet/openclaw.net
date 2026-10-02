using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;

namespace OpenClaw.Agent.Tools;

public sealed class McpNativeTool(
    McpClient client,
    string localName,
    string remoteName,
    string description,
    string parameterSchema,
    bool suppressStructuredContent = false,
    IMcpDelegatedToolInvoker? delegatedToolInvoker = null,
    McpDelegatedCredentialsConfig? delegatedCredentials = null,
    string? endpointId = null,
    Uri? endpoint = null,
    IReadOnlyDictionary<string, string>? staticHeaders = null,
    int requestTimeoutSeconds = 60) : IToolWithContext
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

            // Reads the current user identity from the AsyncLocal execution context and injects it into the MCP protocol��s _meta field.
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

            string text;
            bool isError;
            if (delegatedCredentials?.Enabled == true)
            {
                if (delegatedToolInvoker is null || string.IsNullOrWhiteSpace(endpointId) || endpoint is null)
                    throw new InvalidOperationException("Delegated MCP invocation is enabled without a configured HTTP invoker endpoint.");

                var delegatedResponse = await delegatedToolInvoker.InvokeAsync(
                    new McpDelegatedToolCallRequest
                    {
                        EndpointId = endpointId,
                        Endpoint = endpoint,
                        StaticHeaders = staticHeaders ?? new Dictionary<string, string>(),
                        RequestTimeoutSeconds = requestTimeoutSeconds,
                        Policy = delegatedCredentials,
                        RemoteToolName = remoteName,
                        ArgumentsJson = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson,
                        CallerCredentialContext = context?.McpCallerCredentialContext,
                        SuppressStructuredContent = suppressStructuredContent
                    },
                    ct);
                text = delegatedResponse.ResponseText;
                isError = delegatedResponse.IsError;
            }
            else
            {
                var response = await client.SendRequestAsync<CallToolRequestParams, CallToolResult>(
                    RequestMethods.ToolsCall,
                    callParams,
                    cancellationToken: ct);
                text = FormatResponseContent(response, suppressStructuredContent);
                isError = response.IsError ?? false;
            }

            // The ToolOutcomeException + "mcp_tool_error" FailureCode path only fires
            // when the caller supplies a ToolExecutionContext (the meta-skill executor
            // route through IToolWithContext). Direct ITool.ExecuteAsync(argsJson, ct)
            // callers pass context=null and silently degrade to the prefixed string,
            // which preserves backwards compatibility but loses the typed failure
            // signal. If you add a new direct caller, surface the boolean explicitly
            // — do not rely on string inspection.
            if (isError && context is not null)
                throw new ToolOutcomeException($"Error: {text}", "failed", "mcp_tool_error", text);
            return isError ? $"Error: {text}" : text;
        }
        catch (ToolOutcomeException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            return $"Error: Invalid JSON arguments for MCP tool '{localName}': {ex.Message}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (McpDelegatedToolInvocationException ex)
        {
            return $"Error: {ex.FailureCode}";
        }
        catch (Exception ex)
        {
            return delegatedCredentials?.Enabled == true
                ? "Error: MCP_DELEGATED_INVOCATION_FAILED"
                : $"Error: MCP tool '{localName}' failed: {ex.Message}";
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

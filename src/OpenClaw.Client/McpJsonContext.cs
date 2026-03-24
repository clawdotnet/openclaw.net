using System.Text.Json.Serialization;

namespace OpenClaw.Client;

[JsonSerializable(typeof(McpJsonRpcResponse))]
[JsonSerializable(typeof(McpJsonRpcError))]
[JsonSerializable(typeof(McpInitializeRequest))]
[JsonSerializable(typeof(McpClientCapabilities))]
[JsonSerializable(typeof(McpClientInfo))]
[JsonSerializable(typeof(McpInitializeResult))]
[JsonSerializable(typeof(McpCapabilities))]
[JsonSerializable(typeof(McpToolCapabilities))]
[JsonSerializable(typeof(McpResourceCapabilities))]
[JsonSerializable(typeof(McpPromptCapabilities))]
[JsonSerializable(typeof(McpServerInfo))]
[JsonSerializable(typeof(McpCallToolRequest))]
[JsonSerializable(typeof(McpToolDefinition))]
[JsonSerializable(typeof(McpToolListResult))]
[JsonSerializable(typeof(McpTextContent))]
[JsonSerializable(typeof(McpCallToolResult))]
[JsonSerializable(typeof(McpResourceDefinition))]
[JsonSerializable(typeof(McpResourceListResult))]
[JsonSerializable(typeof(McpResourceTemplateDefinition))]
[JsonSerializable(typeof(McpResourceTemplateListResult))]
[JsonSerializable(typeof(McpReadResourceRequest))]
[JsonSerializable(typeof(McpResourceTextContents))]
[JsonSerializable(typeof(McpReadResourceResult))]
[JsonSerializable(typeof(McpPromptDefinition))]
[JsonSerializable(typeof(McpPromptArgumentDefinition))]
[JsonSerializable(typeof(McpPromptListResult))]
[JsonSerializable(typeof(McpGetPromptRequest))]
[JsonSerializable(typeof(McpPromptMessage))]
[JsonSerializable(typeof(McpGetPromptResult))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
internal sealed partial class McpJsonContext : JsonSerializerContext;

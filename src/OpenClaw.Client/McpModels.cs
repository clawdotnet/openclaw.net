using System.Text.Json;

namespace OpenClaw.Client;
 
public sealed class McpJsonRpcRequest
{
    public string Jsonrpc { get; init; } = "2.0";
    public required string Id { get; init; }
    public required string Method { get; init; }
    public JsonElement Params { get; init; }
}
 
public sealed class McpJsonRpcError
{
    public int Code { get; init; }
    public required string Message { get; init; }
}
 
public sealed class McpJsonRpcResponse
{
    public string Jsonrpc { get; init; } = "2.0";
    public JsonElement Id { get; init; }
    public JsonElement Result { get; init; }
    public McpJsonRpcError? Error { get; init; }
}
 
public sealed class McpInitializeRequest
{
    public string? ProtocolVersion { get; init; }
    public McpClientCapabilities Capabilities { get; init; } = new();
    public McpClientInfo ClientInfo { get; init; } = new() { Name = "openclaw-client", Version = "1.0.0" };
}

public sealed class McpClientCapabilities;

public sealed class McpClientInfo
{
    public required string Name { get; init; }
    public required string Version { get; init; }
}
 
public sealed class McpInitializeResult
{
    public string ProtocolVersion { get; init; } = "2025-03-26";
    public required McpCapabilities Capabilities { get; init; }
    public required McpServerInfo ServerInfo { get; init; }
}
 
public sealed class McpCapabilities
{
    public required McpToolCapabilities Tools { get; init; }
    public required McpResourceCapabilities Resources { get; init; }
    public required McpPromptCapabilities Prompts { get; init; }
}
 
public sealed class McpToolCapabilities
{
    public bool ListChanged { get; init; }
}
 
public sealed class McpResourceCapabilities
{
    public bool ListChanged { get; init; }
    public bool SupportsTemplates { get; init; }
}
 
public sealed class McpPromptCapabilities
{
    public bool ListChanged { get; init; }
}
 
public sealed class McpServerInfo
{
    public required string Name { get; init; }
    public required string Version { get; init; }
}
 
public sealed class McpCallToolRequest
{
    public required string Name { get; init; }
    public JsonElement Arguments { get; init; }
}
 
public sealed class McpToolDefinition
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public JsonElement InputSchema { get; init; }
}
 
public sealed class McpToolListResult
{
    public IReadOnlyList<McpToolDefinition> Tools { get; init; } = [];
}
 
public sealed class McpTextContent
{
    public string Type { get; init; } = "text";
    public required string Text { get; init; }
}
 
public sealed class McpCallToolResult
{
    public IReadOnlyList<McpTextContent> Content { get; init; } = [];
    public bool IsError { get; init; }
}
 
public sealed class McpResourceDefinition
{
    public required string Uri { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string MimeType { get; init; } = "application/json";
}
 
public sealed class McpResourceListResult
{
    public IReadOnlyList<McpResourceDefinition> Resources { get; init; } = [];
}
 
public sealed class McpResourceTemplateDefinition
{
    public required string UriTemplate { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string MimeType { get; init; } = "application/json";
}
 
public sealed class McpResourceTemplateListResult
{
    public IReadOnlyList<McpResourceTemplateDefinition> ResourceTemplates { get; init; } = [];
}
 
public sealed class McpReadResourceRequest
{
    public required string Uri { get; init; }
}
 
public sealed class McpResourceTextContents
{
    public required string Uri { get; init; }
    public string MimeType { get; init; } = "application/json";
    public required string Text { get; init; }
}
 
public sealed class McpReadResourceResult
{
    public IReadOnlyList<McpResourceTextContents> Contents { get; init; } = [];
}
 
public sealed class McpPromptDefinition
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<McpPromptArgumentDefinition> Arguments { get; init; } = [];
}
 
public sealed class McpPromptArgumentDefinition
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool Required { get; init; }
}
 
public sealed class McpPromptListResult
{
    public IReadOnlyList<McpPromptDefinition> Prompts { get; init; } = [];
}
 
public sealed class McpGetPromptRequest
{
    public required string Name { get; init; }
    public Dictionary<string, string> Arguments { get; init; } = [];
}
 
public sealed class McpPromptMessage
{
    public string Role { get; init; } = "user";
    public McpTextContent Content { get; init; } = new() { Text = string.Empty };
}
 
public sealed class McpGetPromptResult
{
    public string? Description { get; init; }
    public IReadOnlyList<McpPromptMessage> Messages { get; init; } = [];
}

using System.Text.Json;
using ModelContextProtocol.Client;
using OpenClaw.McpApp.Models;

namespace OpenClaw.McpApp.Shared;

/// <summary>
/// Provides metadata and capability information about an MCP App.
/// This is the primary integration point: each discovered MCP App
/// surfaces its identity, tools, resources, and prompts through
/// this interface so that the OpenClaw gateway can register them.
/// </summary>
public interface IMcpAppInfoProvider
{
    /// <summary>The canonical app id.</summary>
    string AppId { get; }

    /// <summary>Human-readable display name.</summary>
    string DisplayName { get; }

    /// <summary>Short description of the app.</summary>
    string Description { get; }

    /// <summary>Semantic version.</summary>
    string Version { get; }

    /// <summary>The app's manifest.</summary>
    McpAppManifest Manifest { get; }

    /// <summary>Current lifecycle state.</summary>
    McpAppInstallState State { get; }

    /// <summary>Whether the app has an MCP App UI bundle.</summary>
    bool HasUi { get; }

    /// <summary>
    /// URI of the main UI resource (only when <see cref="HasUi"/> is true).
    /// The resource is served with MIME type <c>text/html;profile=mcp-app</c>.
    /// </summary>
    string? UiResourceUri { get; }

    /// <summary>
    /// Returns the tool descriptors discovered from this MCP App.
    /// Called after the MCP client has connected and enumerated tools.
    /// </summary>
    IReadOnlyList<McpAppToolDescriptor> GetToolDescriptors();

    /// <summary>
    /// Returns the resource descriptors discovered from this MCP App.
    /// </summary>
    IReadOnlyList<McpAppResourceDescriptor> GetResourceDescriptors();

    /// <summary>
    /// Returns the prompt descriptors discovered from this MCP App.
    /// </summary>
    IReadOnlyList<McpAppPromptDescriptor> GetPromptDescriptors();

    /// <summary>
    /// The connected MCP client, if the app is in Running state.
    /// Null before connection or after disconnection.
    /// </summary>
    McpClient? Client { get; }
}

/// <summary>
/// Describes an MCP tool discovered from an MCP App.
/// </summary>
public sealed class McpAppToolDescriptor
{
    /// <summary>Original tool name as reported by the MCP App.</summary>
    public required string RemoteName { get; init; }

    /// <summary>Local tool name after applying prefix/renaming rules.</summary>
    public required string LocalName { get; init; }

    /// <summary>Human-readable description.</summary>
    public required string Description { get; init; }

    /// <summary>JSON Schema for tool input parameters (raw JSON string).</summary>
    public string InputSchemaText { get; init; } = "{}";

    /// <summary>
    /// If the tool has MCP App UI metadata (_meta.ui.resourceUri),
    /// this holds the associated UI resource URI.
    /// </summary>
    public string? UiResourceUri { get; init; }

    /// <summary>
    /// Arbitrary metadata annotations from the tool definition.
    /// </summary>
    public Dictionary<string, JsonElement> Meta { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Describes an MCP resource discovered from an MCP App.
/// </summary>
public sealed class McpAppResourceDescriptor
{
    /// <summary>Resource URI or URI template (RFC 6570).</summary>
    public required string Uri { get; init; }

    /// <summary>Human-readable name.</summary>
    public required string Name { get; init; }

    /// <summary>Optional description.</summary>
    public string? Description { get; init; }

    /// <summary>MIME type of the resource content.</summary>
    public string MimeType { get; init; } = "application/json";

    /// <summary>
    /// Whether this resource is an MCP App UI bundle
    /// (MIME type text/html;profile=mcp-app).
    /// </summary>
    public bool IsUiResource { get; init; }
}

/// <summary>
/// Describes an MCP prompt discovered from an MCP App.
/// </summary>
public sealed class McpAppPromptDescriptor
{
    /// <summary>Prompt name.</summary>
    public required string Name { get; init; }

    /// <summary>Optional description.</summary>
    public string? Description { get; init; }

    /// <summary>Prompt argument definitions.</summary>
    public IReadOnlyList<McpAppPromptArgumentDescriptor> Arguments { get; init; } = [];
}

/// <summary>
/// Describes an argument for an MCP prompt.
/// </summary>
public sealed class McpAppPromptArgumentDescriptor
{
    /// <summary>Argument name.</summary>
    public required string Name { get; init; }

    /// <summary>Optional description.</summary>
    public string? Description { get; init; }

    /// <summary>Whether the argument is required.</summary>
    public bool Required { get; init; }
}

/// <summary>
/// Default implementation of <see cref="IMcpAppInfoProvider"/>
/// that derives metadata from the manifest and runtime state.
/// </summary>
public sealed class McpAppInfoProvider : IMcpAppInfoProvider
{
    private readonly List<McpAppToolDescriptor> _toolDescriptors = [];
    private readonly List<McpAppResourceDescriptor> _resourceDescriptors = [];
    private readonly List<McpAppPromptDescriptor> _promptDescriptors = [];

    public McpAppInfoProvider(McpAppInstallState state, McpClient? client = null)
    {
        State = state;
        Client = client;
    }

    public string AppId => Manifest.Id;
    public string DisplayName => Manifest.Name ?? Manifest.Id;
    public string Description => Manifest.Description ?? string.Empty;
    public string Version => Manifest.Version;
    public McpAppManifest Manifest => State.Manifest;
    public McpAppInstallState State { get; }
    public bool HasUi => Manifest.HasUi;
    public string? UiResourceUri => Manifest.UiResourceUri;
    public McpClient? Client { get; }

    public IReadOnlyList<McpAppToolDescriptor> GetToolDescriptors() => _toolDescriptors;

    public IReadOnlyList<McpAppResourceDescriptor> GetResourceDescriptors() => _resourceDescriptors;

    public IReadOnlyList<McpAppPromptDescriptor> GetPromptDescriptors() => _promptDescriptors;

    /// <summary>
    /// Populates the tool descriptors from the MCP client.
    /// Call after connecting to the MCP App server.
    /// </summary>
    internal void SetToolDescriptors(IEnumerable<McpAppToolDescriptor> tools)
    {
        _toolDescriptors.Clear();
        _toolDescriptors.AddRange(tools);
        State.DiscoveredToolCount = _toolDescriptors.Count;
    }

    /// <summary>
    /// Populates the resource descriptors from the MCP client.
    /// </summary>
    internal void SetResourceDescriptors(IEnumerable<McpAppResourceDescriptor> resources)
    {
        _resourceDescriptors.Clear();
        _resourceDescriptors.AddRange(resources);
        State.DiscoveredResourceCount = _resourceDescriptors.Count;
    }

    /// <summary>
    /// Populates the prompt descriptors from the MCP client.
    /// </summary>
    internal void SetPromptDescriptors(IEnumerable<McpAppPromptDescriptor> prompts)
    {
        _promptDescriptors.Clear();
        _promptDescriptors.AddRange(prompts);
        State.DiscoveredPromptCount = _promptDescriptors.Count;
    }
}
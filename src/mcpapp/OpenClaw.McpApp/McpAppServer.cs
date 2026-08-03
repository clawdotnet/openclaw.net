using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenClaw.Core.Plugins;
using OpenClaw.McpApp.Models;
using OpenClaw.McpApp.Shared;

namespace OpenClaw.McpApp;

/// <summary>
/// Manages the lifecycle of a single MCP App — connecting, disconnecting,
/// and enumerating tools/resources/prompts from the MCP server.
/// Produces an <see cref="IMcpAppInfoProvider"/> with complete metadata.
/// </summary>
public sealed class McpAppServer : IAsyncDisposable
{
    private readonly McpAppInstallState _state;
    private readonly McpAppEntryConfig? _entryConfig;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private McpClient? _client;
    private McpAppInfoProvider? _infoProvider;
    private bool _disposed;

    public McpAppServer(McpAppInstallState state, McpAppEntryConfig? entryConfig, ILogger logger)
    {
        _state = state;
        _entryConfig = entryConfig;
        _logger = logger;
    }

    /// <summary>The app id from the manifest.</summary>
    public string AppId => _state.Manifest.Id;

    /// <summary>Current lifecycle state.</summary>
    public McpAppLifecycle Lifecycle => _state.Lifecycle;

    /// <summary>
    /// Connect to the MCP App server, enumerate tools/resources/prompts,
    /// and return a populated <see cref="IMcpAppInfoProvider"/>.
    /// </summary>
    public async Task<IMcpAppInfoProvider> ConnectAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();

            if (_infoProvider is not null && _state.Lifecycle == McpAppLifecycle.Running)
                return _infoProvider;

            _state.Lifecycle = McpAppLifecycle.Loaded;
            _state.StateChangedAt = DateTimeOffset.UtcNow;

            var manifest = _state.Manifest;
            var transport = ResolveTransport();
            var timeout = ResolveStartupTimeout();

            _logger.LogInformation("Connecting to McpApp '{AppId}' via {Transport}", manifest.Id, transport);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout));

            _client = await CreateClientAsync(transport, manifest, timeoutCts.Token);

            // Build the info provider
            _infoProvider = new McpAppInfoProvider(_state, _client);

            // Enumerate capabilities
            await EnumerateToolsAsync(timeoutCts.Token);
            await EnumerateResourcesAsync(timeoutCts.Token);
            await EnumeratePromptsAsync(timeoutCts.Token);

            _state.Lifecycle = McpAppLifecycle.Running;
            _state.StateChangedAt = DateTimeOffset.UtcNow;

            _logger.LogInformation(
                "McpApp '{AppId}' connected: {ToolCount} tools, {ResourceCount} resources, {PromptCount} prompts",
                manifest.Id, _state.DiscoveredToolCount, _state.DiscoveredResourceCount, _state.DiscoveredPromptCount);

            return _infoProvider;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _state.Lifecycle = McpAppLifecycle.Failed;
            _state.StateChangedAt = DateTimeOffset.UtcNow;
            _state.LastError = ex.Message;
            _logger.LogError(ex, "Failed to connect to McpApp '{AppId}'", _state.Manifest.Id);

            if (_client is not null)
            {
                try
                {
                    await DisposeClientAsync(_client);
                }
                catch (Exception disposeEx)
                {
                    _logger.LogWarning(disposeEx, "Error disposing failed McpApp client for '{AppId}'", _state.Manifest.Id);
                }

                _client = null;
            }

            _infoProvider?.SetClient(null);

            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Disconnect from the MCP App server.
    /// </summary>
    public async Task DisconnectAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_client is not null)
            {
                await DisposeClientAsync(_client);
                _client = null;
            }

            _infoProvider?.SetClient(null);

            _state.Lifecycle = McpAppLifecycle.Stopped;
            _state.StateChangedAt = DateTimeOffset.UtcNow;
            _logger.LogInformation("McpApp '{AppId}' disconnected", _state.Manifest.Id);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task EnumerateToolsAsync(CancellationToken ct)
    {
        if (_client is null || _infoProvider is null)
            return;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(ResolveRequestTimeout()));

            var response = await _client.ListToolsAsync(cancellationToken: timeoutCts.Token);

            var descriptors = new List<McpAppToolDescriptor>();
            foreach (var tool in response)
            {
                var remoteName = tool.Name;
                if (string.IsNullOrWhiteSpace(remoteName))
                    continue;

                if (!TryGetSupportedInputSchema(tool.ProtocolTool.InputSchema, out var publishedSchema, out var schemaFailureReason))
                {
                    _logger.LogWarning(
                        "McpApp '{AppId}' tool '{ToolName}' {Reason} and will be skipped.",
                        _state.Manifest.Id,
                        remoteName,
                        schemaFailureReason);
                    continue;
                }

                var localName = ResolveToolName(remoteName);
                var inputSchema = publishedSchema.GetRawText();
                var meta = SerializeMeta(tool.ProtocolTool.Meta);

                descriptors.Add(new McpAppToolDescriptor
                {
                    RemoteName = remoteName,
                    LocalName = localName,
                    Description = tool.Description ?? $"MCP App tool '{remoteName}' from '{_state.Manifest.Id}'.",
                    InputSchemaText = inputSchema,
                    UiResourceUri = ResolveUiResourceUri(tool.ProtocolTool.Meta),
                    Meta = meta,
                });
            }

            _infoProvider.SetToolDescriptors(descriptors);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate tools from McpApp '{AppId}'", _state.Manifest.Id);
        }
    }

    private async Task EnumerateResourcesAsync(CancellationToken ct)
    {
        if (_client is null || _infoProvider is null)
            return;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(ResolveRequestTimeout()));

            var response = await _client.ListResourcesAsync(cancellationToken: timeoutCts.Token);

            var descriptors = new List<McpAppResourceDescriptor>();
            foreach (var resource in response)
            {
                var mimeType = resource.MimeType ?? "application/json";
                var isUi = string.Equals(mimeType, "text/html;profile=mcp-app", StringComparison.OrdinalIgnoreCase);

                descriptors.Add(new McpAppResourceDescriptor
                {
                    Uri = resource.Uri ?? string.Empty,
                    Name = resource.Name ?? resource.Uri ?? "Unnamed",
                    Description = resource.Description,
                    MimeType = mimeType,
                    IsUiResource = isUi,
                });
            }

            _infoProvider.SetResourceDescriptors(descriptors);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate resources from McpApp '{AppId}'", _state.Manifest.Id);
        }
    }

    private async Task EnumeratePromptsAsync(CancellationToken ct)
    {
        if (_client is null || _infoProvider is null)
            return;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(ResolveRequestTimeout()));

            var response = await _client.ListPromptsAsync(cancellationToken: timeoutCts.Token);

            var descriptors = new List<McpAppPromptDescriptor>();
            foreach (var prompt in response)
            {
                descriptors.Add(new McpAppPromptDescriptor
                {
                    Name = prompt.Name,
                    Description = prompt.Description,
                });
            }

            _infoProvider.SetPromptDescriptors(descriptors);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate prompts from McpApp '{AppId}'", _state.Manifest.Id);
        }
    }

    private string ResolveToolName(string remoteName)
    {
        var prefix = _entryConfig?.ToolNamePrefix ?? _state.Manifest.ToolNamePrefix;
        if (string.IsNullOrWhiteSpace(prefix))
            return SanitizeLlmToolNamePart(remoteName);

        var name = SanitizeLlmToolNamePrefixPart(prefix) + SanitizeLlmToolNamePart(remoteName);
        return name.Replace('.', '_');
    }

    private static string SanitizeLlmToolNamePrefixPart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "mcp";

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (IsLlmToolNameChar(ch))
                sb.Append(char.ToLowerInvariant(ch));
            else if (ch > 0x7F)
                sb.Append($"_u{(int)ch:x4}");
            else
                sb.Append('_');
        }

        return sb.Length == 0 ? "mcp" : sb.ToString();
    }

    /// <summary>
    /// Sanitizes a string so every character satisfies the LLM tool-name pattern <c>^[a-zA-Z0-9_-]+$</c>.
    /// Dots are replaced with <c>_</c>; other non-conforming ASCII characters are also replaced with <c>_</c>;
    /// non-ASCII characters are replaced with <c>_uXXXX</c> (lowercase hex code point).
    /// </summary>
    private static string SanitizeLlmToolNamePart(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (IsLlmToolNameChar(ch))
                sb.Append(ch);
            else if (ch > 0x7F)
                sb.Append($"_u{(int)ch:x4}");
            else
                sb.Append('_');
        }

        return sb.Length == 0 ? "_" : sb.ToString();
    }

    private static bool IsLlmToolNameChar(char ch)
        => (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9')
           || ch is '_' or '-';

    private string ResolveTransport()
    {
        var transport = (_entryConfig?.Transport ?? _state.Manifest.Transport)?.Trim();
        if (string.IsNullOrWhiteSpace(transport))
            return "stdio";
        if (transport.Equals("streamable-http", StringComparison.OrdinalIgnoreCase) ||
            transport.Equals("streamable_http", StringComparison.OrdinalIgnoreCase))
        {
            return "http";
        }

        return transport.ToLowerInvariant();
    }

    private int ResolveStartupTimeout()
        => _entryConfig?.StartupTimeoutSeconds ?? _state.Manifest.StartupTimeoutSeconds;

    private int ResolveRequestTimeout()
        => _entryConfig?.RequestTimeoutSeconds ?? _state.Manifest.RequestTimeoutSeconds;

    private async Task<McpClient> CreateClientAsync(string transport, McpAppManifest manifest, CancellationToken ct)
    {
        IClientTransport clientTransport = transport switch
        {
            "stdio" => new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = _entryConfig?.Command ?? manifest.Command!,
                Arguments = manifest.Arguments ?? [],
                WorkingDirectory = manifest.WorkingDirectory,
                EnvironmentVariables = ResolveEnvironment(),
                Name = manifest.Id,
            }),
            "http" => new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(_entryConfig?.Url ?? manifest.Url!),
                AdditionalHeaders = ResolveHeaders(),
                Name = manifest.Id,
            }),
            _ => throw new InvalidOperationException($"Unsupported MCP transport '{transport}' for app '{manifest.Id}'.")
        };

        return await McpClient.CreateAsync(clientTransport, cancellationToken: ct);
    }

    private Dictionary<string, string?>? ResolveEnvironment()
    {
        var merged = new Dictionary<string, string?>(StringComparer.Ordinal);

        // Base from manifest
        foreach (var (key, value) in _state.Manifest.Environment)
            merged[key] = value;

        // Override from entry config
        if (_entryConfig?.Environment is not null)
        {
            foreach (var (key, value) in _entryConfig.Environment)
                merged[key] = value;
        }

        return merged.Count == 0 ? null : merged;
    }

    private Dictionary<string, string>? ResolveHeaders()
    {
        if (_state.Manifest.Headers.Count == 0)
            return null;

        return new Dictionary<string, string>(_state.Manifest.Headers, StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryGetSupportedInputSchema(JsonElement schema, out JsonElement supportedSchema, out string failureReason)
    {
        if (schema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            supportedSchema = default;
            failureReason = "is missing required inputSchema";
            return false;
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            supportedSchema = default;
            failureReason = "published a non-object inputSchema";
            return false;
        }

        if (schema.TryGetProperty("type", out var typeProperty)
            && typeProperty.ValueKind == JsonValueKind.String
            && !string.Equals(typeProperty.GetString(), "object", StringComparison.OrdinalIgnoreCase))
        {
            supportedSchema = default;
            failureReason = "published a non-object inputSchema";
            return false;
        }

        supportedSchema = schema;
        failureReason = string.Empty;
        return true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(McpAppServer));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        await DisconnectAsync();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private static async ValueTask DisposeClientAsync(McpClient client)
    {
        if (client is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (client is IDisposable disposable)
            disposable.Dispose();
    }

    private static Dictionary<string, JsonElement> SerializeMeta(JsonObject? meta)
    {
        if (meta is null || meta.Count == 0)
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in meta)
        {
            if (value is null)
                continue;

            using var document = JsonDocument.Parse(value.ToJsonString());
            result[key] = document.RootElement.Clone();
        }

        return result;
    }

    private static string? ResolveUiResourceUri(JsonObject? meta)
    {
        if (meta is null)
            return null;

        if (meta["ui"] is JsonObject ui &&
            ui["resourceUri"] is JsonValue resourceValue &&
            resourceValue.TryGetValue<string>(out var resourceUri) &&
            !string.IsNullOrWhiteSpace(resourceUri))
        {
            return resourceUri;
        }

        if (meta["ui/resourceUri"] is JsonValue flatValue &&
            flatValue.TryGetValue<string>(out var flatUri) &&
            !string.IsNullOrWhiteSpace(flatUri))
        {
            return flatUri;
        }

        return null;
    }
}

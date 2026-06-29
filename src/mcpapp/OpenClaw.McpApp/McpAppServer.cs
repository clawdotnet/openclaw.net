using System.Text.Json;
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _state.Lifecycle = McpAppLifecycle.Failed;
            _state.StateChangedAt = DateTimeOffset.UtcNow;
            _state.LastError = ex.Message;
            _logger.LogError(ex, "Failed to connect to McpApp '{AppId}'", _state.Manifest.Id);

            if (_client is not null)
            {
                try { await DisposeClientAsync(_client); } catch { }
                _client = null;
            }

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

                var localName = ResolveToolName(remoteName);
                var inputSchema = tool.JsonSchema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                    ? "{}"
                    : tool.JsonSchema.GetRawText();

                descriptors.Add(new McpAppToolDescriptor
                {
                    RemoteName = remoteName,
                    LocalName = localName,
                    Description = tool.Description ?? $"MCP App tool '{remoteName}' from '{_state.Manifest.Id}'.",
                    InputSchemaText = inputSchema,
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
            return remoteName;

        return prefix + remoteName;
    }

    private string ResolveTransport()
        => _entryConfig?.Transport ?? _state.Manifest.Transport?.ToLowerInvariant() ?? "stdio";

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
}
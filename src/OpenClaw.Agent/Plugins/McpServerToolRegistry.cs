using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;

namespace OpenClaw.Agent.Plugins;

/// <summary>
/// Discovers tools from configured MCP servers and registers them as native OpenClaw tools.
/// </summary>
public sealed class McpServerToolRegistry : IDisposable, IAsyncDisposable
{
    private readonly McpPluginsConfig _config;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _loadSemaphore = new(1, 1);
    private readonly object _disposeGate = new();
    private readonly List<DiscoveredMcpTool> _tools = [];
    private readonly List<McpClient> _clients = [];
    private readonly Dictionary<string, (McpClient Client, List<DiscoveredMcpTool> Tools, McpServerConfig Config)> _workspaceServers
        = new(StringComparer.Ordinal);
    private readonly Dictionary<string, McpClient> _clientsByServerId = new(StringComparer.Ordinal);
    private Task? _disposeTask;
    private bool _loaded;
    private bool _registered;
    private bool _disposed;

    /// <summary>
    /// Creates a registry for configured MCP servers.
    /// </summary>
    public McpServerToolRegistry(McpPluginsConfig config, ILogger logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Connects to configured MCP servers and registers discovered tools into the native registry.
    /// </summary>
    public async Task RegisterToolsAsync(NativePluginRegistry nativeRegistry, CancellationToken ct)
    {
        ThrowIfDisposed();
        await _loadSemaphore.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            if (_registered)
                return;

            var tools = await LoadInternalAsync(ct);
            foreach (var tool in tools)
                nativeRegistry.RegisterExternalTool(tool.Tool, tool.PluginId, tool.Detail);

            _registered = true;
        }
        finally
        {
            _loadSemaphore.Release();
        }
    }

    internal async Task<IReadOnlyList<DiscoveredMcpTool>> LoadAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        await _loadSemaphore.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            return _loaded ? _tools : await LoadInternalAsync(ct);
        }
        finally
        {
            _loadSemaphore.Release();
        }
    }

    public McpClient? GetClientByServerId(string serverId)
    {
        if (_clientsByServerId.TryGetValue(serverId, out var configured))
            return configured;

        return _workspaceServers.TryGetValue(serverId, out var workspace) ? workspace.Client : null;
    }

    public async Task<McpWorkspaceReloadResult> ReloadWorkspaceServersAsync(
        Dictionary<string, McpServerConfig>? newServers,
        CancellationToken ct)
    {
        ThrowIfDisposed();
        await _loadSemaphore.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();

            var addedTools = new List<ITool>();
            var removedToolNames = new List<string>();
            var desiredServers = newServers is null
                ? new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
                : new Dictionary<string, McpServerConfig>(newServers, StringComparer.Ordinal);

            var serversToRemove = new List<string>();
            foreach (var (serverId, serverState) in _workspaceServers)
            {
                if (!desiredServers.TryGetValue(serverId, out var newConfig) ||
                    !newConfig.Enabled ||
                    !ServerConfigEquivalent(serverState.Config, newConfig))
                {
                    serversToRemove.Add(serverId);
                }
            }

            foreach (var serverId in serversToRemove)
            {
                var workspaceState = _workspaceServers[serverId];
                _workspaceServers.Remove(serverId);
                _clients.Remove(workspaceState.Client);

                foreach (var tool in workspaceState.Tools)
                {
                    removedToolNames.Add(tool.Tool.Name);
                    _tools.Remove(tool);
                }

                await DisposeClientAsync(workspaceState.Client).ConfigureAwait(false);
            }

            foreach (var (serverId, serverConfig) in desiredServers)
            {
                if (!serverConfig.Enabled || _workspaceServers.ContainsKey(serverId))
                    continue;

                McpClient? client = null;
                try
                {
                    var transport = CreateTransport(serverId, serverConfig);
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(serverConfig.StartupTimeoutSeconds));
                    client = await McpClient.CreateAsync(transport, cancellationToken: timeoutCts.Token);

                    var displayName = string.IsNullOrWhiteSpace(serverConfig.Name) ? serverId : serverConfig.Name!;
                    var pluginId = $"mcp:{serverId}";
                    var descriptors = await LoadToolsFromClientAsync(client, serverId, pluginId, displayName, serverConfig, ct);
                    var discoveredTools = descriptors
                        .Select(tool => new DiscoveredMcpTool(
                            pluginId,
                            new McpNativeTool(client, tool.LocalName, tool.RemoteName, tool.Description, tool.InputSchemaText, tool.HasUi),
                            displayName))
                        .ToList();

                    _workspaceServers[serverId] = (client, discoveredTools, CloneServerConfig(serverConfig));
                    _clients.Add(client);
                    _tools.AddRange(discoveredTools);
                    addedTools.AddRange(discoveredTools.Select(tool => tool.Tool));
                }
                catch (Exception ex)
                {
                    if (client is not null)
                    {
                        try
                        {
                            await DisposeClientAsync(client).ConfigureAwait(false);
                        }
                        catch (Exception disposeEx)
                        {
                            _logger.LogDebug(disposeEx, "Workspace MCP: failed to dispose client for server '{ServerId}' after connection failure.", serverId);
                        }
                    }

                    _logger.LogError(ex, "Workspace MCP: failed to connect to server '{ServerId}', skipping", serverId);
                }
            }

            return new McpWorkspaceReloadResult(addedTools, removedToolNames);
        }
        finally
        {
            _loadSemaphore.Release();
        }
    }

    private async Task<IReadOnlyList<DiscoveredMcpTool>> LoadInternalAsync(CancellationToken ct)
    {
        if (_loaded)
            return _tools;

        if (!_config.Enabled)
        {
            _loaded = true;
            return _tools;
        }

        var discoveredTools = new List<DiscoveredMcpTool>();
        var discoveredClients = new List<McpClient>();

        try
        {
            foreach (var (serverId, serverConfig) in _config.Servers ?? [])
            {
                if (!serverConfig.Enabled)
                    continue;

                var transport = CreateTransport(serverId, serverConfig);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(serverConfig.StartupTimeoutSeconds));
                var client = await McpClient.CreateAsync(transport, cancellationToken: timeoutCts.Token);
                discoveredClients.Add(client);

                var displayName = string.IsNullOrWhiteSpace(serverConfig.Name) ? serverId : serverConfig.Name!;
                var pluginId = $"mcp:{serverId}";

                var tools = await LoadToolsFromClientAsync(client, serverId, pluginId, displayName, serverConfig, ct);
                _clientsByServerId[serverId] = client;

                foreach (var tool in tools)
                {
                    discoveredTools.Add(new DiscoveredMcpTool(
                        pluginId,
                        new McpNativeTool(client, tool.LocalName, tool.RemoteName, tool.Description, tool.InputSchemaText, tool.HasUi),
                        displayName));
                }
            }

            _clients.AddRange(discoveredClients);
            _tools.AddRange(discoveredTools);
            _loaded = true;
            return _tools;
        }
        catch
        {
            foreach (var client in discoveredClients)
            {
                try
                {
                    await DisposeClientAsync(client);
                }
                catch
                {
                }
            }
            throw;
        }
    }

    private async Task<IReadOnlyList<McpToolDescriptor>> LoadToolsFromClientAsync(
        McpClient client,
        string serverId,
        string pluginId,
        string displayName,
        McpServerConfig config,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(config.RequestTimeoutSeconds));
        var response = await client.ListToolsAsync(cancellationToken: timeoutCts.Token);

        var tools = new List<McpToolDescriptor>();
        foreach (var tool in response)
        {
            var remoteName = tool.Name;
            if (string.IsNullOrWhiteSpace(remoteName))
                throw new InvalidOperationException($"MCP server '{displayName}' returned a tool entry with an empty name.");

            var meta = tool.ProtocolTool.Meta;
            if (!IsToolModelVisible(meta))
            {
                _logger.LogInformation(
                    "MCP server '{DisplayName}': skipping app-only tool '{Tool}' (visibility excludes model).",
                    displayName,
                    remoteName);
                continue;
            }

            var localName = ResolveToolName(serverId, config.ToolNamePrefix, remoteName);
            var description = !string.IsNullOrWhiteSpace(tool.Description)
                ? $"{tool.Description} (from MCP server '{displayName}')"
                : $"MCP tool '{remoteName}' from server '{displayName}'.";
            var inputSchema = ResolveInputSchemaText(tool.JsonSchema);
            var hasUi = ToolHasUi(meta);
            tools.Add(new McpToolDescriptor(localName, remoteName, description, inputSchema, hasUi));
        }

        _logger.LogInformation("MCP server enabled: {ServerId} ({DisplayName}) with {ToolCount} tool(s)",
            serverId, displayName, tools.Count);
        return tools;
    }

    private static string ResolveToolName(string serverId, string? toolNamePrefix, string remoteName)
    {
        var prefix = toolNamePrefix;
        if (prefix is null)
            prefix = $"{SanitizePrefixPart(serverId)}.";

        return string.IsNullOrEmpty(prefix) ? remoteName : prefix + remoteName;
    }

    private static string SanitizePrefixPart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "mcp";

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.')
                sb.Append(char.ToLowerInvariant(ch));
            else
                sb.Append('_');
        }

        return sb.Length == 0 ? "mcp" : sb.ToString();
    }

    private static string ResolveInputSchemaText(JsonElement inputSchema)
    {
        if (inputSchema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return "{}";

        return inputSchema.GetRawText();
    }

    internal static bool IsToolModelVisible(JsonObject? meta)
    {
        if (meta is null)
            return true;
        if (meta["ui"] is not JsonObject ui)
            return true;
        if (ui["visibility"] is not JsonArray visibility)
            return true;

        return visibility
            .OfType<JsonValue>()
            .Any(static value =>
                value.TryGetValue<string>(out var role) &&
                string.Equals(role, "model", StringComparison.Ordinal));
    }

    internal static bool ToolHasUi(JsonObject? meta)
    {
        if (meta is null)
            return false;

        if (meta["ui"] is JsonObject ui &&
            ui["resourceUri"] is JsonValue resourceValue &&
            resourceValue.TryGetValue<string>(out var resourceUri) &&
            !string.IsNullOrEmpty(resourceUri))
        {
            return true;
        }

        if (meta["ui/resourceUri"] is JsonValue flatValue &&
            flatValue.TryGetValue<string>(out var flatResourceUri) &&
            !string.IsNullOrEmpty(flatResourceUri))
        {
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            disposeTask = _disposeTask;
        }

        await disposeTask.ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private static IClientTransport CreateTransport(string serverId, McpServerConfig config)
    {
        var transport = config.NormalizeTransport();
        return transport switch
        {
            "stdio" => new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = config.Command!,
                Arguments = config.Arguments ?? [],
                WorkingDirectory = config.WorkingDirectory,
                EnvironmentVariables = ResolveEnv(config.Environment),
                Name = serverId,
            }),
            "http" => new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(config.Url!),
                AdditionalHeaders = ResolveHeaders(config.Headers),
                Name = serverId,
            }),
            _ => throw new InvalidOperationException($"Unsupported MCP transport '{config.Transport}' for server '{serverId}'.")
        };
    }

    private static Dictionary<string, string?>? ResolveEnv(Dictionary<string, string>? environment)
    {
        if (environment is null || environment.Count == 0)
            return null;

        var resolved = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (name, rawValue) in environment)
        {
            if (rawValue is null)
            {
                resolved[name] = null;
                continue;
            }
            var value = SecretResolver.Resolve(rawValue);
            if (value is null && rawValue.StartsWith("env:", StringComparison.Ordinal))
                throw new InvalidOperationException($"Environment variable '{name}' references unset env var '{rawValue[4..]}'");
            resolved[name] = value ?? rawValue;
        }

        return resolved;
    }

    private static Dictionary<string, string>? ResolveHeaders(Dictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
            return null;

        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, rawValue) in headers)
        {
            if (rawValue is null)
            {
                resolved[name] = string.Empty;
                continue;
            }
            var value = SecretResolver.Resolve(rawValue);
            if (value is null && rawValue.StartsWith("env:", StringComparison.Ordinal))
                throw new InvalidOperationException($"Header '{name}' references unset env var '{rawValue[4..]}'");
            resolved[name] = value ?? rawValue;
        }

        return resolved;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(McpServerToolRegistry));
    }

    private async Task DisposeCoreAsync()
    {
        List<McpClient> clients;

        await _loadSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;

            _disposed = true;
            clients = [.. _clients];
            _clients.Clear();
            _tools.Clear();
            _workspaceServers.Clear();
            _clientsByServerId.Clear();
        }
        finally
        {
            _loadSemaphore.Release();
        }

        foreach (var client in clients)
        {
            try
            {
                await DisposeClientAsync(client).ConfigureAwait(false);
            }
            catch
            {
            }
        }
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

    internal sealed record DiscoveredMcpTool(string PluginId, ITool Tool, string Detail);

    public sealed record McpWorkspaceReloadResult(
        IReadOnlyList<ITool> AddedTools,
        IReadOnlyList<string> RemovedToolNames);

    private static McpServerConfig CloneServerConfig(McpServerConfig config)
        => new()
        {
            Enabled = config.Enabled,
            Name = config.Name,
            Transport = config.Transport,
            Command = config.Command,
            Arguments = [.. config.Arguments],
            WorkingDirectory = config.WorkingDirectory,
            Url = config.Url,
            ToolNamePrefix = config.ToolNamePrefix,
            StartupTimeoutSeconds = config.StartupTimeoutSeconds,
            RequestTimeoutSeconds = config.RequestTimeoutSeconds,
            Environment = new Dictionary<string, string>(config.Environment, StringComparer.Ordinal),
            Headers = new Dictionary<string, string>(config.Headers, StringComparer.Ordinal),
        };

    private static bool ServerConfigEquivalent(McpServerConfig left, McpServerConfig right)
    {
        return left.Enabled == right.Enabled &&
            string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
            string.Equals(left.NormalizeTransport(), right.NormalizeTransport(), StringComparison.Ordinal) &&
            string.Equals(left.Command, right.Command, StringComparison.Ordinal) &&
            SequenceEqual(left.Arguments, right.Arguments) &&
            string.Equals(left.WorkingDirectory, right.WorkingDirectory, StringComparison.Ordinal) &&
            string.Equals(left.Url, right.Url, StringComparison.Ordinal) &&
            string.Equals(left.ToolNamePrefix, right.ToolNamePrefix, StringComparison.Ordinal) &&
            left.StartupTimeoutSeconds == right.StartupTimeoutSeconds &&
            left.RequestTimeoutSeconds == right.RequestTimeoutSeconds &&
            DictionaryEqual(left.Environment, right.Environment) &&
            DictionaryEqual(left.Headers, right.Headers);
    }

    private static bool SequenceEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static bool DictionaryEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left.Count != right.Count)
            return false;

        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var rightValue) ||
                !string.Equals(value, rightValue, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
    private sealed record McpToolDescriptor(string LocalName, string RemoteName, string Description, string InputSchemaText, bool HasUi);
}

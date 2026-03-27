using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
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
public sealed class McpServerToolRegistry : IDisposable
{
    private readonly McpPluginsConfig _config;
    private readonly ILogger _logger;
    private readonly List<IDisposable> _ownedResources = [];
    private readonly List<DiscoveredMcpTool> _tools = [];
    private readonly List<McpClient> _clients = [];
    private bool _loaded;
    private int _registeredAsOwnedResource;

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
        var tools = await LoadAsync(ct);
        if (Interlocked.Exchange(ref _registeredAsOwnedResource, 1) == 0)
            nativeRegistry.RegisterOwnedResource(this);
        foreach (var tool in tools)
            nativeRegistry.RegisterExternalTool(tool.Tool, tool.PluginId, tool.Detail);
    }

    internal async Task<IReadOnlyList<DiscoveredMcpTool>> LoadAsync(CancellationToken ct)
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
            foreach (var (serverId, serverConfig) in _config.Servers)
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

                foreach (var tool in tools)
                {
                    discoveredTools.Add(new DiscoveredMcpTool(
                        pluginId,
                        new McpNativeTool(client, tool.LocalName, tool.RemoteName, tool.Description, tool.InputSchemaText),
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
                    (client as IDisposable)?.Dispose();
                    (client as IAsyncDisposable)?.DisposeAsync().GetAwaiter().GetResult();
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

            var localName = ResolveToolName(serverId, config.ToolNamePrefix, remoteName);
            var description = !string.IsNullOrWhiteSpace(tool.Description)
                ? $"{tool.Description} (from MCP server '{displayName}')"
                : $"MCP tool '{remoteName}' from server '{displayName}'.";
            var inputSchema = "{}";
            tools.Add(new McpToolDescriptor(localName, remoteName, description, inputSchema));
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

    public void Dispose()
    {
        foreach (var resource in _ownedResources)
            resource.Dispose();
        foreach (var client in _clients)
        {
            try
            {
                (client as IDisposable)?.Dispose();
                (client as IAsyncDisposable)?.DisposeAsync().GetAwaiter().GetResult();
            }
            catch
            {
            }
        }
    }



    private static string NormalizeTransport(McpServerConfig config)
    {
        var transport = config.Transport?.Trim();
        if (string.IsNullOrWhiteSpace(transport))
            return string.IsNullOrWhiteSpace(config.Url) ? "stdio" : "http";

        if (transport.Equals("streamable-http", StringComparison.OrdinalIgnoreCase) ||
            transport.Equals("streamable_http", StringComparison.OrdinalIgnoreCase))
        {
            return "http";
        }

        return transport.ToLowerInvariant();
    }

    private static IClientTransport CreateTransport(string serverId, McpServerConfig config)
    {
        var transport = NormalizeTransport(config);
        return transport switch
        {
            "stdio" => new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = config.Command!,
                Arguments = config.Arguments,
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

    private static Dictionary<string, string?>? ResolveEnv(Dictionary<string, string> environment)
    {
        if (environment.Count == 0)
            return null;

        var resolved = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (name, rawValue) in environment)
        {
            var value = SecretResolver.Resolve(rawValue) ?? rawValue;
            resolved[name] = value;
        }

        return resolved;
    }

    private static Dictionary<string, string>? ResolveHeaders(Dictionary<string, string> headers)
    {
        if (headers.Count == 0)
            return null;

        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, rawValue) in headers)
        {
            var value = SecretResolver.Resolve(rawValue) ?? rawValue;
            resolved[name] = value;
        }

        return resolved;
    }

    internal sealed record DiscoveredMcpTool(string PluginId, ITool Tool, string Detail);
    private sealed record McpToolDescriptor(string LocalName, string RemoteName, string Description, string InputSchemaText);
}

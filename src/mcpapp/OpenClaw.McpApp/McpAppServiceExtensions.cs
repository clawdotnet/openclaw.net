using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Plugins;
using OpenClaw.McpApp.Models;

namespace OpenClaw.McpApp;

/// <summary>
/// DI registration extensions for MCP App core services.
/// This registers discovery, and the app registry infrastructure.
/// Call <see cref="AddOpenClawMcpAppServices"/> during gateway service registration.
/// The actual tool registration into <c>NativePluginRegistry</c> is handled
/// by the gateway composition layer.
/// </summary>
public static class McpAppServiceExtensions
{
    /// <summary>
    /// Registers MCP App discovery and hosting services.
    /// </summary>
    public static IServiceCollection AddOpenClawMcpAppServices(
        this IServiceCollection services,
        McpAppsConfig config)
    {
        services.TryAddSingleton(config);
        services.TryAddSingleton<McpAppDiscovery>();
        services.TryAddSingleton<McpAppRegistry>();

        return services;
    }
}

/// <summary>
/// Central registry for MCP Apps. Discovers apps, manages their lifecycle,
/// and tracks connected app info providers.
///
/// Tool registration into the native plugin registry is handled by the
/// gateway's composition layer (see <c>McpAppToolRegistrationExtensions</c>
/// in OpenClaw.Gateway).
/// </summary>
public sealed class McpAppRegistry : IAsyncDisposable
{
    private readonly McpAppsConfig _config;
    private readonly McpAppDiscovery _discovery;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly List<McpAppServer> _servers = [];
    private readonly List<Shared.IMcpAppInfoProvider> _apps = [];
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _loaded;
    private bool _disposed;

    public McpAppRegistry(McpAppsConfig config, McpAppDiscovery discovery, ILoggerFactory loggerFactory)
    {
        _config = config;
        _discovery = discovery;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<McpAppRegistry>();
    }

    /// <summary>
    /// All currently registered MCP App info providers.
    /// </summary>
    public IReadOnlyList<Shared.IMcpAppInfoProvider> Apps
    {
        get
        {
            lock (_lock)
                return _apps.ToList();
        }
    }

    /// <summary>
    /// Discovers, filters, and connects to all enabled MCP Apps.
    /// After this returns, <see cref="Apps"/> contains all successfully
    /// connected apps whose tools are ready to be registered.
    /// </summary>
    public async Task LoadAllAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();

            if (_loaded)
                return;

            var states = _discovery.Discover(ct);
            _logger.LogInformation("MCP App discovery found {Count} candidate(s)", states.Count);

            foreach (var state in states)
            {
                ct.ThrowIfCancellationRequested();

                if (!_discovery.IsAppAllowed(state))
                    continue;

                if (!state.IsValid)
                {
                    _logger.LogWarning("Skipping invalid McpApp '{AppId}': {Errors}",
                        state.Manifest.Id, string.Join("; ", state.ValidationErrors));
                    continue;
                }

                try
                {
                    _config.Entries.TryGetValue(state.Manifest.Id, out var entryConfig);
                    var server = new McpAppServer(
                        state,
                        entryConfig,
                        _loggerFactory.CreateLogger<McpAppServer>());

                    var infoProvider = await server.ConnectAsync(ct);
                    _servers.Add(server);
                    _apps.Add(infoProvider);

                    _logger.LogInformation(
                        "McpApp '{AppId}' loaded: {ToolCount} tools, {ResourceCount} resources",
                        state.Manifest.Id,
                        infoProvider.GetToolDescriptors().Count,
                        infoProvider.GetResourceDescriptors().Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to load McpApp '{AppId}'", state.Manifest.Id);
                }
            }

            _loaded = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Get an info provider by app id.
    /// </summary>
    public Shared.IMcpAppInfoProvider? GetApp(string appId)
    {
        lock (_lock)
            return _apps.FirstOrDefault(a => a.AppId == appId);
    }

    /// <summary>
    /// Get all server instances for lifecycle management.
    /// </summary>
    internal IReadOnlyList<McpAppServer> Servers
    {
        get
        {
            lock (_lock)
                return _servers.ToList();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(McpAppRegistry));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        await _lock.WaitAsync();
        try
        {
            if (_disposed)
                return;

            _disposed = true;

            foreach (var server in _servers)
            {
                try
                {
                    await server.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disposing McpApp server");
                }
            }

            _servers.Clear();
            _apps.Clear();
        }
        finally
        {
            _lock.Release();
        }

        GC.SuppressFinalize(this);
    }
}
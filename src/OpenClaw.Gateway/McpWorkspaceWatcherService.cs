using System.Threading.Channels;
using System.Linq;
using OpenClaw.Agent;
using OpenClaw.Agent.Plugins;
using OpenClaw.Core.Plugins;
using OpenClaw.Gateway.Mcp;

namespace OpenClaw.Gateway;

internal sealed class McpWorkspaceWatcherService : IAsyncDisposable, IDisposable
{
    private static readonly string[] McpJsonRelativePaths =
    [
        Path.Combine(".openclaw", "mcp.json"),
        Path.Combine(".kingcrab", "mcp.json")
    ];
    private readonly McpServerToolRegistry _registry;
    private readonly IAgentRuntime _agentRuntime;
    private readonly ILogger<McpWorkspaceWatcherService> _logger;
    private readonly McpConfigStore _configStore;
    private readonly string? _workspacePath;
    private readonly Channel<bool> _reloadChannel = Channel.CreateUnbounded<bool>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly object _gate = new();
    private CancellationTokenSource? _reloadLoopCts;
    private Task? _reloadLoopTask;
    private bool _started;
    private bool _disposed;

    public McpWorkspaceWatcherService(
        McpServerToolRegistry registry,
        IAgentRuntime agentRuntime,
        string? workspacePath,
        ILogger<McpWorkspaceWatcherService> logger,
        McpConfigStore configStore)
    {
        _registry = registry;
        _agentRuntime = agentRuntime;
        _logger = logger;
        _configStore = configStore;
        _workspacePath = workspacePath;
    }

    public void TriggerReload() => _reloadChannel.Writer.TryWrite(true);

    public void Start(CancellationToken stoppingToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
                return;

            _started = true;
            _reloadLoopCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _reloadLoopTask = RunReloadLoopAsync(_reloadLoopCts.Token);
        }

        var hasWorkspaceFile = !string.IsNullOrWhiteSpace(_workspacePath) &&
            McpJsonRelativePaths.Any(relativePath => File.Exists(Path.Combine(_workspacePath, relativePath)));
        if (hasWorkspaceFile || _configStore is not null)
            TriggerReload();
    }

    public void Dispose()
        => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? reloadLoopCts;
        Task? reloadLoopTask;

        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            reloadLoopCts = _reloadLoopCts;
            _reloadLoopCts = null;
            reloadLoopTask = _reloadLoopTask;
            _reloadLoopTask = null;
        }

        _reloadChannel.Writer.TryComplete();
        reloadLoopCts?.Cancel();
        if (reloadLoopTask is not null)
        {
            try
            {
                await reloadLoopTask;
            }
            catch (OperationCanceledException) when (reloadLoopCts?.IsCancellationRequested == true)
            {
            }
        }

        reloadLoopCts?.Dispose();
    }

    private async Task RunReloadLoopAsync(CancellationToken ct)
    {
        while (await _reloadChannel.Reader.WaitToReadAsync(ct))
        {
            var drainedReloadRequests = 0;
            while (_reloadChannel.Reader.TryRead(out _))
            {
                drainedReloadRequests++;
            }

            try
            {
                _logger.LogDebug("Drained {ReloadRequestCount} pending workspace MCP reload request(s).", drainedReloadRequests);
                var servers = await _configStore.TryLoadServersAsync(ct);
                if (servers is null && !string.IsNullOrWhiteSpace(_workspacePath))
                {
                    servers = await TryReadWorkspaceConfigAsync(ct);
                }

                servers ??= new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
                var reload = await _registry.ReloadWorkspaceServersAsync(servers, ct);
                await _agentRuntime.ApplyMcpToolChangesAsync(reload.AddedTools, reload.RemovedToolNames, ct);
                _logger.LogInformation(
                    "Workspace MCP reload applied. Added {AddedCount} tools, removed {RemovedCount} tools.",
                    reload.AddedTools.Count,
                    reload.RemovedToolNames.Count);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Workspace MCP reload failed.");
            }
        }
    }

    private async Task<Dictionary<string, McpServerConfig>?> TryReadWorkspaceConfigAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_workspacePath))
            return null;

        foreach (var filePath in McpJsonRelativePaths.Select(relativePath => Path.Combine(_workspacePath, relativePath)))
        {
            if (!File.Exists(filePath))
                continue;

            try
            {
                var raw = await File.ReadAllTextAsync(filePath, ct);
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                return McpConfigStore.TryParseServers(raw, filePath, _logger);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Workspace MCP reload fallback failed for {FilePath}", filePath);
            }
        }

        return null;
    }
}

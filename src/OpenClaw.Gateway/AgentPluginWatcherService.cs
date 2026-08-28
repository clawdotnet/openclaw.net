using System.Threading.Channels;
using OpenClaw.Agent;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Gateway;

/// <summary>
/// Watches the Agent Plugin discovery roots (config <c>Plugins:Load:Paths</c>, workspace
/// <c>plugins/</c>, user <c>~/.openclaw/plugins/</c>) for manifest changes (<c>plugin.json</c> /
/// <c>mcp.json</c>) and re-runs Agent Plugin discovery at runtime, so newly installed/removed
/// plugins and their MCP servers take effect without a restart.
///
/// <list type="bullet">
/// <item><description>Newly discovered plugin skill directories are registered with the skill reload
/// path (<c>AgentRuntime</c> plugin skill dirs + <c>SkillWatcherService</c> watch roots) and the
/// skill watcher is notified to reload.</description></item>
/// <item><description>MCP reload is routed through <see cref="McpWorkspaceWatcherService.TriggerReload"/>;
/// its reload loop re-queries the agent-plugin MCP provider, and unchanged servers are kept by the
/// registry's reconciliation, so this is a cheap idempotent trigger.</description></item>
/// </list>
///
/// SKILL.md content edits inside already-known plugin skill directories are hot-reloaded by
/// <see cref="SkillWatcherService"/> itself; this service reacts only to manifest-level changes that
/// alter the discovered package set or its MCP configuration. Only discovery roots that exist at
/// startup are watched (a brand-new root that appears later requires a restart).
/// </summary>
internal sealed class AgentPluginWatcherService : IAsyncDisposable, IDisposable
{
    private readonly AgentPluginRuntimeManager _agentPluginRuntime;
    private readonly IAgentRuntime _agentRuntime;
    private readonly SkillWatcherService _skillWatcher;
    private readonly McpWorkspaceWatcherService _mcpWatcher;
    private readonly ILogger<AgentPluginWatcherService> _logger;
    private readonly string[] _watchRoots;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Channel<byte> _reloadRequests = Channel.CreateUnbounded<byte>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly object _gate = new();
    private CancellationTokenSource? _reloadLoopCts;
    private Task? _reloadLoopTask;
    private CancellationToken _stoppingToken;
    private HashSet<string> _knownSkillDirs;
    private bool _started;
    private bool _disposed;

    public AgentPluginWatcherService(
        AgentPluginRuntimeManager agentPluginRuntime,
        IAgentRuntime agentRuntime,
        SkillWatcherService skillWatcher,
        McpWorkspaceWatcherService mcpWatcher,
        IReadOnlyList<string> watchRoots,
        ILogger<AgentPluginWatcherService> logger)
    {
        _agentPluginRuntime = agentPluginRuntime;
        _agentRuntime = agentRuntime;
        _skillWatcher = skillWatcher;
        _mcpWatcher = mcpWatcher;
        _logger = logger;
        _watchRoots = watchRoots.Where(root => !string.IsNullOrWhiteSpace(root)).ToArray();
        // Seed the diff set with the current (startup) agent-plugin skill directories so that a
        // removal is detected as a change rather than being silently ignored on the first refresh.
        _knownSkillDirs = new HashSet<string>(agentPluginRuntime.GetSkillDirectories(), StringComparer.OrdinalIgnoreCase);
    }

    public void Start(CancellationToken stoppingToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
                return;

            _started = true;
            _stoppingToken = stoppingToken;
            _reloadLoopCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _reloadLoopTask = RunReloadLoopAsync(_reloadLoopCts.Token);
        }

        foreach (var root in _watchRoots)
        {
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.CreationTime |
                                   NotifyFilters.DirectoryName |
                                   NotifyFilters.FileName |
                                   NotifyFilters.LastWrite |
                                   NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                watcher.Changed += OnWatcherChanged;
                watcher.Created += OnWatcherChanged;
                watcher.Deleted += OnWatcherChanged;
                watcher.Renamed += OnWatcherRenamed;
                _watchers.Add(watcher);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to watch Agent Plugin discovery root {Path}", root);
            }
        }

        if (_watchers.Count == 0)
        {
            _logger.LogWarning("Agent Plugin live refresh disabled: no discovery roots are currently available.");
            return;
        }

        _logger.LogInformation("Watching {Count} Agent Plugin discovery root(s) for manifest changes.", _watchers.Count);
    }

    public void Dispose()
        => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        FileSystemWatcher[] watchers;
        CancellationTokenSource? reloadLoopCts;
        Task? reloadLoopTask;

        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            watchers = [.. _watchers];
            _watchers.Clear();
            reloadLoopCts = _reloadLoopCts;
            _reloadLoopCts = null;
            reloadLoopTask = _reloadLoopTask;
            _reloadLoopTask = null;
        }

        foreach (var watcher in watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnWatcherChanged;
            watcher.Created -= OnWatcherChanged;
            watcher.Deleted -= OnWatcherChanged;
            watcher.Renamed -= OnWatcherRenamed;
            watcher.Dispose();
        }

        _reloadRequests.Writer.TryComplete();
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

    private void OnWatcherChanged(object sender, FileSystemEventArgs e)
    {
        if (IsManifestPath(e.FullPath))
            ScheduleReload();
    }

    private void OnWatcherRenamed(object sender, RenamedEventArgs e)
    {
        if (IsManifestPath(e.FullPath) || IsManifestPath(e.OldFullPath))
            ScheduleReload();
    }

    private static bool IsManifestPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var name = Path.GetFileName(path);
        return name.Equals("plugin.json", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("mcp.json", StringComparison.OrdinalIgnoreCase);
    }

    private void ScheduleReload()
    {
        lock (_gate)
        {
            if (_disposed || !_started || _stoppingToken.IsCancellationRequested)
                return;
        }

        _reloadRequests.Writer.TryWrite(0);
    }

    private async Task RunReloadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _reloadRequests.Reader.WaitToReadAsync(ct))
            {
                while (_reloadRequests.Reader.TryRead(out _))
                {
                }

                await WaitForQuietPeriodAsync(ct);
                await TriggerReloadAsync();
            }
        }
        catch (ChannelClosedException)
        {
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task WaitForQuietPeriodAsync(CancellationToken ct)
    {
        while (true)
        {
            var quietPeriodTask = Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            var signalTask = _reloadRequests.Reader.WaitToReadAsync(ct).AsTask();
            var completedTask = await Task.WhenAny(quietPeriodTask, signalTask);
            if (completedTask == quietPeriodTask)
                return;

            if (!await signalTask)
                return;

            while (_reloadRequests.Reader.TryRead(out _))
            {
            }
        }
    }

    private async Task TriggerReloadAsync()
    {
        lock (_gate)
        {
            if (_disposed || _stoppingToken.IsCancellationRequested)
                return;
        }

        try
        {
            var refresh = _agentPluginRuntime.Refresh();

            // 失败边界：逐条记录诊断，错误→LogError，警告→LogWarning（与启动路径一致）。
            foreach (var diag in refresh.Diagnostics)
            {
                if (string.Equals(diag.Severity, "error", StringComparison.OrdinalIgnoreCase))
                    _logger.LogError("Agent Plugin {Surface} issue at {Path}: {Code} — {Message}",
                        diag.Surface, diag.Path, diag.Code, diag.Message);
                else
                    _logger.LogWarning("Agent Plugin {Surface} notice at {Path}: {Code} — {Message}",
                        diag.Surface, diag.Path, diag.Code, diag.Message);
            }

            // Only touch the skill chain when the plugin-packaged skill directory set actually
            // changed (plugin added/removed); SKILL.md content edits are handled by SkillWatcherService.
            var newSkillDirs = new HashSet<string>(refresh.SkillDirectories, StringComparer.OrdinalIgnoreCase);
            if (!newSkillDirs.SetEquals(_knownSkillDirs))
            {
                _knownSkillDirs = newSkillDirs;
                await _agentRuntime.SetPluginSkillDirsAsync(newSkillDirs.ToList(), _stoppingToken);
                foreach (var dir in refresh.SkillDirectories)
                    _skillWatcher.AddWatchRoot(dir);
                // Routes through the skill watcher's reload channel: re-reads skills from the updated
                // plugin skill dirs and pushes the new snapshot to the skill artifact runtime.
                _skillWatcher.NotifySkillChanged();
            }

            // MCP reconciliation keeps unchanged servers, so a trigger on every manifest change is a
            // cheap idempotent no-op when nothing MCP-related moved.
            _mcpWatcher.TriggerReload();
        }
        catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
        {
            // Shutdown path.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh Agent Plugins after manifest change.");
        }
    }
}

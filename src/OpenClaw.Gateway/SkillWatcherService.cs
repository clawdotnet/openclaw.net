using OpenClaw.Agent;
using OpenClaw.Core.Skills;
using System.Threading.Channels;

namespace OpenClaw.Gateway;

internal sealed class SkillWatcherService : IAsyncDisposable, IDisposable
{
    private readonly IAgentRuntime _agentRuntime;
    private readonly ILogger<SkillWatcherService> _logger;
    private readonly Action<IReadOnlyList<SkillDefinition>>? _onSkillsReloaded;
    private readonly Channel<byte> _reloadRequests = Channel.CreateUnbounded<byte>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly object _gate = new();
    private readonly HashSet<string> _watchedRoots = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _reloadLoopCts;
    private Task? _reloadLoopTask;
    private CancellationToken _stoppingToken;
    private bool _started;
    private bool _disposed;

    public SkillWatcherService(
        SkillsConfig config,
        string? workspacePath,
        IReadOnlyList<string>? pluginSkillDirs,
        IAgentRuntime agentRuntime,
        ILogger<SkillWatcherService> logger,
        Action<IReadOnlyList<SkillDefinition>>? onSkillsReloaded = null)
    {
        _agentRuntime = agentRuntime;
        _logger = logger;
        _onSkillsReloaded = onSkillsReloaded;
        foreach (var root in GetWatchRoots(config, workspacePath, pluginSkillDirs)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _watchedRoots.Add(root);
        }
    }

    public void Start(CancellationToken stoppingToken)
    {
        string[] roots;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
                return;

            _started = true;
            _stoppingToken = stoppingToken;
            _reloadLoopCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _reloadLoopTask = RunReloadLoopAsync(_reloadLoopCts.Token);
            roots = [.. _watchedRoots];
        }

        foreach (var root in roots)
        {
            CreateWatchersForRoot(root);
        }

        if (_watchers.Count == 0)
        {
            _logger.LogInformation("Skill watcher disabled because no skill directories are available.");
            return;
        }

        _logger.LogInformation("Watching {Count} skill watchers for SKILL.md and contract JSON changes.", _watchers.Count);
    }

    /// <summary>
    /// Adds a plugin skill directory to the watch set at runtime (used when Agent Plugin discovery
    /// finds a newly installed plugin). Creates watchers immediately if already started; otherwise
    /// the root is picked up by a later Start().
    /// </summary>
    internal void AddWatchRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return;

        lock (_gate)
        {
            if (_disposed || !_watchedRoots.Add(root))
                return;

            if (_started)
                CreateWatchersForRoot(root);
        }
    }

    private void CreateWatchersForRoot(string root)
    {
        try
        {
            Directory.CreateDirectory(root);

            var watcher = new FileSystemWatcher(root, "SKILL.md")
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
            lock (_gate)
            {
                _watchers.Add(watcher);
            }

            // Also watch contract files (artifacts.json, projection indexes, etc.)
            var jsonWatcher = new FileSystemWatcher(root, "*.json")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.CreationTime |
                               NotifyFilters.FileName |
                               NotifyFilters.LastWrite |
                               NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            jsonWatcher.Changed += OnWatcherChanged;
            jsonWatcher.Created += OnWatcherChanged;
            jsonWatcher.Deleted += OnWatcherChanged;
            jsonWatcher.Renamed += OnWatcherRenamed;
            lock (_gate)
            {
                _watchers.Add(jsonWatcher);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to watch skill directory {Path}", root);
        }
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

    private static IEnumerable<string> GetWatchRoots(
        SkillsConfig config,
        string? workspacePath,
        IReadOnlyList<string>? pluginSkillDirs)
    {
        foreach (var dir in config.Load.ExtraDirs)
        {
            if (!string.IsNullOrWhiteSpace(dir))
                yield return dir;
        }

        if (config.Load.IncludeBundled)
            yield return Path.Combine(AppContext.BaseDirectory, "skills");

        if (config.Load.IncludeManaged)
        {
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".openclaw",
                "skills");
        }

        if (pluginSkillDirs is not null)
        {
            foreach (var dir in pluginSkillDirs)
            {
                if (!string.IsNullOrWhiteSpace(dir))
                    yield return dir;
            }
        }

        if (config.Load.IncludeWorkspace && !string.IsNullOrWhiteSpace(workspacePath))
            yield return Path.Combine(workspacePath, "skills");
    }

    private void OnWatcherChanged(object sender, FileSystemEventArgs e)
    {
        if (ShouldReloadForPath(e.FullPath))
            ScheduleReload();
    }

    private void OnWatcherRenamed(object sender, RenamedEventArgs e)
    {
        if (ShouldReloadForPath(e.FullPath) || ShouldReloadForPath(e.OldFullPath))
            ScheduleReload();
    }

    internal void NotifySkillChanged() => ScheduleReload();

    private static bool ShouldReloadForPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (string.Equals(Path.GetFileName(path), "SKILL.md", StringComparison.OrdinalIgnoreCase))
            return true;

        var normalizedPath = path.Replace('\\', '/');
        return normalizedPath.Contains("/contracts/", StringComparison.OrdinalIgnoreCase) &&
               normalizedPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
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
            var loadedSkillNames = await _agentRuntime.ReloadSkillsAsync(_stoppingToken);
            _logger.LogInformation("Reloaded {Count} skills after file change.", loadedSkillNames.Count);
            if (_onSkillsReloaded is not null)
            {
                var skills = _agentRuntime.LoadedSkills;
                _onSkillsReloaded(skills);
            }
        }
        catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
        {
            // Shutdown path.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reload skills after file change.");
        }
    }
}

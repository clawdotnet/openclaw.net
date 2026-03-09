using OpenClaw.Agent;
using OpenClaw.Core.Skills;

namespace OpenClaw.Gateway;

internal sealed class SkillWatcherService : IDisposable
{
    private readonly IAgentRuntime _agentRuntime;
    private readonly ILogger<SkillWatcherService> _logger;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly object _gate = new();
    private readonly string[] _watchRoots;
    private Timer? _debounceTimer;
    private CancellationToken _stoppingToken;
    private bool _started;
    private bool _disposed;
    private int _reloadInProgress;

    public SkillWatcherService(
        SkillsConfig config,
        string? workspacePath,
        IReadOnlyList<string>? pluginSkillDirs,
        IAgentRuntime agentRuntime,
        ILogger<SkillWatcherService> logger)
    {
        _agentRuntime = agentRuntime;
        _logger = logger;
        _watchRoots = GetWatchRoots(config, workspacePath, pluginSkillDirs)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void Start(CancellationToken stoppingToken)
    {
        if (_started)
            return;

        _started = true;
        _stoppingToken = stoppingToken;

        foreach (var root in _watchRoots)
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
                _watchers.Add(watcher);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to watch skill directory {Path}", root);
            }
        }

        if (_watchers.Count == 0)
        {
            _logger.LogInformation("Skill watcher disabled because no skill directories are available.");
            return;
        }

        stoppingToken.Register(Dispose);
        _logger.LogInformation("Watching {Count} skill directories for SKILL.md changes.", _watchers.Count);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        lock (_gate)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnWatcherChanged;
            watcher.Created -= OnWatcherChanged;
            watcher.Deleted -= OnWatcherChanged;
            watcher.Renamed -= OnWatcherRenamed;
            watcher.Dispose();
        }

        _watchers.Clear();
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

    private void OnWatcherChanged(object sender, FileSystemEventArgs e) => ScheduleReload();

    private void OnWatcherRenamed(object sender, RenamedEventArgs e) => ScheduleReload();

    private void ScheduleReload()
    {
        if (_disposed || _stoppingToken.IsCancellationRequested)
            return;

        lock (_gate)
        {
            _debounceTimer ??= new Timer(_ => _ = TriggerReloadAsync(), null, Timeout.Infinite, Timeout.Infinite);
            _debounceTimer.Change(TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
        }
    }

    private async Task TriggerReloadAsync()
    {
        if (_disposed || _stoppingToken.IsCancellationRequested)
            return;

        if (Interlocked.Exchange(ref _reloadInProgress, 1) == 1)
            return;

        try
        {
            var loadedSkillNames = await _agentRuntime.ReloadSkillsAsync(_stoppingToken);
            _logger.LogInformation("Reloaded {Count} skills after file change.", loadedSkillNames.Count);
        }
        catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reload skills after file change.");
        }
        finally
        {
            Interlocked.Exchange(ref _reloadInProgress, 0);
        }
    }
}

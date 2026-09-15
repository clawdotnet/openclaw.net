namespace OpenClaw.Adapters.Nacos.Events;

/// <summary>
/// In-memory test double for <see cref="INacosConfigService"/>. Tests call
/// <see cref="Publish"/> to simulate a Nacos publish event — matching listeners fire
/// synchronously and the latest published config is returned by subsequent
/// <see cref="GetConfigAsync"/> calls. Mirrors the role of
/// <c>FakeNacosRouterMcpTools</c> in the Router test surface.
/// </summary>
public sealed class FakeNacosConfigService : INacosConfigService
{
    private readonly object _gate = new();
    private NacosConfig? _seed;
    private readonly List<Subscription> _listeners = new();

    public Task<NacosConfig?> GetConfigAsync(string dataId, string group, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_seed is { DataId: var d, Group: var g } && d == dataId && g == group)
                return Task.FromResult<NacosConfig?>(_seed);
            return Task.FromResult<NacosConfig?>(null);
        }
    }

    public Task<IDisposable> AddListenerAsync(string dataId, string group, Action<NacosConfig> onChange, CancellationToken ct)
        => Task.FromResult(AddListener(dataId, group, onChange));

    public IDisposable AddListener(string dataId, string group, Action<NacosConfig> onChange)
    {
        ArgumentNullException.ThrowIfNull(onChange);
        var sub = new Subscription(this, dataId, group, onChange);
        lock (_gate) { _listeners.Add(sub); }
        return sub;
    }

    /// <summary>
    /// Simulates a Nacos publish: stores the config as the current snapshot and
    /// fires every active subscription whose <c>(dataId, group)</c> matches.
    /// Disposed subscriptions are skipped.
    /// </summary>
    public void Publish(NacosConfig config)
    {
        Action<NacosConfig>[] toFire;
        lock (_gate)
        {
            _seed = config;
            toFire = _listeners
                .Where(l => !l.Disposed && l.DataId == config.DataId && l.Group == config.Group)
                .Select(l => l.OnChange)
                .ToArray();
        }
        foreach (var cb in toFire) cb(config);
    }

    private sealed class Subscription : IDisposable
    {
        private readonly FakeNacosConfigService _owner;
        private int _disposed;

        public string DataId { get; }
        public string Group { get; }
        public Action<NacosConfig> OnChange { get; }
        public bool Disposed => Volatile.Read(ref _disposed) != 0;

        public Subscription(FakeNacosConfigService owner, string dataId, string group, Action<NacosConfig> onChange)
        {
            _owner = owner;
            DataId = dataId;
            Group = group;
            OnChange = onChange;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                lock (_owner._gate) { _owner._listeners.Remove(this); }
            }
        }
    }
}
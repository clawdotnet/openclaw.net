using System.Text;
using System.Text.Json;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Agent.Tools;

internal sealed class HomeAssistantIndex
{
    private readonly HomeAssistantConfig _config;
    private readonly HomeAssistantWsApi _wsApi;

    private readonly object _gate = new();
    private DateTimeOffset _lastStatesRefresh = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRegistryRefresh = DateTimeOffset.MinValue;
    private DateTimeOffset _lastServicesRefresh = DateTimeOffset.MinValue;

    private readonly Dictionary<string, Entry> _byEntityId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _servicesByDomain = new(StringComparer.Ordinal);

    private const int RegistryRefreshMinutes = 15;
    private const int ServicesRefreshMinutes = 15;
    private const int StatesRefreshSeconds = 10;

    public HomeAssistantIndex(HomeAssistantConfig config)
    {
        _config = config;
        _wsApi = new HomeAssistantWsApi(config);
    }

    public Entry? Get(string entityId)
    {
        lock (_gate)
            return _byEntityId.TryGetValue(entityId, out var e) ? e : null;
    }

    public async Task EnsureWarmAsync(HomeAssistantRestClient rest, CancellationToken ct)
    {
        var shouldRefresh = false;
        lock (_gate)
        {
            if ((DateTimeOffset.UtcNow - _lastStatesRefresh) > TimeSpan.FromSeconds(StatesRefreshSeconds))
            {
                _lastStatesRefresh = DateTimeOffset.UtcNow;
                shouldRefresh = true;
            }
        }

        if (!shouldRefresh)
            return;

        var raw = await rest.GetStatesAsync(ct);
        RefreshStatesFromJson(raw);
    }

    public async Task RefreshRegistriesAsync(CancellationToken ct)
    {
        var shouldRefresh = false;
        lock (_gate)
        {
            if ((DateTimeOffset.UtcNow - _lastRegistryRefresh) > TimeSpan.FromMinutes(RegistryRefreshMinutes))
            {
                _lastRegistryRefresh = DateTimeOffset.UtcNow;
                shouldRefresh = true;
            }
        }

        if (!shouldRefresh)
            return;

        var areas = await _wsApi.ListAreasAsync(ct);
        var devices = await _wsApi.ListDevicesAsync(ct);
        var entities = await _wsApi.ListEntityRegistryAsync(ct);

        lock (_gate)
        {
            var areaById = areas.ToDictionary(a => a.AreaId, a => a.Name, StringComparer.Ordinal);
            var deviceById = devices.ToDictionary(d => d.DeviceId, d => d, StringComparer.Ordinal);

            foreach (var er in entities)
            {
                if (!_byEntityId.TryGetValue(er.EntityId, out var entry))
                {
                    entry = new Entry { EntityId = er.EntityId };
                    _byEntityId[er.EntityId] = entry;
                }

                entry.Platform = er.Platform;

                var areaId = er.AreaId;
                if (string.IsNullOrWhiteSpace(areaId) && !string.IsNullOrWhiteSpace(er.DeviceId) && deviceById.TryGetValue(er.DeviceId, out var dev))
                    areaId = dev.AreaId;

                entry.AreaName = !string.IsNullOrWhiteSpace(areaId) && areaById.TryGetValue(areaId, out var an) ? an : entry.AreaName;
                entry.DeviceName = !string.IsNullOrWhiteSpace(er.DeviceId) && deviceById.TryGetValue(er.DeviceId, out var d) ? d.Name : entry.DeviceName;

                if (!string.IsNullOrWhiteSpace(er.Name))
                    entry.FriendlyName = er.Name;
            }
        }
    }

    public async Task<IReadOnlyList<string>> GetServicesForDomainAsync(HomeAssistantRestClient rest, string domain, CancellationToken ct)
    {
        var shouldRefresh = false;
        lock (_gate)
        {
            if ((DateTimeOffset.UtcNow - _lastServicesRefresh) > TimeSpan.FromMinutes(ServicesRefreshMinutes))
            {
                _lastServicesRefresh = DateTimeOffset.UtcNow;
                shouldRefresh = true;
            }
        }

        if (shouldRefresh)
        {
            var raw = await rest.GetServicesAsync(ct);
            RefreshServicesFromJson(raw);
        }

        lock (_gate)
        {
            return _servicesByDomain.TryGetValue(domain, out var list) ? list.ToArray() : Array.Empty<string>();
        }
    }

    public IEnumerable<Entry> Query(string? domain, string? area, string? nameContains)
    {
        var areaNorm = string.IsNullOrWhiteSpace(area) ? null : area.Trim();
        var nameNorm = string.IsNullOrWhiteSpace(nameContains) ? null : nameContains.Trim();
        var domainNorm = string.IsNullOrWhiteSpace(domain) ? null : domain.Trim();

        List<Entry> snapshot;
        lock (_gate)
            snapshot = _byEntityId.Values.ToList();

        IEnumerable<Entry> q = snapshot;

        if (!string.IsNullOrWhiteSpace(domainNorm))
        {
            q = q.Where(e => e.EntityId.StartsWith(domainNorm + ".", StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(areaNorm))
        {
            q = q.Where(e => !string.IsNullOrWhiteSpace(e.AreaName)
                             && string.Equals(e.AreaName, areaNorm, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(nameNorm))
        {
            q = q.Where(e =>
                (!string.IsNullOrWhiteSpace(e.FriendlyName) && e.FriendlyName.Contains(nameNorm, StringComparison.OrdinalIgnoreCase))
                || e.EntityId.Contains(nameNorm, StringComparison.OrdinalIgnoreCase));
        }

        // Rank: entries with friendly name first, then those with area.
        return q
            .OrderByDescending(e => !string.IsNullOrWhiteSpace(e.FriendlyName))
            .ThenByDescending(e => !string.IsNullOrWhiteSpace(e.AreaName))
            .ThenBy(e => e.EntityId, StringComparer.OrdinalIgnoreCase);
    }

    public string ToJson(IReadOnlyList<Entry> entries)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartArray();
            foreach (var e in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("entity_id", e.EntityId);
                if (!string.IsNullOrWhiteSpace(e.FriendlyName)) writer.WriteString("friendly_name", e.FriendlyName);
                if (!string.IsNullOrWhiteSpace(e.AreaName)) writer.WriteString("area", e.AreaName);
                if (!string.IsNullOrWhiteSpace(e.DeviceName)) writer.WriteString("device", e.DeviceName);
                if (!string.IsNullOrWhiteSpace(e.Platform)) writer.WriteString("platform", e.Platform);
                if (!string.IsNullOrWhiteSpace(e.State)) writer.WriteString("state", e.State);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private void RefreshStatesFromJson(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return;

            lock (_gate)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var entityId = item.TryGetProperty("entity_id", out var eid) ? eid.GetString() : null;
                    if (string.IsNullOrWhiteSpace(entityId))
                        continue;

                    if (!_byEntityId.TryGetValue(entityId, out var entry))
                    {
                        entry = new Entry { EntityId = entityId };
                        _byEntityId[entityId] = entry;
                    }

                    entry.State = item.TryGetProperty("state", out var st) ? st.GetString() : entry.State;

                    if (item.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
                    {
                        if (attrs.TryGetProperty("friendly_name", out var fn))
                        {
                            var friendly = fn.GetString();
                            if (!string.IsNullOrWhiteSpace(friendly))
                                entry.FriendlyName = friendly;
                        }
                    }
                }
            }
        }
        catch
        {
            // ignore; tool will fall back to raw output elsewhere
        }
    }

    private void RefreshServicesFromJson(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return;

            var newMap = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var domain in doc.RootElement.EnumerateArray())
            {
                var domainName = domain.TryGetProperty("domain", out var dn) ? dn.GetString() : null;
                if (string.IsNullOrWhiteSpace(domainName))
                    continue;

                if (!domain.TryGetProperty("services", out var services) || services.ValueKind != JsonValueKind.Object)
                    continue;

                var list = new List<string>();
                foreach (var svc in services.EnumerateObject())
                    list.Add(svc.Name);

                list.Sort(StringComparer.OrdinalIgnoreCase);
                newMap[domainName] = list;
            }

            lock (_gate)
            {
                _servicesByDomain.Clear();
                foreach (var (k, v) in newMap)
                    _servicesByDomain[k] = v;
            }
        }
        catch
        {
            // ignore
        }
    }

    internal sealed class Entry
    {
        public required string EntityId { get; init; }
        public string? FriendlyName { get; set; }
        public string? AreaName { get; set; }
        public string? DeviceName { get; set; }
        public string? Platform { get; set; }
        public string? State { get; set; }
    }
}


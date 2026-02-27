using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Agent.Tools;

public sealed class HomeAssistantTool : ITool, IDisposable
{
    private readonly HomeAssistantConfig _config;
    private readonly HomeAssistantRestClient _rest;
    private readonly HomeAssistantIndex _index;

    public HomeAssistantTool(HomeAssistantConfig config, HttpClient? httpClient = null)
    {
        _config = config;
        _rest = new HomeAssistantRestClient(config, httpClient);
        _index = new HomeAssistantIndex(config);
    }

    public string Name => "home_assistant";

    public string Description =>
        "Interact with a Home Assistant instance (read-only). List entities, read state, list services, and resolve targets by area/name.";

    public string ParameterSchema => """
        {
          "type": "object",
          "properties": {
            "op": {
              "type": "string",
              "enum": ["list_entities","get_state","list_services","resolve_targets","describe_entity"],
              "description": "Operation to perform"
            },
            "entity_id": { "type": "string", "description": "Entity id (for get_state/describe_entity)" },
            "domain": { "type": "string", "description": "Optional domain filter (e.g. light, switch)" },
            "area": { "type": "string", "description": "Optional area filter (P2 indexing)" },
            "name_contains": { "type": "string", "description": "Optional name substring filter (P2 indexing)" },
            "limit": { "type": "integer", "description": "Maximum items to return" },
            "format": { "type": "string", "enum": ["text","json"], "default": "text" }
          },
          "required": ["op"]
        }
        """;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var args = JsonDocument.Parse(argumentsJson);
        var root = args.RootElement;
        var op = root.GetProperty("op").GetString() ?? "";
        var format = root.TryGetProperty("format", out var fmt) ? (fmt.GetString() ?? "text") : "text";
        var domain = root.TryGetProperty("domain", out var d) ? d.GetString() : null;
        var area = root.TryGetProperty("area", out var a) ? a.GetString() : null;
        var nameContains = root.TryGetProperty("name_contains", out var nc) ? nc.GetString() : null;
        var limit = root.TryGetProperty("limit", out var l) ? l.GetInt32() : _config.MaxEntities;
        limit = Math.Clamp(limit, 1, Math.Max(1, _config.MaxEntities));

        return op switch
        {
            "list_entities" => await ListEntitiesAsync(domain, area, nameContains, limit, format, ct),
            "get_state" => await GetStateAsync(root, format, ct),
            "list_services" => await ListServicesAsync(format, ct),
            "resolve_targets" => await ResolveTargetsAsync(domain, area, nameContains, limit, format, ct),
            "describe_entity" => await DescribeEntityAsync(root, format, ct),
            _ => $"Error: Unknown op '{op}'."
        };
    }

    private async Task<string> ListEntitiesAsync(string? domain, string? area, string? nameContains, int limit, string format, CancellationToken ct)
    {
        await _index.EnsureWarmAsync(_rest, ct);

        var matches = _index.Query(domain, area, nameContains)
            .Take(limit)
            .ToList();

        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return _index.ToJson(matches);
        }

        if (matches.Count == 0)
            return "No entities matched.";

        var sb = new StringBuilder();
        for (var i = 0; i < matches.Count; i++)
        {
            var e = matches[i];
            sb.Append('[').Append(i + 1).Append("] ").Append(e.EntityId);
            if (!string.IsNullOrWhiteSpace(e.FriendlyName))
                sb.Append("  \"").Append(e.FriendlyName).Append('"');
            if (!string.IsNullOrWhiteSpace(e.AreaName))
                sb.Append("  (").Append(e.AreaName).Append(')');
            if (!string.IsNullOrWhiteSpace(e.State))
                sb.Append("  = ").Append(e.State);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private async Task<string> GetStateAsync(JsonElement root, string format, CancellationToken ct)
    {
        if (!root.TryGetProperty("entity_id", out var eidProp))
            return "Error: entity_id is required for get_state.";

        var entityId = eidProp.GetString();
        if (string.IsNullOrWhiteSpace(entityId))
            return "Error: entity_id is required for get_state.";

        var raw = await _rest.GetStateAsync(entityId, ct);
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
            return raw;

        return TryFormatSingleState(raw) ?? raw;
    }

    private async Task<string> ListServicesAsync(string format, CancellationToken ct)
    {
        var raw = await _rest.GetServicesAsync(ct);
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
            return raw;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var sb = new StringBuilder();
            foreach (var domain in doc.RootElement.EnumerateArray())
            {
                var domainName = domain.TryGetProperty("domain", out var dn) ? dn.GetString() : null;
                if (string.IsNullOrWhiteSpace(domainName))
                    continue;

                if (domain.TryGetProperty("services", out var services) && services.ValueKind == JsonValueKind.Object)
                {
                    foreach (var svc in services.EnumerateObject())
                    {
                        sb.Append(domainName).Append('.').Append(svc.Name).AppendLine();
                    }
                }
            }

            var text = sb.ToString();
            return text.Length == 0 ? "No services found." : text;
        }
        catch
        {
            return raw;
        }
    }

    private async Task<string> ResolveTargetsAsync(string? domain, string? area, string? nameContains, int limit, string format, CancellationToken ct)
    {
        // Ensure we have registry metadata for area/name resolution (P2)
        await _index.RefreshRegistriesAsync(ct);
        await _index.EnsureWarmAsync(_rest, ct);

        var matches = _index.Query(domain, area, nameContains)
            .Take(limit)
            .ToList();

        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
            return _index.ToJson(matches);

        if (matches.Count == 0)
            return "No targets matched.";

        var sb = new StringBuilder();
        sb.AppendLine("Matches:");
        for (var i = 0; i < matches.Count; i++)
        {
            var e = matches[i];
            sb.Append("  - ").Append(e.EntityId);
            if (!string.IsNullOrWhiteSpace(e.FriendlyName))
                sb.Append("  \"").Append(e.FriendlyName).Append('"');
            if (!string.IsNullOrWhiteSpace(e.AreaName))
                sb.Append("  (").Append(e.AreaName).Append(')');
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private async Task<string> DescribeEntityAsync(JsonElement root, string format, CancellationToken ct)
    {
        if (!root.TryGetProperty("entity_id", out var eidProp))
            return "Error: entity_id is required for describe_entity.";

        var entityId = eidProp.GetString();
        if (string.IsNullOrWhiteSpace(entityId))
            return "Error: entity_id is required for describe_entity.";

        await _index.RefreshRegistriesAsync(ct);
        await _index.EnsureWarmAsync(_rest, ct);

        var entry = _index.Get(entityId);
        var raw = await _rest.GetStateAsync(entityId, ct);

        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
            return raw;

        var sb = new StringBuilder();
        sb.AppendLine($"entity_id: {entityId}");
        if (entry is not null)
        {
            if (!string.IsNullOrWhiteSpace(entry.FriendlyName))
                sb.AppendLine($"name: {entry.FriendlyName}");
            if (!string.IsNullOrWhiteSpace(entry.AreaName))
                sb.AppendLine($"area: {entry.AreaName}");
            if (!string.IsNullOrWhiteSpace(entry.DeviceName))
                sb.AppendLine($"device: {entry.DeviceName}");
            if (!string.IsNullOrWhiteSpace(entry.Platform))
                sb.AppendLine($"platform: {entry.Platform}");
        }

        var formatted = TryFormatSingleState(raw);
        if (!string.IsNullOrWhiteSpace(formatted))
        {
            sb.AppendLine();
            sb.AppendLine(formatted);
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine(raw);
        }

        // Best-effort: list common service operations for the entity domain.
        var entityDomain = entityId.Split('.', 2)[0];
        var services = await _index.GetServicesForDomainAsync(_rest, entityDomain, ct);
        if (services.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("services:");
            foreach (var svc in services)
                sb.AppendLine($"  - {entityDomain}.{svc}");
        }

        return sb.ToString();
    }

    private static string? TryFormatSingleState(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            var entityId = root.TryGetProperty("entity_id", out var eid) ? eid.GetString() : null;
            var state = root.TryGetProperty("state", out var st) ? st.GetString() : null;
            var lastChanged = root.TryGetProperty("last_changed", out var lc) ? lc.GetString() : null;

            var friendly = "";
            if (root.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
            {
                if (attrs.TryGetProperty("friendly_name", out var fn))
                    friendly = fn.GetString() ?? "";
            }

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(entityId))
                sb.AppendLine($"entity_id: {entityId}");
            if (!string.IsNullOrWhiteSpace(friendly))
                sb.AppendLine($"name: {friendly}");
            if (!string.IsNullOrWhiteSpace(state))
                sb.AppendLine($"state: {state}");

            if (!string.IsNullOrWhiteSpace(lastChanged) &&
                DateTimeOffset.TryParse(lastChanged, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                sb.AppendLine($"last_changed: {parsed:O}");
            }

            return sb.ToString().TrimEnd();
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _rest.Dispose();
}

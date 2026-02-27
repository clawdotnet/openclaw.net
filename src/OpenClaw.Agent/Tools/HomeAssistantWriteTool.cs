using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;

namespace OpenClaw.Agent.Tools;

public sealed class HomeAssistantWriteTool : ITool, IDisposable
{
    private readonly HomeAssistantConfig _config;
    private readonly HomeAssistantRestClient _rest;

    public HomeAssistantWriteTool(HomeAssistantConfig config, HttpClient? httpClient = null)
    {
        _config = config;
        _rest = new HomeAssistantRestClient(config, httpClient);
    }

    public string Name => "home_assistant_write";

    public string Description =>
        "Control Home Assistant devices by calling services (write operations). Use with care.";

    public string ParameterSchema => """
        {
          "type": "object",
          "properties": {
            "op": { "type": "string", "enum": ["call_service","call_services"] },
            "domain": { "type": "string" },
            "service": { "type": "string" },
            "entity_id": {
              "oneOf": [
                { "type": "string" },
                { "type": "array", "items": { "type": "string" } },
                { "type": "null" }
              ]
            },
            "data": { "type": ["object","null"] },
            "calls": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "domain": { "type": "string" },
                  "service": { "type": "string" },
                  "entity_id": {
                    "oneOf": [
                      { "type": "string" },
                      { "type": "array", "items": { "type": "string" } },
                      { "type": "null" }
                    ]
                  },
                  "data": { "type": ["object","null"] }
                },
                "required": ["domain","service"]
              }
            }
          },
          "required": ["op"]
        }
        """;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var args = JsonDocument.Parse(argumentsJson);
        var root = args.RootElement;
        var op = root.GetProperty("op").GetString() ?? "";

        return op switch
        {
            "call_service" => await CallServiceAsync(root, ct),
            "call_services" => await CallServicesAsync(root, ct),
            _ => $"Error: Unknown op '{op}'."
        };
    }

    private async Task<string> CallServiceAsync(JsonElement root, CancellationToken ct)
    {
        var domain = RequiredString(root, "domain");
        var service = RequiredString(root, "service");
        var serviceName = $"{domain}.{service}";

        if (!GlobMatcher.IsAllowed(_config.Policy.AllowServiceGlobs, _config.Policy.DenyServiceGlobs, serviceName))
            return $"Error: Service '{serviceName}' is not allowed by policy.";

        var entityIds = ReadEntityIds(root);
        foreach (var entityId in entityIds)
        {
            if (!GlobMatcher.IsAllowed(_config.Policy.AllowEntityIdGlobs, _config.Policy.DenyEntityIdGlobs, entityId))
                return $"Error: Entity '{entityId}' is not allowed by policy.";
        }

        var bodyJson = BuildServiceCallBody(root, entityIds);
        var result = await _rest.CallServiceAsync(domain, service, bodyJson, ct);
        return result;
    }

    private async Task<string> CallServicesAsync(JsonElement root, CancellationToken ct)
    {
        if (!root.TryGetProperty("calls", out var callsProp) || callsProp.ValueKind != JsonValueKind.Array)
            return "Error: calls is required for call_services.";

        var sb = new StringBuilder();
        var i = 0;
        foreach (var call in callsProp.EnumerateArray())
        {
            i++;
            var domain = RequiredString(call, "domain");
            var service = RequiredString(call, "service");
            var serviceName = $"{domain}.{service}";

            if (!GlobMatcher.IsAllowed(_config.Policy.AllowServiceGlobs, _config.Policy.DenyServiceGlobs, serviceName))
            {
                sb.AppendLine($"[{i}] Error: Service '{serviceName}' is not allowed by policy.");
                continue;
            }

            var entityIds = ReadEntityIds(call);
            var denied = entityIds.FirstOrDefault(eid =>
                !GlobMatcher.IsAllowed(_config.Policy.AllowEntityIdGlobs, _config.Policy.DenyEntityIdGlobs, eid));
            if (denied is not null)
            {
                sb.AppendLine($"[{i}] Error: Entity '{denied}' is not allowed by policy.");
                continue;
            }

            var bodyJson = BuildServiceCallBody(call, entityIds);
            try
            {
                var result = await _rest.CallServiceAsync(domain, service, bodyJson, ct);
                sb.AppendLine($"[{i}] OK: {serviceName}");
                if (!string.IsNullOrWhiteSpace(result))
                    sb.AppendLine(result);
            }
            catch (Exception ex)
            {
                sb.AppendLine($"[{i}] Error calling {serviceName}: {ex.Message}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildServiceCallBody(JsonElement root, IReadOnlyList<string> entityIds)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();

            if (entityIds.Count == 1)
                writer.WriteString("entity_id", entityIds[0]);
            else if (entityIds.Count > 1)
            {
                writer.WritePropertyName("entity_id");
                writer.WriteStartArray();
                foreach (var eid in entityIds)
                    writer.WriteStringValue(eid);
                writer.WriteEndArray();
            }

            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in data.EnumerateObject())
                {
                    writer.WritePropertyName(prop.Name);
                    prop.Value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static List<string> ReadEntityIds(JsonElement root)
    {
        if (!root.TryGetProperty("entity_id", out var eid))
            return [];

        if (eid.ValueKind == JsonValueKind.String)
        {
            var s = eid.GetString();
            return string.IsNullOrWhiteSpace(s) ? [] : [s];
        }

        if (eid.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in eid.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        list.Add(s);
                }
            }
            return list;
        }

        return [];
    }

    private static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"Missing required string field '{name}'.");

        var value = prop.GetString();
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Missing required string field '{name}'.");

        return value;
    }

    public void Dispose() => _rest.Dispose();
}


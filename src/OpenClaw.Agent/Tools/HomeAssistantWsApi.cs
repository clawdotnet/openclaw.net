using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;

namespace OpenClaw.Agent.Tools;

internal sealed class HomeAssistantWsApi
{
    private readonly HomeAssistantConfig _config;

    public HomeAssistantWsApi(HomeAssistantConfig config) => _config = config;

    public async Task<IReadOnlyList<AreaEntry>> ListAreasAsync(CancellationToken ct)
    {
        var result = await CallAsync("config/area_registry/list", payload: null, ct);
        return ParseAreas(result);
    }

    public async Task<IReadOnlyList<DeviceEntry>> ListDevicesAsync(CancellationToken ct)
    {
        var result = await CallAsync("config/device_registry/list", payload: null, ct);
        return ParseDevices(result);
    }

    public async Task<IReadOnlyList<EntityRegistryEntry>> ListEntityRegistryAsync(CancellationToken ct)
    {
        var result = await CallAsync("config/entity_registry/list", payload: null, ct);
        return ParseEntityRegistry(result);
    }

    private async Task<JsonElement> CallAsync(string type, JsonElement? payload, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        var url = BuildWebSocketUrl(_config.BaseUrl);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _config.TimeoutSeconds)));

        await ws.ConnectAsync(url, cts.Token);

        // Expect auth_required
        var first = await ReceiveJsonAsync(ws, cts.Token);
        var firstType = first.TryGetProperty("type", out var ft) ? ft.GetString() : null;
        if (!string.Equals(firstType, "auth_required", StringComparison.Ordinal))
            throw new InvalidOperationException($"Home Assistant WS: expected auth_required, got '{firstType ?? "(null)"}'.");

        var token = SecretResolver.Resolve(_config.TokenRef);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Home Assistant token not configured (TokenRef).");

        await SendJsonAsync(ws, BuildAuth(token), cts.Token);
        var authReply = await ReceiveJsonAsync(ws, cts.Token);
        var authType = authReply.TryGetProperty("type", out var at) ? at.GetString() : null;
        if (!string.Equals(authType, "auth_ok", StringComparison.Ordinal))
        {
            var message = authReply.TryGetProperty("message", out var msg) ? msg.GetString() : null;
            throw new InvalidOperationException($"Home Assistant WS auth failed: {authType} {message}");
        }

        var requestId = 1;
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", requestId);
            writer.WriteString("type", type);
            if (payload is { ValueKind: JsonValueKind.Object })
            {
                foreach (var prop in payload.Value.EnumerateObject())
                {
                    writer.WritePropertyName(prop.Name);
                    prop.Value.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }

        await ws.SendAsync(ms.ToArray().AsMemory(), WebSocketMessageType.Text, endOfMessage: true, cancellationToken: cts.Token);

        while (true)
        {
            var msg = await ReceiveJsonAsync(ws, cts.Token);
            var msgType = msg.TryGetProperty("type", out var mt) ? mt.GetString() : null;
            if (!string.Equals(msgType, "result", StringComparison.Ordinal))
                continue;

            var id = msg.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number ? idProp.GetInt32() : -1;
            if (id != requestId)
                continue;

            var success = msg.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
            if (!success)
            {
                var err = msg.TryGetProperty("error", out var e) ? e.ToString() : "(unknown error)";
                throw new InvalidOperationException($"Home Assistant WS call '{type}' failed: {err}");
            }

            if (!msg.TryGetProperty("result", out var res))
                throw new InvalidOperationException("Home Assistant WS: missing result.");

            return res.Clone();
        }
    }

    private static Uri BuildWebSocketUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl.TrimEnd('/'), UriKind.Absolute, out var baseUri))
            throw new ArgumentException($"Invalid Home Assistant BaseUrl: {baseUrl}");

        var scheme = baseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        var builder = new UriBuilder(baseUri)
        {
            Scheme = scheme,
            Path = "/api/websocket",
            Query = ""
        };
        return builder.Uri;
    }

    private static byte[] BuildAuth(string token)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "auth");
            writer.WriteString("access_token", token);
            writer.WriteEndObject();
        }
        return ms.ToArray();
    }

    private static async Task SendJsonAsync(ClientWebSocket ws, byte[] json, CancellationToken ct)
        => await ws.SendAsync(json.AsMemory(), WebSocketMessageType.Text, endOfMessage: true, cancellationToken: ct);

    private static async Task<JsonElement> ReceiveJsonAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();

        while (true)
        {
            var result = await ws.ReceiveAsync(buffer.AsMemory(), ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("WebSocket closed.");

            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                break;
        }

        var text = Encoding.UTF8.GetString(ms.ToArray());
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static IReadOnlyList<AreaEntry> ParseAreas(JsonElement result)
    {
        var list = new List<AreaEntry>();
        if (result.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in result.EnumerateArray())
        {
            var id = item.TryGetProperty("area_id", out var aid) ? aid.GetString() : null;
            var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                list.Add(new AreaEntry(id, name));
        }

        return list;
    }

    private static IReadOnlyList<DeviceEntry> ParseDevices(JsonElement result)
    {
        var list = new List<DeviceEntry>();
        if (result.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in result.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var did) ? did.GetString() : null;
            var name = item.TryGetProperty("name_by_user", out var nu) ? nu.GetString() : null;
            name ??= item.TryGetProperty("name", out var n) ? n.GetString() : null;
            var areaId = item.TryGetProperty("area_id", out var aid) ? aid.GetString() : null;
            if (!string.IsNullOrWhiteSpace(id))
                list.Add(new DeviceEntry(id, name ?? "", areaId));
        }

        return list;
    }

    private static IReadOnlyList<EntityRegistryEntry> ParseEntityRegistry(JsonElement result)
    {
        var list = new List<EntityRegistryEntry>();
        if (result.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in result.EnumerateArray())
        {
            var entityId = item.TryGetProperty("entity_id", out var eid) ? eid.GetString() : null;
            if (string.IsNullOrWhiteSpace(entityId))
                continue;

            var platform = item.TryGetProperty("platform", out var p) ? p.GetString() : null;
            var deviceId = item.TryGetProperty("device_id", out var did) ? did.GetString() : null;
            var areaId = item.TryGetProperty("area_id", out var aid) ? aid.GetString() : null;
            var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;

            list.Add(new EntityRegistryEntry(entityId, platform, deviceId, areaId, name));
        }

        return list;
    }

    internal sealed record AreaEntry(string AreaId, string Name);
    internal sealed record DeviceEntry(string DeviceId, string Name, string? AreaId);
    internal sealed record EntityRegistryEntry(string EntityId, string? Platform, string? DeviceId, string? AreaId, string? Name);
}


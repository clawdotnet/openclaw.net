using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;

namespace OpenClaw.Agent.Integrations;

public sealed class HomeAssistantEventBridge : BackgroundService
{
    private readonly HomeAssistantConfig _config;
    private readonly ILogger<HomeAssistantEventBridge> _logger;
    private readonly ChannelWriter<InboundMessage> _inbound;

    private readonly Dictionary<string, DateTimeOffset> _cooldowns = new(StringComparer.Ordinal);
    private DateTimeOffset _lastGlobalEmit = DateTimeOffset.MinValue;

    public HomeAssistantEventBridge(
        HomeAssistantConfig config,
        ILogger<HomeAssistantEventBridge> logger,
        ChannelWriter<InboundMessage> inbound)
    {
        _config = config;
        _logger = logger;
        _inbound = inbound;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.Enabled || _config.Events.Enabled != true)
        {
            _logger.LogInformation("Home Assistant event bridge disabled.");
            return;
        }

        var backoff = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
                backoff = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Home Assistant event bridge error; reconnecting in {Delay}s", backoff.TotalSeconds);
                await Task.Delay(backoff, stoppingToken);
                backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        var url = BuildWebSocketUrl(_config.BaseUrl);
        await ws.ConnectAsync(url, ct);

        var first = await ReceiveJsonAsync(ws, ct);
        var firstType = first.TryGetProperty("type", out var ft) ? ft.GetString() : null;
        if (!string.Equals(firstType, "auth_required", StringComparison.Ordinal))
            throw new InvalidOperationException($"Home Assistant WS: expected auth_required, got '{firstType ?? "(null)"}'.");

        var token = SecretResolver.Resolve(_config.TokenRef);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Home Assistant token not configured (TokenRef).");

        await SendJsonAsync(ws, BuildAuth(token), ct);
        var authReply = await ReceiveJsonAsync(ws, ct);
        var authType = authReply.TryGetProperty("type", out var at) ? at.GetString() : null;
        if (!string.Equals(authType, "auth_ok", StringComparison.Ordinal))
        {
            var message = authReply.TryGetProperty("message", out var msg) ? msg.GetString() : null;
            throw new InvalidOperationException($"Home Assistant WS auth failed: {authType} {message}");
        }

        var id = 1;
        foreach (var eventType in _config.Events.SubscribeEventTypes)
        {
            if (string.IsNullOrWhiteSpace(eventType))
                continue;

            var sub = BuildSubscribe(++id, eventType.Trim());
            await SendJsonAsync(ws, sub, ct);
            var subReply = await WaitForResultAsync(ws, id, ct);
            if (!subReply.TryGetProperty("success", out var s) || s.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException($"Home Assistant subscribe_events failed for '{eventType}'.");
        }

        _logger.LogInformation("Home Assistant event bridge connected; subscribed to {Count} event types.", _config.Events.SubscribeEventTypes.Length);

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var msg = await ReceiveJsonAsync(ws, ct);
            var type = msg.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (!string.Equals(type, "event", StringComparison.Ordinal))
                continue;

            if (!msg.TryGetProperty("event", out var ev) || ev.ValueKind != JsonValueKind.Object)
                continue;

            await HandleEventAsync(ev, ct);
        }
    }

    private async Task HandleEventAsync(JsonElement ev, CancellationToken ct)
    {
        var eventType = ev.TryGetProperty("event_type", out var et) ? et.GetString() : null;
        var entityId = "";
        var fromState = "";
        var toState = "";
        var friendlyName = "";

        if (ev.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            entityId = data.TryGetProperty("entity_id", out var eid) ? (eid.GetString() ?? "") : "";

            if (data.TryGetProperty("old_state", out var oldSt) && oldSt.ValueKind == JsonValueKind.Object)
                fromState = oldSt.TryGetProperty("state", out var os) ? (os.GetString() ?? "") : "";
            if (data.TryGetProperty("new_state", out var newSt) && newSt.ValueKind == JsonValueKind.Object)
            {
                toState = newSt.TryGetProperty("state", out var ns) ? (ns.GetString() ?? "") : "";

                if (newSt.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
                    friendlyName = attrs.TryGetProperty("friendly_name", out var fn) ? (fn.GetString() ?? "") : "";
            }
        }

        if (!string.IsNullOrWhiteSpace(entityId))
        {
            if (!GlobMatcher.IsAllowed(_config.Events.AllowEntityIdGlobs, _config.Events.DenyEntityIdGlobs, entityId))
                return;
        }

        // Pick a rule (if any)
        var now = DateTimeOffset.UtcNow;
        var info = new HomeAssistantRuleEngine.EventInfo(
            eventType ?? "",
            entityId,
            fromState,
            toState,
            friendlyName);

        var matchedRule = HomeAssistantRuleEngine.SelectRule(_config.Events, info, DateTime.Now);

        if (matchedRule is null && !_config.Events.EmitAllMatchingEvents)
            return;

        if (!TryConsumeCooldown(matchedRule, now))
            return;

        var text = HomeAssistantRuleEngine.Render(_config.Events, matchedRule, info);
        var msg = new InboundMessage
        {
            ChannelId = _config.Events.ChannelId,
            SessionId = _config.Events.SessionId,
            SenderId = "system",
            Text = text
        };

        await _inbound.WriteAsync(msg, ct);
    }

    private bool TryConsumeCooldown(HomeAssistantEventRule? rule, DateTimeOffset now)
    {
        var globalCooldown = TimeSpan.FromSeconds(Math.Max(0, _config.Events.GlobalCooldownSeconds));
        if (globalCooldown > TimeSpan.Zero && (now - _lastGlobalEmit) < globalCooldown)
            return false;

        if (rule is null)
        {
            _lastGlobalEmit = now;
            return true;
        }

        var cooldown = TimeSpan.FromSeconds(Math.Max(0, rule.CooldownSeconds));
        if (cooldown <= TimeSpan.Zero)
        {
            _lastGlobalEmit = now;
            return true;
        }

        var key = $"rule:{rule.Name}";
        if (_cooldowns.TryGetValue(key, out var last) && (now - last) < cooldown)
            return false;

        _cooldowns[key] = now;
        _lastGlobalEmit = now;
        return true;
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

    private static byte[] BuildSubscribe(int id, string eventType)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteString("type", "subscribe_events");
            writer.WriteString("event_type", eventType);
            writer.WriteEndObject();
        }
        return ms.ToArray();
    }

    private static async Task<JsonElement> WaitForResultAsync(ClientWebSocket ws, int id, CancellationToken ct)
    {
        while (true)
        {
            var msg = await ReceiveJsonAsync(ws, ct);
            var type = msg.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (!string.Equals(type, "result", StringComparison.Ordinal))
                continue;

            var msgId = msg.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : -1;
            if (msgId != id)
                continue;

            return msg.Clone();
        }
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
}

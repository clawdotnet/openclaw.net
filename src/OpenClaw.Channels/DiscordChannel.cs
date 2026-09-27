using System.Buffers;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Http;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Channels;

/// <summary>
/// A channel adapter for the Discord Bot API.
/// Uses a Gateway WebSocket for receiving messages and REST API for sending.
/// Interaction webhooks (slash commands) are handled separately in the gateway.
/// </summary>
public sealed class DiscordChannel : IChannelAdapter
{
    private const string ApiBase = "https://discord.com/api/v10";
    private const string GatewayUrl = "wss://gateway.discord.gg/?v=10&encoding=json";
    private const int GatewayIntentGuildMessages = 1 << 9;
    private const int GatewayIntentDirectMessages = 1 << 12;
    private const int GatewayIntentMessageContent = 1 << 15;

    private readonly DiscordChannelConfig _config;
    private readonly HttpClient _http;
    private readonly ILogger<DiscordChannel> _logger;
    private readonly string _botToken;
    private readonly string? _applicationId;
    private readonly bool _ownsHttp;

    private ClientWebSocket? _gateway;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private int? _lastSequence;
    private string? _sessionId;
    private string? _resumeGatewayUrl;

    public DiscordChannel(DiscordChannelConfig config, ILogger<DiscordChannel> logger)
        : this(config, logger, http: null)
    {
    }

    public DiscordChannel(DiscordChannelConfig config, ILogger<DiscordChannel> logger, HttpClient? http)
    {
        _config = config;
        _logger = logger;
        _http = http ?? HttpClientFactory.Create();
        _ownsHttp = http is null;

        var tokenSource = SecretResolver.Resolve(config.BotTokenRef) ?? config.BotToken;
        _botToken = tokenSource ?? throw new InvalidOperationException("Discord bot token not configured or missing from environment.");

        _applicationId = SecretResolver.Resolve(config.ApplicationIdRef) ?? config.ApplicationId;
    }

    public string ChannelType => "discord";
    public string ChannelId => "discord";

    public event Func<InboundMessage, CancellationToken, ValueTask>? OnMessageReceived;

    public async Task StartAsync(CancellationToken ct)
    {
        if (_config.RegisterSlashCommands && !string.IsNullOrWhiteSpace(_applicationId))
            await RegisterSlashCommandsAsync(ct);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveLoop = RunGatewayLoopAsync(_cts.Token);
    }

    public async ValueTask SendAsync(OutboundMessage outbound, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(outbound.Text)) return;

        try
        {
            var (markers, remaining) = MediaMarkerProtocol.Extract(outbound.Text);
            var text = string.IsNullOrWhiteSpace(remaining) ? outbound.Text : remaining;

            // Discord message limit is 2000 chars
            if (text.Length > 2000)
                text = text[..2000];

            var payload = new DiscordCreateMessageRequest { Content = text };
            var response = await SendMessageRequestAsync(outbound.RecipientId, payload, ct);

            // Handle rate limiting
            if ((int)response.StatusCode == 429)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
                _logger.LogWarning("Discord rate limited, retrying after {RetryAfter}ms.", retryAfter.TotalMilliseconds);
                response.Dispose();
                await Task.Delay(retryAfter, ct);
                response = await SendMessageRequestAsync(outbound.RecipientId, payload, ct);
            }

            response.EnsureSuccessStatusCode();
            response.Dispose();
            _logger.LogInformation("Sent Discord message to channel {ChannelId}.", outbound.RecipientId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Discord message to {RecipientId}.", outbound.RecipientId);
        }
    }

    private async Task<HttpResponseMessage> SendMessageRequestAsync(
        string recipientId,
        DiscordCreateMessageRequest payload,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/channels/{recipientId}/messages");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bot", _botToken);
        request.Content = JsonContent.Create(payload, DiscordJsonContext.Default.DiscordCreateMessageRequest);
        return await _http.SendAsync(request, ct);
    }

    private async Task RunGatewayLoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        const int maxBackoff = 60;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                _gateway = new ClientWebSocket();
                var url = _resumeGatewayUrl ?? GatewayUrl;
                await _gateway.ConnectAsync(new Uri(url), ct);
                _logger.LogInformation("Connected to Discord Gateway.");

                backoff = TimeSpan.FromSeconds(1);
                await ProcessGatewayMessagesAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Discord Gateway disconnected. Reconnecting in {Backoff}s.", backoff.TotalSeconds);
            }
            finally
            {
                if (_gateway?.State == WebSocketState.Open)
                {
                    try { await _gateway.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
                    catch { /* best-effort close */ }
                }
                _gateway?.Dispose();
                _gateway = null;
            }

            await Task.Delay(backoff, ct);
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, maxBackoff));
        }
    }

    private async Task ProcessGatewayMessagesAsync(CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        Task? heartbeatTask = null;
        CancellationTokenSource? heartbeatCts = null;

        try
        {
            while (!ct.IsCancellationRequested && _gateway?.State == WebSocketState.Open)
            {
                buffer.Clear();
                ValueWebSocketReceiveResult result;
                do
                {
                    var memory = buffer.GetMemory(4096);
                    result = await _gateway.ReceiveAsync(memory, ct);
                    buffer.Advance(result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                    return;

                using var doc = JsonDocument.Parse(buffer.WrittenMemory);
                var root = doc.RootElement;

                var op = root.GetProperty("op").GetInt32();
                if (root.TryGetProperty("s", out var seqProp) && seqProp.ValueKind == JsonValueKind.Number)
                    _lastSequence = seqProp.GetInt32();

                switch (op)
                {
                    case 10: // Hello — start heartbeat and identify
                        var interval = root.GetProperty("d").GetProperty("heartbeat_interval").GetInt32();
                        heartbeatCts?.Cancel();
                        heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        heartbeatTask = RunHeartbeatAsync(interval, heartbeatCts.Token);

                        if (_sessionId is not null)
                            await SendResumeAsync(ct);
                        else
                            await SendIdentifyAsync(ct);
                        break;

                    case 0: // Dispatch
                        var eventName = root.TryGetProperty("t", out var tProp) ? tProp.GetString() : null;
                        if (root.TryGetProperty("d", out var data))
                            await HandleDispatchAsync(eventName, data, ct);
                        break;

                    case 7: // Reconnect
                        _logger.LogInformation("Discord Gateway requested reconnect.");
                        return;

                    case 9: // Invalid Session — re-identify
                        _sessionId = null;
                        _lastSequence = null;
                        await Task.Delay(Random.Shared.Next(1000, 5000), ct);
                        await SendIdentifyAsync(ct);
                        break;

                    case 11: // Heartbeat ACK — no action needed
                        break;
                }
            }
        }
        finally
        {
            heartbeatCts?.Cancel();
            if (heartbeatTask is not null)
            {
                try { await heartbeatTask; }
                catch (OperationCanceledException) { }
            }
            heartbeatCts?.Dispose();
        }
    }

    private async Task RunHeartbeatAsync(int intervalMs, CancellationToken ct)
    {
        // Jitter the first heartbeat
        await Task.Delay(Random.Shared.Next(0, intervalMs), ct);
        while (!ct.IsCancellationRequested)
        {
            await SendGatewayPayloadAsync(1, _lastSequence?.ToString() ?? "null", ct);
            await Task.Delay(intervalMs, ct);
        }
    }

    private async Task SendIdentifyAsync(CancellationToken ct)
    {
        var intents = GatewayIntentGuildMessages | GatewayIntentDirectMessages | GatewayIntentMessageContent;
        var payload = "{\"op\":2,\"d\":{\"token\":\"" + _botToken + "\",\"intents\":" + intents +
            ",\"properties\":{\"os\":\"linux\",\"browser\":\"openclaw\",\"device\":\"openclaw\"}}}";
        await SendRawAsync(payload, ct);
    }

    private async Task SendResumeAsync(CancellationToken ct)
    {
        var payload = "{\"op\":6,\"d\":{\"token\":\"" + _botToken + "\",\"session_id\":\"" + _sessionId +
            "\",\"seq\":" + (_lastSequence ?? 0) + "}}";
        await SendRawAsync(payload, ct);
    }

    private async Task SendGatewayPayloadAsync(int op, string data, CancellationToken ct)
    {
        var payload = $"{{\"op\":{op},\"d\":{data}}}";
        await SendRawAsync(payload, ct);
    }

    private async Task SendRawAsync(string payload, CancellationToken ct)
    {
        if (_gateway?.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(payload);
        await _gateway.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private async Task HandleDispatchAsync(string? eventName, JsonElement data, CancellationToken ct)
    {
        switch (eventName)
        {
            case "READY":
                _sessionId = data.TryGetProperty("session_id", out var sid) ? sid.GetString() : null;
                _resumeGatewayUrl = data.TryGetProperty("resume_gateway_url", out var rgu) ? rgu.GetString() : null;
                _logger.LogInformation("Discord Gateway ready. Session: {SessionId}.", _sessionId);
                break;

            case "MESSAGE_CREATE":
                await HandleMessageCreateAsync(data, ct);
                break;
        }
    }

    private async Task HandleMessageCreateAsync(JsonElement data, CancellationToken ct)
    {
        // Ignore bot messages
        if (data.TryGetProperty("author", out var author) &&
            author.TryGetProperty("bot", out var bot) &&
            bot.ValueKind == JsonValueKind.True)
            return;

        var userId = author.TryGetProperty("id", out var uid) ? uid.GetString() : null;
        var text = data.TryGetProperty("content", out var content) ? content.GetString() : null;
        var channelId = data.TryGetProperty("channel_id", out var cid) ? cid.GetString() : null;
        var messageId = data.TryGetProperty("id", out var mid) ? mid.GetString() : null;
        var guildId = data.TryGetProperty("guild_id", out var gid) ? gid.GetString() : null;

        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(text))
            return;

        // Guild allowlist
        if (_config.AllowedGuildIds.Length > 0 && !string.IsNullOrWhiteSpace(guildId))
        {
            if (!Array.Exists(_config.AllowedGuildIds, id => string.Equals(id, guildId, StringComparison.Ordinal)))
                return;
        }

        // Channel allowlist
        if (_config.AllowedChannelIds.Length > 0 && !string.IsNullOrWhiteSpace(channelId))
        {
            if (!Array.Exists(_config.AllowedChannelIds, id => string.Equals(id, channelId, StringComparison.Ordinal)))
                return;
        }

        // User allowlist
        if (_config.AllowedFromUserIds.Length > 0)
        {
            if (!Array.Exists(_config.AllowedFromUserIds, id => string.Equals(id, userId, StringComparison.Ordinal)))
                return;
        }

        if (text.Length > _config.MaxInboundChars)
            text = text[.._config.MaxInboundChars];

        // Thread detection
        var isThread = data.TryGetProperty("thread", out _) ||
                       data.TryGetProperty("message_reference", out _) &&
                       data.TryGetProperty("position", out _); // thread messages have position
        var isDm = guildId is null;

        // Session mapping
        string? sessionId;
        if (isThread && channelId is not null)
            sessionId = $"discord:thread:{channelId}";
        else if (isDm)
            sessionId = null; // default: discord:{userId}
        else if (channelId is not null)
            sessionId = $"discord:{channelId}:{userId}";
        else
            sessionId = null;

        // Reply-to mapping
        string? replyToId = null;
        if (data.TryGetProperty("message_reference", out var msgRef) &&
            msgRef.TryGetProperty("message_id", out var refId))
            replyToId = refId.GetString();

        var message = new InboundMessage
        {
            ChannelId = "discord",
            SenderId = userId,
            SessionId = sessionId,
            Text = text,
            MessageId = messageId,
            ReplyToMessageId = replyToId,
            IsGroup = !isDm,
            GroupId = guildId,
        };

        if (OnMessageReceived is not null)
            await OnMessageReceived(message, ct);
    }

    private async Task RegisterSlashCommandsAsync(CancellationToken ct)
    {
        try
        {
            var commandName = _config.SlashCommandPrefix;
            var payload = $$"""[{"name":"{{commandName}}","description":"Send a message to OpenClaw","type":1,"options":[{"name":"message","description":"Your message","type":3,"required":true}]}]""";

            using var request = new HttpRequestMessage(HttpMethod.Put, $"{ApiBase}/applications/{_applicationId}/commands");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bot", _botToken);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                _logger.LogInformation("Registered Discord slash command '/{Command}'.", commandName);
            else
                _logger.LogWarning("Failed to register Discord slash commands: {Status}.", response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to register Discord slash commands.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_receiveLoop is not null)
        {
            try { await _receiveLoop; }
            catch (OperationCanceledException) { }
        }
        _cts?.Dispose();
        _gateway?.Dispose();
        if (_ownsHttp)
            _http.Dispose();
    }
}

public sealed class DiscordCreateMessageRequest
{
    [JsonPropertyName("content")]
    public required string Content { get; set; }
}

public sealed class DiscordInteraction
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("data")]
    public DiscordInteractionData? Data { get; set; }

    [JsonPropertyName("guild_id")]
    public string? GuildId { get; set; }

    [JsonPropertyName("channel_id")]
    public string? ChannelId { get; set; }

    [JsonPropertyName("member")]
    public DiscordInteractionMember? Member { get; set; }

    [JsonPropertyName("user")]
    public DiscordInteractionUser? User { get; set; }

    [JsonPropertyName("token")]
    public string? Token { get; set; }
}

public sealed class DiscordInteractionData
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("options")]
    public DiscordInteractionOption[]? Options { get; set; }
}

public sealed class DiscordInteractionOption
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("value")]
    public JsonElement? Value { get; set; }
}

public sealed class DiscordInteractionMember
{
    [JsonPropertyName("user")]
    public DiscordInteractionUser? User { get; set; }
}

public sealed class DiscordInteractionUser
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }
}

public sealed class DiscordInteractionResponse
{
    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("data")]
    public DiscordInteractionResponseData? Data { get; set; }
}

public sealed class DiscordInteractionResponseData
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }
}

[JsonSerializable(typeof(DiscordCreateMessageRequest))]
[JsonSerializable(typeof(DiscordInteraction))]
[JsonSerializable(typeof(DiscordInteractionData))]
[JsonSerializable(typeof(DiscordInteractionOption))]
[JsonSerializable(typeof(DiscordInteractionMember))]
[JsonSerializable(typeof(DiscordInteractionUser))]
[JsonSerializable(typeof(DiscordInteractionResponse))]
[JsonSerializable(typeof(DiscordInteractionResponseData))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class DiscordJsonContext : JsonSerializerContext;

using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Http;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Channels;

public delegate ValueTask TelegramUpdateProcessor(
    string payload,
    Func<InboundMessage, CancellationToken, ValueTask> enqueue,
    CancellationToken ct);

/// <summary>
/// A channel adapter for the Telegram Bot API using webhooks or long polling.
/// </summary>
public sealed class TelegramChannel : IChannelAdapter
{
    private const int MaxMessageChars = 4096;
    private const int MaxCaptionChars = 1024;
    private const int MaxPollingBackoffSeconds = 60;
    private const int ConflictRetryFloorSeconds = 30;

    private readonly TelegramChannelConfig _config;
    private readonly HttpClient _http;
    private readonly ILogger<TelegramChannel> _logger;
    private readonly string _botToken;
    private readonly bool _ownsHttp;
    private readonly TelegramUpdateProcessor? _updateProcessor;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public TelegramChannel(TelegramChannelConfig config, ILogger<TelegramChannel> logger)
        : this(config, logger, http: null, updateProcessor: null)
    {
    }

    public TelegramChannel(TelegramChannelConfig config, ILogger<TelegramChannel> logger, HttpClient? http)
        : this(config, logger, http, updateProcessor: null)
    {
    }

    public TelegramChannel(
        TelegramChannelConfig config,
        ILogger<TelegramChannel> logger,
        HttpClient? http,
        TelegramUpdateProcessor? updateProcessor)
        : this(config, logger, http, updateProcessor, static (delay, ct) => Task.Delay(delay, ct))
    {
    }

    internal TelegramChannel(
        TelegramChannelConfig config,
        ILogger<TelegramChannel> logger,
        HttpClient? http,
        TelegramUpdateProcessor? updateProcessor,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        _config = config;
        _logger = logger;
        _http = http ?? HttpClientFactory.Create();
        _ownsHttp = http is null;
        _updateProcessor = updateProcessor;
        _delayAsync = delayAsync;

        var tokenSource = SecretResolver.Resolve(config.BotTokenRef) ?? config.BotToken;

        _botToken = tokenSource ?? throw new InvalidOperationException("Telegram bot token not configured or missing from environment.");
    }

    public string ChannelType => "telegram";
    public string ChannelId => "telegram";
    public event Func<InboundMessage, CancellationToken, ValueTask>? OnMessageReceived;

    public async Task StartAsync(CancellationToken ct)
    {
        if (!_config.UsesLongPolling())
            return;

        if (_updateProcessor is null)
            throw new InvalidOperationException("Telegram long polling requires an inbound update processor.");

        var retryDelay = TimeSpan.FromSeconds(_config.PollingRetryDelaySeconds);
        var maxRetryDelay = TimeSpan.FromSeconds(Math.Max(MaxPollingBackoffSeconds, _config.PollingRetryDelaySeconds));
        var webhookRemoved = false;
        long? offset = null;
        while (!ct.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                if (!webhookRemoved)
                {
                    await DeleteWebhookAsync(ct);
                    webhookRemoved = true;
                    _logger.LogInformation("Telegram long polling started.");
                }

                using var response = await GetUpdatesAsync(offset, ct);
                foreach (var update in response.RootElement.GetProperty("result").EnumerateArray())
                {
                    var updateId = update.GetProperty("update_id").GetInt64();
                    await _updateProcessor(update.GetRawText(), EnqueueInboundAsync, ct);
                    offset = checked(updateId + 1);
                }

                retryDelay = TimeSpan.FromSeconds(_config.PollingRetryDelaySeconds);
                continue;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (TelegramBotApiException ex) when (!IsRetryable(ex.ErrorCode))
            {
                _logger.LogError(
                    ex,
                    "Telegram long polling stopped after a permanent Bot API error ({ErrorCode}). Check the bot token and channel configuration.",
                    ex.ErrorCode);
                throw;
            }
            catch (TelegramBotApiException ex)
            {
                delay = GetTelegramRetryDelay(ex, retryDelay);
                LogTelegramRetry(ex, delay);
            }
            catch (Exception ex)
            {
                delay = retryDelay;
                _logger.LogWarning(
                    ex,
                    "Telegram long polling cycle failed; retrying in {DelaySeconds} seconds.",
                    delay.TotalSeconds);
            }

            try
            {
                await _delayAsync(delay, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }

            retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, maxRetryDelay.TotalSeconds));
        }
    }

    public async ValueTask SendAsync(OutboundMessage outbound, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(outbound.Text)) return;

        if (!TelegramChatId.TryCreate(outbound.RecipientId, out var chatId))
        {
            _logger.LogWarning("Telegram SendAsync aborted: RecipientId is not configured.");
            return;
        }

        var replyToMessageId = TryParseReplyToMessageId(outbound.ReplyToMessageId);
        try
        {
            var (markers, remaining) = MediaMarkerProtocol.Extract(outbound.Text);
            var media = markers
                .Select(static marker => TelegramMediaRequest.TryCreate(marker, out var request) ? request : null)
                .OfType<TelegramMediaRequest>()
                .ToList();

            if (media.Count == 0)
            {
                var text = markers.Count == 0 ? outbound.Text : remaining;
                if (string.IsNullOrWhiteSpace(text))
                {
                    _logger.LogWarning("Telegram SendAsync skipped unsupported media-only message to {ChatId}.", chatId);
                    return;
                }

                var first = true;
                foreach (var chunk in ChunkText(text, MaxMessageChars))
                {
                    await SendMessageAsync(chatId, chunk, first ? replyToMessageId : null, ct);
                    first = false;
                }

                return;
            }

            var caption = string.IsNullOrWhiteSpace(remaining) ? null : remaining;
            var captionForMedia = caption is not null && caption.Length > MaxCaptionChars
                ? caption[..(MaxCaptionChars - 1)] + "…"
                : caption;
            var captionSentAsCaption = false;

            for (var i = 0; i < media.Count; i++)
            {
                var request = media[i];
                var cap = i == 0 && request.SupportsCaption ? captionForMedia : null;
                captionSentAsCaption = captionSentAsCaption || cap is not null;
                await SendMediaAsync(chatId, request, cap, i == 0 ? replyToMessageId : null, ct);
            }

            if (caption is not null && !captionSentAsCaption)
            {
                foreach (var chunk in ChunkText(caption, MaxMessageChars))
                    await SendMessageAsync(chatId, chunk, replyToMessageId: null, ct);
            }

            // If caption was truncated, send remainder as a follow-up message.
            if (captionSentAsCaption && caption is not null && caption.Length > MaxCaptionChars)
            {
                var rest = caption[(MaxCaptionChars - 1)..].Trim();
                foreach (var chunk in ChunkText(rest, MaxMessageChars))
                    await SendMessageAsync(chatId, chunk, replyToMessageId: null, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Telegram message to {ChatId}", chatId);
        }
    }

    private async Task SendMessageAsync(TelegramChatId chatId, string text, int? replyToMessageId, CancellationToken ct)
    {
        var url = $"https://api.telegram.org/bot{_botToken}/sendMessage";
        var payload = new TelegramMessagePayload
        {
            ChatId = chatId,
            Text = text,
            ReplyToMessageId = replyToMessageId
        };
        var response = await _http.PostAsJsonAsync(url, payload, TelegramJsonContext.Default.TelegramMessagePayload, ct);
        response.EnsureSuccessStatusCode();
        _logger.LogInformation("Sent Telegram message to {ChatId}", chatId);
    }

    private async Task DeleteWebhookAsync(CancellationToken ct)
    {
        var dropPendingUpdates = _config.DropPendingUpdatesOnStart ? "true" : "false";
        var url = $"https://api.telegram.org/bot{_botToken}/deleteWebhook?drop_pending_updates={dropPendingUpdates}";
        using var response = await _http.GetAsync(url, ct);
        using var document = await EnsureBotApiSuccessAsync(response, ct);
    }

    private async Task<JsonDocument> GetUpdatesAsync(long? offset, CancellationToken ct)
    {
        var query = $"limit=100&timeout={_config.PollingTimeoutSeconds}" +
            "&allowed_updates=%5B%22message%22%2C%22channel_post%22%2C%22edited_message%22%2C%22edited_channel_post%22%5D";
        if (offset is not null)
            query = $"offset={offset.Value}&{query}";

        var url = $"https://api.telegram.org/bot{_botToken}/getUpdates?{query}";
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        return await EnsureBotApiSuccessAsync(response, ct);
    }

    private static async Task<JsonDocument> EnsureBotApiSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct),
                cancellationToken: ct);
        }
        catch (JsonException ex)
        {
            var statusCode = (int)response.StatusCode;
            throw new TelegramBotApiException(
                statusCode,
                $"HTTP {statusCode} ({response.ReasonPhrase ?? "unknown status"}) returned an invalid JSON response.",
                retryAfter: null,
                ex);
        }

        if (response.IsSuccessStatusCode &&
            document.RootElement.TryGetProperty("ok", out var ok) &&
            ok.ValueKind == JsonValueKind.True)
        {
            return document;
        }

        var root = document.RootElement;
        var errorCode = root.TryGetProperty("error_code", out var errorCodeNode) &&
            errorCodeNode.TryGetInt32(out var parsedErrorCode)
                ? parsedErrorCode
                : (int)response.StatusCode;
        var description = root.TryGetProperty("description", out var descriptionNode)
            ? descriptionNode.GetString()
            : null;
        TimeSpan? retryAfter = null;
        if (root.TryGetProperty("parameters", out var parameters) &&
            parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("retry_after", out var retryAfterNode) &&
            retryAfterNode.TryGetInt32(out var retryAfterSeconds) &&
            retryAfterSeconds > 0)
        {
            retryAfter = TimeSpan.FromSeconds(retryAfterSeconds);
        }

        document.Dispose();
        throw new TelegramBotApiException(errorCode, description ?? "unknown error", retryAfter);
    }

    private static bool IsRetryable(int errorCode)
        => errorCode is < 400 or 408 or 409 or 429 or >= 500;

    private static TimeSpan GetTelegramRetryDelay(TelegramBotApiException exception, TimeSpan backoff)
    {
        if (exception.RetryAfter is { } retryAfter)
            return retryAfter;

        if (exception.ErrorCode == 409)
            return TimeSpan.FromSeconds(Math.Max(backoff.TotalSeconds, ConflictRetryFloorSeconds));

        return backoff;
    }

    private void LogTelegramRetry(TelegramBotApiException exception, TimeSpan delay)
    {
        if (exception.ErrorCode == 429)
        {
            _logger.LogWarning(
                "Telegram Bot API rate limit reached; retrying in {DelaySeconds} seconds.",
                delay.TotalSeconds);
            return;
        }

        if (exception.ErrorCode == 409)
        {
            _logger.LogWarning(
                "Telegram long polling conflict: another getUpdates consumer may be using this bot token. " +
                "Ensure only one poller is active; retrying in {DelaySeconds} seconds.",
                delay.TotalSeconds);
            return;
        }

        _logger.LogWarning(
            exception,
            "Telegram Bot API returned a temporary error ({ErrorCode}); retrying in {DelaySeconds} seconds.",
            exception.ErrorCode,
            delay.TotalSeconds);
    }

    private ValueTask EnqueueInboundAsync(InboundMessage message, CancellationToken ct)
    {
        var handler = OnMessageReceived;
        return handler is null ? ValueTask.CompletedTask : handler(message, ct);
    }

    private async Task SendMediaAsync(
        TelegramChatId chatId,
        TelegramMediaRequest request,
        string? caption,
        int? replyToMessageId,
        CancellationToken ct)
    {
        var url = $"https://api.telegram.org/bot{_botToken}/{request.MethodName}";
        var payload = new TelegramMediaPayload
        {
            ChatId = chatId,
            Caption = string.IsNullOrWhiteSpace(caption) ? null : caption,
            ReplyToMessageId = replyToMessageId
        };

        request.Apply(payload);

        var response = await _http.PostAsJsonAsync(url, payload, TelegramJsonContext.Default.TelegramMediaPayload, ct);
        response.EnsureSuccessStatusCode();
        _logger.LogInformation("Sent Telegram {MediaType} to {ChatId}", request.MediaType, chatId);
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsHttp)
            _http.Dispose();
        return ValueTask.CompletedTask;
    }

    private int? TryParseReplyToMessageId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var messageId))
            return messageId;

        _logger.LogWarning("Telegram ReplyToMessageId '{ReplyToMessageId}' is not numeric and will be ignored.", value);
        return null;
    }

    private static IEnumerable<string> ChunkText(string text, int limit)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        for (var i = 0; i < text.Length; i += limit)
            yield return text.Substring(i, Math.Min(limit, text.Length - i));
    }
}

internal sealed class TelegramBotApiException : Exception
{
    public TelegramBotApiException(
        int errorCode,
        string description,
        TimeSpan? retryAfter,
        Exception? innerException = null)
        : base($"Telegram Bot API request failed ({errorCode}): {description}", innerException)
    {
        ErrorCode = errorCode;
        RetryAfter = retryAfter;
    }

    public int ErrorCode { get; }
    public TimeSpan? RetryAfter { get; }
}

public sealed class TelegramMessagePayload
{
    [JsonPropertyName("chat_id")]
    public required TelegramChatId ChatId { get; set; }

    [JsonPropertyName("text")]
    public required string Text { get; set; }

    [JsonPropertyName("reply_to_message_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ReplyToMessageId { get; set; }
}

[JsonSerializable(typeof(TelegramMessagePayload))]
[JsonSerializable(typeof(TelegramMediaPayload))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class TelegramJsonContext : JsonSerializerContext;

public sealed class TelegramMediaPayload
{
    [JsonPropertyName("chat_id")]
    public required TelegramChatId ChatId { get; set; }

    [JsonPropertyName("photo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Photo { get; set; }

    [JsonPropertyName("video")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Video { get; set; }

    [JsonPropertyName("audio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Audio { get; set; }

    [JsonPropertyName("document")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Document { get; set; }

    [JsonPropertyName("sticker")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sticker { get; set; }

    [JsonPropertyName("caption")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Caption { get; set; }

    [JsonPropertyName("reply_to_message_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ReplyToMessageId { get; set; }
}

[JsonConverter(typeof(TelegramChatIdJsonConverter))]
public readonly record struct TelegramChatId(string Value)
{
    private static readonly Regex PublicUsernamePattern = new(
        "^@[A-Za-z][A-Za-z0-9_]{4,31}$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public static bool TryCreate(string? value, out TelegramChatId chatId)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            chatId = default;
            return false;
        }

        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) &&
            !PublicUsernamePattern.IsMatch(value))
        {
            chatId = default;
            return false;
        }

        chatId = new TelegramChatId(value);
        return true;
    }

    public override string ToString() => Value;
}

public sealed class TelegramChatIdJsonConverter : JsonConverter<TelegramChatId>
{
    public override TelegramChatId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number => new TelegramChatId(reader.GetInt64().ToString(CultureInfo.InvariantCulture)),
            JsonTokenType.String => new TelegramChatId(reader.GetString() ?? ""),
            _ => throw new JsonException("Telegram chat_id must be a number or string.")
        };
    }

    public override void Write(Utf8JsonWriter writer, TelegramChatId value, JsonSerializerOptions options)
    {
        if (long.TryParse(value.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericValue))
            writer.WriteNumberValue(numericValue);
        else
            writer.WriteStringValue(value.Value);
    }
}

internal sealed record TelegramMediaRequest(
    string MethodName,
    string MediaType,
    string Value,
    bool SupportsCaption)
{
    public static bool TryCreate(MediaMarker marker, out TelegramMediaRequest? request)
    {
        request = marker.Kind switch
        {
            MediaMarkerKind.ImageUrl or MediaMarkerKind.TelegramImageFileId => new("sendPhoto", "photo", marker.Value, SupportsCaption: true),
            MediaMarkerKind.VideoUrl or MediaMarkerKind.TelegramVideoFileId => new("sendVideo", "video", marker.Value, SupportsCaption: true),
            MediaMarkerKind.AudioUrl or MediaMarkerKind.TelegramAudioFileId => new("sendAudio", "audio", marker.Value, SupportsCaption: true),
            MediaMarkerKind.DocumentUrl or MediaMarkerKind.FileUrl or MediaMarkerKind.TelegramDocumentFileId => new("sendDocument", "document", marker.Value, SupportsCaption: true),
            MediaMarkerKind.StickerUrl or MediaMarkerKind.TelegramStickerFileId => new("sendSticker", "sticker", marker.Value, SupportsCaption: false),
            _ => null
        };

        return request is not null;
    }

    public void Apply(TelegramMediaPayload payload)
    {
        switch (MediaType)
        {
            case "photo":
                payload.Photo = Value;
                break;
            case "video":
                payload.Video = Value;
                break;
            case "audio":
                payload.Audio = Value;
                break;
            case "document":
                payload.Document = Value;
                break;
            case "sticker":
                payload.Sticker = Value;
                break;
        }
    }
}

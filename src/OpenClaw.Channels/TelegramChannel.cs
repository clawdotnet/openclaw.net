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

/// <summary>
/// A channel adapter for the Telegram Bot API using raw HTTP webhooks.
/// Inbound traffic is handled by Program.cs (POST /telegram/inbound) which calls this adapter.
/// </summary>
public sealed class TelegramChannel : IChannelAdapter
{
    private const int MaxMessageChars = 4096;
    private const int MaxCaptionChars = 1024;

    private readonly TelegramChannelConfig _config;
    private readonly HttpClient _http;
    private readonly ILogger<TelegramChannel> _logger;
    private readonly string _botToken;
    private readonly bool _ownsHttp;

    public TelegramChannel(TelegramChannelConfig config, ILogger<TelegramChannel> logger)
        : this(config, logger, http: null)
    {
    }

    public TelegramChannel(TelegramChannelConfig config, ILogger<TelegramChannel> logger, HttpClient? http)
    {
        _config = config;
        _logger = logger;
        _http = http ?? HttpClientFactory.Create();
        _ownsHttp = http is null;
        
        var tokenSource = SecretResolver.Resolve(config.BotTokenRef) ?? config.BotToken;

        _botToken = tokenSource ?? throw new InvalidOperationException("Telegram bot token not configured or missing from environment.");
    }

    public string ChannelType => "telegram";
    public string ChannelId => "telegram";
#pragma warning disable CS0067 // Event is never used
    public event Func<InboundMessage, CancellationToken, ValueTask>? OnMessageReceived;
#pragma warning restore CS0067

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

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

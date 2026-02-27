using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Http;
using OpenClaw.Core.Models;

namespace OpenClaw.Channels;

/// <summary>
/// A channel adapter for the Telegram Bot API using raw HTTP webhooks.
/// Inbound traffic is handled by Program.cs (POST /telegram/inbound) which calls this adapter.
/// </summary>
public sealed class TelegramChannel : IChannelAdapter
{
    private readonly TelegramChannelConfig _config;
    private readonly HttpClient _http;
    private readonly ILogger<TelegramChannel> _logger;
    private readonly string _botToken;

    public TelegramChannel(TelegramChannelConfig config, ILogger<TelegramChannel> logger)
    {
        _config = config;
        _logger = logger;
        _http = HttpClientFactory.Create();
        
        var tokenSource = config.BotTokenRef.StartsWith("env:") 
            ? Environment.GetEnvironmentVariable(config.BotTokenRef[4..]) 
            : config.BotToken;

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

        // Try parsing to ensure we send back to a numeric Chat ID
        if (!long.TryParse(outbound.RecipientId, out var chatId))
        {
            _logger.LogWarning("Telegram SendAsync aborted: RecipientId '{RecipientId}' is not a numeric Telegram Chat ID.", outbound.RecipientId);
            return;
        }

        try
        {
            var (markers, remaining) = MediaMarkerProtocol.Extract(outbound.Text);
            var images = markers.Where(m => m.Kind is MediaMarkerKind.ImageUrl or MediaMarkerKind.TelegramImageFileId).ToList();

            if (images.Count == 0)
            {
                await SendMessageAsync(chatId, outbound.Text, ct);
                return;
            }

            const int MaxCaptionChars = 1024;
            var caption = string.IsNullOrWhiteSpace(remaining) ? null : remaining;
            var captionForPhoto = caption is not null && caption.Length > MaxCaptionChars
                ? caption[..MaxCaptionChars] + "…"
                : caption;

            for (var i = 0; i < images.Count; i++)
            {
                var marker = images[i];
                var photo = marker.Value;
                var cap = i == 0 ? captionForPhoto : null;
                await SendPhotoAsync(chatId, photo, cap, ct);
            }

            // If caption was truncated, send remainder as a follow-up message.
            if (caption is not null && caption.Length > MaxCaptionChars)
            {
                var rest = caption[MaxCaptionChars..].Trim();
                if (!string.IsNullOrWhiteSpace(rest))
                    await SendMessageAsync(chatId, rest, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Telegram message to {ChatId}", chatId);
        }
    }

    private async Task SendMessageAsync(long chatId, string text, CancellationToken ct)
    {
        var url = $"https://api.telegram.org/bot{_botToken}/sendMessage";
        var payload = new TelegramMessagePayload { ChatId = chatId, Text = text };
        var response = await _http.PostAsJsonAsync(url, payload, TelegramJsonContext.Default.TelegramMessagePayload, ct);
        response.EnsureSuccessStatusCode();
        _logger.LogInformation("Sent Telegram message to {ChatId}", chatId);
    }

    private async Task SendPhotoAsync(long chatId, string photo, string? caption, CancellationToken ct)
    {
        var url = $"https://api.telegram.org/bot{_botToken}/sendPhoto";
        var payload = new TelegramPhotoPayload
        {
            ChatId = chatId,
            Photo = photo,
            Caption = string.IsNullOrWhiteSpace(caption) ? null : caption
        };
        var response = await _http.PostAsJsonAsync(url, payload, TelegramJsonContext.Default.TelegramPhotoPayload, ct);
        response.EnsureSuccessStatusCode();
        _logger.LogInformation("Sent Telegram photo to {ChatId}", chatId);
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class TelegramMessagePayload
{
    [JsonPropertyName("chat_id")]
    public required long ChatId { get; set; }

    [JsonPropertyName("text")]
    public required string Text { get; set; }
}

[JsonSerializable(typeof(TelegramMessagePayload))]
[JsonSerializable(typeof(TelegramPhotoPayload))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class TelegramJsonContext : JsonSerializerContext;

public sealed class TelegramPhotoPayload
{
    [JsonPropertyName("chat_id")]
    public required long ChatId { get; set; }

    [JsonPropertyName("photo")]
    public required string Photo { get; set; }

    [JsonPropertyName("caption")]
    public string? Caption { get; set; }
}

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Channels;

/// <summary>
/// A channel adapter for the official WhatsApp Business Cloud API (Meta).
/// </summary>
public sealed class WhatsAppChannel : IChannelAdapter
{
    private readonly WhatsAppChannelConfig _config;
    private readonly HttpClient _http;
    private readonly ILogger<WhatsAppChannel> _logger;
    private readonly string _apiToken;

    public WhatsAppChannel(WhatsAppChannelConfig config, HttpClient httpClient, ILogger<WhatsAppChannel> logger)
    {
        _config = config;
        _logger = logger;
        _http = httpClient;

        var resolvedToken = SecretResolver.Resolve(config.CloudApiTokenRef) ?? config.CloudApiToken;
        _apiToken = resolvedToken ?? "";
        if (string.IsNullOrWhiteSpace(_apiToken))
            throw new InvalidOperationException("WhatsApp Cloud API token not configured. Set Channels.WhatsApp.CloudApiToken or Channels.WhatsApp.CloudApiTokenRef.");
    }

    public string ChannelType => "whatsapp";
    public string ChannelId => "whatsapp";

    public event Func<InboundMessage, CancellationToken, ValueTask>? OnMessageReceived;

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public ValueTask SendAsync(OutboundMessage outbound, CancellationToken ct)
    {
        var delivery = new ChannelDeliveryAttempt();
        return delivery.ExecuteAsync(() => SendCoreAsync(outbound, delivery, ct), ct);
    }

    private async Task SendCoreAsync(OutboundMessage outbound, ChannelDeliveryAttempt delivery, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.PhoneNumberId))
        {
            _logger.LogWarning("WhatsApp SendAsync aborted: PhoneNumberId is not configured.");
            throw new ChannelDeliveryException("WhatsApp destination configuration or content is missing.", false);
        }

        var (markers, remainingText) = MediaMarkerProtocol.Extract(outbound.Text);
        if (markers.Count == 0 && string.IsNullOrWhiteSpace(remainingText))
            throw new ChannelDeliveryException("WhatsApp destination configuration or content is missing.", false);

        var url = $"https://graph.facebook.com/v21.0/{_config.PhoneNumberId}/messages";
        var payload = BuildPayload(outbound, markers, remainingText);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiToken);
        request.Content = JsonContent.Create(payload, WhatsAppJsonContext.Default.WhatsAppSendPayload);

        using var response = await _http.SendAsync(request, ct);
        ChannelDeliveryHttp.EnsureAccepted(response);
        delivery.RecordAccepted();
        _logger.LogInformation("Sent WhatsApp message to {RecipientId}", outbound.RecipientId);

    }

    public async ValueTask RaiseInboundAsync(InboundMessage message, CancellationToken ct)
    {
        var handler = OnMessageReceived;
        if (handler is not null)
            await handler(message, ct);
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    private WhatsAppSendPayload BuildPayload(OutboundMessage outbound, IReadOnlyList<MediaMarker> markers, string remainingText)
    {
        var payload = new WhatsAppSendPayload
        {
            To = outbound.RecipientId,
            Context = string.IsNullOrWhiteSpace(outbound.ReplyToMessageId)
                ? null
                : new WhatsAppContextObj { MessageId = outbound.ReplyToMessageId }
        };

        if (markers.Count == 0)
        {
            payload.Type = "text";
            payload.Text = new WhatsAppTextObj { Body = remainingText };
            return payload;
        }

        if (markers.Count > 1)
            throw new ChannelDeliveryException("WhatsApp Cloud API requires one attachment per send.", false);

        var marker = markers[0];
        payload.Type = MarkerKindToMessageType(marker.Kind);
        if (!SupportsCaption(payload.Type) && !string.IsNullOrWhiteSpace(remainingText))
            throw new ChannelDeliveryException("This WhatsApp media type does not support accompanying text.", false);
        var media = new WhatsAppMediaObj
        {
            Link = MarkerKindToLink(marker),
            Caption = SupportsCaption(payload.Type) && !string.IsNullOrWhiteSpace(remainingText) ? remainingText : null,
            Filename = payload.Type is "document" && marker.Kind is MediaMarkerKind.FileUrl or MediaMarkerKind.DocumentUrl
                ? GetFileName(marker.Value)
                : null
        };

        switch (payload.Type)
        {
            case "image":
                payload.Image = media;
                break;
            case "video":
                payload.Video = media;
                break;
            case "audio":
                payload.Audio = media;
                break;
            case "document":
                payload.Document = media;
                break;
            case "sticker":
                payload.Sticker = media;
                break;
            default:
                throw new ChannelDeliveryException($"Unsupported WhatsApp media type '{payload.Type}'.", false);
        }

        return payload;
    }

    private static string MarkerKindToMessageType(MediaMarkerKind kind)
        => kind switch
        {
            MediaMarkerKind.ImageUrl => "image",
            MediaMarkerKind.VideoUrl => "video",
            MediaMarkerKind.AudioUrl => "audio",
            MediaMarkerKind.DocumentUrl or MediaMarkerKind.FileUrl => "document",
            MediaMarkerKind.StickerUrl => "sticker",
            _ => throw new ChannelDeliveryException($"WhatsApp Cloud API does not support marker kind '{kind}'.", false)
        };

    private static string MarkerKindToLink(MediaMarker marker)
    {
        if (Uri.TryCreate(marker.Value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return marker.Value;

        throw new ChannelDeliveryException("WhatsApp Cloud API media requires an absolute http(s) URL.", false);
    }

    private static bool SupportsCaption(string messageType)
        => messageType is "image" or "video" or "document";

    private static string? GetFileName(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return null;

        var fileName = Path.GetFileName(uri.LocalPath);
        return string.IsNullOrWhiteSpace(fileName) ? null : fileName;
    }
}

public sealed class WhatsAppSendPayload
{
    [JsonPropertyName("messaging_product")]
    public string MessagingProduct { get; set; } = "whatsapp";

    [JsonPropertyName("recipient_type")]
    public string RecipientType { get; set; } = "individual";

    [JsonPropertyName("context")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WhatsAppContextObj? Context { get; set; }

    [JsonPropertyName("to")]
    public required string To { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WhatsAppTextObj? Text { get; set; }

    [JsonPropertyName("image")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WhatsAppMediaObj? Image { get; set; }

    [JsonPropertyName("video")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WhatsAppMediaObj? Video { get; set; }

    [JsonPropertyName("audio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WhatsAppMediaObj? Audio { get; set; }

    [JsonPropertyName("document")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WhatsAppMediaObj? Document { get; set; }

    [JsonPropertyName("sticker")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WhatsAppMediaObj? Sticker { get; set; }
}

public sealed class WhatsAppContextObj
{
    [JsonPropertyName("message_id")]
    public required string MessageId { get; set; }
}

public sealed class WhatsAppTextObj
{
    [JsonPropertyName("body")]
    public required string Body { get; set; }

    [JsonPropertyName("preview_url")]
    public bool PreviewUrl { get; set; } = false;
}

public sealed class WhatsAppMediaObj
{
    [JsonPropertyName("link")]
    public required string Link { get; set; }

    [JsonPropertyName("caption")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Caption { get; set; }

    [JsonPropertyName("filename")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Filename { get; set; }
}

[JsonSerializable(typeof(WhatsAppSendPayload))]
[JsonSerializable(typeof(WhatsAppInboundPayload))]
public partial class WhatsAppJsonContext : JsonSerializerContext;

// Models for Inbound Webhook Parsing
public sealed class WhatsAppInboundPayload
{
    [JsonPropertyName("object")]
    public string? Object { get; set; }

    [JsonPropertyName("entry")]
    public WhatsAppEntry[]? Entry { get; set; }
}

public sealed class WhatsAppEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("changes")]
    public WhatsAppChange[]? Changes { get; set; }
}

public sealed class WhatsAppChange
{
    [JsonPropertyName("value")]
    public WhatsAppValue? Value { get; set; }

    [JsonPropertyName("field")]
    public string? Field { get; set; }
}

public sealed class WhatsAppValue
{
    [JsonPropertyName("messaging_product")]
    public string? MessagingProduct { get; set; }

    [JsonPropertyName("contacts")]
    public WhatsAppContact[]? Contacts { get; set; }

    [JsonPropertyName("messages")]
    public WhatsAppMessage[]? Messages { get; set; }
}

public sealed class WhatsAppContact
{
    [JsonPropertyName("profile")]
    public WhatsAppProfile? Profile { get; set; }

    [JsonPropertyName("wa_id")]
    public string? WaId { get; set; }
}

public sealed class WhatsAppProfile
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public sealed class WhatsAppMessage
{
    [JsonPropertyName("from")]
    public string? From { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("text")]
    public WhatsAppTextObj? Text { get; set; }
}

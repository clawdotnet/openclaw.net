using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Channels;

/// <summary>
/// A channel adapter for a WhatsApp bridge (e.g. whatsmeow proxy).
/// Expects a simple HTTP POST protocol for sending and receives webhooks in a simple format.
/// </summary>
public sealed class WhatsAppBridgeChannel : IChannelAdapter
{
    private readonly WhatsAppChannelConfig _config;
    private readonly HttpClient _http;
    private readonly ILogger<WhatsAppBridgeChannel> _logger;
    private readonly string _bridgeToken;

    public WhatsAppBridgeChannel(WhatsAppChannelConfig config, HttpClient httpClient, ILogger<WhatsAppBridgeChannel> logger)
    {
        _config = config;
        _logger = logger;
        _http = httpClient;

        var resolvedToken = SecretResolver.Resolve(config.BridgeTokenRef) ?? config.BridgeToken;
        _bridgeToken = resolvedToken ?? "";
    }

    public string ChannelType => "whatsapp-bridge";
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
        if (string.IsNullOrWhiteSpace(_config.BridgeUrl))
        {
            _logger.LogWarning("WhatsApp Bridge SendAsync aborted: BridgeUrl is not configured.");
            throw new ChannelDeliveryException("WhatsApp destination configuration or content is missing.", false);
        }

        var (markers, remainingText) = MediaMarkerProtocol.Extract(outbound.Text);
        if (markers.Count == 0 && string.IsNullOrWhiteSpace(remainingText))
            throw new ChannelDeliveryException("WhatsApp destination configuration or content is missing.", false);

        WhatsAppBridgeAttachmentPayload[]? attachments = null;
        if (markers.Count > 0)
        {
            attachments = new WhatsAppBridgeAttachmentPayload[markers.Count];
            for (var i = 0; i < markers.Count; i++)
            {
                attachments[i] = new WhatsAppBridgeAttachmentPayload
                {
                    Type = MarkerKindToMediaType(markers[i].Kind),
                    Url = markers[i].Value
                };
            }
        }

        var payload = new WhatsAppBridgeSendPayload
        {
            To = outbound.RecipientId,
            Text = remainingText,
            ReplyToMessageId = outbound.ReplyToMessageId,
            Attachments = attachments
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _config.BridgeUrl);
        if (!string.IsNullOrEmpty(_bridgeToken))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _bridgeToken);
        }
        request.Content = JsonContent.Create(payload, WhatsAppBridgeJsonContext.Default.WhatsAppBridgeSendPayload);

        using var response = await _http.SendAsync(request, ct);
        ChannelDeliveryHttp.EnsureAccepted(response);
        delivery.RecordAccepted();
        _logger.LogInformation("Sent WhatsApp Bridge message to {RecipientId}", outbound.RecipientId);

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

    private static string MarkerKindToMediaType(MediaMarkerKind kind)
        => kind switch
        {
            MediaMarkerKind.ImageUrl or MediaMarkerKind.ImagePath => "image",
            MediaMarkerKind.VideoUrl => "video",
            MediaMarkerKind.AudioUrl => "audio",
            MediaMarkerKind.DocumentUrl or MediaMarkerKind.FileUrl or MediaMarkerKind.FilePath => "document",
            MediaMarkerKind.StickerUrl => "sticker",
            _ => throw new ChannelDeliveryException($"WhatsApp bridge does not support marker kind '{kind}'.", false)
        };
}

public sealed class WhatsAppBridgeSendPayload
{
    [JsonPropertyName("to")]
    public required string To { get; set; }

    [JsonPropertyName("text")]
    public required string Text { get; set; }

    [JsonPropertyName("reply_to_message_id")]
    public string? ReplyToMessageId { get; set; }

    [JsonPropertyName("attachments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WhatsAppBridgeAttachmentPayload[]? Attachments { get; set; }
}

public sealed class WhatsAppBridgeAttachmentPayload
{
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }

    [JsonPropertyName("caption")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Caption { get; set; }

    [JsonPropertyName("mimeType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; set; }

    [JsonPropertyName("fileName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FileName { get; set; }

    [JsonPropertyName("gifPlayback")]
    public bool GifPlayback { get; set; }
}

public sealed class WhatsAppBridgeInboundPayload
{
    [JsonPropertyName("from")]
    public required string From { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("account_id")]
    public string? AccountId { get; set; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }

    [JsonPropertyName("sender_name")]
    public string? SenderName { get; set; }

    [JsonPropertyName("message_id")]
    public string? MessageId { get; set; }

    [JsonPropertyName("reply_to_message_id")]
    public string? ReplyToMessageId { get; set; }

    [JsonPropertyName("is_group")]
    public bool IsGroup { get; set; }

    [JsonPropertyName("group_id")]
    public string? GroupId { get; set; }

    [JsonPropertyName("group_name")]
    public string? GroupName { get; set; }

    [JsonPropertyName("mentioned_ids")]
    public string[]? MentionedIds { get; set; }

    [JsonPropertyName("media_type")]
    public string? MediaType { get; set; }

    [JsonPropertyName("media_url")]
    public string? MediaUrl { get; set; }

    [JsonPropertyName("media_mime_type")]
    public string? MediaMimeType { get; set; }

    [JsonPropertyName("media_file_name")]
    public string? MediaFileName { get; set; }

    [JsonPropertyName("attachments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WhatsAppBridgeAttachmentPayload[]? Attachments { get; set; }
}

[JsonSerializable(typeof(WhatsAppBridgeSendPayload))]
[JsonSerializable(typeof(WhatsAppBridgeInboundPayload))]
[JsonSerializable(typeof(WhatsAppBridgeAttachmentPayload))]
[JsonSerializable(typeof(WhatsAppBridgeAttachmentPayload[]))]
public partial class WhatsAppBridgeJsonContext : JsonSerializerContext;

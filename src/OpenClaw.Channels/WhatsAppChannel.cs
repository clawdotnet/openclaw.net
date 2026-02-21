using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

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

        var tokenSource = config.CloudApiTokenRef.StartsWith("env:")
            ? Environment.GetEnvironmentVariable(config.CloudApiTokenRef[4..])
            : config.CloudApiToken;

        _apiToken = tokenSource ?? "";
    }

    public string ChannelType => "whatsapp";
    public string ChannelId => "whatsapp";

    public event Func<InboundMessage, CancellationToken, ValueTask>? OnMessageReceived;

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public async ValueTask SendAsync(OutboundMessage outbound, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(outbound.Text)) return;
        if (string.IsNullOrWhiteSpace(_config.PhoneNumberId))
        {
            _logger.LogWarning("WhatsApp SendAsync aborted: PhoneNumberId is not configured.");
            return;
        }

        var url = $"https://graph.facebook.com/v21.0/{_config.PhoneNumberId}/messages";
        var payload = new WhatsAppSendPayload
        {
            To = outbound.RecipientId,
            Text = new WhatsAppTextObj { Body = outbound.Text }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiToken);
        request.Content = JsonContent.Create(payload, WhatsAppJsonContext.Default.WhatsAppSendPayload);

        try
        {
            var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            _logger.LogInformation("Sent WhatsApp message to {RecipientId}", outbound.RecipientId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send WhatsApp message to {RecipientId}", outbound.RecipientId);
        }
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
}

public sealed class WhatsAppSendPayload
{
    [JsonPropertyName("messaging_product")]
    public string MessagingProduct { get; set; } = "whatsapp";

    [JsonPropertyName("recipient_type")]
    public string RecipientType { get; set; } = "individual";

    [JsonPropertyName("to")]
    public required string To { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    public required WhatsAppTextObj Text { get; set; }
}

public sealed class WhatsAppTextObj
{
    [JsonPropertyName("body")]
    public required string Body { get; set; }

    [JsonPropertyName("preview_url")]
    public bool PreviewUrl { get; set; } = false;
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

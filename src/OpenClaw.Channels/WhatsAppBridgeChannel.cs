using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

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

        var tokenSource = config.BridgeTokenRef.StartsWith("env:")
            ? Environment.GetEnvironmentVariable(config.BridgeTokenRef[4..])
            : config.BridgeToken;

        _bridgeToken = tokenSource ?? "";
    }

    public string ChannelType => "whatsapp-bridge";
    public string ChannelId => "whatsapp";

    public event Func<InboundMessage, CancellationToken, ValueTask>? OnMessageReceived;

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public async ValueTask SendAsync(OutboundMessage outbound, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(outbound.Text)) return;
        if (string.IsNullOrWhiteSpace(_config.BridgeUrl))
        {
            _logger.LogWarning("WhatsApp Bridge SendAsync aborted: BridgeUrl is not configured.");
            return;
        }

        var payload = new WhatsAppBridgeSendPayload
        {
            To = outbound.RecipientId,
            Text = outbound.Text
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _config.BridgeUrl);
        if (!string.IsNullOrEmpty(_bridgeToken))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _bridgeToken);
        }
        request.Content = JsonContent.Create(payload, WhatsAppBridgeJsonContext.Default.WhatsAppBridgeSendPayload);

        try
        {
            var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            _logger.LogInformation("Sent WhatsApp Bridge message to {RecipientId}", outbound.RecipientId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send WhatsApp Bridge message to {RecipientId}", outbound.RecipientId);
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

public sealed class WhatsAppBridgeSendPayload
{
    [JsonPropertyName("to")]
    public required string To { get; set; }

    [JsonPropertyName("text")]
    public required string Text { get; set; }
}

public sealed class WhatsAppBridgeInboundPayload
{
    [JsonPropertyName("from")]
    public required string From { get; set; }

    [JsonPropertyName("text")]
    public required string Text { get; set; }

    [JsonPropertyName("sender_name")]
    public string? SenderName { get; set; }
}

[JsonSerializable(typeof(WhatsAppBridgeSendPayload))]
[JsonSerializable(typeof(WhatsAppBridgeInboundPayload))]
public partial class WhatsAppBridgeJsonContext : JsonSerializerContext;

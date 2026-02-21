using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
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

    public TelegramChannel(TelegramChannelConfig config, IHttpClientFactory httpClientFactory, ILogger<TelegramChannel> logger)
    {
        _config = config;
        _logger = logger;
        _http = httpClientFactory.CreateClient("TelegramChannel");
        
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

        var url = $"https://api.telegram.org/bot{_botToken}/sendMessage";
        var payload = new TelegramPayload
        {
            ChatId = chatId,
            Text = outbound.Text
        };

        try
        {
            var response = await _http.PostAsJsonAsync(url, payload, TelegramJsonContext.Default.TelegramPayload, ct);
            response.EnsureSuccessStatusCode();
            _logger.LogInformation("Sent Telegram message to {ChatId}", chatId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Telegram message to {ChatId}", chatId);
        }
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class TelegramPayload
{
    [JsonPropertyName("chat_id")]
    public required long ChatId { get; set; }

    [JsonPropertyName("text")]
    public required string Text { get; set; }
}

[JsonSerializable(typeof(TelegramPayload))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class TelegramJsonContext : JsonSerializerContext;

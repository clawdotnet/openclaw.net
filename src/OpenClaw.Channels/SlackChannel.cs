using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
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
/// A channel adapter for the Slack Web API.
/// Inbound traffic arrives via Slack Events API webhooks handled in the gateway.
/// </summary>
public sealed partial class SlackChannel : IChannelAdapter
{
    private readonly SlackChannelConfig _config;
    private readonly HttpClient _http;
    private readonly ILogger<SlackChannel> _logger;
    private readonly string _botToken;

    public SlackChannel(SlackChannelConfig config, ILogger<SlackChannel> logger)
    {
        _config = config;
        _logger = logger;
        _http = HttpClientFactory.Create();

        var tokenSource = SecretResolver.Resolve(config.BotTokenRef) ?? config.BotToken;
        _botToken = tokenSource ?? throw new InvalidOperationException("Slack bot token not configured or missing from environment.");
    }

    public string ChannelType => "slack";
    public string ChannelId => "slack";
#pragma warning disable CS0067 // Event is never used
    public event Func<InboundMessage, CancellationToken, ValueTask>? OnMessageReceived;
#pragma warning restore CS0067

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public async ValueTask SendAsync(OutboundMessage outbound, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(outbound.Text)) return;

        try
        {
            var (markers, remaining) = MediaMarkerProtocol.Extract(outbound.Text);
            var text = ConvertToMrkdwn(string.IsNullOrWhiteSpace(remaining) ? outbound.Text : remaining);

            var payload = new SlackPostMessageRequest
            {
                Channel = outbound.RecipientId,
                Text = text,
                ThreadTs = outbound.ReplyToMessageId
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/chat.postMessage");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _botToken);
            request.Content = JsonContent.Create(payload, SlackJsonContext.Default.SlackPostMessageRequest);

            using var response = await _http.SendAsync(request, ct);

            if ((int)response.StatusCode == 429)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
                _logger.LogWarning("Slack rate limited for {Channel}. Retry-After: {RetryAfter}s.", outbound.RecipientId, retryAfter.TotalSeconds);
                return;
            }

            response.EnsureSuccessStatusCode();

            // Slack returns 200 with ok=false for application-level errors
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.False)
            {
                var error = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : "unknown";
                _logger.LogError("Slack API error sending to {Channel}: {Error}", outbound.RecipientId, error);
                return;
            }

            _logger.LogInformation("Sent Slack message to {Channel}", outbound.RecipientId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Slack message to {Channel}", outbound.RecipientId);
        }
    }

    /// <summary>
    /// Converts basic Markdown to Slack mrkdwn format.
    /// </summary>
    internal static string ConvertToMrkdwn(string markdown)
    {
        // Convert **bold** to *bold*
        var result = BoldRegex().Replace(markdown, "*$1*");
        // Convert [text](url) to <url|text>
        result = LinkRegex().Replace(result, "<$2|$1>");
        return result;
    }

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)]+)\)")]
    private static partial Regex LinkRegex();

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class SlackPostMessageRequest
{
    [JsonPropertyName("channel")]
    public required string Channel { get; set; }

    [JsonPropertyName("text")]
    public required string Text { get; set; }

    [JsonPropertyName("thread_ts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThreadTs { get; set; }
}

public sealed class SlackEventWrapper
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("challenge")]
    public string? Challenge { get; set; }

    [JsonPropertyName("team_id")]
    public string? TeamId { get; set; }

    [JsonPropertyName("event")]
    public SlackEvent? Event { get; set; }

    [JsonPropertyName("event_id")]
    public string? EventId { get; set; }
}

public sealed class SlackEvent
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("subtype")]
    public string? Subtype { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }

    [JsonPropertyName("bot_id")]
    public string? BotId { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("channel")]
    public string? Channel { get; set; }

    [JsonPropertyName("channel_type")]
    public string? ChannelType { get; set; }

    [JsonPropertyName("ts")]
    public string? Ts { get; set; }

    [JsonPropertyName("thread_ts")]
    public string? ThreadTs { get; set; }
}

[JsonSerializable(typeof(SlackPostMessageRequest))]
[JsonSerializable(typeof(SlackEventWrapper))]
[JsonSerializable(typeof(SlackEvent))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class SlackJsonContext : JsonSerializerContext;

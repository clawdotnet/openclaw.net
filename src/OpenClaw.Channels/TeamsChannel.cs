using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
/// Channel adapter for Microsoft Teams via the Azure Bot Framework REST API.
/// Inbound messages arrive via webhook (POST /api/messages).
/// Outbound messages are sent via the Bot Connector REST API.
/// </summary>
public sealed class TeamsChannel : IChannelAdapter
{
    private readonly TeamsChannelConfig _config;
    private readonly HttpClient _http;
    private readonly ILogger<TeamsChannel> _logger;
    private readonly string _appId;
    private readonly string _appPassword;
    private readonly string _tenantId;

    private string? _cachedToken;
    private DateTimeOffset _tokenExpiry;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);

    /// <summary>
    /// Stores conversation references keyed by recipientId (AAD object ID or conversation ID).
    /// Populated when inbound messages arrive via <see cref="StoreConversationReference"/>.
    /// </summary>
    private readonly ConcurrentDictionary<string, TeamsConversationReference> ConversationReferences = new(StringComparer.Ordinal);

    public TeamsChannel(TeamsChannelConfig config, HttpClient httpClient, ILogger<TeamsChannel> logger)
    {
        _config = config;
        _http = httpClient;
        _logger = logger;

        _appId = SecretResolver.Resolve(config.AppIdRef) ?? config.AppId
            ?? throw new InvalidOperationException("Teams App ID not configured. Set Channels.Teams.AppId or env:TEAMS_APP_ID.");
        _appPassword = SecretResolver.Resolve(config.AppPasswordRef) ?? config.AppPassword
            ?? throw new InvalidOperationException("Teams App Password not configured. Set Channels.Teams.AppPassword or env:TEAMS_APP_PASSWORD.");
        _tenantId = SecretResolver.Resolve(config.TenantIdRef) ?? config.TenantId
            ?? throw new InvalidOperationException("Teams Tenant ID not configured. Set Channels.Teams.TenantId or env:TEAMS_TENANT_ID.");
    }

    public string ChannelType => "teams";
    public string ChannelId => "teams";

#pragma warning disable CS0067
    public event Func<InboundMessage, CancellationToken, ValueTask>? OnMessageReceived;
#pragma warning restore CS0067

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public async ValueTask SendAsync(OutboundMessage outbound, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(outbound.Text)) return;

        var reference = ConversationReferences.GetValueOrDefault(outbound.RecipientId);
        if (reference is null)
        {
            _logger.LogWarning("Teams SendAsync: no conversation reference for '{RecipientId}'. Bot must receive a message first.", outbound.RecipientId);
            return;
        }

        var chunks = ChunkText(outbound.Text, _config.TextChunkLimit, _config.ChunkMode);
        var token = await GetTokenAsync(ct);

        foreach (var chunk in chunks)
        {
            var activity = new TeamsOutboundActivity
            {
                Type = "message",
                Text = chunk,
                From = new TeamsAccount { Id = _appId },
                Conversation = new TeamsConversationAccount { Id = reference.ConversationId },
                Recipient = new TeamsAccount { Id = reference.UserId ?? outbound.RecipientId }
            };

            // Thread reply support
            if (_config.ReplyStyle == "thread" && outbound.ReplyToMessageId is not null)
            {
                activity.ReplyToId = outbound.ReplyToMessageId;
            }

            var url = $"{reference.ServiceUrl.TrimEnd('/')}/v3/conversations/{reference.ConversationId}/activities";

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = JsonContent.Create(activity, TeamsJsonContext.Default.TeamsOutboundActivity);

            try
            {
                using var response = await _http.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send Teams message to conversation '{ConversationId}'", reference.ConversationId);
            }
        }
    }

    /// <summary>
    /// Stores a conversation reference from an inbound activity for later proactive messaging.
    /// Called by the webhook handler.
    /// </summary>
    public void StoreConversationReference(string senderId, TeamsConversationReference reference)
    {
        ConversationReferences[senderId] = reference;
        // Also store by conversation ID for group chat routing
        if (!string.Equals(senderId, reference.ConversationId, StringComparison.Ordinal))
            ConversationReferences[reference.ConversationId] = reference;
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
        _tokenGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiry.AddMinutes(-5))
            return _cachedToken;

        await _tokenGate.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiry.AddMinutes(-5))
                return _cachedToken;

            var tokenUrl = $"https://login.microsoftonline.com/{_tenantId}/oauth2/v2.0/token";
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _appId,
                ["client_secret"] = _appPassword,
                ["scope"] = "https://api.botframework.com/.default"
            });

            using var response = await _http.PostAsync(tokenUrl, content, ct);
            response.EnsureSuccessStatusCode();

            var tokenResponse = await response.Content.ReadFromJsonAsync(TeamsJsonContext.Default.TeamsTokenResponse, ct);
            if (tokenResponse is null || string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
                throw new InvalidOperationException("Failed to acquire Teams OAuth token — empty response.");

            _cachedToken = tokenResponse.AccessToken;
            _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn);
            _logger.LogDebug("Acquired Teams OAuth token, expires in {ExpiresIn}s", tokenResponse.ExpiresIn);
            return _cachedToken;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private static List<string> ChunkText(string text, int limit, string mode)
    {
        if (text.Length <= limit)
            return [text];

        var chunks = new List<string>();

        if (mode == "newline")
        {
            var current = new StringBuilder();
            foreach (var line in text.Split('\n'))
            {
                if (current.Length + line.Length + 1 > limit && current.Length > 0)
                {
                    chunks.Add(current.ToString());
                    current.Clear();
                }
                if (current.Length > 0) current.Append('\n');
                current.Append(line);
            }
            if (current.Length > 0) chunks.Add(current.ToString());
        }
        else
        {
            for (var i = 0; i < text.Length; i += limit)
                chunks.Add(text.Substring(i, Math.Min(limit, text.Length - i)));
        }

        return chunks;
    }
}

// ── Models ──────────────────────────────────────────────

public sealed class TeamsConversationReference
{
    public required string ServiceUrl { get; init; }
    public required string ConversationId { get; init; }
    public string? UserId { get; init; }
    public string? TenantId { get; init; }
    public string? ConversationType { get; init; }
}

public sealed class TeamsOutboundActivity
{
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("text")]
    public required string Text { get; set; }

    [JsonPropertyName("from")]
    public TeamsAccount? From { get; set; }

    [JsonPropertyName("conversation")]
    public TeamsConversationAccount? Conversation { get; set; }

    [JsonPropertyName("recipient")]
    public TeamsAccount? Recipient { get; set; }

    [JsonPropertyName("replyToId")]
    public string? ReplyToId { get; set; }
}

public sealed class TeamsAccount
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public sealed class TeamsConversationAccount
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }
}

public sealed class TeamsTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }
}

// ── Inbound Activity Models (for webhook parsing) ──────

public sealed class TeamsInboundActivity
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("from")]
    public TeamsActivityFrom? From { get; set; }

    [JsonPropertyName("conversation")]
    public TeamsActivityConversation? Conversation { get; set; }

    [JsonPropertyName("channelId")]
    public string? ActivityChannelId { get; set; }

    [JsonPropertyName("serviceUrl")]
    public string? ServiceUrl { get; set; }

    [JsonPropertyName("entities")]
    public TeamsActivityEntity[]? Entities { get; set; }

    [JsonPropertyName("channelData")]
    public JsonElement? ChannelData { get; set; }

    [JsonPropertyName("replyToId")]
    public string? ReplyToId { get; set; }
}

public sealed class TeamsActivityFrom
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("aadObjectId")]
    public string? AadObjectId { get; set; }
}

public sealed class TeamsActivityConversation
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("conversationType")]
    public string? ConversationType { get; set; }

    [JsonPropertyName("tenantId")]
    public string? TenantId { get; set; }

    [JsonPropertyName("isGroup")]
    public bool? IsGroup { get; set; }
}

public sealed class TeamsActivityEntity
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("mentioned")]
    public TeamsActivityMentioned? Mentioned { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

public sealed class TeamsActivityMentioned
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

[JsonSerializable(typeof(TeamsOutboundActivity))]
[JsonSerializable(typeof(TeamsTokenResponse))]
[JsonSerializable(typeof(TeamsInboundActivity))]
public partial class TeamsJsonContext : JsonSerializerContext;

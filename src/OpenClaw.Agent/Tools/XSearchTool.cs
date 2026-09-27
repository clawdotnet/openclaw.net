using System.Net.Http;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Http;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Search posts on the X (Twitter) platform using the X API v2.
/// </summary>
public sealed class XSearchTool : ITool, IDisposable
{
    private readonly HttpClient _http;
    private readonly string? _bearerToken;

    public XSearchTool()
    {
        _http = HttpClientFactory.Create();
        _bearerToken = SecretResolver.Resolve("env:X_BEARER_TOKEN");
    }

    public string Name => "x_search";
    public string Description => "Search posts on the X (Twitter) platform. Requires X_BEARER_TOKEN environment variable.";
    public string ParameterSchema => """{"type":"object","properties":{"query":{"type":"string","description":"Search query (supports X search operators)"},"max_results":{"type":"integer","description":"Maximum results to return (10-100, default 10)"}},"required":["query"]}""";

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_bearerToken))
            return "Error: X API bearer token not configured. Set X_BEARER_TOKEN environment variable.";

        using var args = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = args.RootElement;

        var query = GetString(root, "query");
        if (string.IsNullOrWhiteSpace(query))
            return "Error: 'query' is required.";

        var maxResults = 10;
        if (root.TryGetProperty("max_results", out var mr) && mr.ValueKind == JsonValueKind.Number)
            maxResults = Math.Clamp(mr.GetInt32(), 10, 100);

        try
        {
            var url = $"https://api.x.com/2/tweets/search/recent?query={Uri.EscapeDataString(query)}&max_results={maxResults}&tweet.fields=created_at,author_id,text,public_metrics";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _bearerToken);

            using var response = await _http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                return $"Error: X API returned {(int)response.StatusCode}. {errorBody}";
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement;

            if (!data.TryGetProperty("data", out var tweets) || tweets.ValueKind != JsonValueKind.Array)
                return "No results found.";

            var sb = new System.Text.StringBuilder();
            var count = 0;
            foreach (var tweet in tweets.EnumerateArray())
            {
                count++;
                var text = tweet.TryGetProperty("text", out var t) ? t.GetString() : "";
                var authorId = tweet.TryGetProperty("author_id", out var a) ? a.GetString() : "unknown";
                var createdAt = tweet.TryGetProperty("created_at", out var c) ? c.GetString() : "";
                sb.AppendLine($"[{count}] @{authorId} ({createdAt})");
                sb.AppendLine(text);
                sb.AppendLine();
            }

            return sb.Length > 0 ? sb.ToString().TrimEnd() : "No results found.";
        }
        catch (Exception ex)
        {
            return $"Error: X search failed: {ex.Message}";
        }
    }

    public void Dispose() => _http.Dispose();

    private static string? GetString(JsonElement root, string property)
        => root.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}

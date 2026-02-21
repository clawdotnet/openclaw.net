using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Http;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Native replica of the OpenClaw web-search plugin.
/// Supports Tavily, Brave Search, and SearXNG backends.
/// </summary>
public sealed class WebSearchTool : ITool, IDisposable
{
    private readonly WebSearchConfig _config;
    private readonly HttpClient _http;

    public WebSearchTool(WebSearchConfig config, HttpClient? httpClient = null)
    {
        _config = config;
        _http = httpClient ?? HttpClientFactory.Create();
    }

    public string Name => "web_search";
    public string Description =>
        "Search the web for current information. Returns titles, URLs, and snippets.";
    public string ParameterSchema => """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "Search query" },
            "max_results": { "type": "integer", "default": 5, "description": "Maximum results to return" }
          },
          "required": ["query"]
        }
        """;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var args = JsonDocument.Parse(argumentsJson);
        var query = args.RootElement.GetProperty("query").GetString()!;
        var maxResults = args.RootElement.TryGetProperty("max_results", out var mr)
            ? mr.GetInt32()
            : _config.MaxResults;

        maxResults = Math.Clamp(maxResults, 1, 20);

        try
        {
            return _config.Provider.ToLowerInvariant() switch
            {
                "tavily" => await SearchTavilyAsync(query, maxResults, ct),
                "brave" => await SearchBraveAsync(query, maxResults, ct),
                "searxng" => await SearchSearxngAsync(query, maxResults, ct),
                _ => $"Error: Unsupported search provider '{_config.Provider}'. Use tavily, brave, or searxng."
            };
        }
        catch (HttpRequestException ex)
        {
            return $"Error: Search request failed — {ex.Message}";
        }
        catch (TaskCanceledException)
        {
            return "Error: Search request timed out.";
        }
    }

    private async Task<string> SearchTavilyAsync(string query, int maxResults, CancellationToken ct)
    {
        var apiKey = ResolveKey()
            ?? throw new InvalidOperationException("web-search: Tavily API key not configured.");

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.tavily.com/search");
        request.Headers.Add("Authorization", $"Bearer {apiKey}");

        var body = new Dictionary<string, object?>
        {
            ["query"] = query,
            ["max_results"] = maxResults,
            ["include_answer"] = false
        };

        // Build JSON manually for AOT safety
        request.Content = new StringContent(
            BuildSearchJson(body),
            Encoding.UTF8,
            "application/json");

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return FormatResults(doc.RootElement, "results", "title", "url", "content");
    }

    private async Task<string> SearchBraveAsync(string query, int maxResults, CancellationToken ct)
    {
        var apiKey = ResolveKey()
            ?? throw new InvalidOperationException("web-search: Brave API key not configured.");

        var url = $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query)}&count={maxResults}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("X-Subscription-Token", apiKey);

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

        if (doc.RootElement.TryGetProperty("web", out var web)
            && web.TryGetProperty("results", out var results))
        {
            return FormatResultsFromArray(results, "title", "url", "description");
        }

        return "No results found.";
    }

    private async Task<string> SearchSearxngAsync(string query, int maxResults, CancellationToken ct)
    {
        var endpoint = _config.Endpoint?.TrimEnd('/')
            ?? throw new InvalidOperationException("web-search: SearXNG endpoint not configured.");

        var url = $"{endpoint}/search?q={Uri.EscapeDataString(query)}&format=json&pageno=1";

        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return FormatResults(doc.RootElement, "results", "title", "url", "content", maxResults);
    }

    private static string FormatResults(
        JsonElement root,
        string arrayProp,
        string titleProp,
        string urlProp,
        string snippetProp,
        int? limit = null)
    {
        if (!root.TryGetProperty(arrayProp, out var results) || results.GetArrayLength() == 0)
            return "No results found.";

        return FormatResultsFromArray(results, titleProp, urlProp, snippetProp, limit);
    }

    private static string FormatResultsFromArray(
        JsonElement results,
        string titleProp,
        string urlProp,
        string snippetProp,
        int? limit = null)
    {
        var sb = new StringBuilder();
        var count = 0;

        foreach (var item in results.EnumerateArray())
        {
            if (limit.HasValue && count >= limit.Value) break;

            var title = item.TryGetProperty(titleProp, out var t) ? t.GetString() : "(no title)";
            var url = item.TryGetProperty(urlProp, out var u) ? u.GetString() : "";
            var snippet = item.TryGetProperty(snippetProp, out var s) ? s.GetString() : "";

            count++;
            sb.AppendLine($"[{count}] {title}");
            if (!string.IsNullOrEmpty(url))
                sb.AppendLine($"    {url}");
            if (!string.IsNullOrEmpty(snippet))
                sb.AppendLine($"    {snippet}");
            sb.AppendLine();
        }

        return count > 0 ? sb.ToString() : "No results found.";
    }

    private string? ResolveKey() => SecretResolver.Resolve(_config.ApiKey);

    public void Dispose() => _http.Dispose();

    /// <summary>AOT-safe JSON builder for simple search request bodies.</summary>
    private static string BuildSearchJson(Dictionary<string, object?> data)
    {
        using var ms = new System.IO.MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in data)
            {
                writer.WritePropertyName(key);
                switch (value)
                {
                    case null: writer.WriteNullValue(); break;
                    case string s: writer.WriteStringValue(s); break;
                    case int i: writer.WriteNumberValue(i); break;
                    case bool b: writer.WriteBooleanValue(b); break;
                    default: writer.WriteStringValue(value.ToString()); break;
                }
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}

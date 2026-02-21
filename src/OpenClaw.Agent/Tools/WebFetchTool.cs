using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Http;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Native replica of the OpenClaw web-fetch plugin.
/// Fetches a URL and returns the body as plain text (HTML tags stripped).
/// </summary>
public sealed partial class WebFetchTool : ITool, IDisposable
{
    private readonly WebFetchConfig _config;
    private readonly HttpClient _http;

    public WebFetchTool(WebFetchConfig config, HttpClient? httpClient = null)
    {
        _config = config;
        _http = httpClient ?? HttpClientFactory.Create();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(config.UserAgent);
    }

    public string Name => "web_fetch";
    public string Description =>
        "Fetch a web page and return its text content. HTML tags are stripped. " +
        "Use for reading documentation, articles, or API responses.";
    public string ParameterSchema => """
        {
          "type": "object",
          "properties": {
            "url": { "type": "string", "description": "The URL to fetch" },
            "max_length": { "type": "integer", "description": "Maximum characters to return (default: auto)" }
          },
          "required": ["url"]
        }
        """;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var args = JsonDocument.Parse(argumentsJson);
        var url = args.RootElement.GetProperty("url").GetString()!;
        var maxLength = args.RootElement.TryGetProperty("max_length", out var ml)
            ? ml.GetInt32()
            : _config.MaxSizeKb * 1024;

        maxLength = Math.Clamp(maxLength, 100, _config.MaxSizeKb * 1024);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return "Error: Invalid URL. Only http:// and https:// are supported.";
        }

        // SSRF protection: block internal/metadata IPs
        var ssrfError = await CheckSsrfAsync(uri);
        if (ssrfError is not null)
            return ssrfError;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (!response.IsSuccessStatusCode)
                return $"Error: HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";

            // Read the body with size limit using pooled buffer to avoid LOH allocation
            var maxBytes = _config.MaxSizeKb * 1024;
            var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            var buffer = ArrayPool<byte>.Shared.Rent(maxBytes);
            try
            {
                var totalRead = 0;
                int bytesRead;
                while (totalRead < maxBytes &&
                       (bytesRead = await stream.ReadAsync(buffer.AsMemory(totalRead, maxBytes - totalRead), cts.Token)) > 0)
                {
                    totalRead += bytesRead;
                }

                var raw = Encoding.UTF8.GetString(buffer, 0, totalRead);
                var truncated = totalRead == maxBytes;

            // For HTML, strip tags and extract readable text
            string text;
            if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                text = ExtractTextFromHtml(raw);
            }
            else
            {
                text = raw;
            }

            // Apply max_length safely
            if (maxLength > 0 && text.Length > maxLength)
            {
                text = text.Substring(0, maxLength);
                truncated = true;
            }

            var header = $"URL: {url}\nContent-Type: {contentType}\nLength: {text.Length} chars{(truncated ? " (truncated)" : "")}\n\n";
            return header + text;
            } // end of try for ArrayPool buffer
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (HttpRequestException ex)
        {
            return $"Error: Request failed — {ex.Message}";
        }
        catch (TaskCanceledException)
        {
            return "Error: Request timed out.";
        }
    }

    /// <summary>
    /// Extract readable text from HTML. Strips tags, scripts, styles, and excessive whitespace.
    /// Lightweight — no external HTML parser dependency needed.
    /// </summary>
    internal static string ExtractTextFromHtml(string html)
    {
        // Remove script/style blocks entirely
        var cleaned = ScriptStyleRegex().Replace(html, " ");

        // Remove all HTML tags
        cleaned = TagRegex().Replace(cleaned, " ");

        // Decode basic HTML entities
        cleaned = cleaned
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&#39;", "'")
            .Replace("&nbsp;", " ");

        // Collapse whitespace
        cleaned = WhitespaceRegex().Replace(cleaned, " ").Trim();

        // Collapse multiple newlines
        cleaned = MultiNewlineRegex().Replace(cleaned, "\n\n");

        return cleaned;
    }

    /// <summary>
    /// SSRF protection: resolve the hostname and block internal/metadata IPs.
    /// </summary>
    private static async Task<string?> CheckSsrfAsync(Uri uri)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.Host);
            foreach (var ip in addresses)
            {
                if (IsBlockedIp(ip))
                    return $"Error: Access denied — the resolved address ({ip}) is blocked for security reasons.";
            }
            return null;
        }
        catch (Exception ex)
        {
            return $"Error: DNS resolution failed for {uri.Host} — {ex.Message}";
        }
    }

    /// <summary>
    /// Returns true if the IP is loopback, link-local, private (RFC 1918), or a cloud metadata address.
    /// </summary>
    private static bool IsBlockedIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv6LinkLocal) return true;
        if (ip.IsIPv6SiteLocal) return true;

        var bytes = ip.GetAddressBytes();
        return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && bytes.Length == 4 && (
            bytes[0] == 10 ||                          // 10.0.0.0/8
            (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) || // 172.16.0.0/12
            (bytes[0] == 192 && bytes[1] == 168) ||    // 192.168.0.0/16
            (bytes[0] == 169 && bytes[1] == 254) ||    // 169.254.0.0/16 (link-local + cloud metadata)
            bytes[0] == 127 ||                          // 127.0.0.0/8
            bytes[0] == 0                               // 0.0.0.0/8
        );
    }

    [GeneratedRegex(@"<(script|style)\b[^>]*>[\s\S]*?</\1>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptStyleRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultiNewlineRegex();

    public void Dispose() => _http.Dispose();
}

using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Http;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Native replica of the OpenClaw web-fetch plugin.
/// Fetches a URL and returns the body as plain text (HTML tags stripped).
/// </summary>
public sealed partial class WebFetchTool : ITool, IDisposable
{
    private readonly WebFetchConfig _config;
    private readonly HttpClient _http;
    private const int MaxRedirects = 5;
    private const int MaxTimeoutSeconds = 120;

    // Sends a real browser fingerprint; many sites block bots with minimal UAs.
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    public WebFetchTool(WebFetchConfig config, HttpClient? httpClient = null, UrlSafetyConfig? urlSafety = null)
    {
        _config = config;
        _http = httpClient ?? HttpClientFactory.Create(allowAutoRedirect: false);
        // UA is set per-request to allow Cloudflare fallback; do not set on client defaults.
    }

    public string Name => "web_fetch";
    public string Description =>
        "Fetch a web page and return its content as Markdown (default), plain text, or raw HTML. " +
        "Markdown output preserves document structure (headings, links, code) and is best for LLM analysis. " +
        "Use for reading documentation, articles, or API responses.";
    public string ParameterSchema => """
        {
          "type": "object",
          "properties": {
            "url": { "type": "string", "description": "The URL to fetch" },
            "format": {
              "type": "string",
              "enum": ["markdown", "text", "html"],
              "description": "Output format: 'markdown' (default, preserves structure for LLMs), 'text' (plain text), 'html' (raw HTML)"
            },
            "timeout": { "type": "integer", "description": "Request timeout in seconds (1–120, overrides server default)" },
            "max_length": { "type": "integer", "description": "Maximum characters to return (default: auto)" },
            "language": { "type": "string", "description": "Preferred content language as BCP-47 tag, e.g. 'zh-CN', 'en-US', 'ja'. Sent as Accept-Language header. Default: 'zh-CN,zh;q=0.9,en;q=0.8' (Chinese-first for sites with both languages)." }
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

        var format = args.RootElement.TryGetProperty("format", out var fmt)
            ? fmt.GetString() ?? "markdown"
            : "markdown";

        var timeoutSeconds = args.RootElement.TryGetProperty("timeout", out var to)
            ? Math.Clamp(to.GetInt32(), 1, MaxTimeoutSeconds)
            : _config.TimeoutSeconds;

        var language = args.RootElement.TryGetProperty("language", out var lang)
            ? lang.GetString()?.Trim()
            : null;
        // Build Accept-Language: if caller specified a tag, put it first; otherwise default Chinese-first
        // so multilingual sites (like zread.ai) return the preferred localisation.
        var acceptLanguage = string.IsNullOrEmpty(language)
            ? "zh-CN,zh;q=0.9,en;q=0.8"
            : $"{language};q=1.0,en;q=0.5";

        var acceptHeader = format switch
        {
            "markdown" => "text/markdown;q=1.0, text/x-markdown;q=0.9, text/plain;q=0.8, text/html;q=0.7, */*;q=0.1",
            "text"     => "text/plain;q=1.0, text/markdown;q=0.9, text/html;q=0.8, */*;q=0.1",
            "html"     => "text/html;q=1.0, application/xhtml+xml;q=0.9, text/plain;q=0.8, */*;q=0.1",
            _          => "text/html, application/xhtml+xml, application/xml;q=0.9, */*;q=0.8",
        };

        maxLength = Math.Clamp(maxLength, 100, _config.MaxSizeKb * 1024);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return "Error: Invalid URL. Only http:// and https:// are supported.";
        }

        var current = uri;
        var redirects = 0;
        while (true)
        {
            // SSRF protection: block internal/metadata IPs (re-check on each hop)
            var ssrfError = await CheckSsrfAsync(current);
            if (ssrfError is not null)
                return ssrfError;

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
                request.Headers.TryAddWithoutValidation("Accept", acceptHeader);
                request.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);

                var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                // Cloudflare bot-detection (403 + cf-mitigated header): retry with honest identity UA.
                if ((int)response.StatusCode == 403 && response.Headers.Contains("cf-mitigated"))
                {
                    response.Dispose();
                    using var retryReq = new HttpRequestMessage(HttpMethod.Get, current);
                    retryReq.Headers.TryAddWithoutValidation("User-Agent", _config.UserAgent);
                    retryReq.Headers.TryAddWithoutValidation("Accept", acceptHeader);
                    response = await _http.SendAsync(retryReq, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                }

                using var _ = response;

                if (IsRedirectStatus(response.StatusCode))
                {
                    if (redirects++ >= MaxRedirects)
                        return "Error: Too many redirects.";

                    var location = response.Headers.Location;
                    if (location is null)
                        return $"Error: Redirect ({(int)response.StatusCode}) with no Location header.";

                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (next.Scheme != "http" && next.Scheme != "https")
                        return "Error: Redirected to a non-http(s) URL, which is not allowed.";

                    current = next;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    return $"Error: HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";

                // Image content: return an informational note rather than raw binary.
                if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
                    !contentType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase))
                {
                    return $"[Image at {current} — Content-Type: {contentType}. Use BrowserTool with 'screenshot' action to render visually.]";
                }

                // Read the body with size limit using a small pooled read buffer + MemoryStream
                // to avoid renting a potentially large (up to 512KB) array upfront from ArrayPool.
                var maxBytes = _config.MaxSizeKb * 1024;
                var stream = await response.Content.ReadAsStreamAsync(cts.Token);
                const int ReadBufferSize = 16 * 1024; // 16KB read chunks
                var readBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
                try
                {
                    using var ms = new System.IO.MemoryStream();
                    int bytesRead;
                    while (ms.Length < maxBytes &&
                           (bytesRead = await stream.ReadAsync(readBuffer.AsMemory(0, (int)Math.Min(ReadBufferSize, maxBytes - ms.Length)), cts.Token)) > 0)
                    {
                        ms.Write(readBuffer, 0, bytesRead);
                    }

                    var totalRead = (int)ms.Length;
                    var raw = Encoding.UTF8.GetString(ms.GetBuffer(), 0, totalRead);
                    var truncated = totalRead == maxBytes;

                    // Format-aware content processing
                    string text;
                    if (format == "html")
                    {
                        text = raw;
                    }
                    else if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                    {
                        text = format == "text" ? ExtractTextFromHtml(raw) : ConvertHtmlToMarkdown(raw);
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

                    var urlLine = current.ToString();
                    var redirectNote = redirects > 0 ? $" (redirected from {url})" : "";
                    var header = $"URL: {urlLine}{redirectNote}\nContent-Type: {contentType}\nFormat: {format}\nLength: {text.Length} chars{(truncated ? " (truncated)" : "")}\n\n";
                    return header + text;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(readBuffer);
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

        // Decode HTML entities (including numeric entities like &#20013; common in Chinese sites)
        cleaned = WebUtility.HtmlDecode(cleaned);

        // Collapse whitespace
        cleaned = WhitespaceRegex().Replace(cleaned, " ").Trim();

        // Collapse multiple newlines
        cleaned = MultiNewlineRegex().Replace(cleaned, "\n\n");

        return cleaned;
    }

    /// <summary>
    /// Convert HTML to Markdown, preserving document structure (headings, links, code, lists).
    /// Uses regex-based, AOT-safe transformation — no external parser dependency.
    /// </summary>
    internal static string ConvertHtmlToMarkdown(string html)
    {
        // 1. Remove script/style blocks entirely
        var cleaned = ScriptStyleRegex().Replace(html, "");

        // 2. Pre/code blocks — must run before inline <code> to avoid double-processing
        cleaned = PreCodeRegex().Replace(cleaned, m =>
        {
            var content = TagRegex().Replace(m.Groups[1].Value, "");
            content = WebUtility.HtmlDecode(content);
            return $"\n\n```\n{content.Trim()}\n```\n\n";
        });

        // 3. Headings
        cleaned = HeadingRegex().Replace(cleaned, m =>
        {
            var level = int.Parse(m.Groups[1].Value);
            var text = TagRegex().Replace(m.Groups[2].Value, " ").Trim();
            text = WebUtility.HtmlDecode(text);
            return $"\n\n{new string('#', level)} {text}\n\n";
        });

        // 4. Links — convert before stripping tags so href is preserved
        cleaned = HtmlLinkRegex().Replace(cleaned, m =>
        {
            var href = m.Groups[1].Value.Trim();
            var text = TagRegex().Replace(m.Groups[2].Value, "").Trim();
            text = WebUtility.HtmlDecode(text);
            return string.IsNullOrWhiteSpace(text) ? href : $"[{text}]({href})";
        });

        // 5. Bold / strong
        cleaned = BoldRegex().Replace(cleaned, m =>
        {
            var text = TagRegex().Replace(m.Groups[1].Value, "").Trim();
            return $"**{text}**";
        });

        // 6. Italic / em
        cleaned = ItalicRegex().Replace(cleaned, m =>
        {
            var text = TagRegex().Replace(m.Groups[1].Value, "").Trim();
            return $"*{text}*";
        });

        // 7. Inline code
        cleaned = InlineCodeRegex().Replace(cleaned, m =>
        {
            var text = TagRegex().Replace(m.Groups[1].Value, "");
            return $"`{text}`";
        });

        // 8. List items
        cleaned = ListItemRegex().Replace(cleaned, m =>
        {
            var text = TagRegex().Replace(m.Groups[1].Value, " ").Trim();
            text = WebUtility.HtmlDecode(text);
            return $"\n- {text}";
        });

        // 9. Horizontal rules
        cleaned = HrRegex().Replace(cleaned, "\n\n---\n\n");

        // 10. Block-level closing tags → double newline; <br> → single newline
        cleaned = BlockEndTagRegex().Replace(cleaned, "\n\n");
        cleaned = BrTagRegex().Replace(cleaned, "\n");

        // 11. Strip remaining tags
        cleaned = TagRegex().Replace(cleaned, " ");

        // 12. Decode HTML entities (including numeric entities like &#20013; common in Chinese sites)
        cleaned = WebUtility.HtmlDecode(cleaned);

        // 13. Normalize whitespace, then collapse excess newlines
        cleaned = WhitespaceRegex().Replace(cleaned, " ").Trim();
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
                    return $"Error: URL blocked by safety policy - resolved address ({ip}) is blocked for security reasons.";
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
        if (ip.IsIPv4MappedToIPv6)
            return IsBlockedIp(ip.MapToIPv4());

        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv6LinkLocal) return true;
        if (ip.IsIPv6SiteLocal) return true;

        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && bytes.Length == 4)
        {
            return
                bytes[0] == 10 || // 10.0.0.0/8
                (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) || // 172.16.0.0/12
                (bytes[0] == 192 && bytes[1] == 168) || // 192.168.0.0/16
                (bytes[0] == 169 && bytes[1] == 254) || // 169.254.0.0/16 (link-local + cloud metadata)
                bytes[0] == 127 || // 127.0.0.0/8
                bytes[0] == 0; // 0.0.0.0/8
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && bytes.Length == 16)
        {
            // Unique local addresses fc00::/7
            if ((bytes[0] & 0xFE) == 0xFC)
                return true;
        }

        return false;
    }

    private static bool IsRedirectStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    [GeneratedRegex(@"<(script|style)\b[^>]*>[\s\S]*?</\1>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptStyleRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultiNewlineRegex();

    // ── Markdown conversion regexes ──────────────────────────────────────────

    [GeneratedRegex(@"<pre[^>]*>\s*(?:<code[^>]*>)?(.*?)(?:</code>)?\s*</pre>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex PreCodeRegex();

    [GeneratedRegex(@"<h([1-6])[^>]*>(.*?)</h\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"<a\s[^>]*href=[""']([^""']*)[""'][^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlLinkRegex();

    [GeneratedRegex(@"<(?:strong|b)[^>]*>(.*?)</(?:strong|b)>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"<(?:em|i)[^>]*>(.*?)</(?:em|i)>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ItalicRegex();

    [GeneratedRegex(@"<code[^>]*>(.*?)</code>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"<li[^>]*>(.*?)</li>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ListItemRegex();

    [GeneratedRegex(@"<hr\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex HrRegex();

    [GeneratedRegex(@"</(?:p|div|article|section|header|footer|h[1-6]|ul|ol|blockquote|table|tr|thead|tbody)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEndTagRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BrTagRegex();

    public void Dispose() => _http.Dispose();
}

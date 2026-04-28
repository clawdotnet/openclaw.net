using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Http;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Native replica of the OpenClaw web-fetch plugin.
/// Fetches a URL and returns the body as plain text (HTML tags stripped).
/// </summary>
public sealed partial class WebFetchTool : ITool, IDisposable
{
    private readonly WebFetchConfig _config;
    private readonly UrlSafetyConfig _urlSafety;
    private readonly HttpClient _http;
    private const int MaxRedirects = 5;

    public WebFetchTool(WebFetchConfig config, HttpClient? httpClient = null, UrlSafetyConfig? urlSafety = null)
    {
        _config = config;
        _urlSafety = config.UrlSafety ?? urlSafety ?? new UrlSafetyConfig();
        _http = httpClient ?? HttpClientFactory.Create(allowAutoRedirect: false);
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

        var current = uri;
        var redirects = 0;
        while (true)
        {
            var safety = await UrlSafetyValidator.ValidateHttpUrlAsync(current, _urlSafety, ct);
            if (!safety.Allowed)
                return safety.ToToolError();

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));

                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

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

                    var urlLine = current.ToString();
                    var redirectNote = redirects > 0 ? $" (redirected from {url})" : "";
                    var header = $"URL: {urlLine}{redirectNote}\nContent-Type: {contentType}\nLength: {text.Length} chars{(truncated ? " (truncated)" : "")}\n\n";
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

    public void Dispose() => _http.Dispose();
}

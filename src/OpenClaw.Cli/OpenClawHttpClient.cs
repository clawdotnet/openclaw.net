using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Models;

namespace OpenClaw.Cli;

internal sealed class OpenClawHttpClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _chatCompletionsUri;

    public OpenClawHttpClient(string baseUrl, string? authToken)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("Base URL is required.", nameof(baseUrl));

        var normalized = baseUrl.TrimEnd('/');
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var baseUri))
            throw new ArgumentException($"Invalid base URL: {baseUrl}", nameof(baseUrl));

        _chatCompletionsUri = new Uri(baseUri, "/v1/chat/completions");

        _http = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("openclaw-cli/1.0");

        if (!string.IsNullOrWhiteSpace(authToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
    }

    public async Task<OpenAiChatCompletionResponse> ChatCompletionAsync(
        OpenAiChatCompletionRequest request,
        CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _chatCompletionsUri)
        {
            Content = BuildJsonContent(request)
        };

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw await CreateHttpErrorAsync(resp, cancellationToken);

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        var parsed = await JsonSerializer.DeserializeAsync(stream,
            CoreJsonContext.Default.OpenAiChatCompletionResponse, cancellationToken);

        if (parsed is null)
            throw new InvalidOperationException("Empty response body.");

        return parsed;
    }

    public async Task<string> StreamChatCompletionAsync(
        OpenAiChatCompletionRequest request,
        Action<string> onText,
        CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _chatCompletionsUri)
        {
            Content = BuildJsonContent(request)
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw await CreateHttpErrorAsync(resp, cancellationToken);

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 16 * 1024, leaveOpen: false);

        var sb = new StringBuilder();

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;

            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var data = line["data:".Length..].TrimStart();
            if (data.Length == 0)
                continue;

            if (data == "[DONE]")
                break;

            OpenAiStreamChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize(data, CoreJsonContext.Default.OpenAiStreamChunk);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to parse SSE chunk: {data}", ex);
            }

            var delta = chunk?.Choices.Count > 0 ? chunk.Choices[0].Delta.Content : null;
            if (string.IsNullOrEmpty(delta))
                continue;

            sb.Append(delta);
            onText(delta);
        }

        return sb.ToString();
    }

    private static HttpContent BuildJsonContent(OpenAiChatCompletionRequest request)
    {
        var json = JsonSerializer.Serialize(request, CoreJsonContext.Default.OpenAiChatCompletionRequest);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private static async Task<Exception> CreateHttpErrorAsync(HttpResponseMessage resp, CancellationToken cancellationToken)
    {
        string? body = null;
        try
        {
            body = await resp.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
            // ignore
        }

        var status = $"{(int)resp.StatusCode} {resp.ReasonPhrase}".Trim();
        if (string.IsNullOrWhiteSpace(body))
            return new HttpRequestException($"HTTP {status}");

        body = body.Trim();
        if (body.Length > 8000)
            body = body[..8000] + "…";

        return new HttpRequestException($"HTTP {status}\n{body}");
    }

    public void Dispose() => _http.Dispose();
}

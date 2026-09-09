using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace OpenClaw.Dashboard.Services;

public class ApiService
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _jsonOptions;
    private string? _csrfToken;
    private string? _apiKey;

    public ApiService(HttpClient http)
    {
        _http = http;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    public void SetBaseUrl(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return;
        }

        var normalized = baseUrl.EndsWith("/", StringComparison.Ordinal)
            ? baseUrl
            : baseUrl + "/";
        _http.BaseAddress = new Uri(normalized);
    }

    public void SetApiKey(string? apiKey) => _apiKey = apiKey;

    public void SetCsrfToken(string? token) => _csrfToken = token;

    public async Task<T?> GetAsync<T>(string url)
    {
        using var request = CreateRequest(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await response.Content
            .ReadFromJsonAsync<T>(_jsonOptions)
            .ConfigureAwait(false);
    }

    public async Task<HttpResponseMessage> GetRawAsync(string url)
    {
        var request = CreateRequest(HttpMethod.Get, url);
        return await _http.SendAsync(request).ConfigureAwait(false);
    }

    public async Task<T?> PostAsync<T>(string url, object? body = null)
    {
        using var request = CreateRequest(HttpMethod.Post, url, body);
        using var response = await _http.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength == 0)
        {
            return default;
        }

        return await response.Content
            .ReadFromJsonAsync<T>(_jsonOptions)
            .ConfigureAwait(false);
    }

    public async Task<HttpResponseMessage> PostRawAsync(string url, object? body = null, string? bearerToken = null)
    {
        var request = CreateRequest(HttpMethod.Post, url, body);
        if (!string.IsNullOrWhiteSpace(bearerToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return await _http.SendAsync(request).ConfigureAwait(false);
    }

    public async Task<HttpResponseMessage> PutRawAsync(string url, object? body = null)
    {
        var request = CreateRequest(HttpMethod.Put, url, body);
        return await _http.SendAsync(request).ConfigureAwait(false);
    }

    public async Task<HttpResponseMessage> PostContentAsync(string url, HttpContent content)
    {
        var request = CreateRequest(HttpMethod.Post, url);
        request.Content = content;
        return await _http.SendAsync(request).ConfigureAwait(false);
    }

    public async Task<HttpResponseMessage> DeleteAsync(string url)
    {
        var request = CreateRequest(HttpMethod.Delete, url);
        return await _http.SendAsync(request).ConfigureAwait(false);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, NormalizeApiUrl(url));

        var isMutation = method == HttpMethod.Post
            || method == HttpMethod.Put
            || method == HttpMethod.Delete
            || method == HttpMethod.Patch;

        if (!string.IsNullOrEmpty(_apiKey))
        {
            request.Headers.TryAddWithoutValidation("X-Api-Key", _apiKey);
        }

        if (isMutation && !string.IsNullOrEmpty(_csrfToken))
        {
            request.Headers.TryAddWithoutValidation("X-Csrf-Token", _csrfToken);
        }

        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, _jsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private static string NormalizeApiUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out _) || url.StartsWith("/", StringComparison.Ordinal))
        {
            return url;
        }

        return string.Concat("/", url);
    }

    internal JsonSerializerOptions JsonOptions => _jsonOptions;
}

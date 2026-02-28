using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;

namespace OpenClaw.Agent.Tools;

internal sealed class HomeAssistantRestClient : IDisposable
{
    private readonly HomeAssistantConfig _config;
    private readonly HttpClient _http;
    private readonly Uri _baseUri;

    public HomeAssistantRestClient(HomeAssistantConfig config, HttpClient? httpClient = null)
    {
        _config = config;

        if (!Uri.TryCreate(config.BaseUrl.TrimEnd('/'), UriKind.Absolute, out var baseUri))
            throw new ArgumentException($"Invalid Home Assistant BaseUrl: {config.BaseUrl}", nameof(config));

        _baseUri = baseUri;
        _http = httpClient ?? CreateHttpClient(config);

        var token = SecretResolver.Resolve(config.TokenRef);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Home Assistant token not configured. Set OpenClaw:Plugins:Native:HomeAssistant:TokenRef (default env:HOME_ASSISTANT_TOKEN).");

        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<string> GetStatesAsync(CancellationToken ct)
        => await GetStringLimitedAsync(new Uri(_baseUri, "/api/states"), ct);

    public async Task<string> GetStateAsync(string entityId, CancellationToken ct)
        => await GetStringLimitedAsync(new Uri(_baseUri, $"/api/states/{Uri.EscapeDataString(entityId)}"), ct);

    public async Task<string> GetServicesAsync(CancellationToken ct)
        => await GetStringLimitedAsync(new Uri(_baseUri, "/api/services"), ct);

    public async Task<string> CallServiceAsync(string domain, string service, string bodyJson, CancellationToken ct)
    {
        var url = new Uri(_baseUri, $"/api/services/{Uri.EscapeDataString(domain)}/{Uri.EscapeDataString(service)}");
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _config.TimeoutSeconds)));

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
            throw new HttpRequestException("Home Assistant authorization failed (401/403). Check your token.");

        if (!resp.IsSuccessStatusCode)
        {
            var err = await ReadBodyLimitedAsync(resp, cts.Token);
            throw new HttpRequestException($"Home Assistant call_service failed: {(int)resp.StatusCode} {resp.ReasonPhrase}\n{err}");
        }

        return await ReadBodyLimitedAsync(resp, cts.Token);
    }

    private async Task<string> GetStringLimitedAsync(Uri url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _config.TimeoutSeconds)));

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await ReadBodyLimitedAsync(resp, cts.Token);
            throw new HttpRequestException($"Home Assistant request failed: {(int)resp.StatusCode} {resp.ReasonPhrase}\n{err}");
        }

        return await ReadBodyLimitedAsync(resp, cts.Token);
    }

    private async Task<string> ReadBodyLimitedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var maxChars = Math.Max(1_000, _config.MaxOutputChars);

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 16 * 1024, leaveOpen: false);
        var buffer = new char[maxChars + 1];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(0, buffer.Length), ct);
        if (read <= 0)
            return "";

        var take = Math.Min(read, maxChars);
        var text = new string(buffer, 0, take);
        var truncated = read > maxChars;
        if (!truncated)
        {
            var extra = await reader.ReadAsync(buffer.AsMemory(0, 1), ct);
            truncated = extra > 0;
        }

        if (truncated)
            text += "…";
        return text;
    }

    private static HttpClient CreateHttpClient(HomeAssistantConfig config)
    {
        var handler = new SocketsHttpHandler();

        if (!config.VerifyTls)
        {
            handler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            };
        }

        var http = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        return http;
    }

    public void Dispose() => _http.Dispose();

    public static string BuildServiceBodyJson(string? entityIdOrNull, JsonElement? dataOrNull)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();

            if (!string.IsNullOrWhiteSpace(entityIdOrNull))
            {
                writer.WriteString("entity_id", entityIdOrNull);
            }

            if (dataOrNull is { ValueKind: JsonValueKind.Object })
            {
                writer.WritePropertyName("data");
                dataOrNull.Value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }
}

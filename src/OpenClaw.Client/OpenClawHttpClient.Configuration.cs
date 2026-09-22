using System.Text.Json;
using OpenClaw.Core.Models;

namespace OpenClaw.Client;

public sealed partial class OpenClawHttpClient
{
    public async Task<ConfigurationState> GetConfigurationAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, "/admin/configuration"));
        return await SendAsync(request, ConfigurationJsonContext.Default.ConfigurationState, ct);
    }

    public async Task<ConfigurationState> PreviewConfigurationAsync(ConfigurationRequest request, CancellationToken ct = default)
        => await SendConfigurationAsync("preview", request, ct);

    public async Task<ConfigurationState> ApplyConfigurationAsync(ConfigurationRequest request, CancellationToken ct = default)
        => await SendConfigurationAsync("apply", request, ct);

    private async Task<ConfigurationState> SendConfigurationAsync(string operation, ConfigurationRequest body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, $"/admin/configuration/{operation}"))
        { Content = BuildJsonContent(body, ConfigurationJsonContext.Default.ConfigurationRequest) };
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode && response.StatusCode is not System.Net.HttpStatusCode.BadRequest and not System.Net.HttpStatusCode.Conflict)
            throw await CreateHttpErrorAsync(response, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(message.GetString()) && document.RootElement.TryGetProperty("success", out _))
                return JsonSerializer.Deserialize(payload, ConfigurationJsonContext.Default.ConfigurationState)!;
        }
        catch (JsonException) { }
        if (!response.IsSuccessStatusCode) throw await CreateHttpErrorAsync(response, ct);
        throw new InvalidOperationException("The gateway returned an invalid configuration response.");
    }
}

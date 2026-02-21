using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Http;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Native replica of the OpenClaw image-gen plugin.
/// Generates images via OpenAI DALL-E (or compatible APIs).
/// Returns the image URL or base64 data.
/// </summary>
public sealed class ImageGenTool : ITool, IDisposable
{
    private readonly ImageGenConfig _config;
    private readonly HttpClient _http;

    public ImageGenTool(ImageGenConfig config, HttpClient? httpClient = null)
    {
        _config = config;
        _http = httpClient ?? HttpClientFactory.Create();
    }

    public string Name => "image_gen";
    public string Description =>
        "Generate an image from a text prompt. Returns the URL of the generated image.";
    public string ParameterSchema => """
        {
          "type": "object",
          "properties": {
            "prompt": {
              "type": "string",
              "description": "Text description of the image to generate"
            },
            "size": {
              "type": "string",
              "description": "Image size: 1024x1024, 1792x1024, 1024x1792",
              "default": "1024x1024"
            },
            "quality": {
              "type": "string",
              "description": "Image quality: standard or hd",
              "default": "standard"
            }
          },
          "required": ["prompt"]
        }
        """;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var args = JsonDocument.Parse(argumentsJson);
        var prompt = args.RootElement.GetProperty("prompt").GetString()!;
        var size = args.RootElement.TryGetProperty("size", out var s) ? s.GetString() ?? _config.Size : _config.Size;
        var quality = args.RootElement.TryGetProperty("quality", out var q) ? q.GetString() ?? _config.Quality : _config.Quality;

        return _config.Provider.ToLowerInvariant() switch
        {
            "openai" => await GenerateOpenAiAsync(prompt, size, quality, ct),
            _ => $"Error: Unsupported image generation provider '{_config.Provider}'."
        };
    }

    private async Task<string> GenerateOpenAiAsync(string prompt, string size, string quality, CancellationToken ct)
    {
        var apiKey = ResolveKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            return "Error: API key not configured. Set ImageGen.ApiKey.";

        var endpoint = _config.Endpoint?.TrimEnd('/') ?? "https://api.openai.com/v1";
        var url = $"{endpoint}/images/generations";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("Authorization", $"Bearer {apiKey}");

        var body = BuildRequestJson(prompt, size, quality);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        try
        {
            using var response = await _http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                return $"Error: Image generation failed (HTTP {(int)response.StatusCode}): {Truncate(errorBody, 500)}";
            }

            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            if (doc.RootElement.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
            {
                var first = data[0];
                var imageUrl = first.TryGetProperty("url", out var u) ? u.GetString() : null;
                var revisedPrompt = first.TryGetProperty("revised_prompt", out var rp) ? rp.GetString() : null;

                var sb = new StringBuilder();
                if (imageUrl is not null)
                    sb.AppendLine($"Image URL: {imageUrl}");
                if (revisedPrompt is not null)
                    sb.AppendLine($"Revised prompt: {revisedPrompt}");

                return sb.Length > 0 ? sb.ToString() : "Image generated but no URL returned.";
            }

            return "Error: Unexpected response format from image API.";
        }
        catch (HttpRequestException ex)
        {
            return $"Error: Image generation request failed — {ex.Message}";
        }
        catch (TaskCanceledException)
        {
            return "Error: Image generation request timed out.";
        }
    }

    /// <summary>AOT-safe JSON builder for the image gen request.</summary>
    private string BuildRequestJson(string prompt, string size, string quality)
    {
        using var ms = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("model", _config.Model);
            writer.WriteString("prompt", prompt);
            writer.WriteNumber("n", 1);
            writer.WriteString("size", size);
            writer.WriteString("quality", quality);
            writer.WriteString("response_format", "url");
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private string? ResolveKey() => SecretResolver.Resolve(_config.ApiKey);

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";

    public void Dispose() => _http.Dispose();
}

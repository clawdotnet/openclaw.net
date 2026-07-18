using System.Text;
using System.Text.Json;
using OpenAI;
using OpenAI.Images;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Http;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;
using System.ClientModel;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Native replica of the OpenClaw image-gen plugin.
/// Generates images through a configurable provider:
/// <list type="bullet">
/// <item><c>openai</c> — OpenAI DALL-E (or OpenAI-compatible endpoints) via the official OpenAI SDK.</item>
/// <item><c>dashscope</c> / <c>qwen</c> — Alibaba Tongyi Qwen image models via the DashScope synchronous API.</item>
/// </list>
/// Returns the image URL (and, for OpenAI, the revised prompt when available).
/// </summary>
public sealed class ImageGenTool : ITool, IDisposable
{
    private readonly ImageGenConfig _config;
    private readonly IModelProfileRegistry? _modelProfiles;

    // Tooling config is required to resolve a policy-approved downloads directory when a
    // provider returns raw image bytes (base64) instead of a URL. May be null in tests.
    private readonly ToolingConfig? _tooling;

    // Lazily created; only used by the DashScope provider (the OpenAI path uses the SDK's own HTTP).
    private HttpClient? _http;

    public ImageGenTool(
        ImageGenConfig config,
        ToolingConfig? tooling = null,
        IModelProfileRegistry? modelProfiles = null)
    {
        _config = config;
        _tooling = tooling;
        _modelProfiles = modelProfiles;
    }

    public string Name => "image_gen";
    public string Description =>
        "Generate an image from a text prompt. Returns the generated image as Markdown " +
        "(![generated image](url)) plus its URL. Include the returned Markdown image verbatim " +
        "in your reply so the user can see the picture.";
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

        var effective = ResolveEffectiveConfig();

        return effective.Provider.ToLowerInvariant() switch
        {
            "openai" => await GenerateOpenAiAsync(prompt, size, quality, effective, ct),
            "dashscope" or "qwen" => await GenerateDashScopeAsync(prompt, size, effective, ct),
            _ => $"Error: Unsupported image generation provider '{effective.Provider}'."
        };
    }

    // ---------------------------------------------------------------------
    // OpenAI (official SDK)
    // ---------------------------------------------------------------------

    private async Task<string> GenerateOpenAiAsync(
        string prompt,
        string size,
        string quality,
        EffectiveImageGenConfig effective,
        CancellationToken ct)
    {
        var apiKey = SecretResolver.Resolve(effective.ApiKey);
        if (string.IsNullOrWhiteSpace(apiKey))
            return "Error: API key not configured. Set ImageGen.ApiKey or bind ImageGen.ModelProfileId to a profile with ApiKey.";

        var clientOptions = new OpenAIClientOptions();
        var endpoint = ResolveOpenAiEndpoint(effective.Endpoint);
        if (!string.IsNullOrWhiteSpace(endpoint))
            clientOptions.Endpoint = new Uri(endpoint);

        // The SDK enforces its own network timeout (ClientPipelineOptions.NetworkTimeout,
        // default 100s) independently of our linked-token timeout. Align it with the
        // configured TimeoutSeconds so slow backends (e.g. local CPU image models) are not
        // cut off prematurely by the SDK's default.
        if (_config.TimeoutSeconds > 0)
            clientOptions.NetworkTimeout = TimeSpan.FromSeconds(_config.TimeoutSeconds);

        var imageClient = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions)
            .GetImageClient(effective.Model);

        var options = new ImageGenerationOptions
        {
            Size = MapSize(size),
            Quality = MapQuality(quality),
            ResponseFormat = GeneratedImageFormat.Uri,
        };

        using var cts = CreateTimeoutCts(ct);
        var effectiveCt = cts?.Token ?? ct;

        try
        {
            var image = (await imageClient.GenerateImageAsync(prompt, options, effectiveCt)).Value;

            // Prefer a URL when the provider returns one.
            var url = image.ImageUri?.ToString();
            if (!string.IsNullOrEmpty(url))
                return FormatImageResult(url, image.RevisedPrompt);

            // No URL: fall back to raw bytes (base64) by saving them locally and emitting
            // an [IMAGE_PATH:] marker for the media pipeline to pick up.
            var bytes = image.ImageBytes;
            if (bytes is not null && bytes.ToArray().Length > 0)
            {
                var savedPath = await SaveImageBytesAsync(bytes.ToArray(), "image/png", ct);
                if (savedPath is null)
                    return "Error: image data was returned but could not be saved. Ensure Tooling.WorkspaceRoot or an AllowedWriteRoot is configured.";
                return FormatImagePathResult(savedPath);
            }

            return "Image generated but no URL or data returned.";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "Error: Image generation request timed out.";
        }
        catch (Exception ex)
        {
            return $"Error: Image generation request failed — {ex.Message}";
        }
    }

    private static string ResolveOpenAiEndpoint(string? configuredEndpoint)
    {
        if (!string.IsNullOrWhiteSpace(configuredEndpoint))
            return configuredEndpoint;

        return "https://api.openai.com/v1";
    }

    /// <summary>
    /// Maps a free-form size string (from config or tool args) to the SDK's strongly-typed
    /// <see cref="GeneratedImageSize"/>. Falls back to a "WxH" custom size, then to 1024x1024.
    /// Sizes not exposed as stable SDK constants (e.g. 1024x1536) are handled via the "WxH" parser.
    /// </summary>
    private static GeneratedImageSize MapSize(string? size)
    {
        var value = (size ?? "").Trim().ToLowerInvariant();
        switch (value)
        {
            case "256x256": return GeneratedImageSize.W256xH256;
            case "512x512": return GeneratedImageSize.W512xH512;
            case "1024x1024": return GeneratedImageSize.W1024xH1024;
            case "1024x1792": return GeneratedImageSize.W1024xH1792;
            case "1792x1024": return GeneratedImageSize.W1792xH1024;
        }

        var parts = value.Split('x', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2
            && int.TryParse(parts[0], out var width) && width > 0
            && int.TryParse(parts[1], out var height) && height > 0)
        {
            return new GeneratedImageSize(width, height);
        }

        return GeneratedImageSize.W1024xH1024;
    }

    /// <summary>Maps a free-form quality string to the SDK's <see cref="GeneratedImageQuality"/>.</summary>
    private static GeneratedImageQuality MapQuality(string? quality)
        => string.Equals(quality?.Trim(), "hd", StringComparison.OrdinalIgnoreCase)
            ? GeneratedImageQuality.High
            : GeneratedImageQuality.Standard;

    // ---------------------------------------------------------------------
    // DashScope / Tongyi Qwen (synchronous multimodal-generation API)
    // ---------------------------------------------------------------------

    private async Task<string> GenerateDashScopeAsync(
        string prompt,
        string size,
        EffectiveImageGenConfig effective,
        CancellationToken ct)
    {
        var apiKey = SecretResolver.Resolve(effective.ApiKey);
        if (string.IsNullOrWhiteSpace(apiKey))
            return "Error: API key not configured. Set ImageGen.ApiKey or bind ImageGen.ModelProfileId to a profile with ApiKey.";

        if (string.IsNullOrWhiteSpace(effective.Endpoint))
            return "Error: DashScope endpoint not configured. Set ImageGen.Endpoint to the full API path "
                 + "(e.g. https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation).";

        // DashScope's Endpoint is used as the complete API path so a future API/path
        // change only requires a config update, not a code change.
        var url = effective.Endpoint.Trim();

        _http = LazyInitializer.EnsureInitialized(ref _http, static () => HttpClientFactory.Create());

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("Authorization", $"Bearer {apiKey}");
        using var content = BuildDashScopeRequestJson(prompt, size, effective.Model);
        request.Content = content;

        using var cts = CreateTimeoutCts(ct);
        var effectiveCt = cts?.Token ?? ct;

        try
        {
            using var response = await _http.SendAsync(request, effectiveCt);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(effectiveCt);
                return $"Error: Image generation failed (HTTP {(int)response.StatusCode}): {Truncate(errorBody, 500)}";
            }

            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(effectiveCt), cancellationToken: effectiveCt);

            var imageValue = ExtractDashScopeImageUrl(doc.RootElement);
            if (imageValue is not null)
            {
                // DashScope normally returns an http(s) URL, but tolerate a base64 data URI
                // by decoding and saving it locally, mirroring the OpenAI bytes fallback.
                if (TryDecodeDataUri(imageValue, out var dataBytes, out var dataMime))
                {
                    var savedPath = await SaveImageBytesAsync(dataBytes, dataMime, ct);
                    if (savedPath is null)
                        return "Error: image data was returned but could not be saved. Ensure Tooling.WorkspaceRoot or an AllowedWriteRoot is configured.";
                    return FormatImagePathResult(savedPath);
                }

                return FormatImageResult(imageValue, revisedPrompt: null);
            }

            // Surface DashScope's own error code/message when present.
            var code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
            var message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
            if (!string.IsNullOrWhiteSpace(code) || !string.IsNullOrWhiteSpace(message))
                return $"Error: Image generation failed — {code}: {message}";

            return "Error: Unexpected response format from image API.";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "Error: Image generation request timed out.";
        }
        catch (HttpRequestException ex)
        {
            return $"Error: Image generation request failed — {ex.Message}";
        }
    }

    /// <summary>
    /// AOT-safe JSON builder for the DashScope synchronous text-to-image request.
    /// Shape: { model, input: { messages: [{ role, content: [{ text }] }] }, parameters: { ... } }.
    /// </summary>
    private Utf8JsonContent BuildDashScopeRequestJson(string prompt, string size, string model)
    {
        var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);

            writer.WritePropertyName("input");
            writer.WriteStartObject();
            writer.WritePropertyName("messages");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("text", prompt);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WritePropertyName("parameters");
            writer.WriteStartObject();
            writer.WriteString("size", NormalizeDashScopeSize(size));
            writer.WriteBoolean("prompt_extend", _config.PromptExtend);
            writer.WriteBoolean("watermark", _config.Watermark);
            if (!string.IsNullOrWhiteSpace(_config.NegativePrompt))
                writer.WriteString("negative_prompt", _config.NegativePrompt);
            writer.WriteEndObject();

            writer.WriteEndObject();
        }
        ms.Position = 0L;
        return new Utf8JsonContent(ms);
    }

    /// <summary>
    /// Extracts the generated image URL from a DashScope synchronous response:
    /// output.choices[0].message.content[0].image.
    /// </summary>
    private static string? ExtractDashScopeImageUrl(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var output)) return null;
        if (!output.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            return null;

        var message = choices[0];
        if (!message.TryGetProperty("message", out var msg)) return null;
        if (!msg.TryGetProperty("content", out var contentArr) || contentArr.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var part in contentArr.EnumerateArray())
        {
            if (part.TryGetProperty("image", out var img))
            {
                var value = img.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }

        return null;
    }

    /// <summary>
    /// DashScope expects the size as "width*height". Normalizes an OpenAI-style "WxH"
    /// (or already "W*H") value; passes through anything else unchanged.
    /// </summary>
    private static string NormalizeDashScopeSize(string? size)
    {
        var value = (size ?? "").Trim();
        if (value.Length == 0)
            return "1024*1024";

        return value.Replace('x', '*').Replace('X', '*');
    }

    // ---------------------------------------------------------------------
    // Shared helpers
    // ---------------------------------------------------------------------

    private CancellationTokenSource? CreateTimeoutCts(CancellationToken ct)
    {
        if (_config.TimeoutSeconds <= 0)
            return null;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));
        return cts;
    }

    /// <summary>
    /// Formats a successful result so the chat UI renders the image inline.
    /// Emits Markdown image syntax first (so front-ends that render Markdown show the
    /// picture directly) followed by a plain-text URL line (so the raw link is always
    /// available for copying or non-Markdown surfaces).
    /// </summary>
    private static string FormatImageResult(string url, string? revisedPrompt)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"![generated image]({url})");
        sb.AppendLine();
        sb.AppendLine($"Image URL: {url}");
        if (!string.IsNullOrEmpty(revisedPrompt))
            sb.AppendLine($"Revised prompt: {revisedPrompt}");
        return sb.ToString();
    }

    /// <summary>
    /// Formats a base64/bytes result that was saved to disk. Emits an [IMAGE_PATH:...] marker
    /// which GatewayWorkers picks up after the turn: it uploads the bytes to the media store,
    /// exposes them at /media/{id}, and delivers an inline image to the client.
    /// </summary>
    private static string FormatImagePathResult(string path)
        => $"Image generated.{Environment.NewLine}[IMAGE_PATH:{path}]{Environment.NewLine}";

    /// <summary>
    /// Saves raw image bytes to {WorkspaceRoot}/.downloads/ (subject to write-root policy) and
    /// returns the destination path. Returns null when no writable location can be determined.
    /// </summary>
    private async Task<string?> SaveImageBytesAsync(byte[] bytes, string mimeType, CancellationToken ct)
    {
        if (bytes.Length == 0)
            return null;

        var downloadsDir = ResolveDownloadsDirectory();
        if (downloadsDir is null)
            return null;

        try
        {
            Directory.CreateDirectory(downloadsDir);
            var fileName = $"image_{Guid.NewGuid().ToString("N")[..8]}{MimeToExtension(mimeType)}";
            var destPath = Path.Combine(downloadsDir, fileName);
            await File.WriteAllBytesAsync(destPath, bytes, ct);
            return destPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves {WorkspaceRoot}/.downloads/, mirroring PublishFileTool. Returns null when the
    /// tooling policy is unavailable or the directory is not within an allowed write root.
    /// </summary>
    private string? ResolveDownloadsDirectory()
    {
        if (_tooling is null)
            return null;

        var workspaceRaw = SecretResolver.Resolve(_tooling.WorkspaceRoot);
        var workspaceBase = !string.IsNullOrWhiteSpace(workspaceRaw)
            ? workspaceRaw
            : Directory.GetCurrentDirectory();

        var downloadsDir = Path.Combine(Path.GetFullPath(workspaceBase), ".downloads");
        return ToolPathPolicy.IsWriteAllowed(_tooling, downloadsDir) ? downloadsDir : null;
    }

    /// <summary>
    /// Parses a "data:image/png;base64,XXXX" URI into raw bytes and its MIME type.
    /// Returns false for plain http(s) URLs or malformed data URIs.
    /// </summary>
    private static bool TryDecodeDataUri(string value, out byte[] bytes, out string mimeType)
    {
        bytes = [];
        mimeType = "image/png";

        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;

        var comma = value.IndexOf(',');
        if (comma < 0)
            return false;

        var header = value[5..comma];       // e.g. "image/png;base64"
        var payload = value[(comma + 1)..];
        if (!header.Contains("base64", StringComparison.OrdinalIgnoreCase))
            return false;

        var semicolon = header.IndexOf(';');
        if (semicolon > 0)
            mimeType = header[..semicolon];
        else if (!string.IsNullOrWhiteSpace(header))
            mimeType = header;

        try
        {
            bytes = Convert.FromBase64String(payload);
            return bytes.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Minimal MIME → file extension mapping for generated images (defaults to .png).</summary>
    private static string MimeToExtension(string? mimeType)
        => (mimeType?.Trim().ToLowerInvariant()) switch
        {
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => ".png"
        };

    private EffectiveImageGenConfig ResolveEffectiveConfig()
    {
        var provider = _config.Provider;
        var model = _config.Model;
        var endpoint = _config.Endpoint;
        var apiKey = _config.ApiKey;

        var profileId = Normalize(_config.ModelProfileId) ?? Normalize(_modelProfiles?.DefaultProfileId);
        if (!string.IsNullOrWhiteSpace(profileId) && _modelProfiles?.TryGet(profileId, out var profile) == true && profile is not null)
        {
            var mappedProvider = MapImageProvider(profile.ProviderId);
            if (!string.IsNullOrWhiteSpace(mappedProvider))
            {
                provider = mappedProvider;
                model = profile.ModelId;
                endpoint = profile.BaseUrl;
                apiKey = profile.ApiKey;
            }
        }

        return new EffectiveImageGenConfig(provider, model, endpoint, apiKey);
    }

    private static string? MapImageProvider(string? providerId)
    {
        var provider = Normalize(providerId);
        return provider switch
        {
            "openai" or "openai-compatible" or "azure-openai" or "aperture" or "groq" or "together" or "lmstudio" => "openai",
            "dashscope" or "qwen" => "dashscope",
            _ => null
        };
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private readonly record struct EffectiveImageGenConfig(
        string Provider,
        string Model,
        string? Endpoint,
        string? ApiKey);

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";

    public void Dispose() => _http?.Dispose();
}

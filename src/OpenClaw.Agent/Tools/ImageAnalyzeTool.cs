using OpenAI;
using OpenAI.Chat;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;
using System.ClientModel;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Analyzes one or more images using a vision-capable LLM (Layer 2 of the image pipeline).
/// Used when the main model does not support vision (<c>Llm:SupportsVision = false</c>).
/// Calls a separately-configured vision model provider so the main orchestration model
/// and the vision model can differ (e.g. DeepSeek text + GPT-4o vision).
/// </summary>
public sealed class ImageAnalyzeTool : ITool
{
    private readonly ImageAnalyzeConfig _config;

    public ImageAnalyzeTool(ImageAnalyzeConfig config) => _config = config;

    public string Name => "image_analyze";

    public string Description =>
        "Analyze or describe the content of one or more images. " +
        "Accepts local file paths or public URLs. " +
        "Returns a detailed description or answer based on the provided prompt.";

    public string ParameterSchema => """
        {
          "type": "object",
          "properties": {
            "image_urls": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Public URLs of images to analyze."
            },
            "image_paths": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Local file paths of images to analyze (read and sent as base64)."
            },
            "prompt": {
              "type": "string",
              "description": "What to ask about the image(s), e.g. 'Describe the image' or 'Extract all text'.",
              "default": "Please describe the image(s) in detail."
            }
          }
        }
        """;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(argumentsJson);
        var root = doc.RootElement;

        var imageUrls = root.TryGetProperty("image_urls", out var urlsProp)
            ? urlsProp.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)).Cast<string>().ToList()
            : [];

        var imagePaths = root.TryGetProperty("image_paths", out var pathsProp)
            ? pathsProp.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)).Cast<string>().ToList()
            : [];

        var prompt = root.TryGetProperty("prompt", out var pp) && !string.IsNullOrWhiteSpace(pp.GetString())
            ? pp.GetString()!
            : "Please describe the image(s) in detail.";

        if (imageUrls.Count == 0 && imagePaths.Count == 0)
            return "Error: No images provided. Supply at least one image_url or image_path.";

        var totalImages = imageUrls.Count + imagePaths.Count;
        if (totalImages > _config.MaxImagesPerCall)
            return $"Error: Too many images ({totalImages}). Maximum allowed per call is {_config.MaxImagesPerCall}.";

        var apiKey = ResolveKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            return "Error: Vision API key not configured. Set Plugins:Native:ImageAnalyze:ApiKey.";

        // Build content parts: text prompt + images
        var parts = new List<ChatMessageContentPart>
        {
            ChatMessageContentPart.CreateTextPart(prompt)
        };

        foreach (var url in imageUrls)
            parts.Add(ChatMessageContentPart.CreateImagePart(new Uri(url)));

        foreach (var path in imagePaths)
        {
            if (!File.Exists(path))
                return $"Error: File not found: {path}";

            try
            {
                var bytes = await File.ReadAllBytesAsync(path, ct);
                var mimeType = InferMimeType(path);
                parts.Add(ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(bytes), mimeType));
            }
            catch (Exception ex)
            {
                return $"Error: Could not read image file '{path}': {ex.Message}";
            }
        }

        return await CallWithSdkAsync(parts, apiKey, ct);
    }

    private async Task<string> CallWithSdkAsync(
        List<ChatMessageContentPart> parts,
        string apiKey,
        CancellationToken ct)
    {
        var endpoint = ResolveEndpoint();
        var clientOptions = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(endpoint))
            clientOptions.Endpoint = new Uri(endpoint);

        var openAiClient = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);
        var chatClient = openAiClient.GetChatClient(_config.Model);

        var messages = new List<ChatMessage> { new UserChatMessage(parts) };
        var options = new ChatCompletionOptions { MaxOutputTokenCount = 1024 };

        using var cts = _config.TimeoutSeconds > 0
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (cts is not null)
            cts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));
        var effectiveCt = cts?.Token ?? ct;

        try
        {
            var completion = await chatClient.CompleteChatAsync(messages, options, effectiveCt);
            var text = completion.Value.Content.FirstOrDefault()?.Text ?? "";
            return Truncate(text.Trim(), _config.MaxOutputChars);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "Error: Vision API request timed out.";
        }
        catch (Exception ex)
        {
            return $"Error: Vision API request failed — {ex.Message}";
        }
    }

    private string ResolveKey()
    {
        if (!string.IsNullOrWhiteSpace(_config.ApiKey))
            return SecretResolver.Resolve(_config.ApiKey) ?? "";

        return Environment.GetEnvironmentVariable("VISION_API_KEY") ?? "";
    }

    private string ResolveEndpoint()
    {
        if (!string.IsNullOrWhiteSpace(_config.Endpoint))
            return _config.Endpoint;

        return _config.Provider.ToLowerInvariant() switch
        {
            "ollama" => "http://localhost:11434/v1",
            _ => "https://api.openai.com/v1"
        };
    }

    private static string InferMimeType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "image/png"
        };

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";
}

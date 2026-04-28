using System.Buffers;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Gateway;

internal sealed class GeminiMultimodalService
{
    private const string ApiBase = "https://generativelanguage.googleapis.com/v1beta/models/";

    private readonly GatewayConfig _config;
    private readonly MediaCacheStore _mediaCache;
    private readonly ILogger<GeminiMultimodalService> _logger;
    private readonly HttpClient _httpClient;

    public GeminiMultimodalService(
        GatewayConfig config,
        MediaCacheStore mediaCache,
        ILogger<GeminiMultimodalService> logger,
        HttpClient? httpClient = null)
    {
        _config = config;
        _mediaCache = mediaCache;
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<string> AnalyzeVisionAsync(
        string prompt,
        string? imagePath,
        string? imageUrl,
        string? mimeType,
        string? model,
        CancellationToken ct)
    {
        var (bytes, resolvedMimeType) = await LoadBinaryAsync(
            imagePath,
            imageUrl,
            mimeType,
            defaultMimeType: "image/png",
            maxBytes: null,
            ct);
        var requestBody = BuildVisionRequest(prompt, bytes, resolvedMimeType);
        using var response = await _httpClient.PostAsync(
            BuildGenerateContentUri(model ?? _config.Multimodal.VisionModel),
            new StringContent(requestBody, Encoding.UTF8, "application/json"),
            ct);

        var payload = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gemini vision request failed: {(int)response.StatusCode} {payload}");

        return ExtractText(payload) ?? "No vision response was returned.";
    }

    public async Task<AudioTranscriptionResult> TranscribeAudioAsync(
        string audioUrl,
        string? mimeType,
        string? model,
        int maxAudioBytes,
        CancellationToken ct)
    {
        var (bytes, resolvedMimeType) = await LoadBinaryAsync(
            path: null,
            url: audioUrl,
            mimeType,
            defaultMimeType: "audio/ogg",
            maxBytes: maxAudioBytes,
            ct);
        var requestBody = BuildTranscriptionRequest(bytes, resolvedMimeType);
        using var response = await _httpClient.PostAsync(
            BuildGenerateContentUri(model ?? _config.Multimodal.Transcription.Model),
            new StringContent(requestBody, Encoding.UTF8, "application/json"),
            ct);

        var payload = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gemini transcription request failed: {(int)response.StatusCode} {payload}");

        var transcript = ExtractText(payload)?.Trim();
        if (string.IsNullOrWhiteSpace(transcript))
            throw new InvalidOperationException("Gemini did not return a transcript.");

        return new AudioTranscriptionResult
        {
            Provider = "gemini",
            Text = transcript,
            MimeType = resolvedMimeType,
            SizeBytes = bytes.Length
        };
    }

    public async Task<TextToSpeechSynthesisResult> SynthesizeSpeechAsync(
        string text,
        string? voiceName,
        string? model,
        CancellationToken ct)
    {
        var requestBody = BuildSpeechRequest(text, voiceName ?? _config.Multimodal.TextToSpeech.VoiceName);
        using var response = await _httpClient.PostAsync(
            BuildGenerateContentUri(model ?? _config.Multimodal.TextToSpeech.Model),
            new StringContent(requestBody, Encoding.UTF8, "application/json"),
            ct);

        var payload = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gemini speech request failed: {(int)response.StatusCode} {payload}");

        var (audioBytes, mimeType) = ExtractInlineData(payload)
            ?? throw new InvalidOperationException("Gemini did not return audio data.");

        var base64 = Convert.ToBase64String(audioBytes.ToArray());
        var dataUrl = $"data:{mimeType};base64,{base64}";
        var asset = await _mediaCache.SaveAsync(audioBytes, mimeType, "speech.wav", ct);
        return new TextToSpeechSynthesisResult
        {
            Provider = "gemini",
            Asset = asset,
            Marker = $"[AUDIO_URL:{dataUrl}]",
            DataUrl = dataUrl
        };
    }

    private Uri BuildGenerateContentUri(string model)
    {
        var apiKey = SecretResolver.Resolve(_config.Llm.ApiKey)
            ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY")
            ?? _config.Llm.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("A Google Gemini API key is required for multimodal features.");

        return new Uri($"{ApiBase}{Uri.EscapeDataString(model)}:generateContent?key={Uri.EscapeDataString(apiKey)}", UriKind.Absolute);
    }

    private static string BuildVisionRequest(string prompt, ReadOnlyMemory<byte> bytes, string mimeType)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WritePropertyName("contents");
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WriteString("role", "user");
        writer.WritePropertyName("parts");
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WriteString("text", prompt);
        writer.WriteEndObject();
        writer.WriteStartObject();
        writer.WritePropertyName("inline_data");
        writer.WriteStartObject();
        writer.WriteString("mime_type", mimeType);
        writer.WriteString("data", Convert.ToBase64String(bytes.Span));
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string BuildTranscriptionRequest(ReadOnlyMemory<byte> bytes, string mimeType)
    {
        const string Prompt = "Transcribe this voice memo. Return only the transcript text, preserving the speaker's wording as closely as possible.";
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WritePropertyName("contents");
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WriteString("role", "user");
        writer.WritePropertyName("parts");
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WriteString("text", Prompt);
        writer.WriteEndObject();
        writer.WriteStartObject();
        writer.WritePropertyName("inline_data");
        writer.WriteStartObject();
        writer.WriteString("mime_type", mimeType);
        writer.WriteString("data", Convert.ToBase64String(bytes.Span));
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string BuildSpeechRequest(string text, string voiceName)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WritePropertyName("contents");
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WritePropertyName("parts");
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WriteString("text", text);
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WritePropertyName("generationConfig");
        writer.WriteStartObject();
        writer.WritePropertyName("responseModalities");
        writer.WriteStartArray();
        writer.WriteStringValue("AUDIO");
        writer.WriteEndArray();
        writer.WritePropertyName("speechConfig");
        writer.WriteStartObject();
        writer.WritePropertyName("voiceConfig");
        writer.WriteStartObject();
        writer.WritePropertyName("prebuiltVoiceConfig");
        writer.WriteStartObject();
        writer.WriteString("voiceName", voiceName);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private async Task<(ReadOnlyMemory<byte> Data, string MimeType)> LoadBinaryAsync(
        string? path,
        string? url,
        string? mimeType,
        string? defaultMimeType,
        int? maxBytes,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            var fullPath = Path.GetFullPath(path);
            EnforceMaxBytes(new FileInfo(fullPath).Length, maxBytes);
            var bytes = await File.ReadAllBytesAsync(fullPath, ct);
            return (bytes, mimeType ?? GuessMimeType(fullPath));
        }

        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("image_url or image_path is required.");

        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var (dataBytes, dataMimeType) = ParseDataUrl(url);
            EnforceMaxBytes(dataBytes.Length, maxBytes);
            return (dataBytes, mimeType ?? dataMimeType ?? defaultMimeType ?? "application/octet-stream");
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            var fullPath = Path.GetFullPath(uri.LocalPath);
            EnforceMaxBytes(new FileInfo(fullPath).Length, maxBytes);
            var bytes = await File.ReadAllBytesAsync(fullPath, ct);
            return (bytes, mimeType ?? GuessMimeType(fullPath));
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var remoteUri) ||
            (remoteUri.Scheme != Uri.UriSchemeHttp && remoteUri.Scheme != Uri.UriSchemeHttps))
        {
            var fullPath = Path.GetFullPath(url);
            EnforceMaxBytes(new FileInfo(fullPath).Length, maxBytes);
            var bytes = await File.ReadAllBytesAsync(fullPath, ct);
            return (bytes, mimeType ?? GuessMimeType(fullPath));
        }

        using var response = await _httpClient.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length)
            EnforceMaxBytes(length, maxBytes);
        var bytesFromUrl = await response.Content.ReadAsByteArrayAsync(ct);
        EnforceMaxBytes(bytesFromUrl.Length, maxBytes);
        var responseMimeType = response.Content.Headers.ContentType?.MediaType;
        return (bytesFromUrl, mimeType ?? responseMimeType ?? defaultMimeType ?? "image/png");
    }

    private static (byte[] Data, string? MimeType) ParseDataUrl(string url)
    {
        var commaIndex = url.IndexOf(',', StringComparison.Ordinal);
        if (commaIndex < 0)
            throw new InvalidOperationException("Unsupported audio data URL.");

        var metadata = url[5..commaIndex];
        var payload = url[(commaIndex + 1)..];
        var metadataParts = metadata.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var mimeType = metadataParts.FirstOrDefault(static part => part.Contains('/', StringComparison.Ordinal));
        var isBase64 = metadataParts.Any(static part => part.Equals("base64", StringComparison.OrdinalIgnoreCase));
        return isBase64
            ? (Convert.FromBase64String(payload), mimeType)
            : (Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload)), mimeType);
    }

    private static void EnforceMaxBytes(long sizeBytes, int? maxBytes)
    {
        if (maxBytes is > 0 && sizeBytes > maxBytes.Value)
            throw new InvalidOperationException($"Audio is too large to transcribe ({sizeBytes} bytes > {maxBytes.Value} bytes).");
    }

    private static string? ExtractText(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var content) ||
                !content.TryGetProperty("parts", out var parts) ||
                parts.ValueKind != JsonValueKind.Array)
                continue;

            var sb = new StringBuilder();
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    sb.Append(text.GetString());
            }

            if (sb.Length > 0)
                return sb.ToString();
        }

        return null;
    }

    private static (ReadOnlyMemory<byte> Data, string MimeType)? ExtractInlineData(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var content) ||
                !content.TryGetProperty("parts", out var parts) ||
                parts.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var part in parts.EnumerateArray())
            {
                if (!part.TryGetProperty("inlineData", out var inlineData) && !part.TryGetProperty("inline_data", out inlineData))
                    continue;

                var mimeType = inlineData.TryGetProperty("mimeType", out var mime) ? mime.GetString()
                    : inlineData.TryGetProperty("mime_type", out mime) ? mime.GetString()
                    : "audio/wav";
                var data = inlineData.TryGetProperty("data", out var bytesProp) ? bytesProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(data))
                    continue;

                return (Convert.FromBase64String(data), mimeType ?? "audio/wav");
            }
        }

        return null;
    }

    private static string GuessMimeType(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".ogg" or ".oga" => "audio/ogg",
            ".opus" => "audio/ogg",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".m4a" => "audio/mp4",
            ".webm" => "audio/webm",
            _ => "application/octet-stream"
        };
}

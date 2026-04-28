using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal sealed class AudioTranscriptionRequest
{
    public required string AudioUrl { get; init; }
    public string? MimeType { get; init; }
    public string? FileName { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public int MaxAudioBytes { get; init; }
    public IReadOnlyList<string> AllowedLocalFileRoots { get; init; } = [];
}

internal sealed class AudioTranscriptionResult
{
    public required string Provider { get; init; }
    public required string Text { get; init; }
    public string? MimeType { get; init; }
    public long SizeBytes { get; init; }
}

internal interface IAudioTranscriptionProvider
{
    string Name { get; }
    Task<AudioTranscriptionResult> TranscribeAsync(AudioTranscriptionRequest request, CancellationToken ct);
}

internal sealed class AudioTranscriptionService
{
    private readonly GatewayConfig _config;
    private readonly ILogger<AudioTranscriptionService> _logger;
    private readonly IReadOnlyDictionary<string, IAudioTranscriptionProvider> _providers;

    public AudioTranscriptionService(
        GatewayConfig config,
        IEnumerable<IAudioTranscriptionProvider> providers,
        ILogger<AudioTranscriptionService> logger)
    {
        _config = config;
        _logger = logger;
        _providers = providers.ToDictionary(static provider => provider.Name, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<AudioTranscriptionResult> TranscribeAsync(InboundMessage message, CancellationToken ct)
    {
        if (!_config.Multimodal.Enabled || !_config.Multimodal.Transcription.Enabled)
            throw new InvalidOperationException("Voice transcription is disabled by configuration.");

        if (!IsAudioMessage(message))
            throw new InvalidOperationException("Only inbound audio messages can be transcribed.");

        var providerName = ResolveProviderName(_config.Multimodal.Transcription.Provider);

        if (!_providers.TryGetValue(providerName, out var provider))
            throw new InvalidOperationException($"Unknown audio transcription provider '{providerName}'.");

        var timeoutSeconds = Math.Max(1, _config.Multimodal.Transcription.TimeoutSeconds);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            return await provider.TranscribeAsync(new AudioTranscriptionRequest
            {
                AudioUrl = message.MediaUrl!,
                MimeType = message.MediaMimeType,
                FileName = message.MediaFileName,
                Provider = providerName,
                Model = NormalizeOptional(_config.Multimodal.Transcription.Model),
                MaxAudioBytes = Math.Max(1, _config.Multimodal.Transcription.MaxAudioBytes),
                AllowedLocalFileRoots = BuildAllowedLocalFileRoots(_config)
            }, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogWarning("Voice transcription timed out after {TimeoutSeconds}s.", timeoutSeconds);
            throw new TimeoutException($"Voice transcription timed out after {timeoutSeconds}s.");
        }
    }

    public IReadOnlyList<string> ListProviders()
        => _providers.Keys.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase).ToArray();

    public static bool IsAudioMessage(InboundMessage message)
        => string.Equals(message.MediaType, "audio", StringComparison.OrdinalIgnoreCase)
           && !string.IsNullOrWhiteSpace(message.MediaUrl);

    public static string ResolveProviderName(string? provider)
        => string.IsNullOrWhiteSpace(provider) ? "gemini" : provider.Trim();

    public static bool ShouldDegrade(string? failureMode)
    {
        var normalized = failureMode?.Trim();
        return !string.Equals(normalized, "strict", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(normalized, "fail", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(normalized, "throw", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<string> BuildAllowedLocalFileRoots(GatewayConfig config)
    {
        var roots = new List<string>();
        AddRoot(roots, config.Multimodal.MediaCachePath);

        var worker = config.Channels.WhatsApp.FirstPartyWorker;
        AddRoot(roots, worker.MediaCachePath);
        if (!string.IsNullOrWhiteSpace(worker.StoragePath))
            AddRoot(roots, Path.Combine(worker.StoragePath, "media-cache"));

        foreach (var account in worker.Accounts)
            AddRoot(roots, account.MediaCachePath);

        return roots
            .Select(static root => Path.GetFullPath(root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddRoot(List<string> roots, string? root)
    {
        if (!string.IsNullOrWhiteSpace(root))
            roots.Add(root);
    }
}

internal sealed class GeminiAudioTranscriptionProvider : IAudioTranscriptionProvider
{
    private readonly GeminiMultimodalService _gemini;

    public GeminiAudioTranscriptionProvider(GeminiMultimodalService gemini)
    {
        _gemini = gemini;
    }

    public string Name => "gemini";

    public Task<AudioTranscriptionResult> TranscribeAsync(AudioTranscriptionRequest request, CancellationToken ct)
        => _gemini.TranscribeAudioAsync(
            request.AudioUrl,
            request.MimeType,
            request.Model,
            request.MaxAudioBytes,
            request.AllowedLocalFileRoots,
            ct);
}

internal static class VoiceMemoTranscriptionText
{
    public static string PrependTranscript(string messageText, AudioTranscriptionResult result)
    {
        var transcript = result.Text.Trim();
        var block = $"[VOICE_TRANSCRIPT provider={result.Provider}]\n{transcript}\n[/VOICE_TRANSCRIPT]";
        return string.IsNullOrWhiteSpace(messageText)
            ? block
            : $"{block}\n{messageText}";
    }

    public static string AppendUnavailable(string messageText, string reason)
    {
        var marker = $"[VOICE_TRANSCRIPTION_UNAVAILABLE: {reason}]";
        return string.IsNullOrWhiteSpace(messageText)
            ? marker
            : $"{messageText}\n{marker}";
    }

    public static string FailureReason(Exception exception)
        => exception switch
        {
            TimeoutException => "timeout",
            InvalidOperationException invalid when invalid.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase) => "disabled",
            InvalidOperationException invalid when invalid.Message.Contains("unknown audio transcription provider", StringComparison.OrdinalIgnoreCase) => "unsupported_provider",
            InvalidOperationException invalid when invalid.Message.Contains("too large", StringComparison.OrdinalIgnoreCase) => "audio_too_large",
            InvalidOperationException invalid when invalid.Message.Contains("unsupported", StringComparison.OrdinalIgnoreCase) => "unsupported_audio",
            _ => "transcription_failed"
        };
}

namespace OpenClaw.Core.Models;

public sealed class MultimodalConfig
{
    public bool Enabled { get; set; } = true;
    public string MediaCachePath { get; set; } = "./memory/media-cache";
    public string LiveProvider { get; set; } = "gemini";
    public string VisionProvider { get; set; } = "gemini";
    public string VisionModel { get; set; } = "gemini-2.5-flash";
    public AudioTranscriptionConfig Transcription { get; set; } = new();
    public VideoProcessingConfig Video { get; set; } = new();
    public TextToSpeechConfig TextToSpeech { get; set; } = new();
    public GeminiLiveConfig GeminiLive { get; set; } = new();
    public ElevenLabsConfig ElevenLabs { get; set; } = new();

    /// <summary>
    /// How chat attachments (images/files a user sent) are carried in session history and inlined
    /// into provider requests. See
    /// docs/superpowers/specs/2026-10-19-attachment-by-reference-design.md.
    /// </summary>
    public AttachmentConfig Attachments { get; set; } = new();

    /// <summary>
    /// Limits on what <see cref="MediaCachePath"/> is allowed to accumulate.
    /// </summary>
    public MediaCacheConfig MediaCache { get; set; } = new();
}

/// <summary>
/// Bounds on the media cache directory: how large one asset may be and how much the directory may
/// hold before old assets are removed.
/// </summary>
/// <remarks>
/// <para>
/// The cache is written by several paths — channel uploads, provider-generated audio, extracted video
/// frames, files an agent publishes — and each one used to write whatever it was handed. Nothing ever
/// removed anything either, so the directory grew for the lifetime of the deployment.
/// </para>
/// <para>
/// <see cref="MaxFileBytes"/> is a policy ceiling: an asset bigger than this is refused, and the caller
/// degrades (the attachment stays a path, or the upload answers 413). <see cref="RetentionDays"/> and
/// <see cref="MaxTotalBytes"/> are enforced by a periodic sweep, not at write time — a full cache must
/// not start rejecting writes that are individually fine.
/// </para>
/// <para>
/// Setting <see cref="RetentionDays"/> to zero or less disables the sweep entirely, which is the
/// pre-existing behaviour. That is a deliberate escape hatch for deployments that archive the directory
/// themselves and would rather OpenClaw not delete anything.
/// </para>
/// </remarks>
public sealed class MediaCacheConfig
{
    /// <summary>
    /// Largest single asset accepted into the cache. Applies to every writer, and is also the ceiling
    /// the upload endpoint advertises.
    /// </summary>
    public long MaxFileBytes { get; set; } = 50L * 1024 * 1024;

    /// <summary>
    /// How long an asset is kept, measured from when it was written. A session that still references an
    /// expired asset keeps working: the reference falls back to a placeholder once the file is gone.
    /// </summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>
    /// Total bytes of asset data the directory may hold. When the sweep finds more, it removes the
    /// oldest assets first until the total fits. Metadata sidecars are not counted: they are a few
    /// hundred bytes each and always removed together with their asset.
    /// </summary>
    public long MaxTotalBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>How often the sweep runs. Only used when <see cref="RetentionDays"/> is positive.</summary>
    public int SweepIntervalMinutes { get; set; } = 60;
}

/// <summary>
/// Attachment reference / inlining policy.
/// </summary>
/// <remarks>
/// Every value here is a cost ceiling, not a capability switch: raising them makes each provider
/// request carry more media. <see cref="ReferenceMode"/> is the only escape hatch — setting it false
/// restores the pre-reference behaviour (markers stay in prose, every occurrence is sent as a URI).
/// </remarks>
public sealed class AttachmentConfig
{
    /// <summary>
    /// When false, attachments are left as marker text in the turn and rendered exactly as before the
    /// reference work. Intended as a rollback switch, not a tuning knob.
    /// </summary>
    public bool ReferenceMode { get; set; } = true;

    /// <summary>
    /// How many of the most recent user turns get their attachments inlined. Older turns keep a text
    /// placeholder. Two covers "look at the picture again"; more only raises the per-request image
    /// billing ceiling.
    /// </summary>
    public int InlineRecentUserTurns { get; set; } = 2;

    /// <summary>
    /// Total bytes of locally-read attachment data allowed in one provider request. Remote URLs are
    /// not counted (their size is unknown before fetching and the provider fetches them itself).
    /// </summary>
    public long MaxInlineBytesPerRequest { get; set; } = 20L * 1024 * 1024;

    /// <summary>Maximum attachments inlined from a single turn; the rest become placeholders.</summary>
    public int MaxInlineAttachmentsPerTurn { get; set; } = 4;

    /// <summary>Drop a repeated attachment (same source) after its first inline in a request.</summary>
    public bool DeduplicateBySource { get; set; } = true;

    /// <summary>
    /// <c>placeholder</c> (default): an unreadable attachment becomes a text note and the turn still
    /// runs. <c>fail</c>: the turn fails, for deployments that would rather stop than answer blind.
    /// </summary>
    public string FailureMode { get; set; } = "placeholder";
}

public sealed class AudioTranscriptionConfig
{
    public bool Enabled { get; set; } = true;
    public string Provider { get; set; } = "gemini";
    public string Model { get; set; } = "gemini-2.5-flash";
    public int MaxAudioBytes { get; set; } = 25 * 1024 * 1024;
    public int TimeoutSeconds { get; set; } = 30;
    public bool InjectAudioMarker { get; set; } = true;
    public string FailureMode { get; set; } = "degrade";
}

public sealed class VideoProcessingConfig
{
    public bool Enabled { get; set; } = true;
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    public int MaxVideoBytes { get; set; } = 100 * 1024 * 1024;
    public int MaxDurationSeconds { get; set; } = 120;
    public int MaxFrames { get; set; } = 8;
    public double FrameIntervalSeconds { get; set; } = 5;
    public int FrameWidth { get; set; } = 768;
    public bool ExtractAudioTranscript { get; set; }
    public string FailureMode { get; set; } = "degrade";
}

public sealed class TextToSpeechConfig
{
    public bool Enabled { get; set; } = true;
    public string Provider { get; set; } = "gemini";
    public string Model { get; set; } = "gemini-2.5-flash-preview-tts";
    public string VoiceName { get; set; } = "Kore";
    public string? VoiceId { get; set; }
}

public sealed class GeminiLiveConfig
{
    public bool Enabled { get; set; } = true;
    public string Model { get; set; } = "gemini-2.0-flash-live-001";
    public string Endpoint { get; set; } = "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";
    public string[] ResponseModalities { get; set; } = ["TEXT"];
    public string? VoiceName { get; set; }
    public bool InputTranscription { get; set; } = true;
    public bool OutputTranscription { get; set; } = true;
}

public sealed class ElevenLabsConfig
{
    public bool Enabled { get; set; } = true;
    public string Endpoint { get; set; } = "https://api.elevenlabs.io";
    public string? ApiKey { get; set; }
    public string VoiceId { get; set; } = "JBFqnCBsd6RMkjVDRZzb";
    public string Model { get; set; } = "eleven_multilingual_v2";
    public string OutputFormat { get; set; } = "mp3_44100_128";
}

public sealed class StoredMediaAsset
{
    public required string Id { get; init; }
    public required string MediaType { get; init; }
    public string FileName { get; init; } = "";
    public string Path { get; init; } = "";
    public long SizeBytes { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class LiveSessionOpenRequest
{
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string[]? ResponseModalities { get; init; }
    public string? SystemInstruction { get; init; }
    public string? VoiceName { get; init; }
}

public sealed class LiveSessionOpened
{
    public required string SessionId { get; init; }
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public string[] ResponseModalities { get; init; } = [];
}

public sealed class LiveClientEnvelope
{
    public string Type { get; init; } = "";
    public string? Text { get; init; }
    public string? Base64Data { get; init; }
    public string? MimeType { get; init; }
    public bool TurnComplete { get; init; }
}

public sealed class LiveServerEnvelope
{
    public string Type { get; init; } = "";
    public string? Text { get; init; }
    public string? Base64Data { get; init; }
    public string? MimeType { get; init; }
    public bool TurnComplete { get; init; }
    public bool Interrupted { get; init; }
    public string? Error { get; init; }
}

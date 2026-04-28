namespace OpenClaw.Core.Models;

public sealed class MultimodalConfig
{
    public bool Enabled { get; set; } = true;
    public string MediaCachePath { get; set; } = "./memory/media-cache";
    public string LiveProvider { get; set; } = "gemini";
    public string VisionProvider { get; set; } = "gemini";
    public string VisionModel { get; set; } = "gemini-2.5-flash";
    public AudioTranscriptionConfig Transcription { get; set; } = new();
    public TextToSpeechConfig TextToSpeech { get; set; } = new();
    public GeminiLiveConfig GeminiLive { get; set; } = new();
    public ElevenLabsConfig ElevenLabs { get; set; } = new();
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

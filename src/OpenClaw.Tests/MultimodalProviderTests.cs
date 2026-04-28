using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using Xunit;

namespace OpenClaw.Tests;

public sealed class MultimodalProviderTests
{
    [Fact]
    public async Task AudioTranscriptionService_Disabled_Throws()
    {
        var config = new GatewayConfig();
        config.Multimodal.Transcription.Enabled = false;
        var service = new AudioTranscriptionService(
            config,
            [new StubAudioTranscriptionProvider("gemini", "ignored")],
            NullLogger<AudioTranscriptionService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TranscribeAsync(new InboundMessage
            {
                ChannelId = "whatsapp",
                SenderId = "sender",
                Text = "",
                MediaType = "audio",
                MediaUrl = "data:audio/ogg;base64,AQID"
            }, CancellationToken.None));

        Assert.Contains("disabled", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AudioTranscriptionService_UsesConfiguredProvider()
    {
        var config = new GatewayConfig();
        config.Multimodal.Transcription.Provider = "other";
        var providerA = new StubAudioTranscriptionProvider("gemini", "wrong");
        var providerB = new StubAudioTranscriptionProvider("other", "hello from audio");
        var service = new AudioTranscriptionService(
            config,
            [providerA, providerB],
            NullLogger<AudioTranscriptionService>.Instance);

        var result = await service.TranscribeAsync(new InboundMessage
        {
            ChannelId = "whatsapp",
            SenderId = "sender",
            Text = "",
            MediaType = "audio",
            MediaUrl = "data:audio/ogg;base64,AQID"
        }, CancellationToken.None);

        Assert.False(providerA.WasCalled);
        Assert.True(providerB.WasCalled);
        Assert.Equal("other", result.Provider);
        Assert.Equal("hello from audio", result.Text);
    }

    [Fact]
    public async Task GeminiAudioTranscriptionProvider_ReturnsTranscript()
    {
        var config = new GatewayConfig();
        config.Llm.ApiKey = "raw:test-key";
        config.Multimodal.Transcription.Model = "gemini-test";
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var handler = new CallbackHttpMessageHandler(request =>
        {
            captured = request;
            capturedBody = request.Content is null
                ? null
                : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"candidates":[{"content":{"parts":[{"text":"Meet me at 4."}]}}]}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var gemini = new GeminiMultimodalService(
            config,
            new MediaCacheStore(Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("N"))),
            NullLogger<GeminiMultimodalService>.Instance,
            new HttpClient(handler));
        var provider = new GeminiAudioTranscriptionProvider(gemini);

        var result = await provider.TranscribeAsync(new AudioTranscriptionRequest
        {
            AudioUrl = "data:audio/ogg;base64,AQID",
            MimeType = "audio/ogg",
            Model = "gemini-test",
            MaxAudioBytes = 1024
        }, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-test:generateContent?key=test-key", captured!.RequestUri!.ToString());
        Assert.Contains("\"mime_type\":\"audio/ogg\"", capturedBody, StringComparison.Ordinal);
        Assert.Contains("\"data\":\"AQID\"", capturedBody, StringComparison.Ordinal);
        Assert.Equal("gemini", result.Provider);
        Assert.Equal("Meet me at 4.", result.Text);
        Assert.Equal("audio/ogg", result.MimeType);
        Assert.Equal(3, result.SizeBytes);
    }

    [Fact]
    public async Task GeminiAudioTranscriptionProvider_RejectsOversizedAudio()
    {
        var config = new GatewayConfig();
        config.Llm.ApiKey = "raw:test-key";
        var gemini = new GeminiMultimodalService(
            config,
            new MediaCacheStore(Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("N"))),
            NullLogger<GeminiMultimodalService>.Instance,
            new HttpClient(new CallbackHttpMessageHandler(_ => throw new InvalidOperationException("HTTP should not be called."))));
        var provider = new GeminiAudioTranscriptionProvider(gemini);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.TranscribeAsync(new AudioTranscriptionRequest
            {
                AudioUrl = "data:audio/ogg;base64,AQID",
                MimeType = "audio/ogg",
                Model = "gemini-test",
                MaxAudioBytes = 2
            }, CancellationToken.None));

        Assert.Contains("too large", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VoiceMemoTranscriptionText_PrependsTranscriptAndUnavailableMarker()
    {
        var transcribed = VoiceMemoTranscriptionText.PrependTranscript(
            "original text",
            new AudioTranscriptionResult
            {
                Provider = "gemini",
                Text = "transcribed text",
                MimeType = "audio/ogg",
                SizeBytes = 42
            });

        Assert.Equal(
            "[VOICE_TRANSCRIPT provider=gemini]\ntranscribed text\n[/VOICE_TRANSCRIPT]\noriginal text",
            transcribed);

        Assert.Equal(
            "original text\n[VOICE_TRANSCRIPTION_UNAVAILABLE: timeout]",
            VoiceMemoTranscriptionText.AppendUnavailable("original text", "timeout"));
    }

    [Fact]
    public async Task ElevenLabsProvider_SynthesizeSpeechAsync_UsesConfiguredEndpointAndReturnsDataUrl()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        var config = new GatewayConfig();
        config.Multimodal.ElevenLabs.Endpoint = "https://example.test";
        config.Multimodal.ElevenLabs.ApiKey = "raw:test-key";
        config.Multimodal.ElevenLabs.VoiceId = "voice123";
        config.Multimodal.ElevenLabs.Model = "eleven_turbo_v2";
        config.Multimodal.ElevenLabs.OutputFormat = "mp3_44100_128";

        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var handler = new CallbackHttpMessageHandler(request =>
        {
            captured = request;
            capturedBody = request.Content is null
                ? null
                : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4])
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");
            return response;
        });
        var provider = new ElevenLabsTextToSpeechProvider(
            config,
            new MediaCacheStore(storagePath),
            new HttpClient(handler));

        var result = await provider.SynthesizeSpeechAsync(new TextToSpeechRequest
        {
            Text = "Hello from ElevenLabs"
        }, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("https://example.test/v1/text-to-speech/voice123?output_format=mp3_44100_128", captured!.RequestUri!.ToString());
        Assert.Equal("test-key", captured.Headers.GetValues("xi-api-key").Single());
        Assert.Contains("Hello from ElevenLabs", capturedBody, StringComparison.Ordinal);
        Assert.Equal("elevenlabs", result.Provider);
        Assert.Equal("audio/mpeg", result.Asset.MediaType);
        Assert.StartsWith("data:audio/mpeg;base64,", result.DataUrl, StringComparison.Ordinal);
        Assert.StartsWith("[AUDIO_URL:data:audio/mpeg;base64,", result.Marker, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveSessionService_UsesRequestedProvider()
    {
        var providerA = new StubLiveSessionProvider("gemini");
        var providerB = new StubLiveSessionProvider("other");
        var service = new LiveSessionService(
            new GatewayConfig(),
            [providerA, providerB],
            NullLogger<LiveSessionService>.Instance);

        using var socket = new ClientWebSocket();
        await service.BridgeAsync(socket, new LiveSessionOpenRequest { Provider = "other" }, CancellationToken.None);

        Assert.False(providerA.WasCalled);
        Assert.True(providerB.WasCalled);
    }

    private sealed class CallbackHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _callback;

        public CallbackHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> callback)
        {
            _callback = callback;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_callback(request));
    }

    private sealed class StubLiveSessionProvider : ILiveSessionProvider
    {
        public StubLiveSessionProvider(string name)
        {
            Name = name;
        }

        public string Name { get; }
        public bool WasCalled { get; private set; }

        public Task BridgeAsync(WebSocket clientSocket, LiveSessionOpenRequest request, CancellationToken ct)
        {
            WasCalled = true;
            return Task.CompletedTask;
        }
    }

    private sealed class StubAudioTranscriptionProvider : IAudioTranscriptionProvider
    {
        private readonly string _text;

        public StubAudioTranscriptionProvider(string name, string text)
        {
            Name = name;
            _text = text;
        }

        public string Name { get; }
        public bool WasCalled { get; private set; }

        public Task<AudioTranscriptionResult> TranscribeAsync(AudioTranscriptionRequest request, CancellationToken ct)
        {
            WasCalled = true;
            return Task.FromResult(new AudioTranscriptionResult
            {
                Provider = Name,
                Text = _text,
                MimeType = request.MimeType,
                SizeBytes = 0
            });
        }
    }
}

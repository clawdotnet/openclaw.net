using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal sealed class VideoFrameExtractionRequest
{
    public required string SourceLabel { get; init; }
    public required string MediaType { get; init; }
    public Uri? Uri { get; init; }
    public byte[]? Data { get; init; }
    public string? FileName { get; init; }
}

internal sealed class VideoFrameExtractionResult
{
    public bool Succeeded { get; init; }
    public string? Issue { get; init; }
    public string SourceLabel { get; init; } = "";
    public double? DurationSeconds { get; init; }
    public IReadOnlyList<VideoFrameData> Frames { get; init; } = [];
    public AudioTranscriptionResult? AudioTranscript { get; init; }
}

internal sealed class VideoFrameData
{
    public required int Index { get; init; }
    public TimeSpan? Timestamp { get; init; }
    public required string DataUrl { get; init; }
    public required StoredMediaAsset Asset { get; init; }
}

internal interface IVideoFrameExtractionService
{
    Task<VideoFrameExtractionResult> ExtractFramesAsync(VideoFrameExtractionRequest request, CancellationToken ct);
}

internal sealed class VideoFrameExtractionService : IVideoFrameExtractionService
{
    private readonly GatewayConfig _config;
    private readonly MediaCacheStore _mediaCache;
    private readonly ILogger<VideoFrameExtractionService> _logger;
    private readonly AudioTranscriptionService? _audioTranscription;

    public VideoFrameExtractionService(
        GatewayConfig config,
        MediaCacheStore mediaCache,
        ILogger<VideoFrameExtractionService> logger,
        AudioTranscriptionService? audioTranscription = null)
    {
        _config = config;
        _mediaCache = mediaCache;
        _logger = logger;
        _audioTranscription = audioTranscription;
    }

    public async Task<VideoFrameExtractionResult> ExtractFramesAsync(VideoFrameExtractionRequest request, CancellationToken ct)
    {
        var video = _config.Multimodal.Video;
        if (!_config.Multimodal.Enabled || !video.Enabled)
            return Fail(request, "video preprocessing is disabled");

        var workingRoot = Path.Combine(Path.GetFullPath(_config.Multimodal.MediaCachePath), "video-processing", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingRoot);

        try
        {
            var inputPath = await MaterializeInputAsync(request, workingRoot, video, ct);
            var duration = await ProbeDurationAsync(video.FfprobePath, inputPath, ct);
            if (duration.HasValue && duration.Value > Math.Max(1, video.MaxDurationSeconds))
                return Fail(request, $"video duration {duration.Value:F1}s exceeds limit {Math.Max(1, video.MaxDurationSeconds)}s");

            var outputDirectory = Path.Combine(workingRoot, "frames");
            Directory.CreateDirectory(outputDirectory);
            await ExtractFramesWithFfmpegAsync(video, inputPath, outputDirectory, ct);

            var frames = await LoadExtractedFramesAsync(outputDirectory, video, ct);
            if (frames.Count == 0)
                return Fail(request, "ffmpeg did not produce any frames");

            var transcript = video.ExtractAudioTranscript
                ? await TryExtractAudioTranscriptAsync(video, inputPath, workingRoot, ct)
                : null;

            return new VideoFrameExtractionResult
            {
                Succeeded = true,
                SourceLabel = request.SourceLabel,
                DurationSeconds = duration,
                Frames = frames,
                AudioTranscript = transcript
            };
        }
        catch (Exception ex) when (AudioTranscriptionService.ShouldDegrade(video.FailureMode))
        {
            _logger.LogWarning(ex, "Video frame extraction degraded for {SourceLabel}.", request.SourceLabel);
            return Fail(request, VideoFailureReason(ex));
        }
        finally
        {
            TryDeleteDirectory(workingRoot);
        }
    }

    private async Task<string> MaterializeInputAsync(
        VideoFrameExtractionRequest request,
        string workingRoot,
        VideoProcessingConfig video,
        CancellationToken ct)
    {
        if (request.Data is { Length: > 0 } bytes)
        {
            EnforceVideoSize(bytes.Length, video);
            var path = Path.Combine(workingRoot, "input" + GuessExtension(request.MediaType, request.FileName));
            await File.WriteAllBytesAsync(path, bytes, ct);
            return path;
        }

        if (request.Uri is null)
            throw new InvalidOperationException("Video input did not include a URI or data payload.");

        if (request.Uri.IsFile)
        {
            var localPath = Path.GetFullPath(request.Uri.LocalPath);
            if (!File.Exists(localPath))
                throw new InvalidOperationException($"Video file was not found: {localPath}");

            EnforceVideoSize(new FileInfo(localPath).Length, video);
            return localPath;
        }

        var uriText = request.Uri.ToString();
        if (uriText.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var (data, mediaType) = ParseDataUrl(uriText, request.MediaType);
            EnforceVideoSize(data.Length, video);
            var path = Path.Combine(workingRoot, "input" + GuessExtension(mediaType ?? request.MediaType, request.FileName));
            await File.WriteAllBytesAsync(path, data, ct);
            return path;
        }

        throw new InvalidOperationException("Remote video URLs are not accepted by the deterministic embedded video preprocessor. Provide a local file URI or data content.");
    }

    private static async Task<double?> ProbeDurationAsync(string ffprobePath, string inputPath, CancellationToken ct)
    {
        var result = await RunProcessAsync(
            ffprobePath,
            "ffprobe",
            [
                "-v", "error",
                "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1",
                inputPath
            ],
            ct);

        if (result.ExitCode != 0)
            throw new InvalidOperationException($"ffprobe failed: {TrimProcessOutput(result)}");

        var text = result.StandardOutput.Trim();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : null;
    }

    private static Task ExtractFramesWithFfmpegAsync(
        VideoProcessingConfig video,
        string inputPath,
        string outputDirectory,
        CancellationToken ct)
    {
        var maxFrames = Math.Max(1, video.MaxFrames);
        var interval = Math.Max(0.1, video.FrameIntervalSeconds);
        var width = Math.Max(64, video.FrameWidth);
        var framePattern = Path.Combine(outputDirectory, "frame-%03d.jpg");
        var filter = $"fps=1/{interval.ToString(CultureInfo.InvariantCulture)},scale={width}:-1:force_original_aspect_ratio=decrease";
        return RunFfmpegOrThrowAsync(
            video.FfmpegPath,
            [
                "-hide_banner",
                "-loglevel", "error",
                "-y",
                "-i", inputPath,
                "-vf", filter,
                "-frames:v", maxFrames.ToString(CultureInfo.InvariantCulture),
                framePattern
            ],
            ct);
    }

    private async Task<List<VideoFrameData>> LoadExtractedFramesAsync(
        string outputDirectory,
        VideoProcessingConfig video,
        CancellationToken ct)
    {
        var interval = Math.Max(0.1, video.FrameIntervalSeconds);
        var frames = new List<VideoFrameData>();
        foreach (var path in Directory.EnumerateFiles(outputDirectory, "*.jpg").OrderBy(static item => item, StringComparer.OrdinalIgnoreCase))
        {
            var bytes = await File.ReadAllBytesAsync(path, ct);
            var asset = await _mediaCache.SaveAsync(bytes, "image/jpeg", Path.GetFileName(path), ct);
            frames.Add(new VideoFrameData
            {
                Index = frames.Count,
                Timestamp = TimeSpan.FromSeconds(frames.Count * interval),
                DataUrl = $"data:image/jpeg;base64,{Convert.ToBase64String(bytes)}",
                Asset = asset
            });
        }

        return frames;
    }

    private async Task<AudioTranscriptionResult?> TryExtractAudioTranscriptAsync(
        VideoProcessingConfig video,
        string inputPath,
        string workingRoot,
        CancellationToken ct)
    {
        if (_audioTranscription is null)
        {
            if (AudioTranscriptionService.ShouldDegrade(video.FailureMode))
                return null;

            throw new InvalidOperationException("Video audio transcription is enabled, but no audio transcription service is registered.");
        }

        var audioPath = Path.Combine(workingRoot, "audio.wav");
        try
        {
            await RunFfmpegOrThrowAsync(
                video.FfmpegPath,
                [
                    "-hide_banner",
                    "-loglevel", "error",
                    "-y",
                    "-i", inputPath,
                    "-vn",
                    "-ac", "1",
                    "-ar", "16000",
                    audioPath
                ],
                ct);

            if (!File.Exists(audioPath) || new FileInfo(audioPath).Length == 0)
                return null;

            var bytes = await File.ReadAllBytesAsync(audioPath, ct);
            var asset = await _mediaCache.SaveAsync(bytes, "audio/wav", "video-audio.wav", ct);
            return await _audioTranscription.TranscribeAudioUrlAsync(asset.Path, "audio/wav", asset.FileName, ct);
        }
        catch (IOException) when (AudioTranscriptionService.ShouldDegrade(video.FailureMode))
        {
            return null;
        }
        catch (UnauthorizedAccessException) when (AudioTranscriptionService.ShouldDegrade(video.FailureMode))
        {
            return null;
        }
        catch (InvalidOperationException) when (AudioTranscriptionService.ShouldDegrade(video.FailureMode))
        {
            return null;
        }
    }

    private VideoFrameExtractionResult Fail(VideoFrameExtractionRequest request, string issue)
    {
        if (!AudioTranscriptionService.ShouldDegrade(_config.Multimodal.Video.FailureMode))
            throw new InvalidOperationException(issue);

        return new VideoFrameExtractionResult
        {
            Succeeded = false,
            SourceLabel = request.SourceLabel,
            Issue = issue,
            Frames = []
        };
    }

    private static async Task RunFfmpegOrThrowAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var result = await RunProcessAsync(executable, "ffmpeg", arguments, ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed: {TrimProcessOutput(result)}");
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string executable,
        string defaultExecutable,
        IReadOnlyList<string> arguments,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(executable) ? defaultExecutable : executable,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException($"{startInfo.FileName} did not start.");

        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return new ProcessResult(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw;
        }
    }

    private static void EnforceVideoSize(long size, VideoProcessingConfig video)
    {
        var limit = Math.Max(1, video.MaxVideoBytes);
        if (size > limit)
            throw new InvalidOperationException($"Video is too large to process ({size} bytes > {limit} bytes).");
    }

    private static string VideoFailureReason(Exception exception)
        => exception switch
        {
            System.ComponentModel.Win32Exception => "ffmpeg_or_ffprobe_missing",
            InvalidOperationException invalid when invalid.Message.Contains("too large", StringComparison.OrdinalIgnoreCase) => "video_too_large",
            InvalidOperationException invalid when invalid.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) => "video_not_found",
            InvalidOperationException invalid when invalid.Message.Contains("duration", StringComparison.OrdinalIgnoreCase) => "video_too_long",
            InvalidOperationException invalid when invalid.Message.Contains("Remote video URLs", StringComparison.OrdinalIgnoreCase) => "unsupported_video_url",
            _ => "video_preprocessing_failed"
        };

    private static string TrimProcessOutput(ProcessResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        return string.IsNullOrWhiteSpace(detail) ? $"exit_code={result.ExitCode}" : detail.Trim();
    }

    private static string GuessExtension(string? mediaType, string? fileName)
    {
        var existing = string.IsNullOrWhiteSpace(fileName) ? null : Path.GetExtension(fileName);
        if (!string.IsNullOrWhiteSpace(existing))
            return existing;

        return mediaType?.ToLowerInvariant() switch
        {
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            "video/quicktime" => ".mov",
            _ => ".video"
        };
    }

    private static (byte[] Data, string? MimeType) ParseDataUrl(string url, string? fallbackMediaType)
    {
        var commaIndex = url.IndexOf(',', StringComparison.Ordinal);
        if (commaIndex < 0)
            throw new InvalidOperationException("Invalid data URL.");

        var header = url[..commaIndex];
        var payload = url[(commaIndex + 1)..];
        var isBase64 = header.Contains(";base64", StringComparison.OrdinalIgnoreCase);
        var mediaType = header.Length > "data:".Length
            ? header["data:".Length..].Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            : null;
        var effectiveMediaType = mediaType ?? fallbackMediaType;
        if (!isBase64 && effectiveMediaType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true)
            throw new InvalidOperationException("Video data URLs must be base64 encoded.");

        return (isBase64 ? Convert.FromBase64String(payload) : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload)), mediaType);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Best-effort cleanup only.
        }
        catch (IOException)
        {
            // Best-effort cleanup only.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup only.
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        catch (NotSupportedException)
        {
        }
    }

    private readonly record struct ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}

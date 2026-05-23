using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using Xunit;

namespace OpenClaw.Tests;

public class VideoFrameExtractionServiceTests
{
    [Fact]
    public async Task ExtractFramesAsync_UsesFfprobeAndFfmpegAndCachesOrderedFrames()
    {
        using var temp = new TempDirectory();
        var ffprobe = CreateFakeFfprobe(temp.Path, "12.5");
        var ffmpeg = CreateFakeFfmpeg(temp.Path);
        var config = new GatewayConfig
        {
            Multimodal = new MultimodalConfig
            {
                MediaCachePath = Path.Combine(temp.Path, "media"),
                Video = new VideoProcessingConfig
                {
                    FfprobePath = ffprobe,
                    FfmpegPath = ffmpeg,
                    MaxFrames = 2,
                    FrameIntervalSeconds = 4,
                    FrameWidth = 320,
                    FailureMode = "strict"
                }
            }
        };
        var service = new VideoFrameExtractionService(
            config,
            new MediaCacheStore(config.Multimodal.MediaCachePath),
            NullLogger<VideoFrameExtractionService>.Instance);

        var result = await service.ExtractFramesAsync(
            new VideoFrameExtractionRequest
            {
                SourceLabel = "fixture",
                MediaType = "video/mp4",
                Data = [1, 2, 3, 4],
                FileName = "fixture.mp4"
            },
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(12.5, result.DurationSeconds);
        Assert.Equal(2, result.Frames.Count);
        Assert.Equal("data:image/jpeg;base64,Zmlyc3Q=", result.Frames[0].DataUrl);
        Assert.Equal("data:image/jpeg;base64,c2Vjb25k", result.Frames[1].DataUrl);
        Assert.All(result.Frames, frame => Assert.True(File.Exists(frame.Asset.Path)));
    }

    [Fact]
    public async Task ExtractFramesAsync_DegradesWhenFfmpegIsMissingAndConfiguredToDegrade()
    {
        using var temp = new TempDirectory();
        var ffprobe = CreateFakeFfprobe(temp.Path, "4");
        var config = new GatewayConfig
        {
            Multimodal = new MultimodalConfig
            {
                MediaCachePath = Path.Combine(temp.Path, "media"),
                Video = new VideoProcessingConfig
                {
                    FfprobePath = ffprobe,
                    FfmpegPath = Path.Combine(temp.Path, "missing-ffmpeg"),
                    FailureMode = "degrade"
                }
            }
        };
        var service = new VideoFrameExtractionService(
            config,
            new MediaCacheStore(config.Multimodal.MediaCachePath),
            NullLogger<VideoFrameExtractionService>.Instance);

        var result = await service.ExtractFramesAsync(
            new VideoFrameExtractionRequest
            {
                SourceLabel = "fixture",
                MediaType = "video/mp4",
                Data = [1, 2, 3, 4],
                FileName = "fixture.mp4"
            },
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("ffmpeg_or_ffprobe_missing", result.Issue);
    }

    [Fact]
    public async Task ExtractFramesAsync_ThrowsForOversizedVideosInStrictMode()
    {
        using var temp = new TempDirectory();
        var config = new GatewayConfig
        {
            Multimodal = new MultimodalConfig
            {
                MediaCachePath = Path.Combine(temp.Path, "media"),
                Video = new VideoProcessingConfig
                {
                    MaxVideoBytes = 2,
                    FailureMode = "strict"
                }
            }
        };
        var service = new VideoFrameExtractionService(
            config,
            new MediaCacheStore(config.Multimodal.MediaCachePath),
            NullLogger<VideoFrameExtractionService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExtractFramesAsync(
            new VideoFrameExtractionRequest
            {
                SourceLabel = "fixture",
                MediaType = "video/mp4",
                Data = [1, 2, 3],
                FileName = "fixture.mp4"
            },
            CancellationToken.None));

        Assert.Contains("too large", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractFramesAsync_ReportsMissingLocalVideoBeforeFileInfoFailure()
    {
        using var temp = new TempDirectory();
        var config = new GatewayConfig
        {
            Multimodal = new MultimodalConfig
            {
                MediaCachePath = Path.Combine(temp.Path, "media"),
                Video = new VideoProcessingConfig
                {
                    FailureMode = "degrade"
                }
            }
        };
        var service = new VideoFrameExtractionService(
            config,
            new MediaCacheStore(config.Multimodal.MediaCachePath),
            NullLogger<VideoFrameExtractionService>.Instance);

        var missingPath = Path.Combine(temp.Path, "missing.mp4");
        var result = await service.ExtractFramesAsync(
            new VideoFrameExtractionRequest
            {
                SourceLabel = "missing",
                MediaType = "video/mp4",
                Uri = new UriBuilder { Scheme = Uri.UriSchemeFile, Path = missingPath }.Uri
            },
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("video_not_found", result.Issue);
    }

    [Fact]
    public async Task ExtractFramesAsync_RejectsNonBase64VideoDataUrlsBeforeFfmpeg()
    {
        using var temp = new TempDirectory();
        var config = new GatewayConfig
        {
            Multimodal = new MultimodalConfig
            {
                MediaCachePath = Path.Combine(temp.Path, "media"),
                Video = new VideoProcessingConfig
                {
                    FailureMode = "strict"
                }
            }
        };
        var service = new VideoFrameExtractionService(
            config,
            new MediaCacheStore(config.Multimodal.MediaCachePath),
            NullLogger<VideoFrameExtractionService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExtractFramesAsync(
            new VideoFrameExtractionRequest
            {
                SourceLabel = "inline",
                MediaType = "video/mp4",
                Uri = new Uri("data:video/mp4,not-binary-safe")
            },
            CancellationToken.None));

        Assert.Contains("base64", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateFakeFfprobe(string root, string duration)
    {
        var path = Path.Combine(root, OperatingSystem.IsWindows() ? "ffprobe.cmd" : "ffprobe");
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, $"@echo off\r\necho {duration}\r\n");
        }
        else
        {
            File.WriteAllText(path, $"#!/bin/sh\nprintf '%s\\n' '{duration}'\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private static string CreateFakeFfmpeg(string root)
    {
        var path = Path.Combine(root, OperatingSystem.IsWindows() ? "ffmpeg.cmd" : "ffmpeg");
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, """
@echo off
set last=
for %%a in (%*) do set last=%%~a
for %%d in ("%last%") do set outdir=%%~dpd
<nul set /p dummy=first>"%outdir%frame-001.jpg"
<nul set /p dummy=second>"%outdir%frame-002.jpg"
exit /b 0
""");
        }
        else
        {
            File.WriteAllText(path, """
#!/bin/sh
for last do :; done
case "$last" in
  *frame-%03d.jpg)
    dir=$(dirname "$last")
    printf first > "$dir/frame-001.jpg"
    printf second > "$dir/frame-002.jpg"
    ;;
  *)
    printf audio > "$last"
    ;;
esac
exit 0
""");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openclaw-video-" + Guid.NewGuid().ToString("N"));

        public TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}

using System.Net;
using System.Security.Cryptography;
using System.Text;
using OpenClaw.LayaService.Models;
using Xunit;

namespace OpenClaw.LayaService.Tests;

public sealed class HuggingFaceDownloaderTests
{
    private const string Revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task DownloadAsync_FetchesAllowlistedFilesWritesVerifiedManifestAndNotices()
    {
        using var directory = new TemporaryDirectory();
        var handler = new FakeHandler((request, _) => Task.FromResult(Success(request)));
        using var http = new HttpClient(handler);

        var manifestPath = await HuggingFaceDownloader.DownloadAsync(
            new DownloadOptions(directory.Path, Revision, ["english", "multilingual", "typed-decisions"]), http, CancellationToken.None);

        Assert.Equal(Path.Combine(directory.Path, Revision, "manifest.json"), manifestPath);
        Assert.Equal(15, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("huggingface.co", request.Host);
            Assert.StartsWith($"/convaiinnovations/laya/resolve/{Revision}/", request.AbsolutePath, StringComparison.Ordinal);
        });
        Assert.True(File.Exists(Path.Combine(directory.Path, Revision, "LAYA-NOTICE.md")));
        Assert.True(File.Exists(Path.Combine(directory.Path, Revision, "LAYA-LICENSE.txt")));

        var verified = ModelManifest.LoadAndVerify(manifestPath, Revision);
        Assert.Equal(3, verified.Checkpoints.Count);
        var normalizedTokenizer = File.ReadAllText(Path.Combine(directory.Path, Revision, "hub", "tokenizer", "tokenizer_config.json"));
        Assert.Contains("PreTrainedTokenizerFast", normalizedTokenizer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadAsync_RejectsInvalidRevisionCheckpointHttpErrorsAndRedirects()
    {
        using var directory = new TemporaryDirectory();
        using var successfulHttp = new HttpClient(new FakeHandler((request, _) => Task.FromResult(Success(request))));

        await Assert.ThrowsAsync<ArgumentException>(() => HuggingFaceDownloader.DownloadAsync(
            new DownloadOptions(directory.Path, "latest", ["english"]), successfulHttp, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => HuggingFaceDownloader.DownloadAsync(
            new DownloadOptions(directory.Path, Revision, ["unknown"]), successfulHttp, CancellationToken.None));

        foreach (var statusCode in new[] { HttpStatusCode.NotFound, HttpStatusCode.Redirect })
        {
            using var failingHttp = new HttpClient(new FakeHandler((request, _) => Task.FromResult(new HttpResponseMessage(statusCode)
            {
                RequestMessage = request,
                Headers = { Location = new Uri("https://example.invalid/redirect") }
            })));
            await Assert.ThrowsAsync<HttpRequestException>(() => HuggingFaceDownloader.DownloadAsync(
                new DownloadOptions(directory.Path, Revision, ["english"]), failingHttp, CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(directory.Path, Revision, "manifest.json")));
        }
    }

    [Fact]
    public async Task DownloadAsync_FollowsSingleRedirectToOfficialHuggingFaceCdn()
    {
        using var directory = new TemporaryDirectory();
        var handler = new FakeHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "huggingface.co")
            {
                var cdnUri = new Uri("https://us.aws.cdn.hf.co" + request.RequestUri.PathAndQuery + "?signed=test");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
                {
                    RequestMessage = request,
                    Headers = { Location = cdnUri }
                });
            }

            return Task.FromResult(Success(request));
        });
        using var http = new HttpClient(handler);

        var manifestPath = await HuggingFaceDownloader.DownloadAsync(
            new DownloadOptions(directory.Path, Revision, ["english"]), http, CancellationToken.None);

        Assert.Equal(10, handler.Requests.Count);
        Assert.Equal(5, handler.Requests.Count(request => request.Host == "huggingface.co"));
        Assert.Equal(5, handler.Requests.Count(request => request.Host.EndsWith(".cdn.hf.co", StringComparison.Ordinal)));
        Assert.Single(ModelManifest.LoadAndVerify(manifestPath, Revision).Checkpoints);
    }

    [Fact]
    public async Task DownloadAsync_FollowsPinnedResolveCacheThenOfficialCdn()
    {
        using var directory = new TemporaryDirectory();
        var handler = new FakeHandler((request, _) =>
        {
            var file = GetModelFile(request.RequestUri!);
            if (request.RequestUri!.Host == "huggingface.co" && !request.RequestUri.AbsolutePath.Contains("/api/resolve-cache/", StringComparison.Ordinal))
            {
                var cachePath = $"/api/resolve-cache/models/convaiinnovations/laya/{Revision}/{Uri.EscapeDataString(GetSourceFile(request.RequestUri))}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
                {
                    RequestMessage = request,
                    Headers = { Location = new Uri(cachePath + "?etag=fixture", UriKind.Relative) }
                });
            }

            if (request.RequestUri.Host == "huggingface.co" && file == "model.safetensors")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
                {
                    RequestMessage = request,
                    Headers = { Location = new Uri($"https://us.aws.cdn.hf.co/{file}?signed=fixture") }
                });
            }

            return Task.FromResult(SuccessForFile(request, file));
        });
        using var http = new HttpClient(handler);

        var manifestPath = await HuggingFaceDownloader.DownloadAsync(
            new DownloadOptions(directory.Path, Revision, ["english"]), http, CancellationToken.None);

        Assert.Equal(11, handler.Requests.Count);
        Assert.Equal(5, handler.Requests.Count(request => request.Host == "huggingface.co" && !request.AbsolutePath.Contains("/api/resolve-cache/", StringComparison.Ordinal)));
        Assert.Equal(5, handler.Requests.Count(request => request.Host == "huggingface.co" && request.AbsolutePath.Contains("/api/resolve-cache/", StringComparison.Ordinal)));
        Assert.Single(handler.Requests, request => request.Host.EndsWith(".cdn.hf.co", StringComparison.Ordinal));
        Assert.Single(ModelManifest.LoadAndVerify(manifestPath, Revision).Checkpoints);
    }

    [Fact]
    public async Task DownloadAsync_RejectsUntrustedCdnLookalikesAndAdditionalRedirects()
    {
        foreach (var scenario in new[] { "lookalike", "second-hop" })
        {
            using var directory = new TemporaryDirectory();
            using var http = new HttpClient(new FakeHandler((request, _) =>
            {
                if (request.RequestUri!.Host == "huggingface.co")
                {
                    var host = scenario == "lookalike" ? "us.aws.cdn.hf.co.attacker.invalid" : "us.aws.cdn.hf.co";
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
                    {
                        RequestMessage = request,
                        Headers = { Location = new Uri($"https://{host}{request.RequestUri.PathAndQuery}") }
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
                {
                    RequestMessage = request,
                    Headers = { Location = new Uri("https://us.aws.cdn.hf.co/another-hop") }
                });
            }));

            await Assert.ThrowsAsync<HttpRequestException>(() => HuggingFaceDownloader.DownloadAsync(
                new DownloadOptions(directory.Path, Revision, ["english"]), http, CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(directory.Path, Revision, "manifest.json")));
        }
    }

    [Fact]
    public async Task DownloadAsync_RejectsResolveCachePathForAnotherRevisionOrFile()
    {
        foreach (var cachePath in new[]
        {
            $"/api/resolve-cache/models/convaiinnovations/laya/{new string('b', 40)}/model.safetensors?etag=test",
            $"/api/resolve-cache/models/convaiinnovations/laya/{Revision}/another.safetensors?etag=test"
        })
        {
            using var directory = new TemporaryDirectory();
            using var http = new HttpClient(new FakeHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
            {
                RequestMessage = request,
                Headers = { Location = new Uri(cachePath, UriKind.Relative) }
            })));

            await Assert.ThrowsAsync<HttpRequestException>(() => HuggingFaceDownloader.DownloadAsync(
                new DownloadOptions(directory.Path, Revision, ["english"]), http, CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(directory.Path, Revision, "manifest.json")));
        }
    }

    [Fact]
    public async Task DownloadAsync_RejectsDefaultRevisionHashMismatchBeforePublishingAsset()
    {
        using var directory = new TemporaryDirectory();
        using var http = new HttpClient(new FakeHandler((request, _) => Task.FromResult(Success(request, Encoding.UTF8.GetBytes("wrong upstream bytes")))));

        await Assert.ThrowsAsync<InvalidDataException>(() => HuggingFaceDownloader.DownloadAsync(
            new DownloadOptions(directory.Path, ModelManifest.DefaultRevision, ["english"]), http, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(directory.Path, ModelManifest.DefaultRevision, "manifest.json")));
        Assert.False(File.Exists(Path.Combine(directory.Path, ModelManifest.DefaultRevision, "hub", "model.safetensors")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(directory.Path, ModelManifest.DefaultRevision), "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DownloadAsync_UsesSeparateRevisionDirectories()
    {
        using var directory = new TemporaryDirectory();
        using var http = new HttpClient(new FakeHandler((request, _) => Task.FromResult(Success(request))));
        var secondRevision = new string('b', 40);

        var first = await HuggingFaceDownloader.DownloadAsync(new DownloadOptions(directory.Path, Revision, ["english"]), http, CancellationToken.None);
        var second = await HuggingFaceDownloader.DownloadAsync(new DownloadOptions(directory.Path, secondRevision, ["english"]), http, CancellationToken.None);

        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public async Task DownloadAsync_FailedUpdateLeavesPreviousManifestUntouched()
    {
        using var directory = new TemporaryDirectory();
        using var successfulHttp = new HttpClient(new FakeHandler((request, _) => Task.FromResult(Success(request))));
        var manifestPath = await HuggingFaceDownloader.DownloadAsync(
            new DownloadOptions(directory.Path, Revision, ["english"]), successfulHttp, CancellationToken.None);
        var originalManifest = await File.ReadAllBytesAsync(manifestPath);
        using var failingHttp = new HttpClient(new FakeHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            RequestMessage = request
        })));

        await Assert.ThrowsAsync<HttpRequestException>(() => HuggingFaceDownloader.DownloadAsync(
            new DownloadOptions(directory.Path, Revision, ["multilingual"]), failingHttp, CancellationToken.None));

        Assert.Equal(originalManifest, await File.ReadAllBytesAsync(manifestPath));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(directory.Path, Revision), "manifest.json.*.tmp"));
    }

    private static HttpResponseMessage Success(HttpRequestMessage request, byte[]? content = null)
    {
        var file = GetModelFile(request.RequestUri!);
        return SuccessForFile(request, file, content);
    }

    private static HttpResponseMessage SuccessForFile(HttpRequestMessage request, string file, byte[]? content = null)
    {
        var bytes = content ?? FixtureContent(file);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(bytes)
        };
    }

    private static string GetModelFile(Uri uri)
        => string.Join('/', GetSourceFile(uri).Split('/').SkipWhile(segment => segment is "multilingual" or "typed-decisions"));

    private static string GetSourceFile(Uri uri)
    {
        var path = uri.AbsolutePath;
        const string resolveMarker = "/resolve/";
        const string cacheMarker = "/api/resolve-cache/models/convaiinnovations/laya/";
        var marker = path.Contains(resolveMarker, StringComparison.Ordinal) ? resolveMarker :
            path.Contains(cacheMarker, StringComparison.Ordinal) ? cacheMarker : null;
        if (marker is null) return path.TrimStart('/');
        var remainder = path[(path.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
        var revisionSeparator = remainder.IndexOf('/', StringComparison.Ordinal);
        return Uri.UnescapeDataString(revisionSeparator < 0 ? remainder : remainder[(revisionSeparator + 1)..]);
    }

    private static byte[] FixtureContent(string file)
        => file == "tokenizer/tokenizer_config.json"
            ? Encoding.UTF8.GetBytes("{\"tokenizer_class\":\"TokenizersBackend\",\"backend\":\"legacy\",\"is_local\":true,\"extra_special_tokens\":[\"a\",\"b\"]}")
            : Encoding.UTF8.GetBytes($"fixture:{file}");

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return responder(request, cancellationToken);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
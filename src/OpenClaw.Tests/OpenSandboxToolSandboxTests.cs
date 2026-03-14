#if OPENCLAW_ENABLE_OPENSANDBOX
using System.Net;
using System.Net.Http;
using System.Text;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClawNet.Sandbox.OpenSandbox;
using Xunit;

namespace OpenClaw.Tests;

public sealed class OpenSandboxToolSandboxTests
{
    [Fact]
    public async Task ExecuteAsync_CreatesLeaseExecutesReusesAndDisposes()
    {
        var handler = new RecordingHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/v1/sandboxes", StringComparison.Ordinal) == true)
            {
                return JsonResponse("""{"id":"sb-1","expiresAt":"2030-01-01T00:00:00Z"}""");
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/renew-expiration", StringComparison.Ordinal) == true)
            {
                return JsonResponse("""{"expiresAt":"2030-01-01T00:00:00Z"}""");
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/exec", StringComparison.Ordinal) == true)
            {
                return JsonResponse("""{"exitCode":0,"stdOut":"ok","stdErr":""}""");
            }

            if (request.Method == HttpMethod.Delete)
                return new HttpResponseMessage(HttpStatusCode.NoContent);

            throw new InvalidOperationException("Unexpected request.");
        });

        await using var sandbox = CreateSandbox(handler);
        var request = new SandboxExecutionRequest
        {
            Command = "echo",
            Arguments = ["hello"],
            Template = "ghcr.io/example/shell:latest",
            LeaseKey = "session:shell"
        };

        var first = await sandbox.ExecuteAsync(request);
        var second = await sandbox.ExecuteAsync(request);

        Assert.Equal("ok", first.Stdout);
        Assert.Equal("ok", second.Stdout);
        Assert.Equal(1, handler.CountRequests("/v1/sandboxes", HttpMethod.Post));
        Assert.Equal(2, handler.CountRequests("/exec", HttpMethod.Post));

        await sandbox.DisposeAsync();

        Assert.Equal(1, handler.CountRequests("/v1/sandboxes/sb-1", HttpMethod.Delete));
    }

    [Fact]
    public async Task ExecuteAsync_UsesPerRequestTtlWhenProvided()
    {
        string? createBody = null;
        var handler = new RecordingHandler(async (request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/v1/sandboxes", StringComparison.Ordinal) == true)
            {
                createBody = request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync();
                return JsonResponse("""{"id":"sb-ttl","expiresAt":"2030-01-01T00:00:00Z"}""");
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/renew-expiration", StringComparison.Ordinal) == true)
                return JsonResponse("""{"expiresAt":"2030-01-01T00:00:00Z"}""");

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/exec", StringComparison.Ordinal) == true)
                return JsonResponse("""{"exitCode":0,"stdOut":"ok","stdErr":""}""");

            if (request.Method == HttpMethod.Delete)
                return new HttpResponseMessage(HttpStatusCode.NoContent);

            throw new InvalidOperationException("Unexpected request.");
        });

        await using var sandbox = CreateSandbox(handler);
        await sandbox.ExecuteAsync(new SandboxExecutionRequest
        {
            Command = "echo",
            Arguments = ["hello"],
            Template = "ghcr.io/example/shell:latest",
            TimeToLiveSeconds = 42
        });

        Assert.Contains("\"timeout\":42", createBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_RecreatesLeaseWhenCachedSandboxIsMissing()
    {
        var createCount = 0;
        var renewCount = 0;
        var handler = new RecordingHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/v1/sandboxes", StringComparison.Ordinal) == true)
            {
                createCount++;
                return JsonResponse($$"""{"id":"sb-{{createCount}}","expiresAt":"2030-01-01T00:00:00Z"}""");
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/renew-expiration", StringComparison.Ordinal) == true)
            {
                renewCount++;
                return renewCount == 2
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : JsonResponse("""{"expiresAt":"2030-01-01T00:00:00Z"}""");
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/exec", StringComparison.Ordinal) == true)
                return JsonResponse("""{"exitCode":0,"stdOut":"ok","stdErr":""}""");

            if (request.Method == HttpMethod.Delete)
                return new HttpResponseMessage(HttpStatusCode.NoContent);

            throw new InvalidOperationException("Unexpected request.");
        });

        await using var sandbox = CreateSandbox(handler);
        var request = new SandboxExecutionRequest
        {
            Command = "echo",
            Arguments = ["hello"],
            Template = "ghcr.io/example/shell:latest",
            LeaseKey = "session:shell"
        };

        await sandbox.ExecuteAsync(request);
        await sandbox.ExecuteAsync(request);

        Assert.Equal(2, createCount);
    }

    [Fact]
    public async Task ExecuteAsync_HttpRequestException_MapsToUnavailableException()
    {
        var handler = new ThrowingHandler(new HttpRequestException("connection refused"));
        await using var sandbox = CreateSandbox(handler);

        await Assert.ThrowsAsync<ToolSandboxUnavailableException>(() =>
            sandbox.ExecuteAsync(new SandboxExecutionRequest
            {
                Command = "echo",
                Arguments = ["hello"],
                Template = "ghcr.io/example/shell:latest"
            }));
    }

    private static OpenSandboxToolSandbox CreateSandbox(HttpMessageHandler handler)
        => new(
            new HttpClient(handler),
            new OpenSandboxOptions
            {
                Endpoint = "http://localhost:5000",
                DefaultTTL = 300
            });

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        private readonly List<(string Path, HttpMethod Method)> _requests = [];

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
            : this((request, cancellationToken) => Task.FromResult(handler(request, cancellationToken)))
        {
        }

        public int CountRequests(string pathSuffix, HttpMethod method)
            => _requests.Count(entry =>
                entry.Method == method &&
                entry.Path.EndsWith(pathSuffix, StringComparison.Ordinal));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Add((request.RequestUri?.AbsolutePath ?? string.Empty, request.Method));
            return await handler(request, cancellationToken);
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(exception);
    }
}
#endif

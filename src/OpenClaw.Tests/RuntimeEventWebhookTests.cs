using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using OpenClaw.Core.Models;
using OpenClaw.Gateway;

using Xunit;

namespace OpenClaw.Tests;

public class RuntimeEventWebhookTests
{
    [Fact]
    public async Task SendAsync_Skips_When_Url_Not_Configured()
    {
        var http = new HttpClient(new RecordingHandler());
        var sut = new RuntimeEventWebhook(http, new RuntimeEventWebhookOptions { Url = "" }, NullLogger<RuntimeEventWebhook>.Instance);

        await sut.SendAsync(NewEntry());

        var handler = (RecordingHandler)http.DisposeAndGetHandler()!;
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task SendAsync_Posts_Event_As_Json_With_Bearer_Token()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var http = new HttpClient(handler);
        var sut = new RuntimeEventWebhook(
            http,
            new RuntimeEventWebhookOptions { Url = "http://sidecar.local/runtime-events", BearerToken = "secret" },
            NullLogger<RuntimeEventWebhook>.Instance);

        await sut.SendAsync(NewEntry());

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("Bearer secret", handler.LastAuthorization);
        Assert.NotNull(handler.LastBody);
        Assert.Contains("\"component\":\"workflow\"", handler.LastBody!);
        Assert.Contains("\"action\":\"run_completed\"", handler.LastBody!);
    }

    [Fact]
    public async Task SendAsync_Retries_Once_On_5xx()
    {
        var handler = new RecordingHandler(HttpStatusCode.InternalServerError, then: HttpStatusCode.OK);
        var http = new HttpClient(handler);
        var sut = new RuntimeEventWebhook(
            http,
            new RuntimeEventWebhookOptions { Url = "http://sidecar.local/runtime-events", RetryDelayMs = 1 },
            NullLogger<RuntimeEventWebhook>.Instance);

        await sut.SendAsync(NewEntry());

        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task SendAsync_Does_Not_Retry_On_401()
    {
        var handler = new RecordingHandler(HttpStatusCode.Unauthorized);
        var http = new HttpClient(handler);
        var sut = new RuntimeEventWebhook(
            http,
            new RuntimeEventWebhookOptions { Url = "http://sidecar.local/runtime-events" },
            NullLogger<RuntimeEventWebhook>.Instance);

        await sut.SendAsync(NewEntry());

        Assert.Equal(1, handler.RequestCount); // stopped after first 401
    }

    private static RuntimeEventEntry NewEntry() => new()
    {
        Id = $"evt_{Guid.NewGuid():N}"[..20],
        Component = "workflow",
        Action = "run_completed",
        Severity = "info",
        Summary = "test",
        Metadata = new Dictionary<string, string>
        {
            ["runId"] = "run-1",
            ["stepName"] = "SecurityReviewer",
        },
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _first;
        private readonly HttpStatusCode? _then;
        public int RequestCount { get; private set; }
        public string? LastAuthorization { get; private set; }
        public string? LastBody { get; private set; }

        public RecordingHandler(HttpStatusCode first = default, HttpStatusCode? then = null)
        {
            _first = first;
            _then = then;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastAuthorization = request.Headers.Authorization?.ToString();
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            var status = RequestCount == 1 ? _first : (_then ?? _first);
            return new HttpResponseMessage(status);
        }
    }
}

internal static class HttpClientTestExtensions
{
    public static HttpMessageHandler? DisposeAndGetHandler(this HttpClient client)
    {
        // HttpClient inherits _handler from HttpMessageInvoker; reach into it
        // via reflection so callers can keep asserting after dispose without
        // re-capturing the reference.
        var t = typeof(HttpClient);
        while (t != null)
        {
            var field = t.GetField("_handler", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field != null)
            {
                var handler = field.GetValue(client) as HttpMessageHandler;
                client.Dispose();
                return handler;
            }
            t = t.BaseType;
        }
        client.Dispose();
        return null;
    }
}
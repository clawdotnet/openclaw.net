using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Models;

namespace OpenClaw.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public class BrowserToolTests
{
    [Fact]
    public async Task BrowserTool_CanNavigateAndGetText()
    {
        var config = new ToolingConfig 
        { 
            EnableBrowserTool = true, 
            BrowserHeadless = true,
            BrowserTimeoutSeconds = 30,
            UrlSafety = new UrlSafetyConfig
            {
                BlockPrivateNetworkTargets = false
            }
        };
        var previousBrowsersPath = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        var isolateBrowserInstall = string.Equals(
            Environment.GetEnvironmentVariable("OPENCLAW_TEST_ISOLATE_PLAYWRIGHT_BROWSERS"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        if (isolateBrowserInstall)
        {
            var browsersPath = Path.Join(Path.GetTempPath(), "openclaw-playwright-tests", "browsers");
            Directory.CreateDirectory(browsersPath);
            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", browsersPath);
        }

        try
        {
            await using var pageServer = LocalPageServer.Start();
            await using var browser = new BrowserTool(config);

            var gotoArgs = $$"""{"action": "goto", "url": "{{pageServer.Url}}"}""";
            var gotoRes = await browser.ExecuteAsync(gotoArgs, CancellationToken.None);
            Assert.Contains("Navigated to", gotoRes);

            var getTextArgs = "{\"action\": \"get_text\", \"selector\": \"h1\"}";
            var textRes = await browser.ExecuteAsync(getTextArgs, CancellationToken.None);
            Assert.Contains("OpenClaw Browser Tool Test", textRes);

            var evalArgs = "{\"action\": \"evaluate\", \"script\": \"Math.max(1, 5)\"}";
            var evalRes = await browser.ExecuteAsync(evalArgs, CancellationToken.None);
            Assert.Equal("5", evalRes);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", previousBrowsersPath);
        }
    }

    private sealed class LocalPageServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _serveTask;

        private LocalPageServer(TcpListener listener)
        {
            _listener = listener;
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Url = new Uri($"http://127.0.0.1:{port}/");
            _serveTask = ServeAsync();
        }

        public Uri Url { get; }

        public static LocalPageServer Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, port: 0);
            listener.Start();
            return new LocalPageServer(listener);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();

            try
            {
                await _serveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is expected when the local fixture server is stopped during disposal.
            }

            _cts.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                try
                {
                    await HandleAsync(client, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private static async Task HandleAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[1024];
                _ = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);

                const string Body =
                    "<!doctype html><html><head><title>OpenClaw Browser Tool Test</title></head>" +
                    "<body><h1>OpenClaw Browser Tool Test</h1></body></html>";
                var bodyBytes = Encoding.UTF8.GetBytes(Body);
                var header = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: text/html; charset=utf-8\r\n" +
                    $"Content-Length: {bodyBytes.Length}\r\n" +
                    "Connection: close\r\n\r\n");

                await stream.WriteAsync(header, ct).ConfigureAwait(false);
                await stream.WriteAsync(bodyBytes, ct).ConfigureAwait(false);
            }
        }
    }
}

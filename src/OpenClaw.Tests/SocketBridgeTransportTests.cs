using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent.Plugins;
using OpenClaw.Core.Observability;
using Xunit;

namespace OpenClaw.Tests;

public sealed class SocketBridgeTransportTests
{
    [Fact]
    public async Task StartAsync_RejectsUnauthenticatedPeer_AndRecordsAuthFailure()
    {
        if (OperatingSystem.IsWindows())
            return;

        var tempDir = Path.Combine("/tmp", $".openclaw-socket-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var socketPath = Path.Combine(tempDir, "bridge.sock");
        var metrics = new RuntimeMetrics();

        await using var transport = new SocketBridgeTransport(
            socketPath,
            tempDir,
            ownsSocketDirectory: true,
            "expected-token",
            NullLogger.Instance,
            metrics);
        await transport.PrepareAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var startTask = transport.StartAsync(Process.GetCurrentProcess(), cts.Token);

        await using (var badClient = await ConnectAsync(socketPath))
        {
            await badClient.Writer.WriteLineAsync("""{"type":"bridge_auth","token":"wrong-token"}""");
            await badClient.Writer.FlushAsync();
        }

        await using (var goodClient = await ConnectAsync(socketPath))
        {
            await goodClient.Writer.WriteLineAsync("""{"type":"bridge_auth","token":"expected-token"}""");
            await goodClient.Writer.FlushAsync();
            await startTask;
        }

        Assert.Equal(1, metrics.PluginBridgeAuthFailures);
    }

    [Fact]
    public async Task DisposeAsync_DoesNotDeleteConfiguredSocketParentDirectory()
    {
        if (OperatingSystem.IsWindows())
            return;

        var tempDir = Path.Combine("/tmp", $".openclaw-socket-configured-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var markerPath = Path.Combine(tempDir, "keep.txt");
        await File.WriteAllTextAsync(markerPath, "keep");
        var socketPath = Path.Combine(tempDir, "bridge.sock");

        await using (var transport = new SocketBridgeTransport(
            socketPath,
            tempDir,
            ownsSocketDirectory: false,
            "expected-token",
            NullLogger.Instance))
        {
            await transport.PrepareAsync(CancellationToken.None);
        }

        Assert.True(Directory.Exists(tempDir));
        Assert.True(File.Exists(markerPath));
        Assert.False(File.Exists(socketPath));
    }

    private static async Task<TestSocketClient> ConnectAsync(string socketPath)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
        var stream = new NetworkStream(socket, ownsSocket: true);
        var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };

        return new TestSocketClient(stream, writer);
    }

    private sealed class TestSocketClient(Stream stream, StreamWriter writer) : IAsyncDisposable
    {
        public StreamWriter Writer { get; } = writer;

        public async ValueTask DisposeAsync()
        {
            await Writer.DisposeAsync();
            await stream.DisposeAsync();
        }
    }
}

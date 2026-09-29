using System.Diagnostics;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent.Plugins;
using Xunit;

namespace OpenClaw.Tests;

public sealed class BridgeTransportBaseTests
{
    [Fact]
    public async Task SendAndWaitAsync_WhenChildClosesWithRequestPending_FailsInsteadOfCancelling()
    {
        var ct = TestContext.Current.CancellationToken;
        var childOutput = new Pipe();
        var childInput = new Pipe();
        await using var transport = new InMemoryBridgeTransport(
            new StreamReader(childOutput.Reader.AsStream()),
            new StreamWriter(childInput.Writer.AsStream()));
        using var child = new StreamReader(childInput.Reader.AsStream());

        var pending = transport.SendAndWaitAsync("execute", null, ct);
        Assert.NotNull(await child.ReadLineAsync(ct));

        // The child exits without answering, as a plugin process does when it crashes mid-request.
        await childOutput.Writer.CompleteAsync();

        // The caller did not cancel, so the request must fail rather than report cancellation,
        // which the tool executor would record as a timeout.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        Assert.Contains("closed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendAndWaitAsync_AfterChildClosed_FailsImmediately()
    {
        var ct = TestContext.Current.CancellationToken;
        var childOutput = new Pipe();
        var childInput = new Pipe();
        await using var transport = new InMemoryBridgeTransport(
            new StreamReader(childOutput.Reader.AsStream()),
            new StreamWriter(childInput.Writer.AsStream()));
        using var child = new StreamReader(childInput.Reader.AsStream());

        var first = transport.SendAndWaitAsync("execute", null, ct);
        Assert.NotNull(await child.ReadLineAsync(ct));
        await childOutput.Writer.CompleteAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);

        // Nothing will ever answer now, so a later request must fail at once instead of waiting out the 60 s timeout.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => transport.SendAndWaitAsync("execute", null, ct).WaitAsync(TimeSpan.FromSeconds(5), ct));
        Assert.Contains("closed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class InMemoryBridgeTransport : BridgeTransportBase
    {
        public InMemoryBridgeTransport(TextReader fromChild, TextWriter toChild)
            : base(NullLogger.Instance)
            => AttachReaderWriter(fromChild, toChild);

        public override Task StartAsync(Process process, CancellationToken ct) => Task.CompletedTask;
    }
}

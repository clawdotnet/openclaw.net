using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Pipeline;

/// <summary>
/// High-throughput, zero-allocation message pipeline using System.Threading.Channels.
/// Bounded channel applies backpressure to prevent OOM under load.
/// </summary>
public sealed class MessagePipeline : IAsyncDisposable
{
    private readonly Channel<InboundMessage> _inbound;
    private readonly Channel<OutboundMessage> _outbound;
    private readonly ILogger? _logger;

    public MessagePipeline(int capacity = 1024, ILogger? logger = null)
    {
        var opts = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        };
        _inbound = Channel.CreateBounded<InboundMessage>(opts);
        _outbound = Channel.CreateBounded<OutboundMessage>(opts);
        _logger = logger;
    }

    public ChannelWriter<InboundMessage> InboundWriter => _inbound.Writer;
    public ChannelReader<InboundMessage> InboundReader => _inbound.Reader;
    public ChannelWriter<OutboundMessage> OutboundWriter => _outbound.Writer;
    public ChannelReader<OutboundMessage> OutboundReader => _outbound.Reader;

    public ValueTask DisposeAsync()
    {
        _inbound.Writer.TryComplete();
        _outbound.Writer.TryComplete();

        DrainDeadLetters(_inbound.Reader, "inbound");
        DrainDeadLetters(_outbound.Reader, "outbound");

        return ValueTask.CompletedTask;
    }

    private void DrainDeadLetters<T>(ChannelReader<T> reader, string channelName)
    {
        var count = 0;
        while (reader.TryRead(out _))
            count++;

        if (count > 0)
            _logger?.LogWarning("MessagePipeline: {Count} {Channel} message(s) dropped during shutdown.", count, channelName);
    }
}

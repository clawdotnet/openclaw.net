using System.Threading.Channels;
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

    public MessagePipeline(int capacity = 1024)
    {
        var opts = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        };
        _inbound = Channel.CreateBounded<InboundMessage>(opts);
        _outbound = Channel.CreateBounded<OutboundMessage>(opts);
    }

    public ChannelWriter<InboundMessage> InboundWriter => _inbound.Writer;
    public ChannelReader<InboundMessage> InboundReader => _inbound.Reader;
    public ChannelWriter<OutboundMessage> OutboundWriter => _outbound.Writer;
    public ChannelReader<OutboundMessage> OutboundReader => _outbound.Reader;

    public ValueTask DisposeAsync()
    {
        _inbound.Writer.TryComplete();
        _outbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

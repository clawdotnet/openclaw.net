using System.Collections.Concurrent;
using System.Threading.Channels;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Backends;

internal sealed class BackendSessionEventStreamStore
{
    private readonly ConcurrentDictionary<int, Channel<BackendEvent>> _subscribers = new();
    private int _nextSubscriberId;

    public void Record(BackendEvent evt)
    {
        foreach (var subscriber in _subscribers.Values)
            subscriber.Writer.TryWrite(evt);
    }

    public EventSubscription Subscribe()
    {
        var id = Interlocked.Increment(ref _nextSubscriberId);
        var channel = Channel.CreateBounded<BackendEvent>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
        _subscribers[id] = channel;
        return new EventSubscription(id, channel.Reader, this);
    }

    private void Unsubscribe(int id)
    {
        if (_subscribers.TryRemove(id, out var channel))
            channel.Writer.TryComplete();
    }

    internal sealed class EventSubscription : IDisposable
    {
        private readonly int _id;
        private readonly BackendSessionEventStreamStore _store;

        public EventSubscription(int id, ChannelReader<BackendEvent> reader, BackendSessionEventStreamStore store)
        {
            _id = id;
            Reader = reader;
            _store = store;
        }

        public ChannelReader<BackendEvent> Reader { get; }

        public void Dispose() => _store.Unsubscribe(_id);
    }
}

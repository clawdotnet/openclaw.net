using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

/// <summary>
/// Adapter for a messaging channel (WhatsApp, Telegram, Discord, etc.).
/// Implementations must be AOT-compatible and allocation-conscious.
/// </summary>
public interface IChannelAdapter : IAsyncDisposable
{
    string ChannelId { get; }
    
    /// <summary>Start listening for inbound messages.</summary>
    Task StartAsync(CancellationToken ct);
    
    /// <summary>Send a message through this channel.</summary>
    ValueTask SendAsync(OutboundMessage message, CancellationToken ct);
    
    /// <summary>Event raised when a message arrives from this channel.</summary>
    event Func<InboundMessage, CancellationToken, ValueTask> OnMessageReceived;
}

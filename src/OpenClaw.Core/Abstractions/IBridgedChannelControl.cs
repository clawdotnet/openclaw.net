using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

public interface IBridgedChannelControl : IChannelAdapter
{
    string? SelfId { get; }
    IReadOnlyList<string> SelfIds { get; }
    ValueTask SendTypingAsync(string recipientId, bool isTyping, string? accountId, CancellationToken ct);
    ValueTask SendReadReceiptAsync(string messageId, string? remoteJid, string? participant, string? accountId, CancellationToken ct);
}

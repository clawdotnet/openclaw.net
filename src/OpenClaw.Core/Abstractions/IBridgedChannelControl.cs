using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

public interface IBridgedChannelControl : IChannelAdapter
{
    string? SelfId { get; }
    IReadOnlyList<string> SelfIds { get; }

    /// <param name="messageId">
    /// Inbound provider message id when available (e.g. Feishu Typing reaction).
    /// WhatsApp-style session typing may ignore it.
    /// </param>
    ValueTask SendTypingAsync(
        string recipientId,
        bool isTyping,
        string? accountId,
        string? messageId,
        CancellationToken ct);

    ValueTask SendReadReceiptAsync(string messageId, string? remoteJid, string? participant, string? accountId, CancellationToken ct);
}

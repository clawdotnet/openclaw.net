using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Channels;

/// <summary>
/// A simple channel adapter used as the default delivery sink for cron jobs when no other channel is specified.
/// Writes outputs to the configured memory folder and logs a short summary.
/// </summary>
public sealed class CronChannel : IChannelAdapter
{
    private readonly string _storagePath;
    private readonly ILogger<CronChannel> _logger;

    public CronChannel(string storagePath, ILogger<CronChannel> logger)
    {
        _storagePath = storagePath;
        _logger = logger;
    }

    public string ChannelId => "cron";

#pragma warning disable CS0067 // Event is never used
    public event Func<InboundMessage, CancellationToken, ValueTask>? OnMessageReceived;
#pragma warning restore CS0067

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public async ValueTask SendAsync(OutboundMessage message, CancellationToken ct)
    {
        var recipient = string.IsNullOrWhiteSpace(message.RecipientId) ? "cron" : message.RecipientId;
        var subject = string.IsNullOrWhiteSpace(message.Subject) ? "OpenClaw Cron" : message.Subject!;

        var dir = Path.Combine(_storagePath, "cron");
        Directory.CreateDirectory(dir);

        var filename = BuildSafeFilename(recipient);
        var path = Path.Combine(dir, filename);

        var sb = new StringBuilder(capacity: Math.Min(8_192, message.Text.Length + 256));
        sb.AppendLine($"received_at: {DateTimeOffset.UtcNow:O}");
        sb.AppendLine($"recipient: {recipient}");
        sb.AppendLine($"subject: {subject}");
        sb.AppendLine();
        sb.AppendLine(message.Text ?? "");
        sb.AppendLine();
        sb.AppendLine("-----");
        sb.AppendLine();

        await File.AppendAllTextAsync(path, sb.ToString(), Encoding.UTF8, ct);

        _logger.LogInformation("Cron output written to {Path} (recipient={Recipient})", path, recipient);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string BuildSafeFilename(string recipient)
    {
        var hintChars = recipient
            .Where(static c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.')
            .Take(32)
            .ToArray();

        var hint = hintChars.Length == 0 ? "recipient" : new string(hintChars);

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(recipient)));
        return $"{hint}-{hash[..12].ToLowerInvariant()}.log";
    }
}


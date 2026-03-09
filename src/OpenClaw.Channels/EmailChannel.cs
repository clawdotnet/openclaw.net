using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;

namespace OpenClaw.Channels;

public sealed class EmailChannel : IChannelAdapter
{
    private readonly EmailConfig _config;
    private readonly ILogger<EmailChannel> _logger;

    public EmailChannel(EmailConfig config, ILogger<EmailChannel> logger)
    {
        _config = config;
        _logger = logger;
    }

    public string ChannelId => "email";

    public event Func<InboundMessage, CancellationToken, ValueTask>? OnMessageReceived;

    public async Task StartAsync(CancellationToken ct)
    {
        if (!_config.InboundEnabled)
            return;

        if (!CanListenForInbound())
        {
            _logger.LogWarning("Email inbound listener is enabled but IMAP host or credentials are not fully configured.");
            return;
        }

        var folderError = InputSanitizer.CheckImapFolderName(_config.InboundFolder);
        if (folderError is not null)
        {
            _logger.LogWarning("Email inbound listener is disabled because the configured IMAP folder is invalid: {Error}", folderError);
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Clamp(_config.InboundPollSeconds, 5, 3600));

        await PollInboxSafeAsync(ct);

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(ct))
            await PollInboxSafeAsync(ct);
    }

    public async ValueTask SendAsync(OutboundMessage message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.SmtpHost))
            throw new InvalidOperationException("Email channel requires Email.SmtpHost to be configured.");

        var to = message.RecipientId;
        if (string.IsNullOrWhiteSpace(to))
            return;

        var password = SecretResolver.Resolve(_config.PasswordRef);
        if (string.IsNullOrWhiteSpace(_config.Username) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Email channel requires Email.Username and Email.PasswordRef to be configured.");

        var from = _config.FromAddress ?? _config.Username;
        var subject = string.IsNullOrWhiteSpace(message.Subject) ? "OpenClaw" : message.Subject!;

        using var client = new SmtpClient();
        await client.ConnectAsync(_config.SmtpHost, _config.SmtpPort, GetSecureSocketOptions(_config.SmtpUseTls, _config.SmtpPort), ct);
        await client.AuthenticateAsync(_config.Username, password, ct);

        var mail = new MimeMessage();
        mail.From.Add(MailboxAddress.Parse(from!));
        mail.To.Add(MailboxAddress.Parse(to));
        mail.Subject = subject;
        mail.Body = new TextPart("plain") { Text = message.Text ?? string.Empty };

        await client.SendAsync(mail, ct);
        await client.DisconnectAsync(true, ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private bool CanListenForInbound()
    {
        var password = SecretResolver.Resolve(_config.PasswordRef);
        return !string.IsNullOrWhiteSpace(_config.ImapHost)
            && !string.IsNullOrWhiteSpace(_config.Username)
            && !string.IsNullOrWhiteSpace(password);
    }

    private async Task PollInboxSafeAsync(CancellationToken ct)
    {
        try
        {
            await PollInboxAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Email inbound polling failed.");
        }
    }

    private async Task PollInboxAsync(CancellationToken ct)
    {
        var password = SecretResolver.Resolve(_config.PasswordRef)!;
        using var client = new ImapClient();
        await client.ConnectAsync(_config.ImapHost!, _config.ImapPort, GetSecureSocketOptions(_config.ImapUseTls, _config.ImapPort), ct);
        await client.AuthenticateAsync(_config.Username!, password, ct);

        var folder = await GetInboundFolderAsync(client, ct);
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);

        var unseen = await folder.SearchAsync(SearchQuery.NotSeen, ct);
        if (unseen.Count == 0)
        {
            await client.DisconnectAsync(true, ct);
            return;
        }

        var maxMessages = Math.Max(1, _config.InboundMaxMessagesPerPoll);
        var startIndex = Math.Max(0, unseen.Count - maxMessages);

        for (var i = startIndex; i < unseen.Count; i++)
        {
            var uid = unseen[i];
            var message = await folder.GetMessageAsync(uid, ct);
            var sender = message.From.Mailboxes.FirstOrDefault();
            var body = ExtractBody(message);

            if (OnMessageReceived is not null)
            {
                await OnMessageReceived(new InboundMessage
                {
                    ChannelId = ChannelId,
                    SenderId = sender?.Address ?? message.From.ToString(),
                    SenderName = sender?.Name,
                    Subject = message.Subject,
                    Text = string.IsNullOrWhiteSpace(body) ? (message.Subject ?? string.Empty) : body,
                    MessageId = string.IsNullOrWhiteSpace(message.MessageId) ? uid.Id.ToString() : message.MessageId,
                    Type = "email",
                    ReceivedAt = message.Date == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : message.Date
                }, ct);
            }

            if (_config.MarkInboundAsRead)
                await folder.AddFlagsAsync([uid], MessageFlags.Seen, true, ct);
        }

        await client.DisconnectAsync(true, ct);
    }

    private async Task<IMailFolder> GetInboundFolderAsync(ImapClient client, CancellationToken ct)
    {
        if (string.Equals(_config.InboundFolder, "INBOX", StringComparison.OrdinalIgnoreCase))
            return client.Inbox;

        return await client.GetFolderAsync(_config.InboundFolder, ct);
    }

    private static string ExtractBody(MimeMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.TextBody))
            return message.TextBody;

        if (!string.IsNullOrWhiteSpace(message.HtmlBody))
            return System.Net.WebUtility.HtmlDecode(
                System.Text.RegularExpressions.Regex.Replace(message.HtmlBody, "<[^>]+>", " "));

        return string.Empty;
    }

    private static SecureSocketOptions GetSecureSocketOptions(bool useTls, int port)
    {
        if (!useTls)
            return SecureSocketOptions.None;

        return port is 465 or 993
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTlsWhenAvailable;
    }
}

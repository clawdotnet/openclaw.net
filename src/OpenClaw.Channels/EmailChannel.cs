using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;

namespace OpenClaw.Channels;

public sealed class EmailChannel : IChannelAdapter
{
    private readonly EmailConfig _config;

    public EmailChannel(EmailConfig config) => _config = config;

    public string ChannelId => "email";

#pragma warning disable CS0067 // Event is never used
    public event Func<InboundMessage, CancellationToken, ValueTask>? OnMessageReceived;
#pragma warning restore CS0067

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

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

        using var client = new System.Net.Mail.SmtpClient(_config.SmtpHost, _config.SmtpPort)
        {
            Credentials = new System.Net.NetworkCredential(_config.Username, password),
            EnableSsl = _config.SmtpUseTls
        };

        using var mail = new System.Net.Mail.MailMessage(from!, to, subject, message.Text ?? "");
        await client.SendMailAsync(mail, ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

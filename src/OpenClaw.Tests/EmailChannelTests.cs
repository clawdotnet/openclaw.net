using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Channels;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using Xunit;

namespace OpenClaw.Tests;

public sealed class EmailChannelTests
{
    [Fact]
    public async Task StartAsync_InboundDisabled_CompletesImmediately()
    {
        var channel = new EmailChannel(new EmailConfig { Enabled = true, InboundEnabled = false }, NullLogger<EmailChannel>.Instance);

        await channel.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_InboundEnabledWithoutImapConfig_DoesNotThrow()
    {
        var channel = new EmailChannel(new EmailConfig { Enabled = true, InboundEnabled = true }, NullLogger<EmailChannel>.Instance);

        await channel.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SendAsync_WithoutSmtpHost_ThrowsInvalidOperationException()
    {
        var channel = new EmailChannel(new EmailConfig { Enabled = true }, NullLogger<EmailChannel>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await channel.SendAsync(new OutboundMessage
            {
                ChannelId = "email",
                RecipientId = "person@example.com",
                Text = "hello"
            }, CancellationToken.None));
    }

    [Fact]
    public async Task SendAsync_WithoutCredentials_ThrowsInvalidOperationException()
    {
        var channel = new EmailChannel(new EmailConfig
        {
            Enabled = true,
            SmtpHost = "smtp.example.com"
        }, NullLogger<EmailChannel>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await channel.SendAsync(new OutboundMessage
            {
                ChannelId = "email",
                RecipientId = "person@example.com",
                Text = "hello"
            }, CancellationToken.None));
    }
}
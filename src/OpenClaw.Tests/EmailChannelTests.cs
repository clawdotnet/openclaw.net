using MailKit;
using MailKit.Net.Smtp;
using MimeKit;
using NSubstitute;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Channels;
using OpenClaw.Core.Abstractions;
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

        await channel.StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartAsync_InboundEnabledWithoutImapConfig_DoesNotThrow()
    {
        var channel = new EmailChannel(new EmailConfig { Enabled = true, InboundEnabled = true }, NullLogger<EmailChannel>.Instance);

        await channel.StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SendAsync_WithoutSmtpHost_ThrowsChannelDeliveryException()
    {
        var channel = new EmailChannel(new EmailConfig { Enabled = true }, NullLogger<EmailChannel>.Instance);

        await Assert.ThrowsAsync<ChannelDeliveryException>(async () =>
            await channel.SendAsync(new OutboundMessage
            {
                ChannelId = "email",
                RecipientId = "person@example.com",
                Text = "hello"
            }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SendAsync_WithoutCredentials_ThrowsChannelDeliveryException()
    {
        var channel = new EmailChannel(new EmailConfig
        {
            Enabled = true,
            SmtpHost = "smtp.example.com"
        }, NullLogger<EmailChannel>.Instance);

        await Assert.ThrowsAsync<ChannelDeliveryException>(async () =>
            await channel.SendAsync(new OutboundMessage
            {
                ChannelId = "email",
                RecipientId = "person@example.com",
                Text = "hello"
            }, TestContext.Current.CancellationToken));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SendAsync_WhenSmtpRejectsOrTransportFails_ShouldClassifyOutcome(bool rejected)
    {
        var smtp = Substitute.For<ISmtpClient>();
        smtp.SendAsync(Arg.Any<MimeMessage>(), Arg.Any<CancellationToken>(), Arg.Any<ITransferProgress>())
            .Returns(_ => Task.FromException<string>(rejected
                ? new SmtpCommandException(SmtpErrorCode.MessageNotAccepted, SmtpStatusCode.MailboxUnavailable, "rejected")
                : new IOException("connection lost")));
        await using var channel = new EmailChannel(new EmailConfig
        {
            SmtpHost = "fixture", Username = "sender@example.test", PasswordRef = "fixture-password"
        }, NullLogger<EmailChannel>.Instance, () => smtp);
        var error = await Assert.ThrowsAsync<ChannelDeliveryException>(() => channel.SendAsync(new OutboundMessage
        {
            ChannelId = "email", RecipientId = "recipient@example.test", Text = "hello"
        }, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(!rejected, error.OutcomeUnknown);
        Assert.False(error.CanRetry);
    }

    [Fact]
    public async Task SendAsync_WhenDisconnectFailsAfterSmtpAcceptance_ShouldStillReportAcceptance()
    {
        var smtp = Substitute.For<ISmtpClient>();
        smtp.SendAsync(Arg.Any<MimeMessage>(), Arg.Any<CancellationToken>(), Arg.Any<ITransferProgress>()).Returns(Task.FromResult("accepted"));
        smtp.DisconnectAsync(true, Arg.Any<CancellationToken>()).Returns(Task.FromException(new IOException("disconnect failed")));
        await using var channel = new EmailChannel(new EmailConfig
        {
            SmtpHost = "fixture", Username = "sender@example.test", PasswordRef = "fixture-password"
        }, NullLogger<EmailChannel>.Instance, () => smtp);
        await channel.SendAsync(new OutboundMessage
        {
            ChannelId = "email", RecipientId = "recipient@example.test", Text = "hello"
        }, TestContext.Current.CancellationToken);
    }
}
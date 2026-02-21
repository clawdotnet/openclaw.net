using System.Net;
using System.Net.Http;
using System.Text;
using OpenClaw.Channels;
using OpenClaw.Core.Contacts;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using Xunit;

namespace OpenClaw.Tests;

public sealed class TwilioSmsTests
{
    [Fact]
    public async Task Webhook_ValidSignature_Accepts()
    {
        var temp = CreateTempDir();
        var contacts = new FileContactStore(temp);

        var config = new TwilioSmsConfig
        {
            Enabled = true,
            ValidateSignature = true,
            WebhookPublicBaseUrl = "https://example.com",
            WebhookPath = "/twilio/sms/inbound",
            AllowedFromNumbers = ["+15551234567"],
            AllowedToNumbers = ["+15557654321"]
        };

        var handler = new TwilioSmsWebhookHandler(config, "token", contacts);
        var form = new Dictionary<string, string>
        {
            ["From"] = "+15551234567",
            ["To"] = "+15557654321",
            ["Body"] = "hello",
            ["MessageSid"] = "SM123"
        };

        var sig = TwilioWebhookVerifier.ComputeSignature(handler.PublicWebhookUrl, form, "token");

        var enqueued = false;
        var res = await handler.HandleAsync(
            form,
            sig,
            (msg, _) =>
            {
                enqueued = true;
                Assert.Equal("sms", msg.ChannelId);
                Assert.Equal("+15551234567", msg.SenderId);
                Assert.Equal("hello", msg.Text);
                Assert.Equal("SM123", msg.MessageId);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(200, res.StatusCode);
        Assert.True(enqueued);
    }

    [Fact]
    public async Task Webhook_InvalidSignature_Rejects()
    {
        var temp = CreateTempDir();
        var contacts = new FileContactStore(temp);

        var config = new TwilioSmsConfig
        {
            Enabled = true,
            ValidateSignature = true,
            WebhookPublicBaseUrl = "https://example.com",
            WebhookPath = "/twilio/sms/inbound",
            AllowedFromNumbers = ["+15551234567"],
            AllowedToNumbers = ["+15557654321"]
        };

        var handler = new TwilioSmsWebhookHandler(config, "token", contacts);
        var form = new Dictionary<string, string>
        {
            ["From"] = "+15551234567",
            ["To"] = "+15557654321",
            ["Body"] = "hello"
        };

        var res = await handler.HandleAsync(form, "bad", (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        Assert.Equal(401, res.StatusCode);
    }

    [Fact]
    public async Task Webhook_AllowlistEnforced()
    {
        var temp = CreateTempDir();
        var contacts = new FileContactStore(temp);

        var config = new TwilioSmsConfig
        {
            Enabled = true,
            ValidateSignature = false,
            AllowedFromNumbers = ["+15551234567"],
            AllowedToNumbers = ["+15557654321"],
            AutoReplyForBlocked = false
        };

        var handler = new TwilioSmsWebhookHandler(config, "token", contacts);
        var form = new Dictionary<string, string>
        {
            ["From"] = "+19999999999",
            ["To"] = "+15557654321",
            ["Body"] = "hello"
        };

        var res = await handler.HandleAsync(form, null, (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        Assert.Equal(401, res.StatusCode);
    }

    [Fact]
    public async Task Webhook_RateLimitPerFrom()
    {
        var temp = CreateTempDir();
        var contacts = new FileContactStore(temp);

        var config = new TwilioSmsConfig
        {
            Enabled = true,
            ValidateSignature = false,
            AllowedFromNumbers = ["+15551234567"],
            AllowedToNumbers = ["+15557654321"],
            RateLimitPerFromPerMinute = 1
        };

        var handler = new TwilioSmsWebhookHandler(config, "token", contacts);
        var form = new Dictionary<string, string>
        {
            ["From"] = "+15551234567",
            ["To"] = "+15557654321",
            ["Body"] = "hello"
        };

        var res1 = await handler.HandleAsync(form, null, (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        var res2 = await handler.HandleAsync(form, null, (_, _) => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal(200, res1.StatusCode);
        Assert.Equal(429, res2.StatusCode);
    }

    [Fact]
    public async Task Webhook_StopStartHelp_UpdateContactOrReply()
    {
        var temp = CreateTempDir();
        var contacts = new FileContactStore(temp);

        var config = new TwilioSmsConfig
        {
            Enabled = true,
            ValidateSignature = false,
            AllowedFromNumbers = ["+15551234567"],
            AllowedToNumbers = ["+15557654321"],
            HelpText = "help"
        };

        var handler = new TwilioSmsWebhookHandler(config, "token", contacts);

        var stop = new Dictionary<string, string> { ["From"] = "+15551234567", ["To"] = "+15557654321", ["Body"] = "STOP" };
        var help = new Dictionary<string, string> { ["From"] = "+15551234567", ["To"] = "+15557654321", ["Body"] = "HELP" };
        var start = new Dictionary<string, string> { ["From"] = "+15551234567", ["To"] = "+15557654321", ["Body"] = "START" };

        var resStop = await handler.HandleAsync(stop, null, (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        Assert.Equal(200, resStop.StatusCode);
        Assert.True((await contacts.GetAsync("+15551234567", CancellationToken.None))!.DoNotText);

        var resHelp = await handler.HandleAsync(help, null, (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        Assert.Equal(200, resHelp.StatusCode);
        Assert.Contains("<Message>help</Message>", resHelp.Body);

        var resStart = await handler.HandleAsync(start, null, (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        Assert.Equal(200, resStart.StatusCode);
        Assert.False((await contacts.GetAsync("+15551234567", CancellationToken.None))!.DoNotText);
    }

    [Fact]
    public async Task Outbound_UsesMessagingServiceSidWhenConfigured()
    {
        var config = new TwilioSmsConfig
        {
            Enabled = true,
            AccountSid = "AC123",
            MessagingServiceSid = "MG123",
            AllowedToNumbers = ["+15551234567"]
        };

        var handler = new CaptureHandler();
        var http = new HttpClient(handler);
        var contacts = new FileContactStore(CreateTempDir());

        var channel = new TwilioSmsChannel(config, "token", contacts, http);

        await channel.SendAsync(new OutboundMessage
        {
            ChannelId = "sms",
            RecipientId = "+15551234567",
            Text = "hi"
        }, CancellationToken.None);

        var body = handler.LastBody ?? "";
        Assert.Contains("MessagingServiceSid=MG123", body);
        Assert.Contains("To=%2B15551234567", body);
        Assert.Contains("Body=hi", body);
    }

    [Fact]
    public async Task Outbound_BlockedWhenDoNotText()
    {
        var temp = CreateTempDir();
        var contacts = new FileContactStore(temp);
        await contacts.SetDoNotTextAsync("+15551234567", true, CancellationToken.None);

        var config = new TwilioSmsConfig
        {
            Enabled = true,
            AccountSid = "AC123",
            MessagingServiceSid = "MG123",
            AllowedToNumbers = ["+15551234567"]
        };

        var handler = new CaptureHandler();
        var http = new HttpClient(handler);

        var channel = new TwilioSmsChannel(config, "token", contacts, http);

        await channel.SendAsync(new OutboundMessage
        {
            ChannelId = "sms",
            RecipientId = "+15551234567",
            Text = "hi"
        }, CancellationToken.None);

        Assert.Null(handler.LastBody);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"sid\":\"SM123\"}", Encoding.UTF8, "application/json")
            };
        }
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }
}

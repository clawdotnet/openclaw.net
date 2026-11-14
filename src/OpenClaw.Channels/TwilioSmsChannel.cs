using System.Collections.Concurrent;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Contacts;
using OpenClaw.Core.Models;

namespace OpenClaw.Channels;

public sealed class TwilioSmsChannel : IChannelAdapter
{
    private readonly TwilioSmsConfig _config;
    private readonly IContactStore _contacts;
    private readonly TwilioSmsClient _client;

    public TwilioSmsChannel(TwilioSmsConfig config, string authToken, IContactStore contacts, HttpClient httpClient)
    {
        _config = config;
        _contacts = contacts;
        _client = new TwilioSmsClient(httpClient, config, authToken);
    }

    public string ChannelId => "sms";

    public event Func<InboundMessage, CancellationToken, ValueTask>? OnMessageReceived;

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public ValueTask SendAsync(OutboundMessage message, CancellationToken ct)
    {
        var delivery = new ChannelDeliveryAttempt();
        return delivery.ExecuteAsync(() => SendCoreAsync(message, delivery, ct), ct);
    }

    private async Task SendCoreAsync(OutboundMessage message, ChannelDeliveryAttempt delivery, CancellationToken ct)
    {
        var to = message.RecipientId;
        if (!IsAllowedRecipient(to))
            throw new ChannelDeliverySuppressedException("Recipient is not allowed by SMS policy.");

        var contact = await _contacts.GetAsync(to, ct);
        if (contact?.DoNotText == true)
            throw new ChannelDeliverySuppressedException("Recipient opted out of SMS.");

        var (ok, result) = await _client.SendAsync(to, message.Text, ct);
        if (!ok)
            throw new ChannelDeliveryException("SMS send was not confirmed.", false, outcomeUnknown: true);
        delivery.RecordAccepted();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public async ValueTask RaiseInboundAsync(InboundMessage message, CancellationToken ct)
    {
        var handler = OnMessageReceived;
        if (handler is not null)
            await handler(message, ct);
    }

    private bool IsAllowedRecipient(string toE164)
    {
        if (_config.AllowedToNumbers.Length == 0)
            return false;

        return _config.AllowedToNumbers.Contains(toE164, StringComparer.Ordinal);
    }
}

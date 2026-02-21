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

    public async ValueTask SendAsync(OutboundMessage message, CancellationToken ct)
    {
        var to = message.RecipientId;
        if (!IsAllowedRecipient(to))
            return;

        var contact = await _contacts.GetAsync(to, ct);
        if (contact?.DoNotText == true)
            return;

        var (ok, result) = await _client.SendAsync(to, message.Text, ct);
        if (!ok)
            throw new InvalidOperationException(result);
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

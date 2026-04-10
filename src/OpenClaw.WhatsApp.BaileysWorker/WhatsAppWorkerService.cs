using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;

namespace OpenClaw.WhatsApp.BaileysWorker;

public sealed class WhatsAppWorkerService : IAsyncDisposable
{
    private const string ChannelId = "whatsapp";
    private IWhatsAppWorkerEngine? _engine;
    private WhatsAppFirstPartyWorkerConfig _config = new();

    public event Func<BridgeNotification, Task>? Notification;

    public Task<BridgeInitResult> InitializeAsync(BridgeInitRequest request)
    {
        _config = request.Config?.Deserialize(CoreJsonContext.Default.WhatsAppFirstPartyWorkerConfig)
            ?? new WhatsAppFirstPartyWorkerConfig();

        _engine = CreateEngine(_config);
        _engine.MessageReceived += HandleMessageAsync;
        _engine.AuthEventReceived += HandleAuthEventAsync;

        return Task.FromResult(new BridgeInitResult
        {
            Channels = [new BridgeChannelRegistration { Id = ChannelId }],
            Capabilities = [PluginCapabilityPolicy.Channels]
        });
    }

    public async Task<object> StartAsync(BridgeChannelControlRequest request)
    {
        EnsureChannel(request.ChannelId);
        var engine = EnsureEngine();
        var result = await engine.StartAsync(CancellationToken.None);
        return new
        {
            selfId = result.SelfIds.FirstOrDefault(),
            selfIds = result.SelfIds
        };
    }

    public async Task<object> StopAsync(BridgeChannelControlRequest request)
    {
        EnsureChannel(request.ChannelId);
        if (_engine is not null)
            await _engine.StopAsync(CancellationToken.None);
        return new { stopped = true };
    }

    public async Task<object> SendAsync(BridgeChannelSendRequest request)
    {
        EnsureChannel(request.ChannelId);
        await EnsureEngine().SendAsync(request, CancellationToken.None);
        return new { sent = true };
    }

    public async Task<object> SendTypingAsync(BridgeChannelTypingRequest request)
    {
        EnsureChannel(request.ChannelId);
        await EnsureEngine().SendTypingAsync(request, CancellationToken.None);
        return new { accepted = true };
    }

    public async Task<object> SendReadReceiptAsync(BridgeChannelReceiptRequest request)
    {
        EnsureChannel(request.ChannelId);
        await EnsureEngine().SendReadReceiptAsync(request, CancellationToken.None);
        return new { accepted = true };
    }

    public async Task<object> SendReactionAsync(BridgeChannelReactionRequest request)
    {
        EnsureChannel(request.ChannelId);
        await EnsureEngine().SendReactionAsync(request, CancellationToken.None);
        return new { accepted = true };
    }

    public async Task<object> DebugSimulateInboundAsync(JsonElement? payload)
    {
        await EnsureEngine().DebugSimulateInboundAsync(payload, CancellationToken.None);
        return new { accepted = true };
    }

    public async Task<object> DebugEmitAuthEventAsync(JsonElement? payload)
    {
        await EnsureEngine().DebugEmitAuthEventAsync(payload, CancellationToken.None);
        return new { accepted = true };
    }

    public object DebugGetState()
        => EnsureEngine().DebugGetState();

    public async Task<object> ShutdownAsync()
    {
        if (_engine is not null)
            await _engine.StopAsync(CancellationToken.None);
        return new { shutdown = true };
    }

    private async Task HandleMessageAsync(WhatsAppWorkerInboundMessage message)
    {
        if (Notification is null)
            return;

        var payload = JsonSerializer.SerializeToElement(new
        {
            channelId = ChannelId,
            senderId = message.SenderId,
            accountId = message.AccountId,
            senderName = message.SenderName,
            text = message.Text,
            sessionId = message.SessionId,
            messageId = message.MessageId,
            replyToMessageId = message.ReplyToMessageId,
            isGroup = message.IsGroup,
            groupId = message.GroupId,
            groupName = message.GroupName,
            mentionedIds = message.MentionedIds,
            mediaType = message.MediaType,
            mediaUrl = message.MediaUrl,
            mediaMimeType = message.MediaMimeType,
            mediaFileName = message.MediaFileName
        });

        await Notification(new BridgeNotification
        {
            Notification = "channel_message",
            Params = payload
        });
    }

    private Task HandleAuthEventAsync(BridgeChannelAuthEvent evt)
    {
        if (Notification is null)
            return Task.CompletedTask;

        var payload = JsonSerializer.SerializeToElement(new
        {
            channelId = ChannelId,
            state = evt.State,
            data = evt.Data,
            accountId = evt.AccountId,
            updatedAtUtc = evt.UpdatedAtUtc
        });

        return Notification(new BridgeNotification
        {
            Notification = "channel_auth_event",
            Params = payload
        });
    }

    private static IWhatsAppWorkerEngine CreateEngine(WhatsAppFirstPartyWorkerConfig config)
        => string.Equals(config.Driver, "simulated", StringComparison.OrdinalIgnoreCase)
            ? new SimulatedWhatsAppWorkerEngine(config)
            : throw new NotSupportedException(
                $"The in-process WhatsApp worker only supports Driver='simulated'. Driver='{config.Driver}' must be launched through the first-party worker host.");

    private IWhatsAppWorkerEngine EnsureEngine()
        => _engine ?? throw new InvalidOperationException("Worker was not initialized.");

    private static void EnsureChannel(string channelId)
    {
        if (!string.Equals(channelId, ChannelId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported channel '{channelId}'.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_engine is not null)
            await _engine.DisposeAsync();
    }
}

public sealed class WhatsAppWorkerInboundMessage
{
    public required string SenderId { get; init; }
    public string? AccountId { get; init; }
    public string? SenderName { get; init; }
    public string Text { get; init; } = "";
    public string? SessionId { get; init; }
    public string? MessageId { get; init; }
    public string? ReplyToMessageId { get; init; }
    public bool IsGroup { get; init; }
    public string? GroupId { get; init; }
    public string? GroupName { get; init; }
    public string[]? MentionedIds { get; init; }
    public string? MediaType { get; init; }
    public string? MediaUrl { get; init; }
    public string? MediaMimeType { get; init; }
    public string? MediaFileName { get; init; }
}

public sealed class WhatsAppWorkerStartResult
{
    public string[] SelfIds { get; init; } = [];
}

public interface IWhatsAppWorkerEngine : IAsyncDisposable
{
    event Func<WhatsAppWorkerInboundMessage, Task>? MessageReceived;
    event Func<BridgeChannelAuthEvent, Task>? AuthEventReceived;

    Task<WhatsAppWorkerStartResult> StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task SendAsync(BridgeChannelSendRequest request, CancellationToken ct);
    Task SendTypingAsync(BridgeChannelTypingRequest request, CancellationToken ct);
    Task SendReadReceiptAsync(BridgeChannelReceiptRequest request, CancellationToken ct);
    Task SendReactionAsync(BridgeChannelReactionRequest request, CancellationToken ct);
    Task DebugSimulateInboundAsync(JsonElement? payload, CancellationToken ct);
    Task DebugEmitAuthEventAsync(JsonElement? payload, CancellationToken ct);
    object DebugGetState();
}

internal sealed class SimulatedWhatsAppWorkerEngine : IWhatsAppWorkerEngine
{
    private readonly WhatsAppFirstPartyWorkerConfig _config;
    private readonly List<BridgeChannelSendRequest> _sends = [];
    private readonly List<BridgeChannelTypingRequest> _typings = [];
    private readonly List<BridgeChannelReceiptRequest> _receipts = [];
    private readonly List<BridgeChannelReactionRequest> _reactions = [];
    private int _startCount;
    private int _stopCount;

    public SimulatedWhatsAppWorkerEngine(WhatsAppFirstPartyWorkerConfig config)
    {
        _config = config;
    }

    public event Func<WhatsAppWorkerInboundMessage, Task>? MessageReceived;
    public event Func<BridgeChannelAuthEvent, Task>? AuthEventReceived;

    public async Task<WhatsAppWorkerStartResult> StartAsync(CancellationToken ct)
    {
        _startCount++;

        var selfIds = new List<string>();
        foreach (var account in _config.Accounts)
        {
            var selfId = $"{account.AccountId}@s.whatsapp.net";
            selfIds.Add(selfId);

            var state = string.Equals(account.PairingMode, "pairing_code", StringComparison.OrdinalIgnoreCase)
                ? "pairing_code"
                : "qr_code";
            var data = state == "pairing_code"
                ? $"PAIR-{account.AccountId}-{account.PhoneNumber ?? "unknown"}"
                : $"SIMULATED:{account.AccountId}";

            if (AuthEventReceived is not null)
            {
                await AuthEventReceived(new BridgeChannelAuthEvent
                {
                    ChannelId = "whatsapp",
                    AccountId = account.AccountId,
                    State = state,
                    Data = data,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                });
            }
        }

        return new WhatsAppWorkerStartResult { SelfIds = selfIds.ToArray() };
    }

    public Task StopAsync(CancellationToken ct)
    {
        _stopCount++;
        return Task.CompletedTask;
    }

    public Task SendAsync(BridgeChannelSendRequest request, CancellationToken ct)
    {
        _sends.Add(request);
        return Task.CompletedTask;
    }

    public Task SendTypingAsync(BridgeChannelTypingRequest request, CancellationToken ct)
    {
        _typings.Add(request);
        return Task.CompletedTask;
    }

    public Task SendReadReceiptAsync(BridgeChannelReceiptRequest request, CancellationToken ct)
    {
        _receipts.Add(request);
        return Task.CompletedTask;
    }

    public Task SendReactionAsync(BridgeChannelReactionRequest request, CancellationToken ct)
    {
        _reactions.Add(request);
        return Task.CompletedTask;
    }

    public async Task DebugSimulateInboundAsync(JsonElement? payload, CancellationToken ct)
    {
        if (payload is not { } element)
            throw new InvalidOperationException("Debug inbound payload is required.");

        var senderId = element.TryGetProperty("senderId", out var senderIdProp)
            ? senderIdProp.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(senderId))
            throw new InvalidOperationException("Debug inbound payload requires senderId.");

        var handler = MessageReceived;
        if (handler is not null)
        {
            string[]? mentionedIds = null;
            if (element.TryGetProperty("mentionedIds", out var mentionedProp) && mentionedProp.ValueKind == JsonValueKind.Array)
            {
                mentionedIds = mentionedProp.EnumerateArray()
                    .Select(static item => item.GetString())
                    .Where(static item => !string.IsNullOrWhiteSpace(item))
                    .Cast<string>()
                    .ToArray();
            }

            await handler(new WhatsAppWorkerInboundMessage
            {
                SenderId = senderId!,
                AccountId = element.TryGetProperty("accountId", out var accountIdProp) ? accountIdProp.GetString() : null,
                SenderName = element.TryGetProperty("senderName", out var senderNameProp) ? senderNameProp.GetString() : null,
                Text = element.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? "" : "",
                SessionId = element.TryGetProperty("sessionId", out var sessionIdProp) ? sessionIdProp.GetString() : null,
                MessageId = element.TryGetProperty("messageId", out var messageIdProp) ? messageIdProp.GetString() : null,
                ReplyToMessageId = element.TryGetProperty("replyToMessageId", out var replyToProp) ? replyToProp.GetString() : null,
                IsGroup = element.TryGetProperty("isGroup", out var isGroupProp) && isGroupProp.GetBoolean(),
                GroupId = element.TryGetProperty("groupId", out var groupIdProp) ? groupIdProp.GetString() : null,
                GroupName = element.TryGetProperty("groupName", out var groupNameProp) ? groupNameProp.GetString() : null,
                MentionedIds = mentionedIds,
                MediaType = element.TryGetProperty("mediaType", out var mediaTypeProp) ? mediaTypeProp.GetString() : null,
                MediaUrl = element.TryGetProperty("mediaUrl", out var mediaUrlProp) ? mediaUrlProp.GetString() : null,
                MediaMimeType = element.TryGetProperty("mediaMimeType", out var mediaMimeTypeProp) ? mediaMimeTypeProp.GetString() : null,
                MediaFileName = element.TryGetProperty("mediaFileName", out var mediaFileNameProp) ? mediaFileNameProp.GetString() : null
            });
        }
    }

    public async Task DebugEmitAuthEventAsync(JsonElement? payload, CancellationToken ct)
    {
        if (payload is not { } element)
            throw new InvalidOperationException("Debug auth payload is required.");

        if (AuthEventReceived is not null)
        {
            await AuthEventReceived(new BridgeChannelAuthEvent
            {
                ChannelId = "whatsapp",
                AccountId = element.TryGetProperty("accountId", out var accountIdProp) ? accountIdProp.GetString() : null,
                State = element.TryGetProperty("state", out var stateProp) ? stateProp.GetString() ?? "unknown" : "unknown",
                Data = element.TryGetProperty("data", out var dataProp) ? dataProp.GetString() : null,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
        }
    }

    public object DebugGetState()
        => new
        {
            driver = _config.Driver,
            startCount = _startCount,
            stopCount = _stopCount,
            sends = _sends,
            typings = _typings,
            receipts = _receipts,
            reactions = _reactions
        };

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class UnavailableBaileysWorkerEngine : IWhatsAppWorkerEngine
{
    private readonly WhatsAppFirstPartyWorkerConfig _config;

    public UnavailableBaileysWorkerEngine(WhatsAppFirstPartyWorkerConfig config)
    {
        _config = config;
    }

    public event Func<WhatsAppWorkerInboundMessage, Task>? MessageReceived
    {
        add { }
        remove { }
    }
    public event Func<BridgeChannelAuthEvent, Task>? AuthEventReceived;

    public async Task<WhatsAppWorkerStartResult> StartAsync(CancellationToken ct)
    {
        if (AuthEventReceived is not null)
        {
            foreach (var account in _config.Accounts.DefaultIfEmpty(new WhatsAppWorkerAccountConfig { AccountId = "default", SessionPath = "./session/default" }))
            {
                await AuthEventReceived(new BridgeChannelAuthEvent
                {
                    ChannelId = "whatsapp",
                    AccountId = account.AccountId,
                    State = "error",
                    Data = "BaileysCSharp transport adapter is not bundled in this build. Switch driver to 'simulated' for tests or supply a supported transport implementation.",
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                });
            }
        }

        return new WhatsAppWorkerStartResult();
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    public Task SendAsync(BridgeChannelSendRequest request, CancellationToken ct) => Task.CompletedTask;
    public Task SendTypingAsync(BridgeChannelTypingRequest request, CancellationToken ct) => Task.CompletedTask;
    public Task SendReadReceiptAsync(BridgeChannelReceiptRequest request, CancellationToken ct) => Task.CompletedTask;
    public Task SendReactionAsync(BridgeChannelReactionRequest request, CancellationToken ct) => Task.CompletedTask;
    public Task DebugSimulateInboundAsync(JsonElement? payload, CancellationToken ct) => Task.CompletedTask;
    public Task DebugEmitAuthEventAsync(JsonElement? payload, CancellationToken ct) => Task.CompletedTask;
    public object DebugGetState() => new { driver = _config.Driver };
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

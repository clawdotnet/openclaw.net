using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenClaw.Agent.Plugins;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;
using OpenClaw.WhatsApp.BaileysWorker;
using Xunit;

namespace OpenClaw.Tests;

[Collection(ResolverAccessorCollection.Name)]
public sealed class WhatsAppWorkerServiceTests : IDisposable
{
    public WhatsAppWorkerServiceTests() => ResolverAccessor.Reset();
    public void Dispose() => ResolverAccessor.Reset();

    [Fact]
    public void ResolveProxyReferences_ResolvesGlobalAndAccountReferences_WithoutMutatingInput()
    {
        UseResolver(reference => reference switch
        {
            "env:WA_PROXY" => "http://global-proxy.example.test:8080",
            "vault:secret/data/wa#account_proxy" => "http://account-proxy.example.test:8080",
            _ => null
        });
        var config = new WhatsAppFirstPartyWorkerConfig
        {
            Proxy = "env:WA_PROXY",
            Accounts =
            [
                new WhatsAppWorkerAccountConfig
                {
                    AccountId = "account-a",
                    Proxy = "vault:secret/data/wa#account_proxy"
                },
                new WhatsAppWorkerAccountConfig
                {
                    AccountId = "account-b",
                    Proxy = "http://proxy.example.test:8080"
                }
            ]
        };

        var resolved = FirstPartyWhatsAppWorkerHost.ResolveProxyReferences(config);

        Assert.Equal("http://global-proxy.example.test:8080", resolved.Proxy);
        Assert.Equal("http://account-proxy.example.test:8080", resolved.Accounts[0].Proxy);
        Assert.Equal("http://proxy.example.test:8080", resolved.Accounts[1].Proxy);
        Assert.Equal("env:WA_PROXY", config.Proxy);
        Assert.Equal("vault:secret/data/wa#account_proxy", config.Accounts[0].Proxy);
    }

    [Fact]
    public void ResolveProxyReferences_MissingReference_ThrowsWithoutEchoingValue()
    {
        UseResolver(_ => null);
        var config = new WhatsAppFirstPartyWorkerConfig
        {
            Accounts =
            [
                new WhatsAppWorkerAccountConfig
                {
                    AccountId = "account-a",
                    Proxy = "env:PRIVATE_PROXY_NAME"
                }
            ]
        };

        var error = Assert.Throws<InvalidOperationException>(
            () => FirstPartyWhatsAppWorkerHost.ResolveProxyReferences(config));

        Assert.Contains("Channels.WhatsApp.FirstPartyWorker.Accounts[0].Proxy", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_PROXY_NAME", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_SimulatedDriver_EmitsPerAccountAuthEvents_AndSelfIds()
    {
        await using var service = new WhatsAppWorkerService();
        var notifications = new List<BridgeNotification>();
        service.Notification += notification =>
        {
            notifications.Add(notification);
            return Task.CompletedTask;
        };

        _ = await service.InitializeAsync(new BridgeInitRequest
        {
            EntryPath = "builtin://test",
            PluginId = "worker-test",
            Config = JsonSerializer.SerializeToElement(new WhatsAppFirstPartyWorkerConfig
            {
                Driver = "simulated",
                Accounts =
                [
                    new WhatsAppWorkerAccountConfig
                    {
                        AccountId = "alpha",
                        SessionPath = "./session/alpha",
                        PairingMode = "qr"
                    },
                    new WhatsAppWorkerAccountConfig
                    {
                        AccountId = "beta",
                        SessionPath = "./session/beta",
                        PairingMode = "pairing_code",
                        PhoneNumber = "+15551234567"
                    }
                ]
            }, CoreJsonContext.Default.WhatsAppFirstPartyWorkerConfig)
        });

        var start = await service.StartAsync(new BridgeChannelControlRequest { ChannelId = "whatsapp" });
        var startJson = JsonSerializer.SerializeToElement(start);

        Assert.Equal("alpha@s.whatsapp.net", startJson.GetProperty("selfId").GetString());
        var selfIds = startJson.GetProperty("selfIds").EnumerateArray().Select(static item => item.GetString()).ToArray();
        Assert.Equal(new[] { "alpha@s.whatsapp.net", "beta@s.whatsapp.net" }, selfIds);

        var authEvents = notifications.Where(static notification => notification.Notification == "channel_auth_event").ToArray();
        Assert.Equal(2, authEvents.Length);
        Assert.Contains(authEvents, evt => evt.Params?.GetProperty("accountId").GetString() == "alpha" && evt.Params?.GetProperty("state").GetString() == "qr_code");
        Assert.Contains(authEvents, evt => evt.Params?.GetProperty("accountId").GetString() == "beta" && evt.Params?.GetProperty("state").GetString() == "pairing_code");
    }

    [Fact]
    public async Task DebugSimulateInboundAsync_EmitsChannelMessage_WithGroupAndMediaFields()
    {
        await using var service = new WhatsAppWorkerService();
        BridgeNotification? captured = null;
        service.Notification += notification =>
        {
            if (notification.Notification == "channel_message")
                captured = notification;
            return Task.CompletedTask;
        };

        _ = await service.InitializeAsync(new BridgeInitRequest
        {
            EntryPath = "builtin://test",
            PluginId = "worker-test",
            Config = JsonSerializer.SerializeToElement(new WhatsAppFirstPartyWorkerConfig
            {
                Driver = "simulated",
                Accounts = [new WhatsAppWorkerAccountConfig { AccountId = "default", SessionPath = "./session/default" }]
            }, CoreJsonContext.Default.WhatsAppFirstPartyWorkerConfig)
        });

        await service.DebugSimulateInboundAsync(JsonSerializer.SerializeToElement(new
        {
            senderId = "person@s.whatsapp.net",
            accountId = "default",
            senderName = "Person",
            text = "hello group",
            sessionId = "sess-group",
            messageId = "msg-1",
            replyToMessageId = "msg-0",
            isGroup = true,
            groupId = "group@g.us",
            groupName = "Team",
            mentionedIds = new[] { "default@s.whatsapp.net" },
            mediaType = "image",
            mediaUrl = "https://example.test/cat.jpg",
            mediaMimeType = "image/jpeg",
            mediaFileName = "cat.jpg"
        }));

        Assert.NotNull(captured);
        Assert.Equal("whatsapp", captured!.Params?.GetProperty("channelId").GetString());
        Assert.Equal("default", captured.Params?.GetProperty("accountId").GetString());
        Assert.Equal("group@g.us", captured.Params?.GetProperty("groupId").GetString());
        Assert.True(captured.Params?.GetProperty("isGroup").GetBoolean());
        Assert.Equal("image", captured.Params?.GetProperty("mediaType").GetString());
        Assert.Equal("https://example.test/cat.jpg", captured.Params?.GetProperty("mediaUrl").GetString());
    }

    [Fact]
    public async Task SimulatedDriver_RecordsSendTypingReceiptAndReactionRequests()
    {
        await using var service = new WhatsAppWorkerService();
        _ = await service.InitializeAsync(new BridgeInitRequest
        {
            EntryPath = "builtin://test",
            PluginId = "worker-test",
            Config = JsonSerializer.SerializeToElement(new WhatsAppFirstPartyWorkerConfig
            {
                Driver = "simulated",
                Accounts = [new WhatsAppWorkerAccountConfig { AccountId = "default", SessionPath = "./session/default" }]
            }, CoreJsonContext.Default.WhatsAppFirstPartyWorkerConfig)
        });

        _ = await service.StartAsync(new BridgeChannelControlRequest { ChannelId = "whatsapp" });
        _ = await service.SendAsync(new BridgeChannelSendRequest
        {
            ChannelId = "whatsapp",
            RecipientId = "group@g.us",
            AccountId = "default",
            Text = "hello",
            SessionId = "sess-1",
            ReplyToMessageId = "prev-1",
            Subject = "subject",
            Attachments =
            [
                new BridgeMediaAttachment
                {
                    Type = "image",
                    Url = "https://example.test/image.png"
                }
            ]
        });
        _ = await service.SendTypingAsync(new BridgeChannelTypingRequest
        {
            ChannelId = "whatsapp",
            RecipientId = "group@g.us",
            AccountId = "default",
            IsTyping = true
        });
        _ = await service.SendReadReceiptAsync(new BridgeChannelReceiptRequest
        {
            ChannelId = "whatsapp",
            MessageId = "prev-1",
            AccountId = "default"
        });
        _ = await service.SendReactionAsync(new BridgeChannelReactionRequest
        {
            ChannelId = "whatsapp",
            MessageId = "prev-1",
            AccountId = "default",
            Emoji = "👍"
        });

        var state = JsonSerializer.SerializeToElement(service.DebugGetState());
        Assert.Equal(1, state.GetProperty("startCount").GetInt32());
        Assert.Equal(1, state.GetProperty("sends").GetArrayLength());
        Assert.Equal(1, state.GetProperty("typings").GetArrayLength());
        Assert.Equal(1, state.GetProperty("receipts").GetArrayLength());
        Assert.Equal(1, state.GetProperty("reactions").GetArrayLength());
    }

    [Fact]
    public async Task InitializeAsync_NonSimulatedDriver_ThrowsNotSupportedException()
    {
        await using var service = new WhatsAppWorkerService();

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => service.InitializeAsync(new BridgeInitRequest
        {
            EntryPath = "builtin://test",
            PluginId = "worker-test",
            Config = JsonSerializer.SerializeToElement(new WhatsAppFirstPartyWorkerConfig
            {
                Driver = "baileys"
            }, CoreJsonContext.Default.WhatsAppFirstPartyWorkerConfig)
        }));

        Assert.Contains("Driver='simulated'", ex.Message, StringComparison.Ordinal);
    }

    private static void UseResolver(Func<string?, string?> resolve)
    {
        var resolver = new DelegateSecretResolver(resolve);
        ResolverAccessor.Use(new ServiceCollection()
            .AddSingleton<ISecretResolver>(resolver)
            .BuildServiceProvider());
    }

    private sealed class DelegateSecretResolver(Func<string?, string?> resolve) : ISecretResolver
    {
        public ValueTask<string?> ResolveAsync(string? secretRef, CancellationToken ct = default)
            => ValueTask.FromResult(resolve(secretRef));

        public string? Resolve(string? secretRef) => resolve(secretRef);

        public bool IsRawRef(string? secretRef) => SecretResolver.IsRawRef(secretRef);
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using Xunit;

namespace OpenClaw.Tests;

public sealed class OperatorRuntimeServicesTests
{
    [Fact]
    public void ProviderPolicyService_Resolve_UsesHighestPriorityExactMatch()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-ops-tests", Guid.NewGuid().ToString("N"));
        var service = new ProviderPolicyService(storagePath, NullLogger<ProviderPolicyService>.Instance);
        service.AddOrUpdate(new ProviderPolicyRule
        {
            Id = "low",
            Priority = 1,
            ChannelId = "telegram",
            ProviderId = "openai",
            ModelId = "gpt-low"
        });
        service.AddOrUpdate(new ProviderPolicyRule
        {
            Id = "high",
            Priority = 10,
            ChannelId = "telegram",
            SenderId = "user1",
            ProviderId = "openai",
            ModelId = "gpt-high"
        });

        var resolved = service.Resolve(
            new Session
            {
                Id = "sess1",
                ChannelId = "telegram",
                SenderId = "user1"
            },
            new LlmProviderConfig
            {
                Provider = "openai",
                Model = "default-model"
            });

        Assert.Equal("high", resolved.RuleId);
        Assert.Equal("gpt-high", resolved.ModelId);
    }

    [Fact]
    public void WebhookDeliveryStore_TracksDedupe_AndDeadLetterLifecycle()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-ops-tests", Guid.NewGuid().ToString("N"));
        var store = new WebhookDeliveryStore(storagePath, NullLogger<WebhookDeliveryStore>.Instance);

        Assert.True(store.TryBegin("telegram", "update-1", TimeSpan.FromMinutes(5)));
        Assert.False(store.TryBegin("telegram", "update-1", TimeSpan.FromMinutes(5)));

        store.RecordDeadLetter(new WebhookDeadLetterRecord
        {
            Entry = new WebhookDeadLetterEntry
            {
                Id = "dead_1",
                Source = "telegram",
                DeliveryKey = "update-1",
                Error = "boom",
                PayloadPreview = "{\"update_id\":1}"
            },
            ReplayMessage = new InboundMessage
            {
                ChannelId = "telegram",
                SenderId = "user1",
                Text = "hello"
            }
        });

        var listed = Assert.Single(store.List());
        Assert.Equal("dead_1", listed.Id);
        Assert.True(store.MarkReplayed("dead_1"));
        Assert.True(store.MarkDiscarded("dead_1"));
        var updated = store.Get("dead_1");
        Assert.NotNull(updated);
        Assert.True(updated!.Entry.Discarded);
        Assert.NotNull(updated.Entry.ReplayedAtUtc);
    }

    [Fact]
    public void ActorRateLimitService_TryConsume_EnforcesConfiguredWindow()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-ops-tests", Guid.NewGuid().ToString("N"));
        var service = new ActorRateLimitService(storagePath, NullLogger<ActorRateLimitService>.Instance);
        service.AddOrUpdate(new ActorRateLimitPolicy
        {
            Id = "rl_1",
            ActorType = "ip",
            EndpointScope = "openai_http",
            BurstLimit = 2,
            BurstWindowSeconds = 60,
            SustainedLimit = 10,
            SustainedWindowSeconds = 600
        });

        Assert.True(service.TryConsume("ip", "127.0.0.1", "openai_http", out _));
        Assert.True(service.TryConsume("ip", "127.0.0.1", "openai_http", out _));
        Assert.False(service.TryConsume("ip", "127.0.0.1", "openai_http", out var blockedByPolicyId));
        Assert.Equal("rl_1", blockedByPolicyId);
        Assert.NotEmpty(service.SnapshotActive());
    }

    [Fact]
    public void PluginHealthService_IncludesPersistedStateWithoutRuntimeReport()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-ops-tests", Guid.NewGuid().ToString("N"));
        var service = new PluginHealthService(storagePath, NullLogger<PluginHealthService>.Instance);
        service.SetDisabled("plugin.alpha", disabled: true, reason: "maintenance");

        var snapshot = Assert.Single(service.ListSnapshots());
        Assert.Equal("plugin.alpha", snapshot.PluginId);
        Assert.True(snapshot.Disabled);
        Assert.False(snapshot.Loaded);
        Assert.Equal("maintenance", snapshot.PendingReason);
    }
}

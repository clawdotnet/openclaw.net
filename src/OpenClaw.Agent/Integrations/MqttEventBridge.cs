using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;

namespace OpenClaw.Agent.Integrations;

public sealed class MqttEventBridge : BackgroundService
{
    private readonly MqttConfig _config;
    private readonly ILogger<MqttEventBridge> _logger;
    private readonly ChannelWriter<InboundMessage> _inbound;

    private readonly Dictionary<string, DateTimeOffset> _cooldowns = new(StringComparer.Ordinal);

    public MqttEventBridge(MqttConfig config, ILogger<MqttEventBridge> logger, ChannelWriter<InboundMessage> inbound)
    {
        _config = config;
        _logger = logger;
        _inbound = inbound;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.Enabled || _config.Events.Enabled != true || _config.Events.Subscriptions.Count == 0)
        {
            _logger.LogInformation("MQTT event bridge disabled.");
            return;
        }

        var backoff = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
                backoff = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MQTT event bridge error; reconnecting in {Delay}s", backoff.TotalSeconds);
                await Task.Delay(backoff, stoppingToken);
                backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var client = OpenClawMqttClientFactory.CreateClient();
        var options = OpenClawMqttClientFactory.CreateOptions(_config);

        client.ApplicationMessageReceivedAsync += async e =>
        {
            try
            {
                await HandleMessageAsync(e, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MQTT message handler failed");
            }
        };

        await client.ConnectAsync(options, ct);

        foreach (var sub in _config.Events.Subscriptions)
        {
            if (string.IsNullOrWhiteSpace(sub.Topic))
                continue;

            if (!GlobMatcher.IsAllowed(_config.Policy.AllowSubscribeTopicGlobs, _config.Policy.DenySubscribeTopicGlobs, sub.Topic))
            {
                _logger.LogWarning("MQTT subscription topic denied by policy: {Topic}", sub.Topic);
                continue;
            }

            var qos = Math.Clamp(sub.Qos, 0, 2);
            var filter = new MqttTopicFilterBuilder()
                .WithTopic(sub.Topic)
                .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)qos)
                .Build();

            await client.SubscribeAsync(filter, ct);
        }

        _logger.LogInformation("MQTT event bridge connected; subscribed to {Count} patterns.", _config.Events.Subscriptions.Count);

        while (!ct.IsCancellationRequested && client.IsConnected)
        {
            await Task.Delay(500, ct);
        }
    }

    private async Task HandleMessageAsync(MqttApplicationMessageReceivedEventArgs e, CancellationToken ct)
    {
        var topic = e.ApplicationMessage.Topic ?? "";
        if (string.IsNullOrWhiteSpace(topic))
            return;

        // Enforce subscribe policy on the actual topic too (defense-in-depth)
        if (!GlobMatcher.IsAllowed(_config.Policy.AllowSubscribeTopicGlobs, _config.Policy.DenySubscribeTopicGlobs, topic))
            return;

        var payloadBytes = PayloadToArray(e.ApplicationMessage.Payload);
        if (payloadBytes.Length > _config.MaxPayloadBytes)
            payloadBytes = payloadBytes[.._config.MaxPayloadBytes];

        var payload = Encoding.UTF8.GetString(payloadBytes);
        MqttMessageCache.Set(topic, payload);

        var sub = FindSubscriptionForTopic(topic);
        if (sub is null)
            return;

        var now = DateTimeOffset.UtcNow;
        if (!TryConsumeCooldown(sub, now))
            return;

        var template = string.IsNullOrWhiteSpace(sub.PromptTemplate)
            ? "MQTT message on {topic}: {payload}"
            : sub.PromptTemplate;

        var text = template
            .Replace("{topic}", topic, StringComparison.Ordinal)
            .Replace("{payload}", payload, StringComparison.Ordinal);

        var msg = new InboundMessage
        {
            ChannelId = _config.Events.ChannelId,
            SessionId = _config.Events.SessionId,
            SenderId = "system",
            Text = text
        };

        await _inbound.WriteAsync(msg, ct);
    }

    private MqttSubscriptionConfig? FindSubscriptionForTopic(string actualTopic)
    {
        // Simple: first configured subscription where the MQTT wildcard pattern matches.
        foreach (var sub in _config.Events.Subscriptions)
        {
            if (string.IsNullOrWhiteSpace(sub.Topic))
                continue;

            if (MqttTopicFilterComparer.Compare(actualTopic, sub.Topic) == MqttTopicFilterCompareResult.IsMatch)
                return sub;
        }

        return null;
    }

    private bool TryConsumeCooldown(MqttSubscriptionConfig sub, DateTimeOffset now)
    {
        var cooldown = TimeSpan.FromSeconds(Math.Max(0, sub.CooldownSeconds));
        if (cooldown <= TimeSpan.Zero)
            return true;

        var key = $"sub:{sub.Topic}";
        if (_cooldowns.TryGetValue(key, out var last) && (now - last) < cooldown)
            return false;

        _cooldowns[key] = now;
        return true;
    }

    private static byte[] PayloadToArray(System.Buffers.ReadOnlySequence<byte> payload)
    {
        if (payload.IsSingleSegment)
            return payload.FirstSpan.ToArray();

        var bytes = new byte[(int)payload.Length];
        var offset = 0;
        foreach (var segment in payload)
        {
            segment.Span.CopyTo(bytes.AsSpan(offset));
            offset += segment.Length;
        }
        return bytes;
    }
}

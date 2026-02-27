using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Protocol;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;

namespace OpenClaw.Agent.Tools;

public sealed class MqttTool : ITool
{
    private readonly MqttConfig _config;

    public MqttTool(MqttConfig config) => _config = config;

    public string Name => "mqtt";

    public string Description =>
        "Subscribe to MQTT topics (read-only). Use subscribe_once for a single message or get_last when event bridge is enabled.";

    public string ParameterSchema => """
        {
          "type": "object",
          "properties": {
            "op": { "type": "string", "enum": ["subscribe_once","get_last"] },
            "topic": { "type": "string" },
            "timeout_ms": { "type": "integer", "default": 5000 },
            "qos": { "type": "integer", "minimum": 0, "maximum": 2, "default": 0 }
          },
          "required": ["op","topic"]
        }
        """;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var args = JsonDocument.Parse(argumentsJson);
        var root = args.RootElement;

        var op = root.GetProperty("op").GetString() ?? "";
        var topic = root.GetProperty("topic").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(topic))
            return "Error: topic is required.";

        return op switch
        {
            "subscribe_once" => await SubscribeOnceAsync(root, topic, ct),
            "get_last" => GetLast(topic),
            _ => $"Error: Unknown op '{op}'."
        };
    }

    private string GetLast(string topicOrGlob)
    {
        if (topicOrGlob.Contains('*', StringComparison.Ordinal))
        {
            var matches = MqttMessageCache.FindByGlob(topicOrGlob);
            if (matches.Count == 0)
                return "No cached messages matched.";

            var sb = new StringBuilder();
            foreach (var (topic, topicPayload, topicReceivedAt) in matches.Take(10))
            {
                sb.AppendLine($"topic: {topic}");
                sb.AppendLine($"received_at: {topicReceivedAt:O}");
                sb.AppendLine(topicPayload);
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }

        var (found, payload, receivedAt) = MqttMessageCache.TryGet(topicOrGlob);
        if (!found)
            return "No cached message for topic. Enable Mqtt.Events.Enabled to cache subscriptions.";

        return $"topic: {topicOrGlob}\nreceived_at: {receivedAt:O}\n{payload}";
    }

    private async Task<string> SubscribeOnceAsync(JsonElement root, string topic, CancellationToken ct)
    {
        if (!GlobMatcher.IsAllowed(_config.Policy.AllowSubscribeTopicGlobs, _config.Policy.DenySubscribeTopicGlobs, topic))
            return $"Error: Subscribe to topic '{topic}' is not allowed by policy.";

        var timeoutMs = root.TryGetProperty("timeout_ms", out var tm) ? tm.GetInt32() : 5000;
        timeoutMs = Math.Clamp(timeoutMs, 100, 120_000);
        var qos = root.TryGetProperty("qos", out var q) ? q.GetInt32() : 0;
        qos = Math.Clamp(qos, 0, 2);

        using var client = OpenClawMqttClientFactory.CreateClient();
        var options = OpenClawMqttClientFactory.CreateOptions(_config);

        var tcs = new TaskCompletionSource<(string topic, string payload)>(TaskCreationOptions.RunContinuationsAsynchronously);

        client.ApplicationMessageReceivedAsync += e =>
        {
            try
            {
                var payloadBytes = e.ApplicationMessage is null
                    ? Array.Empty<byte>()
                    : PayloadToArray(e.ApplicationMessage.Payload);

                var text = Encoding.UTF8.GetString(payloadBytes);
                tcs.TrySetResult((e.ApplicationMessage?.Topic ?? topic, text));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }

            return Task.CompletedTask;
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

        await client.ConnectAsync(options, cts.Token);

        var filter = new MqttTopicFilterBuilder()
            .WithTopic(topic)
            .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)qos)
            .Build();

        await client.SubscribeAsync(filter, cts.Token);

        try
        {
            var (rxTopic, rxPayload) = await tcs.Task.WaitAsync(cts.Token);
            return $"topic: {rxTopic}\n{rxPayload}";
        }
        finally
        {
            try { await client.DisconnectAsync(new MQTTnet.MqttClientDisconnectOptions(), cts.Token); } catch { /* ignore */ }
        }
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

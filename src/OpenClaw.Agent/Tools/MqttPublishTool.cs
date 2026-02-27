using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Protocol;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;

namespace OpenClaw.Agent.Tools;

public sealed class MqttPublishTool : ITool
{
    private readonly MqttConfig _config;

    public MqttPublishTool(MqttConfig config) => _config = config;

    public string Name => "mqtt_publish";

    public string Description => "Publish MQTT messages (write operations). Use with care.";

    public string ParameterSchema => """
        {
          "type": "object",
          "properties": {
            "op": { "type": "string", "enum": ["publish"] },
            "topic": { "type": "string" },
            "payload": { "type": "string" },
            "qos": { "type": "integer", "minimum": 0, "maximum": 2, "default": 0 },
            "retain": { "type": "boolean", "default": false }
          },
          "required": ["op","topic","payload"]
        }
        """;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var args = JsonDocument.Parse(argumentsJson);
        var root = args.RootElement;

        var op = root.GetProperty("op").GetString() ?? "";
        if (!string.Equals(op, "publish", StringComparison.Ordinal))
            return $"Error: Unknown op '{op}'.";

        var topic = root.GetProperty("topic").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(topic))
            return "Error: topic is required.";

        if (!GlobMatcher.IsAllowed(_config.Policy.AllowPublishTopicGlobs, _config.Policy.DenyPublishTopicGlobs, topic))
            return $"Error: Publish to topic '{topic}' is not allowed by policy.";

        var payload = root.GetProperty("payload").GetString() ?? "";
        var qos = root.TryGetProperty("qos", out var q) ? q.GetInt32() : 0;
        qos = Math.Clamp(qos, 0, 2);
        var retain = root.TryGetProperty("retain", out var r) && r.ValueKind == JsonValueKind.True;

        using var client = OpenClawMqttClientFactory.CreateClient();
        var options = OpenClawMqttClientFactory.CreateOptions(_config);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _config.TimeoutSeconds)));

        await client.ConnectAsync(options, cts.Token);

        try
        {
            var msg = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(Encoding.UTF8.GetBytes(payload))
                .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)qos)
                .WithRetainFlag(retain)
                .Build();

            await client.PublishAsync(msg, cts.Token);
        }
        finally
        {
            try { await client.DisconnectAsync(new MQTTnet.MqttClientDisconnectOptions(), cts.Token); } catch { /* ignore */ }
        }

        return "OK";
    }
}

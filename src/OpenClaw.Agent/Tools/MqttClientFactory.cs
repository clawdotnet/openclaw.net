using MQTTnet;
using MQTTnet.Formatter;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;

namespace OpenClaw.Agent.Tools;

internal static class OpenClawMqttClientFactory
{
    public static IMqttClient CreateClient() => new MqttClientFactory().CreateMqttClient();

    public static MqttClientOptions CreateOptions(MqttConfig config)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithClientId(string.IsNullOrWhiteSpace(config.ClientId) ? "openclaw" : config.ClientId)
            .WithTcpServer(config.Host, config.Port)
            .WithProtocolVersion(MqttProtocolVersion.V500);

        var username = SecretResolver.Resolve(config.UsernameRef);
        var password = SecretResolver.Resolve(config.PasswordRef);
        if (!string.IsNullOrWhiteSpace(username))
            builder = builder.WithCredentials(username, password);

        if (config.UseTls)
        {
            builder = builder.WithTlsOptions(new MqttClientTlsOptions
            {
                UseTls = true
            });
        }

        return builder.Build();
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Skills.Meta;
using RedNb.Nacos.DependencyInjection;
namespace OpenClaw.Adapters.Nacos.Events;

public static class NacosEventRegistration
{
    public static void Add(IServiceCollection services, IReadOnlyDictionary<string, JsonElement> settings)
    {
        if (!settings.TryGetValue("nacos", out var raw)) return;
        var options = raw.Deserialize(NacosSettingsJson.Default.NacosOptions) ?? new();
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.ServerAddr)) return;
        services.AddSingleton(options);
        services.AddNacosConfig(o => { o.ServerAddresses = options.ServerAddr; o.Username = options.Username; o.Password = options.Password; o.LongPollTimeout = options.LongPollingTimeoutMs; o.DefaultTimeout = options.LongPollingTimeoutMs; });
        services.AddSingleton<INacosConfigService>(sp => new RedNbNacosConfigService(sp.GetRequiredService<RedNb.Nacos.Config.IConfigService>(), options, sp.GetRequiredService<ILogger<RedNbNacosConfigService>>()));
        services.AddSingleton<ICapabilityChangeSource, NacosConfigSubscriptionService>();
    }
}
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NacosOptions))]
internal sealed partial class NacosSettingsJson : JsonSerializerContext;

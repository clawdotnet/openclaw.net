using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;

namespace OpenClaw.PluginKit;

public interface INativeDynamicPlugin
{
    void Register(INativeDynamicPluginContext context);
}

public interface INativeDynamicPluginContext
{
    string PluginId { get; }
    JsonElement? Config { get; }
    ILogger Logger { get; }

    void RegisterTool(ITool tool);
    void RegisterChannel(IChannelAdapter adapter);
    void RegisterCommand(string name, string description, Func<string, CancellationToken, Task<string>> handler);
    void RegisterProvider(string providerId, string[] models, IChatClient client);
    void RegisterMemoryProvider(string providerId, Func<NativeDynamicMemoryProviderContext, IMemoryStore> factory);
    void RegisterHook(IToolHook hook);
    void RegisterService(INativeDynamicPluginService service);
    void RegisterResultInterceptor(IToolResultInterceptor interceptor);
}

public sealed class NativeDynamicMemoryProviderContext
{
    public required string PluginId { get; init; }
    public required string ProviderId { get; init; }
    public JsonElement? Config { get; init; }
    public required GatewayConfig GatewayConfig { get; init; }
    public required RuntimeMetrics Metrics { get; init; }
    public required ILogger Logger { get; init; }
}

public interface INativeDynamicPluginService
{
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}

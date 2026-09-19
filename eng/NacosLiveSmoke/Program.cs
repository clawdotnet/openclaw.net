using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Adapters.Nacos;
using OpenClaw.Adapters.Nacos.Events;
using OpenClaw.Agent;
using OpenClaw.Agent.Plugins;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Skills;
using OpenClaw.Core.Skills.Meta;
using RedNb.Nacos.Config;

// Only run against an isolated acceptance deployment. The unique dataId is removed on exit.
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var ct = timeout.Token;
var dataId = "openclaw-acceptance-" + Guid.NewGuid().ToString("N");
var options = new NacosOptions
{
    ServerAddr = Required("OPENCLAW_NACOS_SERVER"), DataId = dataId,
    Username = "env:OPENCLAW_NACOS_USERNAME", Password = "env:OPENCLAW_NACOS_PASSWORD"
};
var settings = new Dictionary<string, JsonElement>
{
    ["nacos"] = JsonSerializer.SerializeToElement(options, SmokeJson.Default.NacosOptions)
};
var cache = new CapabilityBindingCache();
var services = new ServiceCollection();
services.AddLogging(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning));
services.AddSingleton<ICapabilityInvalidationSink>(cache);
NacosEventRegistration.Add(services, settings);
await using var host = services.BuildServiceProvider();
var config = host.GetRequiredService<IConfigService>();
var subscription = host.GetRequiredService<ICapabilityChangeSource>();
await using var registry = new McpServerToolRegistry(new McpPluginsConfig(), NullLogger<McpServerToolRegistry>.Instance);
try
{
    Check(!JsonSerializer.IsReflectionEnabledByDefault, "JSON reflection must remain disabled");
    Check(await config.PublishConfigAsync(dataId, options.Group, "initial", ct), "initial config publish failed");
    await subscription.StartAsync(ct);
    await WaitUntil(() => subscription.Status == "active", TimeSpan.FromSeconds(30), ct);
    var tools = await registry.ReloadWorkspaceServersAsync(new Dictionary<string, McpServerConfig>
    {
        ["nacos-mcp-router"] = new() { Enabled = true, Transport = "http", Url = Required("OPENCLAW_NACOS_ROUTER_URL"), ToolNamePrefix = "nacos_mcp_router_" }
    }, ct);
    Check(tools.AddedTools.Count == 3, "Router must expose exactly three tools");
    var providers = new CapabilityProviderRegistry([new NacosCapabilityProvider(registry)]);
    var slots = new CapabilitySlotExecutor(providers, cache);
    var executor = new OpenClawToolExecutor(tools.AddedTools, 30, false, [], []);
    var session = new Session { Id = dataId, SenderId = "acceptance", ChannelId = "acceptance" };
    var turn = new TurnContext { SessionId = session.Id };
    MetaCapabilityRefDefinition[] references =
    [
        new() { Provider = "nacos", Binding = "static", Static = new() { Target = "weather-mcp", ToolName = "get_weather" } },
        new() { Provider = "nacos", Binding = "dynamic", Intent = new() { TaskDescription = "weather city", Keywords = ["weather", "city"] } }
    ];
    var checks = new List<string>();
    async Task Run(MetaCapabilityRefDefinition reference, bool hit)
    {
        var result = await slots.ExecuteGovernedAsync(reference, """{"city":"Oslo"}""", session, turn, executor, Guid.NewGuid().ToString("N"), ct);
        Check(result.ResultStatus == "completed", $"{reference.Binding}: {result.FailureCode}: {result.ResultText}");
        Check(result.BindingTrajectory?.CacheHit == hit, $"{reference.Binding}: expected cacheHit={hit}");
        Check(result.BindingTrajectory!.Revision == cache.Generation, "stale binding generation");
        using var weather = JsonDocument.Parse(result.ResultText);
        Check(weather.RootElement.GetProperty("city").GetString() == "Oslo", "wrong weather city");
        Check(weather.RootElement.GetProperty("temperature_c").ValueKind == JsonValueKind.Number, "weather payload missing temperature");
        checks.Add(reference.Binding + (hit ? ":cache-hit" : ":bound"));
    }
    foreach (var reference in references) { await Run(reference, false); await Run(reference, true); }
    var generation = cache.Generation;
    var elapsed = Stopwatch.StartNew();
    Check(await config.PublishConfigAsync(dataId, options.Group, Guid.NewGuid().ToString("N"), ct), "config update failed");
    await WaitUntil(() => cache.Generation > generation, TimeSpan.FromSeconds(2), ct);
    elapsed.Stop();
    Check(elapsed.ElapsedMilliseconds <= 2000, "publish-to-invalidation exceeded two seconds");
    Check(cache.Count == 0, "event did not clear warmed static and dynamic bindings");
    foreach (var reference in references) await Run(reference, false);
    Check(turn.LlmCallCount == 0, "capability path made an LLM call");
    var report = new SmokeReport
    {
        NativeAot = !RuntimeFeature.IsDynamicCodeSupported,
        JsonReflection = JsonSerializer.IsReflectionEnabledByDefault,
        PublishToInvalidationMs = elapsed.Elapsed.TotalMilliseconds,
        Generation = cache.Generation, Checks = checks.ToArray()
    };
    var json = JsonSerializer.Serialize(report, SmokeJson.Default.SmokeReport);
    if (Environment.GetEnvironmentVariable("OPENCLAW_NACOS_REPORT") is { Length: > 0 } path) await File.WriteAllTextAsync(path, json, ct);
    Console.WriteLine("NACOS_ACCEPTANCE_PASS " + json);
}
finally
{
    // Do not let cleanup hide an acceptance failure or outlive the bounded test run.
    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    try { await config.RemoveConfigAsync(dataId, options.Group, cleanup.Token); } catch (Exception ex) { Console.Error.WriteLine("Config cleanup: " + ex.GetType().Name); }
}

static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException("Set " + name);
static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static async Task WaitUntil(Func<bool> ready, TimeSpan timeout, CancellationToken ct)
{
    var elapsed = Stopwatch.StartNew();
    while (!ready())
    {
        if (elapsed.Elapsed > timeout) throw new TimeoutException("Nacos acceptance condition timed out");
        await Task.Delay(10, ct);
    }
}
internal sealed class SmokeReport
{
    public bool NativeAot { get; set; }
    public bool JsonReflection { get; set; }
    public double PublishToInvalidationMs { get; set; }
    public long Generation { get; set; }
    public string[] Checks { get; set; } = [];
}
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SmokeReport))]
[JsonSerializable(typeof(NacosOptions))]
internal partial class SmokeJson : JsonSerializerContext;

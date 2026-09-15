using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenClaw.Agent;
using OpenClaw.Agent.Plugins;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Plugins;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Mcp;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// Issue #238: when <see cref="McpWorkspaceWatcherService"/> reloads (file change
/// or Nacos publish event), both the session-level <see cref="CapabilityBindingCache"/>
/// AND the runtime-level <c>_addedServers</c> in <see cref="CapabilitySlotExecutor"/>
/// must be wiped. NSubstitute on <see cref="IAgentRuntime"/> lets us assert the
/// runtime clear fired without spinning up a real capability slot.
/// </summary>
public sealed class NacosWorkspaceWatcherRuntimeCacheTests
{
    [Fact]
    public async Task TriggerReload_ClearsBindingCacheAndRuntimeAddCache()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "openclaw-nacos-watcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var registry = new McpServerToolRegistry(new McpPluginsConfig(), NullLogger<McpServerToolRegistry>.Instance);
            var runtime = Substitute.For<IAgentRuntime>();
            var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.ClearCapabilitySlotRuntimeCacheAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            {
                reloaded.TrySetResult();
                return Task.CompletedTask;
            });
            var bindingCache = new CapabilityBindingCache();
            // Real McpConfigStore (internal sealed → NSubstitute/Castle cannot proxy it)
            // pointing at an empty temp dir: TryLoadServersAsync returns null and the
            // watcher reloads an empty registry — exercising the same code path.
            var configStore = new McpConfigStore(tempDir, NullLogger<McpConfigStore>.Instance);

            var intentKey = CapabilityBindingCache.ComputeIntentKey("weather", "city", "First");
            bindingCache.Set("s1", intentKey, "weather-mcp", "get_weather");
            Assert.True(bindingCache.TryGet("s1", intentKey, out _, out _));

            await using var watcher = new McpWorkspaceWatcherService(
                registry, runtime, workspacePath: null,
                NullLogger<McpWorkspaceWatcherService>.Instance,
                configStore, bindingCache);

            watcher.Start(CancellationToken.None);
            // Observe completion of the actual reload instead of relying on scheduler timing.
            await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.False(bindingCache.TryGet("s1", intentKey, out _, out _));
            await runtime.Received().ClearCapabilitySlotRuntimeCacheAsync(Arg.Any<CancellationToken>());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}
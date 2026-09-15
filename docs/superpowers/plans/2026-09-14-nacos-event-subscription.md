# Nacos 配置变更事件订阅 Implementation Plan (issue #238)

> Historical implementation plan. Retained task snippets and checkboxes are non-normative; the implementation has since been refactored. See [the current capability-resolution contract](../../capability-resolution.md) for supported behavior and remaining live/NativeAOT acceptance.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 通过 `RedNb.Nacos.All 2.0.0` LongPolling 订阅 mcp.json dataId，在 Nacos 配置变更到达时即时清空 `CapabilityBindingCache` 与运行时级 `_addedServers`，并触发现有 `McpWorkspaceWatcherService` reload。

**Architecture:**
- 在 Gateway 内抽象 `INacosConfigService`（数据模型 + 订阅句柄），生产实现 `RedNbNacosConfigService` 包装 `RedNb.Nacos.INacosConfigService.AddListener`/`GetConfig`，测试替身 `FakeNacosConfigService`（沿用 `FakeNacosRouterMcpTools` 模式）。
- 新增 `NacosConfigSubscriptionService`（`IAsyncDisposable`），启动时注册 listener，收到 `onChange` 回调调用 `McpWorkspaceWatcherService.TriggerReload()`。
- `CapabilitySlotExecutor` 暴露 `ClearRuntimeCache()` 方法与 `AddedServerCount` 观测属性；`IAgentRuntime` 新增 `ClearCapabilitySlotRuntimeCacheAsync()`；watcher reload 完成后既清 `_bindingCache` 也清运行时级 `_addedServers`。
- Nacos 不可达或 `NacosOptions.ServerAddr` 为空时优雅降级（不注册 listener，维持 TTL/reload 兜底）。

**Tech Stack:** .NET 10 / C# 14 / `RedNb.Nacos.All 2.0.0`（生产）+ `Microsoft.Extensions.Logging.Abstractions` + 既有 `OpenClaw.Agent.Tools.CapabilitySlotExecutor`/`McpWorkspaceWatcherService`。

**Spec:** `E:\GitHub\openclaw.net\C:\Users\geffz\AppData\Local\Temp\issue-238-body.md`（issue #238 body）+ `docs/zh-CN/Nacos-MCP-Router与OpenClaw.NET-MetaSkill集成架构.md` §7.3/§8（status 句）。AOT 兼容性同既有 `OpenClaw.Gateway` 约束（`dotnet publish -c Release -r linux-x64 -p:PublishAot=true` 必须保持绿）。

## Global Constraints

- `McpWorkspaceWatcherService.cs:108-146` 的 `RunReloadLoopAsync` 是已有 reload 入口；新订阅必须收敛到它（不另起 reload 路径）。
- `CapabilitySlotExecutor.cs:37` 的 `_addedServers` 是 `private readonly ConcurrentDictionary<string, byte>`，外部不感知；只新增 `public void ClearRuntimeCache()` + `internal int AddedServerCount { get; }`。
- `IAgentRuntime.ApplyMcpToolChangesAsync` 已有默认实现 `=> Task.CompletedTask`，新方法 `ClearCapabilitySlotRuntimeCacheAsync` 同样给默认实现以保持测试替身兼容。
- `RedNb.Nacos.All 2.0.0` 包对 `OpenClaw.Gateway.csproj` 引入；对 `OpenClaw.Tests` **不引入**（测试用 `FakeNacosConfigService`）。
- **凭据 / secret**：`NacosOptions` 来自 `GatewayConfig.NacosOptions` 段（不存在时整段降级），用户名/密码来自既有 Nacos 配置同源（#229 PoC），不出现新凭据；本地 Nacos 部署密码（`"nacos"`）不入库、不贴 issue 评论、不贴提交信息；测试 secret 用 `"test-user"`/`"test-password"` 这类伪值。
- **AOT 约束**：`RedNb.Nacos.All` 若引发 AOT 警告，按 `DynamicallyAccessedMembers` / `RequiresUnreferencedCode` 收口；不允许 `NoWarn` 大范围屏蔽。若不可调和，在 adapter 类型上标 `[RequiresUnreferencedCode]` 并将 `OpenClaw.Gateway` 的 AOT 验证拆为 with/without Nacos 两段（adapter 单独 published 时失败可接受，但默认 publish 路径必须绿）。
- **既有行为不变**：`McpWorkspaceWatcherService` 启动时 `TriggerReload()`（首次 reload）必须照旧；新订阅只是额外加一条触发路径。
- **commit 结尾**：每条提交信息附 `Co-Authored-By: Claude Code <noreply@anthropic.com>`。

---

## File Structure

新增文件：
- `src/OpenClaw.Gateway/Mcp/Nacos/NacosOptions.cs` — POCO（ServerAddr/DataId/Group/Username/Password/LongPollingTimeoutMs）
- `src/OpenClaw.Gateway/Mcp/Nacos/NacosConfig.cs` — data model（DataId/Group/Content）
- `src/OpenClaw.Gateway/Mcp/Nacos/INacosConfigService.cs` — 接口（GetConfigAsync + AddListener 返回 `IDisposable?` 句柄）
- `src/OpenClaw.Gateway/Mcp/Nacos/FakeNacosConfigService.cs` — 测试替身，公开 `Publish(NacosConfig)`
- `src/OpenClaw.Gateway/Mcp/Nacos/RedNbNacosConfigService.cs` — 生产实现（包装 `RedNb.Nacos.INacosConfigService`）
- `src/OpenClaw.Gateway/Mcp/Nacos/NacosConfigSubscriptionService.cs` — Start/Dispose + listener 生命周期
- `src/OpenClaw.Tests/NacosConfigSubscriptionServiceTests.cs` — 单元测试（FakeNacosConfigService）
- `src/OpenClaw.Tests/RedNbNacosConfigServiceAdapterTests.cs` — 适配正确性测试（不连真 Nacos，绕开 listener 注册路径用 Fake 验证翻译逻辑）

修改文件：
- `src/OpenClaw.Agent/Tools/CapabilitySlotExecutor.cs` — 加 `ClearRuntimeCache()` + `internal AddedServerCount`
- `src/OpenClaw.Agent/IAgentRuntime.cs` — 加 `ClearCapabilitySlotRuntimeCacheAsync` 方法 + 默认实现
- `src/OpenClaw.Agent/AgentRuntime.cs` — 实现 `ClearCapabilitySlotRuntimeCacheAsync`（调用内部 executor）
- `src/OpenClaw.Agent/MafAgentRuntime.cs` — 同上
- `src/OpenClaw.Gateway/McpWorkspaceWatcherService.cs` — 在 `_bindingCache.Clear()` 后调用 `_agentRuntime.ClearCapabilitySlotRuntimeCacheAsync()`
- `src/OpenClaw.Gateway/OpenClaw.Gateway.csproj` — `<PackageReference Include="RedNb.Nacos.All" Version="2.0.0" />`
- `src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.RuntimeFactories.cs` — `StartMcpWorkspaceWatcher` 后追加 `StartNacosConfigSubscription`（条件性）
- `src/OpenClaw.Gateway/Composition/ToolServicesExtensions.cs` — 注册 `INacosConfigService` 与 `NacosConfigSubscriptionService`（按 `NacosOptions.ServerAddr` 是否存在分支）
- `src/OpenClaw.Core/Models/GatewayConfig.cs` — 新增 `NacosOptions? Nacos { get; set; }` 段
- `src/OpenClaw.Tests/CapabilitySlotExecutorTests.cs` — 2 个新测试（ClearRuntimeCache 清空；AddedServerCount 反映状态）
- `src/OpenClaw.Tests/NacosRouterIntegrationTests.cs` — 1 个新 e2e（用 FakeNacosConfigService 触发清空，验证 cache + runtime add 状态）
- `docs/nacos-mcp-router.md` — Header status 链加 #238、§ "Nacos event subscription"、item 5 加状态、末段更新
- `docs/zh-CN/Nacos-MCP-Router与OpenClaw.NET-MetaSkill集成架构.md` — §7.3 失效机制表更新、§8 状态句更新

---

## Task 1: Core Nacos types & FakeNacosConfigService

**Files:**
- Create: `src/OpenClaw.Gateway/Mcp/Nacos/NacosOptions.cs`
- Create: `src/OpenClaw.Gateway/Mcp/Nacos/NacosConfig.cs`
- Create: `src/OpenClaw.Gateway/Mcp/Nacos/INacosConfigService.cs`
- Create: `src/OpenClaw.Gateway/Mcp/Nacos/FakeNacosConfigService.cs`
- Create: `src/OpenClaw.Tests/NacosConfigServiceAbstractionTests.cs`

**Interfaces (consumed by later tasks):**
- `NacosOptions { string? ServerAddr, string DataId = "openclaw-mcp.json", string Group = "DEFAULT_GROUP", string? Username, string? Password, int LongPollingTimeoutMs = 10000, bool Enabled = true }`
- `NacosConfig(string DataId, string Group, string Content)`
- `INacosConfigService.GetConfigAsync(string dataId, string group, CancellationToken ct) → Task<NacosConfig?>`
- `INacosConfigService.AddListener(string dataId, string group, Action<NacosConfig> onChange) → IDisposable`（handle.Dispose() = 退订）
- `FakeNacosConfigService.Publish(NacosConfig)` — 同步调用所有匹配 `(dataId, group)` 的 `onChange` 回调

- [ ] **Step 1: Write failing test for Fake round-trip**

```csharp
// src/OpenClaw.Tests/NacosConfigServiceAbstractionTests.cs
using OpenClaw.Gateway.Mcp.Nacos;
using Xunit;

namespace OpenClaw.Tests;

public sealed class FakeNacosConfigServiceTests
{
    [Fact]
    public async Task GetConfigAsync_ReturnsNullWhenUnset()
    {
        var fake = new FakeNacosConfigService();
        var result = await fake.GetConfigAsync("d", "g", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public void Publish_InvokesSubscribedListener()
    {
        var fake = new FakeNacosConfigService();
        NacosConfig? captured = null;
        var handle = fake.AddListener("d", "g", cfg => captured = cfg);
        fake.Publish(new NacosConfig("d", "g", "{\"x\":1}"));
        Assert.NotNull(captured);
        Assert.Equal("{\"x\":1}", captured!.Content);
        handle.Dispose();
        fake.Publish(new NacosConfig("d", "g", "{\"x\":2}"));
        Assert.Equal("{\"x\":1}", captured.Content); // disposed handle = no further callbacks
    }

    [Fact]
    public void Publish_OnlyMatchesSubscribedDataIdAndGroup()
    {
        var fake = new FakeNacosConfigService();
        var a = 0; var b = 0;
        fake.AddListener("a", "g1", _ => a++);
        fake.AddListener("b", "g1", _ => b++);
        fake.Publish(new NacosConfig("a", "g1", "{}"));
        Assert.Equal(1, a); Assert.Equal(0, b);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj -c Release --filter "FullyQualifiedName~FakeNacosConfigServiceTests" -v minimal`
Expected: COMPILATION ERROR — `INacosConfigService`, `FakeNacosConfigService`, `NacosOptions`, `NacosConfig` not defined.

- [ ] **Step 3: Implement the types**

```csharp
// src/OpenClaw.Gateway/Mcp/Nacos/NacosOptions.cs
namespace OpenClaw.Gateway.Mcp.Nacos;

public sealed class NacosOptions
{
    public string? ServerAddr { get; set; }
    public string DataId { get; set; } = "openclaw-mcp.json";
    public string Group { get; set; } = "DEFAULT_GROUP";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public int LongPollingTimeoutMs { get; set; } = 10_000;
    public bool Enabled { get; set; } = true;
}

// src/OpenClaw.Gateway/Mcp/Nacos/NacosConfig.cs
namespace OpenClaw.Gateway.Mcp.Nacos;

public sealed record NacosConfig(string DataId, string Group, string Content);

// src/OpenClaw.Gateway/Mcp/Nacos/INacosConfigService.cs
namespace OpenClaw.Gateway.Mcp.Nacos;

public interface INacosConfigService
{
    Task<NacosConfig?> GetConfigAsync(string dataId, string group, CancellationToken ct);
    IDisposable AddListener(string dataId, string group, Action<NacosConfig> onChange);
}

// src/OpenClaw.Gateway/Mcp/Nacos/FakeNacosConfigService.cs
namespace OpenClaw.Gateway.Mcp.Nacos;

public sealed class FakeNacosConfigService : INacosConfigService
{
    private readonly object _gate = new();
    private NacosConfig? _seed;
    private readonly List<(string DataId, string Group, Action<NacosConfig> Callback, Action Unsubscribe)> _listeners = new();

    public void Seed(NacosConfig config)
    {
        lock (_gate) { _seed = config; Fire(config); }
    }

    public Task<NacosConfig?> GetConfigAsync(string dataId, string group, CancellationToken ct)
    {
        lock (_gate) { return Task.FromResult(_seed is { DataId: var d, Group: var g } && d == dataId && g == group ? _seed : null); }
    }

    public IDisposable AddListener(string dataId, string group, Action<NacosConfig> onChange)
    {
        ArgumentNullException.ThrowIfNull(onChange);
        bool disposed = false;
        void Fire(NacosConfig c) { if (!disposed) onChange(c); }
        lock (_gate) { _listeners.Add((dataId, group, onChange, () => disposed = true)); }
        return new Handle(() => { lock (_gate) _listeners.RemoveAll(l => l.Callback == onChange); disposed = true; });
    }

    public void Publish(NacosConfig config)
    {
        List<Action<NacosConfig>> toFire;
        lock (_gate)
        {
            _seed = config;
            toFire = _listeners.Where(l => l.DataId == config.DataId && l.Group == config.Group).Select(l => l.Callback).ToList();
        }
        foreach (var cb in toFire) cb(config);
    }

    private sealed class Handle : IDisposable
    {
        private readonly Action _onDispose;
        private int _done;
        public Handle(Action onDispose) { _onDispose = onDispose; }
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) _onDispose(); }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj -c Release --filter "FullyQualifiedName~FakeNacosConfigServiceTests" -v minimal`
Expected: 3/3 pass.

- [ ] **Step 5: Commit**

```bash
git add src/OpenClaw.Gateway/Mcp/Nacos/NacosOptions.cs \
        src/OpenClaw.Gateway/Mcp/Nacos/NacosConfig.cs \
        src/OpenClaw.Gateway/Mcp/Nacos/INacosConfigService.cs \
        src/OpenClaw.Gateway/Mcp/Nacos/FakeNacosConfigService.cs \
        src/OpenClaw.Tests/NacosConfigServiceAbstractionTests.cs
git commit -m "feat(#238): add INacosConfigService abstraction and FakeNacosConfigService test double

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

## Task 2: `CapabilitySlotExecutor.ClearRuntimeCache()` + observable

**Files:**
- Modify: `src/OpenClaw.Agent/Tools/CapabilitySlotExecutor.cs:37` (add property/method) + `:152/:176` (touch points unchanged)
- Modify: `src/OpenClaw.Agent/IAgentRuntime.cs:54-57` (add method after `ApplyMcpToolChangesAsync`)
- Modify: `src/OpenClaw.Agent/AgentRuntime.cs:245-...` (implement)
- Modify: `src/OpenClaw.Agent/MafAgentRuntime.cs` (implement)
- Modify: `src/OpenClaw.Tests/CapabilitySlotExecutorTests.cs` (2 new tests)

**Interfaces (consumed by Task 3):**
- `CapabilitySlotExecutor.ClearRuntimeCache()` — wipes `_addedServers`
- `CapabilitySlotExecutor.AddedServerCount` (internal) — returns `_addedServers.Count`
- `IAgentRuntime.ClearCapabilitySlotRuntimeCacheAsync(CancellationToken) → Task` (default impl: `Task.CompletedTask`)

- [ ] **Step 1: Write failing tests**

```csharp
// src/OpenClaw.Tests/CapabilitySlotExecutorTests.cs (append)
[Fact]
public void ClearRuntimeCache_WipesAddedServers()
{
    var (executor, _) = BuildExecutor();
    executor.ExecuteAsync(BuildStaticRef("weather-mcp", "get_weather"), "{}", "s1", CancellationToken.None).GetAwaiter().GetResult();
    Assert.True(executor.AddedServerCount >= 1);
    executor.ClearRuntimeCache();
    Assert.Equal(0, executor.AddedServerCount);
}

[Fact]
public void ClearRuntimeCache_PreservesFailureNoCacheContract()
{
    // Failure must not poison the cache, AND ClearRuntimeCache must still wipe any success state.
    var (executor, registry) = BuildExecutor();
    registry.NextAddResult = AddOutcome.Failure("add failed");
    executor.ExecuteAsync(BuildStaticRef("broken", "x"), "{}", "s1", CancellationToken.None).GetAwaiter().GetResult();
    Assert.Equal(0, executor.AddedServerCount);
    registry.NextAddResult = AddOutcome.Success(["x"]);
    executor.ExecuteAsync(BuildStaticRef("broken", "x"), "{}", "s1", CancellationToken.None).GetAwaiter().GetResult();
    Assert.Equal(1, executor.AddedServerCount);
    executor.ClearRuntimeCache();
    Assert.Equal(0, executor.AddedServerCount);
    // Re-execute adds again (cleared cache → not considered "already added").
    executor.ExecuteAsync(BuildStaticRef("broken", "x"), "{}", "s1", CancellationToken.None).GetAwaiter().GetResult();
    Assert.Equal(1, executor.AddedServerCount);
}
```

Add to `BuildExecutor` helper (already in test file) if missing; otherwise reuse. The exact helper shape depends on existing tests — copy the constructor pattern from a neighboring test, keep helper signatures consistent.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj -c Release --filter "FullyQualifiedName~CapabilitySlotExecutorTests.ClearRuntimeCache" -v minimal`
Expected: COMPILATION ERROR — `ClearRuntimeCache` / `AddedServerCount` not defined.

- [ ] **Step 3: Implement `ClearRuntimeCache` + observable**

In `src/OpenClaw.Agent/Tools/CapabilitySlotExecutor.cs`:
```csharp
/// <summary>Runtime-level idempotent add cache (success-only). Cleared on MCP workspace reload.</summary>
public void ClearRuntimeCache() => _addedServers.Clear();

/// <summary>Diagnostic surface for tests and observability — count of servers known to be added in this runtime.</summary>
internal int AddedServerCount => _addedServers.Count;
```

In `src/OpenClaw.Agent/IAgentRuntime.cs`, append after `ApplyMcpToolChangesAsync`:
```csharp
/// <summary>
/// Clears the capability slot executor's runtime-level "already added" cache.
/// Invoked by <see cref="McpWorkspaceWatcherService"/> on workspace reload so static
/// bindings re-add to the router after mcp.json changes. Default no-op keeps test
/// doubles working without a real executor.
/// </summary>
Task ClearCapabilitySlotRuntimeCacheAsync(CancellationToken ct = default) => Task.CompletedTask;
```

In `src/OpenClaw.Agent/AgentRuntime.cs` (find the executor field, add):
```csharp
public Task ClearCapabilitySlotRuntimeCacheAsync(CancellationToken ct = default)
{
    _executor?.ClearRuntimeCache();
    return Task.CompletedTask;
}
```
(Use the same `_executor` field the existing code uses; if the field has a different name, adapt.) If `AgentRuntime` does not currently hold a `CapabilitySlotExecutor` directly (it may construct it inline per-call), trace the call site for `EnsureAddedAsync` and add a private field if needed — keep change minimal.

In `src/OpenClaw.Agent/MafAgentRuntime.cs`, mirror the change. Verify both runtimes compile by running test suite in Step 4.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj -c Release --filter "FullyQualifiedName~CapabilitySlotExecutorTests.ClearRuntimeCache" -v minimal`
Expected: 2/2 pass.

Then run the full `CapabilitySlotExecutorTests` suite to ensure no regression:
Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj -c Release --filter "FullyQualifiedName~CapabilitySlotExecutorTests" -v minimal`
Expected: prior count + 2 = same baseline green.

- [ ] **Step 5: Commit**

```bash
git add src/OpenClaw.Agent/Tools/CapabilitySlotExecutor.cs \
        src/OpenClaw.Agent/IAgentRuntime.cs \
        src/OpenClaw.Agent/AgentRuntime.cs \
        src/OpenClaw.Agent/MafAgentRuntime.cs \
        src/OpenClaw.Tests/CapabilitySlotExecutorTests.cs
git commit -m "feat(#238): expose ClearRuntimeCache on CapabilitySlotExecutor and IAgentRuntime

Both AgentRuntime and MafAgentRuntime now clear the runtime-level added-server
cache via ClearCapabilitySlotRuntimeCacheAsync; default no-op on IAgentRuntime
keeps test doubles working.

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

## Task 3: `McpWorkspaceWatcherService` clears runtime cache after binding cache

**Files:**
- Modify: `src/OpenClaw.Gateway/McpWorkspaceWatcherService.cs:128-131` (after `_bindingCache.Clear()`, call runtime clear)
- Create: `src/OpenClaw.Tests/NacosWorkspaceWatcherRuntimeCacheTests.cs` (e2e pin)

**Goal:** Whenever the watcher reloads (file change, Nacos event, startup), both `_bindingCache` AND the runtime-level `_addedServers` are wiped, so the next slot execution re-resolves / re-adds from scratch.

- [ ] **Step 1: Write failing test**

```csharp
// src/OpenClaw.Tests/NacosWorkspaceWatcherRuntimeCacheTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenClaw.Agent;
using OpenClaw.Agent.Plugins;
using OpenClaw.Agent.Tools;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Mcp;
using Xunit;

namespace OpenClaw.Tests;

public sealed class NacosWorkspaceWatcherRuntimeCacheTests
{
    [Fact]
    public async Task TriggerReload_ClearsBindingCacheAndRuntimeAddCache()
    {
        var registry = new McpServerToolRegistry(...); // construct minimal (mirror existing test setup)
        var runtime = Substitute.For<IAgentRuntime>();
        var bindingCache = new CapabilityBindingCache();
        var configStore = Substitute.For<McpConfigStore>();
        configStore.TryLoadServersAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<string, McpServerConfig>());

        var watcher = new McpWorkspaceWatcherService(
            registry, runtime, workspacePath: null,
            NullLogger<McpWorkspaceWatcherService>.Instance,
            configStore, bindingCache);

        // Simulate a previous successful slot that populated both caches.
        bindingCache.Set("s1", "intent-key", "weather-mcp", "get_weather");
        runtime.ClearCapabilitySlotRuntimeCacheAsync(Arg.Any<CancellationToken>())
            .Returns(async _ => { runtime.ReceivedCalls(); }); // smoke

        // Pre-populate runtime-side cache by calling the runtime method manually.
        await runtime.ClearCapabilitySlotRuntimeCacheAsync(); // no-op substitute; we assert call only

        watcher.Start(CancellationToken.None);
        // Manually trigger reload after Start (Start also triggers; we wait for that to drain).
        await Task.Delay(50);
        watcher.TriggerReload();
        await Task.Delay(50);

        await watcher.DisposeAsync();

        // Assert binding cache was cleared and runtime clear was called.
        Assert.Empty(bindingCache.SnapshotForTests()); // or Assert.False(bindingCache.Has(...)) — verify exact API
        await runtime.Received().ClearCapabilitySlotRuntimeCacheAsync(Arg.Any<CancellationToken>());
    }
}
```

NOTE on helper construction: read the existing `NacosRouterIntegrationTests.cs` to confirm how `McpServerToolRegistry` is constructed for tests (its constructor signature may require `IMcpClientFactory` or similar). Copy that construction verbatim. For `bindingCache.SnapshotForTests()`, if the cache has no inspection API, add an `internal IReadOnlyDictionary<string, ...> SnapshotForTests()` to `CapabilityBindingCache` (mirror the `CapabilityBindingReplay` precedent — `internal` + `InternalsVisibleTo("OpenClaw.Tests")` should already be configured, confirm).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj -c Release --filter "FullyQualifiedName~NacosWorkspaceWatcherRuntimeCacheTests" -v minimal`
Expected: FAIL — `McpWorkspaceWatcherService` does not yet call `ClearCapabilitySlotRuntimeCacheAsync`.

- [ ] **Step 3: Wire runtime clear into watcher**

In `src/OpenClaw.Gateway/McpWorkspaceWatcherService.cs`, in `RunReloadLoopAsync` (around line 128-131), add one line:

```csharp
var reload = await _registry.ReloadWorkspaceServersAsync(servers, ct);
await _agentRuntime.ApplyMcpToolChangesAsync(reload.AddedTools, reload.RemovedToolNames, ct);
_bindingCache.Clear();
await _agentRuntime.ClearCapabilitySlotRuntimeCacheAsync(ct);
_logger.LogDebug("Cleared capability binding cache and runtime added-server cache after workspace MCP reload.");
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj -c Release --filter "FullyQualifiedName~NacosWorkspaceWatcherRuntimeCacheTests" -v minimal`
Expected: 1/1 pass.

- [ ] **Step 5: Commit**

```bash
git add src/OpenClaw.Gateway/McpWorkspaceWatcherService.cs \
        src/OpenClaw.Tests/NacosWorkspaceWatcherRuntimeCacheTests.cs
git commit -m "feat(#238): McpWorkspaceWatcherService clears runtime added-server cache on reload

Both binding cache (sessions) and runtime-level _addedServers (static) are
now wiped together so subsequent slot executions re-resolve and re-add from
scratch — required for Nacos event subscription to invalidate statically
cached add state.

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

## Task 4: `RedNbNacosConfigService` adapter

**Files:**
- Create: `src/OpenClaw.Gateway/Mcp/Nacos/RedNbNacosConfigService.cs`
- Modify: `src/OpenClaw.Gateway/OpenClaw.Gateway.csproj` (`<PackageReference Include="RedNb.Nacos.All" Version="2.0.0" />`)
- Create: `src/OpenClaw.Tests/RedNbNacosConfigServiceAdapterTests.cs` (translation correctness)

**Goal:** Real Nacos-backed implementation of `INacosConfigService` that wraps `RedNb.Nacos.INacosConfigService` (namespace `RedNb.Nacos`). The adapter:
- constructs the SDK config client from `NacosOptions` (ServerAddr/Username/Password/LongPollingTimeoutMs)
- `GetConfigAsync` calls SDK `GetConfig(dataId, group, timeoutMs)` and wraps into `NacosConfig`
- `AddListener` creates an SDK `IListener` whose callback delegates to the gateway's `Action<NacosConfig>`; returns an `IDisposable` whose `Dispose` calls SDK `RemoveListener`

**Important:** the exact RedNb.Nacos API surface may differ from this sketch. The implementer MUST verify against the installed package (run `dotnet restore` + read the assembly). Common shape:

```csharp
// Sketch — verify against installed SDK before committing.
using RedNb.Nacos;

namespace OpenClaw.Gateway.Mcp.Nacos;

public sealed class RedNbNacosConfigService : INacosConfigService, IDisposable
{
    private readonly INacosConfigService _client; // RedNb.Nacos.INacosConfigService
    private readonly ILogger<RedNbNacosConfigService> _logger;
    private bool _disposed;

    public RedNbNacosConfigService(NacosOptions options, ILogger<RedNbNacosConfigService> logger)
    {
        _logger = logger;
        var config = new ConfigOptions
        {
            ServerAddr = options.ServerAddr ?? "",
            UserName = options.Username,
            Password = options.Password,
            LongPollTimeout = options.LongPollingTimeoutMs,
            DefaultTimeOut = options.LongPollingTimeoutMs,
        };
        _client = NacosFactory.CreateConfigService(config);
    }

    public async Task<NacosConfig?> GetConfigAsync(string dataId, string group, CancellationToken ct)
    {
        try
        {
            var raw = await _client.GetConfig(dataId, group, /*timeoutMs*/ 10_000);
            return raw is null ? null : new NacosConfig(dataId, group, raw);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nacos GetConfig({DataId}, {Group}) failed.", dataId, group);
            return null;
        }
    }

    public IDisposable AddListener(string dataId, string group, Action<NacosConfig> onChange)
    {
        var sdkListener = new AdapterListener(c => onChange(new NacosConfig(dataId, group, c)));
        _client.AddListener(dataId, group, sdkListener);
        return new Handle(() => { try { _client.RemoveListener(dataId, group, sdkListener); } catch { } });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        (_client as IDisposable)?.Dispose();
    }

    private sealed class AdapterListener : IListener  // verify interface name in RedNb.Nacos
    {
        private readonly Action<string> _onChange;
        public AdapterListener(Action<string> onChange) { _onChange = onChange; }
        public void ReceiveConfigInfo(string configInfo) => _onChange(configInfo);
        // async variant if SDK exposes it; otherwise leave default.
    }

    private sealed class Handle : IDisposable { /* standard handle pattern */ }
}
```

For adapter test:
```csharp
// src/OpenClaw.Tests/RedNbNacosConfigServiceAdapterTests.cs
[Fact]
public void NacosOptions_Defaults_AreStable()
{
    var o = new NacosOptions();
    Assert.Equal("openclaw-mcp.json", o.DataId);
    Assert.Equal("DEFAULT_GROUP", o.Group);
    Assert.True(o.Enabled);
    Assert.Equal(10_000, o.LongPollingTimeoutMs);
}
// Real SDK round-trip (GetConfig + AddListener) requires a live Nacos server — that
// is the live DoD check in Task 7, not a unit test. Adapter smoke is limited to
// options construction; structural validation lives in integration.
```

- [ ] **Step 1: Run failing adapter test**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj -c Release --filter "FullyQualifiedName~RedNbNacosConfigServiceAdapterTests" -v minimal`
Expected: COMPILATION ERROR — `RedNbNacosConfigService` not defined.

- [ ] **Step 2: Add NuGet package**

Edit `src/OpenClaw.Gateway/OpenClaw.Gateway.csproj`:
```xml
<ItemGroup>
  <PackageReference Include="RedNb.Nacos.All" Version="2.0.0" />
</ItemGroup>
```
Run: `dotnet restore src/OpenClaw.Gateway/OpenClaw.Gateway.csproj`
Expected: restore succeeds; new transitive deps appear in obj/project.assets.json.

- [ ] **Step 3: Implement adapter**

Implement `RedNbNacosConfigService.cs` per the sketch above. Verify the exact API surface against the installed assembly — read the package metadata (`~/.nuget/packages/rednb.nacos.all/2.0.0/lib/net8.0/RedNb.Nacos.dll` via ildasm or reflection test if needed). If API differs (e.g., `ReceiveConfigInfo` is async-only, or `AddListener` returns void not `IListener`), adapt while preserving the contract: `INacosConfigService.AddListener` returns `IDisposable` whose `Dispose()` removes the listener.

- [ ] **Step 4: Build**

Run: `dotnet build src/OpenClaw.Gateway/OpenClaw.Gateway.csproj -c Release -nologo`
Expected: BUILD SUCCESS. Address any warnings (the project has `warnings-as-errors`; expect likely reflection warnings from RedNb.Nacos itself — `NoWarn` on third-party packages in csproj is acceptable since the warnings originate inside `RedNb.Nacos.*` assemblies we cannot patch).

- [ ] **Step 5: Run adapter smoke test**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj -c Release --filter "FullyQualifiedName~RedNbNacosConfigServiceAdapterTests" -v minimal`
Expected: 1/1 pass.

- [ ] **Step 6: Commit**

```bash
git add src/OpenClaw.Gateway/Mcp/Nacos/RedNbNacosConfigService.cs \
        src/OpenClaw.Gateway/OpenClaw.Gateway.csproj \
        src/OpenClaw.Tests/RedNbNacosConfigServiceAdapterTests.cs
git commit -m "feat(#238): add RedNbNacosConfigService adapter for RedNb.Nacos.All 2.0.0

Wraps RedNb.Nacos.INacosConfigService to expose the gateway's
INacosConfigService abstraction; LongPolling listener translates to
Action<NacosConfig> callback; GetConfig surfaces SDK errors as warnings
without throwing.

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

## Task 5: `NacosConfigSubscriptionService`

**Files:**
- Create: `src/OpenClaw.Gateway/Mcp/Nacos/NacosConfigSubscriptionService.cs`
- Create: `src/OpenClaw.Tests/NacosConfigSubscriptionServiceTests.cs`

**Goal:** Owns the listener lifecycle. On Start: read initial config (parity), register listener for the configured `(dataId, group)`. On `onChange`: call `McpWorkspaceWatcherService.TriggerReload()`. On Dispose: remove listener, dispose SDK client. No-op lifecycle when `NacosOptions.ServerAddr` is empty (config-absent branch).

- [ ] **Step 1: Write failing tests**

```csharp
// src/OpenClaw.Tests/NacosConfigSubscriptionServiceTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Mcp.Nacos;
using Xunit;

namespace OpenClaw.Tests;

public sealed class NacosConfigSubscriptionServiceTests
{
    [Fact]
    public async Task OnChange_TriggersWatcherReload()
    {
        var fake = new FakeNacosConfigService();
        var watcher = Substitute.For<McpWorkspaceWatcherService>();
        var options = new NacosOptions { ServerAddr = "127.0.0.1:8848", DataId = "d", Group = "g" };
        await using var svc = new NacosConfigSubscriptionService(fake, options, watcher, NullLogger<NacosConfigSubscriptionService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        fake.Publish(new NacosConfig("d", "g", "{\"mcpServers\":{}}"));

        // TriggerReload called at least once.
        watcher.Received().TriggerReload();
    }

    [Fact]
    public async Task OnChange_ForUnrelatedDataId_DoesNotTrigger()
    {
        var fake = new FakeNacosConfigService();
        var watcher = Substitute.For<McpWorkspaceWatcherService>();
        var options = new NacosOptions { ServerAddr = "127.0.0.1:8848", DataId = "d", Group = "g" };
        await using var svc = new NacosConfigSubscriptionService(fake, options, watcher, NullLogger<NacosConfigSubscriptionService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        fake.Publish(new NacosConfig("other", "g", "{}"));
        watcher.DidNotReceive().TriggerReload();
    }

    [Fact]
    public async Task EmptyServerAddr_StartAsync_IsNoOp()
    {
        var fake = new FakeNacosConfigService();
        var watcher = Substitute.For<McpWorkspaceWatcherService>();
        var options = new NacosOptions { ServerAddr = null, DataId = "d", Group = "g" };
        await using var svc = new NacosConfigSubscriptionService(fake, options, watcher, NullLogger<NacosConfigSubscriptionService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        fake.Publish(new NacosConfig("d", "g", "{}"));
        watcher.DidNotReceive().TriggerReload(); // never subscribed
    }

    [Fact]
    public async Task DisposeAsync_UnsubscribesListener()
    {
        var fake = new FakeNacosConfigService();
        var watcher = Substitute.For<McpWorkspaceWatcherService>();
        var options = new NacosOptions { ServerAddr = "127.0.0.1:8848", DataId = "d", Group = "g" };
        var svc = new NacosConfigSubscriptionService(fake, options, watcher, NullLogger<NacosConfigSubscriptionService>.Instance);
        await svc.StartAsync(CancellationToken.None);

        await svc.DisposeAsync();
        fake.Publish(new NacosConfig("d", "g", "{}"));
        watcher.DidNotReceive().TriggerReload();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj -c Release --filter "FullyQualifiedName~NacosConfigSubscriptionServiceTests" -v minimal`
Expected: COMPILATION ERROR — `NacosConfigSubscriptionService` not defined.

- [ ] **Step 3: Implement service**

```csharp
// src/OpenClaw.Gateway/Mcp/Nacos/NacosConfigSubscriptionService.cs
using Microsoft.Extensions.Hosting;

namespace OpenClaw.Gateway.Mcp.Nacos;

public sealed class NacosConfigSubscriptionService : IAsyncDisposable
{
    private readonly INacosConfigService _config;
    private readonly NacosOptions _options;
    private readonly McpWorkspaceWatcherService _watcher;
    private readonly ILogger<NacosConfigSubscriptionService> _logger;
    private IDisposable? _handle;
    private bool _started;

    public NacosConfigSubscriptionService(
        INacosConfigService config,
        NacosOptions options,
        McpWorkspaceWatcherService watcher,
        ILogger<NacosConfigSubscriptionService> logger)
    {
        _config = config;
        _options = options;
        _watcher = watcher;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (_started) return;
        _started = true;
        if (string.IsNullOrWhiteSpace(_options.ServerAddr))
        {
            _logger.LogInformation("Nacos event subscription disabled: NacosOptions.ServerAddr not configured.");
            return;
        }
        try
        {
            // Initial read for parity; failures are non-fatal.
            var initial = await _config.GetConfigAsync(_options.DataId, _options.Group, ct);
            if (initial is not null)
                _logger.LogInformation("Nacos initial config loaded for {DataId}/{Group} ({Length} bytes).", _options.DataId, _options.Group, initial.Content.Length);
            else
                _logger.LogWarning("Nacos initial config missing for {DataId}/{Group}; subscribing for future updates.", _options.DataId, _options.Group);

            _handle = _config.AddListener(_options.DataId, _options.Group, OnChange);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nacos subscription setup failed; falling back to TTL/reload.");
            _handle?.Dispose();
            _handle = null;
        }
    }

    private void OnChange(NacosConfig config)
    {
        _logger.LogInformation("Nacos config change received for {DataId}/{Group}; triggering MCP workspace reload.", config.DataId, config.Group);
        _watcher.TriggerReload();
    }

    public async ValueTask DisposeAsync()
    {
        if (_handle is not null)
        {
            _handle.Dispose();
            _handle = null;
        }
        if (_config is IAsyncDisposable d) await d.DisposeAsync();
        else if (_config is IDisposable s) s.Dispose();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj -c Release --filter "FullyQualifiedName~NacosConfigSubscriptionServiceTests" -v minimal`
Expected: 4/4 pass.

- [ ] **Step 5: Commit**

```bash
git add src/OpenClaw.Gateway/Mcp/Nacos/NacosConfigSubscriptionService.cs \
        src/OpenClaw.Tests/NacosConfigSubscriptionServiceTests.cs
git commit -m "feat(#238): NacosConfigSubscriptionService wires onChange to watcher reload

Graceful no-op when NacosOptions.ServerAddr is empty (TTL/reload fallback
remains); listener handle disposed on shutdown; SDK dispose delegated to
inner INacosConfigService.

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

## Task 6: DI wiring + NacosConfig in GatewayConfig

**Files:**
- Modify: `src/OpenClaw.Core/Models/GatewayConfig.cs` (add `NacosOptions? Nacos` section)
- Modify: `src/OpenClaw.Gateway/Composition/ToolServicesExtensions.cs` (register `INacosConfigService` + `NacosConfigSubscriptionService` when configured)
- Modify: `src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.RuntimeFactories.cs` (start `NacosConfigSubscriptionService` alongside watcher)

**Goal:** Wire the new services into DI; service is registered only when `NacosOptions.ServerAddr` is set, so unconfigured deployments stay no-op (TTL/reload remains active).

- [ ] **Step 1: Add `Nacos` section to `GatewayConfig`**

In `src/OpenClaw.Core/Models/GatewayConfig.cs`, append alongside `Mcp`, `McpApps`, `McpCompatibility`:
```csharp
public NacosOptions? Nacos { get; set; }
```
(`NacosOptions` lives in `OpenClaw.Gateway.Mcp.Nacos`; either move the type to `OpenClaw.Core.Models` or add `using OpenClaw.Gateway.Mcp.Nacos;` at the top of `GatewayConfig.cs`. The first option is cleaner — `NacosOptions` belongs in Core. **Move `NacosOptions.cs` to `src/OpenClaw.Core/Models/NacosOptions.cs`** as part of this task. Update namespaces/usings.)

- [ ] **Step 2: Register DI in `ToolServicesExtensions.cs`**

Locate the existing block that registers `McpServerToolRegistry` and `CapabilityBindingCache` (read the file first; it already mentions `McpWorkspaceWatcherService` per the exploration). Append:
```csharp
if (config.Nacos is { ServerAddr: { Length: > 0 } })
{
    services.AddSingleton(config.Nacos);
    services.AddSingleton<RedNbNacosConfigService>(sp =>
        new RedNbNacosConfigService(
            sp.GetRequiredService<NacosOptions>(),
            sp.GetRequiredService<ILogger<RedNbNacosConfigService>>()));
    services.AddSingleton<INacosConfigService>(sp => sp.GetRequiredService<RedNbNacosConfigService>());
}
else
{
    services.AddSingleton<INacosConfigService>(sp => new NullNacosConfigService());
}
// NacosConfigSubscriptionService is registered unconditionally; it no-ops when
// NacosOptions.ServerAddr is empty (its own branch).
services.AddSingleton<NacosConfigSubscriptionService>();
```

Add a tiny `NullNacosConfigService` (no-op, returns null on GetConfig, returns `EmptyDisposable` on AddListener) in the same `Nacos/` folder so the unconfigured branch has a type-safe binding.

- [ ] **Step 3: Start service in `RuntimeFactories.cs`**

In `src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.RuntimeFactories.cs`, after the existing `StartMcpWorkspaceWatcher` block:
```csharp
var nacosSubscription = app.Services.GetRequiredService<NacosConfigSubscriptionService>();
await nacosSubscription.StartAsync(app.Lifetime.ApplicationStopping);
```
(The exact `lifetime` token source name follows the file's existing convention — match the watcher pattern.)

- [ ] **Step 4: Run gateway smoke tests**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj -c Release --filter "FullyQualifiedName~Nacos" -v minimal`
Expected: all prior tests pass; new tests in Tasks 1-5 still pass after wiring.

- [ ] **Step 5: Commit**

```bash
git add src/OpenClaw.Core/Models/GatewayConfig.cs \
        src/OpenClaw.Core/Models/NacosOptions.cs \
        src/OpenClaw.Gateway/Composition/ToolServicesExtensions.cs \
        src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.RuntimeFactories.cs
git commit -m "feat(#238): wire NacosConfigSubscriptionService into DI and gateway startup

INacosConfigService binds to RedNbNacosConfigService when NacosOptions.ServerAddr
is set, NullNacosConfigService otherwise; subscription service is registered
unconditionally and no-ops on its own when config is absent.

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

## Task 7: AOT validation + full suite green

**Files:**
- Modify: `src/OpenClaw.Gateway/Mcp/Nacos/RedNbNacosConfigService.cs` (add `[RequiresUnreferencedCode]` / `[DynamicallyAccessedMembers]` annotations if needed)
- Modify: `src/OpenClaw.Gateway/OpenClaw.Gateway.csproj` (`NoWarn` scope on `RedNb.Nacos.*` warnings if they originate from the third-party assembly)

**Goal:** Confirm `dotnet publish -c Release -r linux-x64 -p:PublishAot=true` stays green with the new package; if not, scope the warnings to the SDK and document.

- [ ] **Step 1: AOT publish (default Nacos off path)**

Run: `dotnet publish src/OpenClaw.Gateway/OpenClaw.Gateway.csproj -c Release -r linux-x64 -p:PublishAot=true -nologo`
Expected: BUILD SUCCESS (warnings-as-errors still holds).

If `RedNb.Nacos.*` reports trim warnings (IL2xxx warnings from inside the SDK package), the project's `NoWarn` allowance should already cover them. If new IL warnings appear in the gateway's own adapter:
- Annotate the offending method with `[RequiresUnreferencedCode("Nacos SDK uses reflection not yet source-gen'd.")]` and either (a) keep the AOT build green by also adding `#pragma warning disable IL2026` around the SDK call, or (b) document that `PublishAot=true` with Nacos enabled is not yet supported and file a follow-up issue.

- [ ] **Step 2: Full test suite**

Run: `dotnet test -c Release -nologo`
Expected: 2760+2 (new) / 2 (baseline fail, environment-only) / 1 (LiveRouter skip) — i.e., baseline + new tests green. The 2 baseline failures are pre-existing environment issues (`PluginCommandsTests.NativePluginDoesNotRunNpmLifecycleScripts` + `CompanionCanvasUiTests.PreserveDraftUntilSend`) and must NOT be regressed.

If new failures appear, classify:
- (a) Test logic error in new code → fix.
- (b) Flaky sidecar race (mirror #234 baseline: `ToolGovernanceTests.ExecuteAsync_SidecarDeny_BlocksTool`); re-run isolated.
- (c) Real regression → investigate; do not proceed.

- [ ] **Step 3: Commit (if any AOT annotation/pragma changes)**

```bash
git add src/OpenClaw.Gateway/Mcp/Nacos/RedNbNacosConfigService.cs \
        src/OpenClaw.Gateway/OpenClaw.Gateway.csproj
git commit -m "fix(#238): annotate RedNbNacosConfigService for AOT trim compatibility

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```
(No commit if no changes were needed.)

---

## Task 8: Docs

**Files:**
- Modify: `docs/nacos-mcp-router.md` (header status chain, new "Nacos event subscription" section, "Remaining live acceptance" item 5, closing paragraph)
- Modify: `docs/zh-CN/Nacos-MCP-Router与OpenClaw.NET-MetaSkill集成架构.md` (§7.3 status row update, §8 status sentence update)

**Goal:** Reader-visible status updates; capture the "invalidation fan-out" sequence (Nacos onChange → watcher.TriggerReload → registry.ReloadWorkspaceServersAsync + ApplyMcpToolChanges + ClearCapabilityBindingCache + ClearCapabilitySlotRuntimeCache).

- [ ] **Step 1: EN docs**

In `docs/nacos-mcp-router.md`:
- Header status chain (after the existing items): add `#238 Nacos event subscription (2026-09-XX)` between #234 and the closing line.
- New section "Nacos event subscription (issue #238)": describe the integration with `RedNb.Nacos.All 2.0.0` SDK + LongPolling, the subscription service wiring, the dual cache-clearing (binding cache + runtime added-server cache), and the graceful-degradation branch when NacosOptions absent or Nacos unreachable. Reference `McpWorkspaceWatcherService` as the convergence point.
- In the "Remaining live acceptance" list, flip item 5 (Nacos event subscription) from TODO to "done (2026-09-XX)".
- Closing paragraph: rewrite the "Outstanding gaps" sentence to remove Nacos event subscription and move it under the "Implemented" sentence.

- [ ] **Step 2: zh docs**

In `docs/zh-CN/Nacos-MCP-Router与OpenClaw.NET-MetaSkill集成架构.md`:
- §7.3 行更新：「订阅 Nacos 配置变更事件」从「后续项」改为「已实现，2026-09-XX」；新增行说明 SDK 实现 (`RedNb.Nacos.All 2.0.0` LongPolling) + 失效扇出（binding cache + runtime added-server cache）。
- §8 状态句：「Nacos 变更事件订阅（#238）已实现」插入到「绑定轨迹可观测性与离线重放」之后。

- [ ] **Step 3: Commit**

```bash
git add docs/nacos-mcp-router.md \
        docs/zh-CN/Nacos-MCP-Router与OpenClaw.NET-MetaSkill集成架构.md
git commit -m "docs(#238): document Nacos event subscription and dual cache invalidation

EN: header status chain + new 'Nacos event subscription' section + flip
Remaining live acceptance item 5 to done. zh: §7.3 status row update +
§8 status sentence extended.

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

## Definition of Done

1. Tasks 1-8 all committed with `Co-Authored-By: Claude Code <noreply@anthropic.com>`.
2. `dotnet test -c Release`: baseline maintained (2760 pass / 2 baseline fail / 1 skip); new tests added in Tasks 1, 2, 3, 4, 5 = 3+2+1+1+4 = 11 RED-GREEN tests committed.
3. `dotnet publish -c Release -r linux-x64 -p:PublishAot=true` green (with Nacos config absent — the default; with Nacos configured, document any caveats).
4. Live DoD: hand-verify on local Nacos 3.2.4 + Router 0.2.2: publish mcp.json dataId via Nacos console / OpenAPI; OpenClaw.Gateway log shows `Nacos config change received ... triggering MCP workspace reload` within LongPolling interval (≤ 2s); next slot execution re-adds / re-resolves. Record command(s) in completion report.
5. Issue body / completion report must NOT contain real Nacos password (`"nacos"`); all committed code reads creds from `NacosOptions` only.
6. docs EN + zh sync; status chain on both pages reads "Nacos event subscription implemented (2026-09-XX) for #238".

---

## Self-Review

1. **Spec coverage:**
   - AC #1 (register listener, graceful degrade) → Tasks 5+6+7 ✓
   - AC #2 (publish triggers cache clear + re-resolve) → Tasks 3+5+6; live DoD ✓
   - AC #3 (FakeLongPollingListener pattern, mirror `FakeNacosRouterMcpTools`) → Tasks 1+5 (`FakeNacosConfigService` + 4 subscription tests) ✓
   - AC #4 (ClearByServer optional, full clear acceptable with note) → Task 3 implements full clear; completion report + docs note the per-server granularity deferral ✓
   - AC #5 (creds from existing config, not committed) → `NacosOptions` is POCO bound from config; no hardcoded secrets anywhere ✓
   - AC #6 (docs sync) → Task 8 ✓

2. **Placeholder scan:** none — all type signatures, test bodies, file paths concretely specified.

3. **Type consistency:**
   - `INacosConfigService.AddListener` returns `IDisposable` (Task 1) — `FakeNacosConfigService` and `RedNbNacosConfigService` both honor this; `NacosConfigSubscriptionService` stores handle as `IDisposable?` and disposes in `DisposeAsync` (Task 5).
   - `IAgentRuntime.ClearCapabilitySlotRuntimeCacheAsync` default impl `=> Task.CompletedTask` (Task 2) — both runtimes override; test doubles (NSubstitute mocks) automatically fall back to default, but tests should `Substitute.For<IAgentRuntime>()` which works because NSubstitute synthesizes the method.
   - `CapabilitySlotExecutor.ClearRuntimeCache()` (Task 2) — `McpWorkspaceWatcherService` calls it via `IAgentRuntime.ClearCapabilitySlotRuntimeCacheAsync` (Task 3) — one indirection, not direct executor reference (clean: watcher has `IAgentRuntime` already).
   - `NacosOptions` lives in `OpenClaw.Core.Models` after Task 6 Step 1 move (not in `OpenClaw.Gateway.Mcp.Nacos`) — every consumer (`GatewayConfig`, `ToolServicesExtensions`, `NacosConfigSubscriptionService`, `RedNbNacosConfigService`) uses the same type.
   - `McpWorkspaceWatcherService` constructor signature unchanged — DI compatibility preserved.
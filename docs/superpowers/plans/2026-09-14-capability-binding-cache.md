# Capability Binding Cache (会话级 intent 哈希 + TTL/reload 失效) Implementation Plan

> Historical implementation plan. Retained task snippets and checkboxes are non-normative; the implementation has since been refactored. See [the current capability-resolution contract](../../capability-resolution.md) for supported behavior and remaining live/NativeAOT acceptance.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 动态能力槽位的绑定结果按会话缓存（键 = SHA256(intent 规范化串)），TTL 过期与 mcp.json reload 成功时失效，避免每次动态节点重复 `search → add` 两次 Router 往返。

**Architecture:** 新增 `CapabilityBindingCache`（会话级字典，懒过期 TTL，默认 300s，仅缓存成功的解析结果）。`CapabilitySlotExecutor` 动态分支在 `ResolveCoreAsync` 前查缓存、成功后写入，`ExecuteAsync` 增加 `sessionId` 参数（两个 runtime 的调用点已持有 `Session`）。`McpWorkspaceWatcherService` reload 成功后调用 `Clear()`。静态绑定的运行时级缓存沿用 #231 已有的 `_addedServers`（执行器为 DI 单例 → 进程级），本次不改。`resolve_capability` 原生工具不接缓存（契约保持每次实时解析）。

**Tech Stack:** net10.0 / C# 14（LangVersion 14，warnings-as-errors）、xUnit（InMemory 内嵌 HTTP MCP fixture）、`System.Security.Cryptography.SHA256`、`ConcurrentDictionary`。

**Spec:** <https://github.com/clawdotnet/openclaw.net/issues/232> （本计划的设计依据为 issue 正文 §Proposed Change 阶段 1；阶段 2 Nacos 事件订阅不阻塞、留作 open question）

## Global Constraints

- 分支 `nacos`，提交**不推送**（未经用户明确批准）；提交信息以 `feat(#232):` / `test(#232):` / `docs(#232):` 为前缀，结尾附 `Co-Authored-By: Claude Code <noreply@anthropic.com>`。
- TDD：先写失败测试并确认 RED（新类型允许编译级 RED），再最小实现 GREEN；每任务结束跑相关测试过滤器 + 回归。
- nullable warnings-as-errors：新代码不得产生 CS8602/CS8604 等告警；测试断言用 xUnit `Assert`。
- 新代码注释与 commit 用英文；与用户交流用中文。文档中的示例、命令不得含凭据。
- 集成测试须挂 `[Collection(EnvironmentVariableCollection.Name)]`；测试过滤器语法 `FullyQualifiedName~ClassName`。
- `CapabilitySlotExecutor` 当前签名 `ExecuteAsync(MetaCapabilityRefDefinition, string toolArgsJson, CancellationToken)` 与本计划中改名后的签名互斥——Task 2 必须同步更新所有调用点（两个 runtime + 两个测试文件），编译通过是任务内检查点。

---

## Task 1: CapabilityBindingCache 存储与 TTL

**Files:**

- Create: `src/OpenClaw.Agent/Tools/CapabilityBindingCache.cs`
- Test: `src/OpenClaw.Tests/CapabilityBindingCacheTests.cs`

**Interfaces:**

- Consumes: 无（不依赖其他任务）。
- Produces:

  ```csharp
  public sealed class CapabilityBindingCache
  {
      public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(300);
      public CapabilityBindingCache(TimeSpan? ttl = null);                       // null → DefaultTtl
      public static string ComputeIntentKey(string taskDescription, string? keywords, string selectionPolicy); // SHA-256 hex
      public bool TryGet(string sessionId, string intentKey, out string server, out string tool);
      public void Set(string sessionId, string intentKey, string server, string tool);
      public void Clear();
  }
  ```

  后续任务依赖上述五个成员，签名不得变更。

- [ ] **Step 1: 写失败测试**

`src/OpenClaw.Tests/CapabilityBindingCacheTests.cs`：

```csharp
using OpenClaw.Agent.Tools;
using Xunit;

namespace OpenClaw.Tests;

public sealed class CapabilityBindingCacheTests
{
    private const string IntentKey = "key-1";

    [Fact]
    public void Set_TryGet_SameSessionAndKey_HitsAcrossCalls()
    {
        var cache = new CapabilityBindingCache();
        cache.Set("sess-1", IntentKey, "weather-mcp", "get_weather");

        Assert.True(cache.TryGet("sess-1", IntentKey, out var server, out var tool));
        Assert.Equal("weather-mcp", server);
        Assert.Equal("get_weather", tool);
        Assert.True(cache.TryGet("sess-1", IntentKey, out _, out _));
    }

    [Fact]
    public void TryGet_UnknownSessionOrKey_Misses()
    {
        var cache = new CapabilityBindingCache();
        cache.Set("sess-1", IntentKey, "weather-mcp", "get_weather");

        Assert.False(cache.TryGet("sess-2", IntentKey, out _, out _));
        Assert.False(cache.TryGet("sess-1", "other-key", out _, out _));
    }

    [Fact]
    public async Task TryGet_ExpiredEntry_Misses()
    {
        var cache = new CapabilityBindingCache(TimeSpan.FromMilliseconds(50));
        cache.Set("sess-1", IntentKey, "weather-mcp", "get_weather");

        await Task.Delay(200);

        Assert.False(cache.TryGet("sess-1", IntentKey, out _, out _));
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        var cache = new CapabilityBindingCache();
        cache.Set("sess-1", IntentKey, "weather-mcp", "get_weather");
        cache.Set("sess-2", "key-2", "amap-mcp-server", "geocode");

        cache.Clear();

        Assert.False(cache.TryGet("sess-1", IntentKey, out _, out _));
        Assert.False(cache.TryGet("sess-2", "key-2", out _, out _));
    }

    [Theory]
    [InlineData("weather city", "weather,city", "First")]
    [InlineData("weather city", null, "First")]
    [InlineData("ghost-city", "weather", "ExactName")]
    public void ComputeIntentKey_IsDeterministicAndSensitive(string task, string? keywords, string policy)
    {
        var a = CapabilityBindingCache.ComputeIntentKey(task, keywords, policy);
        var b = CapabilityBindingCache.ComputeIntentKey(task, keywords, policy);

        Assert.Equal(64, a.Length);
        Assert.Equal(a, b);
        Assert.NotEqual(a, CapabilityBindingCache.ComputeIntentKey(task + "x", keywords, policy));
        Assert.NotEqual(a, CapabilityBindingCache.ComputeIntentKey(task, keywords, policy + "x"));
    }
}
```

- [ ] **Step 2: 运行测试确认 RED**

Run: `dotnet test src/OpenClaw.Tests -c Release --filter FullyQualifiedName~CapabilityBindingCacheTests`
Expected: 编译失败 CS0246（`CapabilityBindingCache` 不存在）——编译级 RED 可接受。

- [ ] **Step 3: 最小实现**

`src/OpenClaw.Agent/Tools/CapabilityBindingCache.cs`：

```csharp
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Session-scoped cache of resolved capability bindings (issue #232). Entries
/// are keyed by (session id, SHA-256 of the normalised intent); they expire
/// lazily after a configurable TTL (default 300s) and are cleared wholesale
/// when the workspace MCP config reloads. Only successful resolutions are
/// stored — failures are never cached, so the next execution retries.
/// </summary>
public sealed class CapabilityBindingCache
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(300);

    private sealed class CacheEntry(string server, string tool, DateTimeOffset storedAt)
    {
        public string Server { get; } = server;
        public string Tool { get; } = tool;
        public DateTimeOffset StoredAt { get; } = storedAt;
    }

    private readonly ConcurrentDictionary<(string SessionId, string IntentKey), CacheEntry> _entries = new();
    private readonly TimeSpan _ttl;

    public CapabilityBindingCache(TimeSpan? ttl = null)
    {
        _ttl = ttl ?? DefaultTtl;
    }

    /// <summary>
    /// SHA-256 hex of the normalised intent: task_description + keywords +
    /// selection policy. Same fields, same key; any field change, new key.
    /// </summary>
    public static string ComputeIntentKey(string taskDescription, string? keywords, string selectionPolicy)
    {
        var raw = string.Concat(taskDescription, "\n", keywords ?? string.Empty, "\n", selectionPolicy);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    public bool TryGet(string sessionId, string intentKey, out string server, out string tool)
    {
        if (_entries.TryGetValue((sessionId, intentKey), out var entry))
        {
            if (DateTimeOffset.UtcNow - entry.StoredAt <= _ttl)
            {
                server = entry.Server;
                tool = entry.Tool;
                return true;
            }
            _entries.TryRemove((sessionId, intentKey), out _);
        }

        server = string.Empty;
        tool = string.Empty;
        return false;
    }

    public void Set(string sessionId, string intentKey, string server, string tool)
        => _entries[(sessionId, intentKey)] = new CacheEntry(server, tool, DateTimeOffset.UtcNow);

    public void Clear() => _entries.Clear();
}
```

- [ ] **Step 4: 运行测试确认 GREEN**

Run: `dotnet test src/OpenClaw.Tests -c Release --filter FullyQualifiedName~CapabilityBindingCacheTests`
Expected: 7/7 通过（4 个 Fact + 3 组 InlineData 的 Theory）。

- [ ] **Step 5: 提交**

```bash
git add src/OpenClaw.Agent/Tools/CapabilityBindingCache.cs src/OpenClaw.Tests/CapabilityBindingCacheTests.cs
git commit -m "$(cat <<'EOF'
feat(#232): add session-scoped capability binding cache with TTL

Co-Authored-By: Claude Code <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: 执行器接入（动态槽位缓存命中/写入 + sessionId 传递 + DI 注册）

**Files:**

- Modify: `src/OpenClaw.Agent/Tools/CapabilitySlotExecutor.cs`（ctor 增参；`ExecuteAsync` 增 `sessionId`；动态分支接缓存）
- Modify: `src/OpenClaw.Agent/AgentRuntime.cs:2871`（调用点传 `session.Id`）
- Modify: `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs:2648`（同上）
- Modify: `src/OpenClaw.Gateway/Composition/ToolServicesExtensions.cs`（注册 `CapabilityBindingCache` 单例 + 执行器 lambda 增参）
- Test: `src/OpenClaw.Tests/CapabilitySlotExecutorTests.cs`（fixture 与既有用例签名更新 + 2 个新用例）
- Test: `src/OpenClaw.Tests/NacosRouterIntegrationTests.cs`（执行器构造更新 + 1 个新 theory）

**Interfaces:**

- Consumes: Task 1 的 `CapabilityBindingCache` 全部成员。
- Produces: `CapabilitySlotExecutor` 新 ctor `(McpServerToolRegistry registry, CapabilityBindingCache bindingCache)`；`ExecuteAsync(MetaCapabilityRefDefinition capabilityRef, string toolArgsJson, string sessionId, CancellationToken ct)`。Task 3 不依赖本任务产物，但两个 runtime 的调用点签名以此为准。

- [ ] **Step 1: 写失败测试（执行器单元级）**

`src/OpenClaw.Tests/CapabilitySlotExecutorTests.cs`：

(a) fixture 构造改为 `Executor = new CapabilitySlotExecutor(registry, new CapabilityBindingCache())`（`RouterFixture.Executor` 初始化处，原 `new CapabilitySlotExecutor(registry)`）；
(b) 既有 9 个用例的 `ExecuteAsync(...)` 调用统一加 `"sess-1"` 实参（第三个参数），例如 `ExecuteAsync(StaticRef(), """{"city":"Oslo"}""", "sess-1", TestContext.Current.CancellationToken)`；
(c) 新增两个用例：

```csharp
[Fact]
public async Task Execute_Dynamic_SameSessionSecondCall_SkipsResolve()
{
    await using var fixture = await CreateFixtureAsync();

    for (var i = 0; i < 2; i++)
    {
        var result = await fixture.Executor.ExecuteAsync(DynamicRef(), """{"city":"Oslo"}""", "sess-1", TestContext.Current.CancellationToken);
        Assert.Equal(ToolResultStatuses.Completed, result.ResultStatus);
        Assert.Equal("Weather for Oslo: sunny", result.ResultText);
    }

    // Same session: the binding is cached, so the second call only proxies.
    Assert.Equal(new[] { "search", "add:weather-mcp", "use:weather-mcp:get_weather", "use:weather-mcp:get_weather" }, fixture.State.Calls);
}

[Fact]
public async Task Execute_Dynamic_DifferentSession_ResolvesIndependently()
{
    await using var fixture = await CreateFixtureAsync();

    await fixture.Executor.ExecuteAsync(DynamicRef(), """{"city":"Oslo"}""", "sess-1", TestContext.Current.CancellationToken);
    await fixture.Executor.ExecuteAsync(DynamicRef(), """{"city":"Oslo"}""", "sess-2", TestContext.Current.CancellationToken);

    Assert.Equal(new[] { "search", "add:weather-mcp", "use:weather-mcp:get_weather", "search", "add:weather-mcp", "use:weather-mcp:get_weather" }, fixture.State.Calls);
}
```

- [ ] **Step 2: 运行测试确认 RED**

Run: `dotnet test src/OpenClaw.Tests -c Release --filter FullyQualifiedName~CapabilitySlotExecutorTests`
Expected: 编译失败（ctor/方法签名不匹配）——编译级 RED 可接受。

- [ ] **Step 3: 写失败测试（双 runtime e2e）**

`src/OpenClaw.Tests/NacosRouterIntegrationTests.cs` 新增（复制 `DynamicSlot_ResolvesThenUses_AndFallsBackOnUseFailure` 的 fixture 段，改理论体）：

```csharp
[Theory]
[InlineData(false)]
[InlineData(true)]
public async Task DynamicSlot_SameSession_ResolvesOnceThenReusesBinding(bool maf)
{
    var state = new NacosRouterFixtureState();
    var builder = WebApplication.CreateSlimBuilder();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Services.AddSingleton(state);
    builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true).WithTools<FakeNacosRouterMcpTools>();
    await using var server = builder.Build();
    server.MapMcp("/mcp");
    await server.StartAsync(TestContext.Current.CancellationToken);
    await using var registry = new McpServerToolRegistry(new McpPluginsConfig(), NullLogger<McpServerToolRegistry>.Instance);
    var config = ServerConfig(server.Urls.Single() + "/mcp");
    var reload = await registry.ReloadWorkspaceServersAsync(config, TestContext.Current.CancellationToken);
    state.Calls.Clear();
    var skill = LoadDynamicDemo();
    var root = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    using var memory = new FileMemoryStore(root, 4);
    var gatewayConfig = new GatewayConfig { Memory = new MemoryConfig { StoragePath = root } };
    var tools = reload.AddedTools.Append<ITool>(new EmitTextTool()).ToArray();
    var (runtime, chat, execution) = CreateRuntime(maf, tools, memory, skill, gatewayConfig,
        new CapabilitySlotExecutor(registry, new CapabilityBindingCache()));
    try
    {
        // One session, two invocations: the first resolves, the second reuses.
        var session = new Session { Id = "nacos-dyn-cached", SenderId = "test", ChannelId = "test" };
        for (var i = 0; i < 2; i++)
        {
            var method = runtime.GetType().GetMethod("ExecuteMetaSkillAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var result = await (Task<string>)method.Invoke(runtime, [session, skill.Name, "Oslo", TestContext.Current.CancellationToken])!;
            Assert.Equal("Weather for Oslo: sunny", result);
            var run = session.MetaRunHistory[i];
            var query = Assert.Single(run.StepResults, step => step.Id == "query");
            Assert.Equal("completed", query.Status);
        }
        Assert.Equal(new[] { "search", "add:weather-mcp", "use:weather-mcp:get_weather", "use:weather-mcp:get_weather" }, state.Calls);
        Assert.Empty(chat.ReceivedCalls());
        Assert.Empty(execution.ReceivedCalls());
    }
    finally
    {
        if (runtime is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
        else if (runtime is IDisposable disposable) disposable.Dispose();
        memory.Dispose();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
```

说明：`tools` 必须复用首次 `reload.AddedTools`——同一配置二次 `ReloadWorkspaceServersAsync` 返回空 `AddedTools`（server 已注册，同既有 static theory 的 `unchanged` 断言语义）。既有 `DynamicSlot_ResolvesThenUses_AndFallsBackOnUseFailure` 的断言**保持不变**——它用两个不同 session，正是跨会话隔离的 pin。

- [ ] **Step 4: 运行测试确认 RED**

Run: `dotnet test src/OpenClaw.Tests -c Release --filter FullyQualifiedName~NacosRouterIntegrationTests`
Expected: 编译失败（执行器 ctor 增参后既有构造处不匹配）。

- [ ] **Step 5: 最小实现**

(a) `CapabilitySlotExecutor.cs`：

```csharp
private readonly McpServerToolRegistry _registry;
private readonly CapabilityBindingCache _bindingCache;
// (保留既有 _addedServers 字段与注释不变)

public CapabilitySlotExecutor(McpServerToolRegistry registry, CapabilityBindingCache bindingCache)
{
    _registry = registry;
    _bindingCache = bindingCache;
}

public async Task<ToolExecutionResult> ExecuteAsync(
    MetaCapabilityRefDefinition capabilityRef, string toolArgsJson, string sessionId, CancellationToken ct)
```

动态分支（替换现有 `else` 块中的解析段，保持失败分支不变）：

```csharp
else
{
    var intent = capabilityRef.Intent!;
    var request = new ResolveCapabilityRequest(
        intent.TaskDescription,
        intent.Keywords.Count > 0 ? string.Join(",", intent.Keywords) : null,
        capabilityRef.SelectionPolicy == "exact_name"
            ? ResolveCapabilitySelectionPolicy.ExactName
            : ResolveCapabilitySelectionPolicy.First);

    var intentKey = CapabilityBindingCache.ComputeIntentKey(
        request.TaskDescription, request.KeyWords, request.SelectionPolicy.ToString());
    if (!string.IsNullOrEmpty(sessionId) &&
        _bindingCache.TryGet(sessionId, intentKey, out var cachedServer, out var cachedTool))
    {
        server = cachedServer;
        tool = cachedTool;
    }
    else
    {
        var (binding, failure) = await ResolveCapabilityTool.ResolveCoreAsync(_registry, request, ct);
        if (binding is null)
        {
            var code = failure!.FailureCode == ResolveCapabilityFailureCodes.RouterUnavailable
                ? CapabilitySlotFailureCodes.RouterUnavailable
                : CapabilitySlotFailureCodes.ResolveFailed;
            return Fail(code, $"capability resolve failed: {failure.FailureCode}", toolArgsJson);
        }

        server = binding.Server;
        tool = binding.Tool;
        if (!string.IsNullOrEmpty(sessionId))
            _bindingCache.Set(sessionId, intentKey, server, tool);
    }
}
```

类 doc comment 增补一句：`Dynamic bindings are cached per session via the injected CapabilityBindingCache (TTL + reload invalidation, issue #232).`

(b) 两个 runtime 调用点（`AgentRuntime.cs:2871`、`MafAgentRuntime.cs:2648`）：

```csharp
lastResult = await _capabilitySlotExecutor.ExecuteAsync(capabilityRef, toolArgsJson, session.Id, effectiveCt);
```

(c) `ToolServicesExtensions.cs`（在现有 `AddSingleton` 的 CapabilitySlotExecutor lambda 前）：

```csharp
// Capability binding cache (#232): session-scoped dynamic bindings, cleared
// on workspace MCP reload by McpWorkspaceWatcherService.
services.AddSingleton<CapabilityBindingCache>();
services.AddSingleton(sp =>
    new CapabilitySlotExecutor(
        sp.GetRequiredService<McpServerToolRegistry>(),
        sp.GetRequiredService<CapabilityBindingCache>()));
```

(d) 既有测试构造处：`NacosRouterIntegrationTests.cs` 两个 theory 与 `CapabilitySlotExecutorTests.cs` 全部 `new CapabilitySlotExecutor(registry)` → `new CapabilitySlotExecutor(registry, new CapabilityBindingCache())`；`CapabilitySlotExecutorTests` 里直接 `new CapabilitySlotExecutor(registry)` 的 `Execute_NoRouterClient_ReturnsCapabilityRouterUnavailable` 同理。

- [ ] **Step 6: 运行测试确认 GREEN**

Run: `dotnet test src/OpenClaw.Tests -c Release --filter "FullyQualifiedName~CapabilitySlot|FullyQualifiedName~NacosRouterIntegrationTests"`
Expected: 全绿（原 23 通过 + 新 2 Fact + 新 theory 2 组 = 27 通过，1 跳过 LiveRouter）。

- [ ] **Step 7: 提交**

```bash
git add src/OpenClaw.Agent src/OpenClaw.Gateway/Composition/ToolServicesExtensions.cs src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs src/OpenClaw.Tests
git commit -m "$(cat <<'EOF'
feat(#232): cache dynamic capability bindings per session in the slot executor

Co-Authored-By: Claude Code <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: reload 失效钩子（watcher 清空缓存）

**Files:**

- Modify: `src/OpenClaw.Gateway/McpWorkspaceWatcherService.cs`（ctor 末位增参 `CapabilityBindingCache bindingCache`；reload 成功后 `Clear()`）
- Modify: `src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.RuntimeFactories.cs`（`StartMcpWorkspaceWatcher` 传参）
- Test: `src/OpenClaw.Tests/GatewayRuntimeLifecycleTests.cs`（新增 AC3 用例 + 既有 watcher 用例 ctor 增参）
- Test: `src/OpenClaw.Tests/McpServerToolRegistryTests.cs:114,160`（两处 watcher 构造增参）

**Interfaces:**

- Consumes: Task 1 的 `CapabilityBindingCache`（`Set`/`TryGet`）；Task 2 无依赖。
- Produces: `McpWorkspaceWatcherService` ctor 新签名 `(registry, agentRuntime, workspacePath, logger, configStore, bindingCache)`——后续任务不再改此 ctor。

- [ ] **Step 1: 写失败测试**

`src/OpenClaw.Tests/GatewayRuntimeLifecycleTests.cs`（文件顶部加 `using OpenClaw.Agent.Tools;`；新用例放 `McpWorkspaceWatcherService_TriggerReload_AppliesToolChanges` 之后）：

```csharp
[Fact]
public async Task McpWorkspaceWatcherService_ReloadSuccess_ClearsCapabilityBindingCache()
{
    var root = CreateTempRoot();
    Directory.CreateDirectory(root);
    await using var registry = new McpServerToolRegistry(
        new McpPluginsConfig
        {
            Enabled = false,
            Servers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
        },
        NullLogger<McpServerToolRegistry>.Instance);
    try
    {
        var runtime = Substitute.For<IAgentRuntime>();
        var store = new McpConfigStore(root, NullLogger<McpConfigStore>.Instance);
        await store.SaveAsync("""{"enabled":true,"servers":{}}""", TestContext.Current.CancellationToken);
        var cache = new CapabilityBindingCache();
        var key = CapabilityBindingCache.ComputeIntentKey("weather city", "weather,city", "First");
        cache.Set("sess-1", key, "weather-mcp", "get_weather");

        using var service = new McpWorkspaceWatcherService(
            registry,
            runtime,
            workspacePath: null,
            NullLogger<McpWorkspaceWatcherService>.Instance,
            store,
            cache);

        using var cts = new CancellationTokenSource();
        service.Start(cts.Token);
        service.TriggerReload();

        await WaitForConditionAsync(
            () => !cache.TryGet("sess-1", key, out _, out _),
            TimeSpan.FromSeconds(3),
            TestContext.Current.CancellationToken);
    }
    finally
    {
        DeleteDirectoryIfPresent(root);
    }
}
```

同时给既有 3 处 watcher 构造追加末位实参 `new CapabilityBindingCache()`（`GatewayRuntimeLifecycleTests.cs` 1 处、`McpServerToolRegistryTests.cs` 2 处；后一个文件顶部加 `using OpenClaw.Agent.Tools;`）。

- [ ] **Step 2: 运行测试确认 RED**

Run: `dotnet test src/OpenClaw.Tests -c Release --filter "FullyQualifiedName~GatewayRuntimeLifecycleTests|FullyQualifiedName~McpServerToolRegistryTests"`
Expected: 编译失败（watcher ctor 缺参）——编译级 RED 可接受。

- [ ] **Step 3: 最小实现**

(a) `McpWorkspaceWatcherService.cs`：

```csharp
private readonly CapabilityBindingCache _bindingCache;

public McpWorkspaceWatcherService(
    McpServerToolRegistry registry,
    IAgentRuntime agentRuntime,
    string? workspacePath,
    ILogger<McpWorkspaceWatcherService> logger,
    McpConfigStore configStore,
    CapabilityBindingCache bindingCache)
{
    _registry = registry;
    _agentRuntime = agentRuntime;
    _logger = logger;
    _configStore = configStore;
    _workspacePath = workspacePath;
    _bindingCache = bindingCache;
}
```

`RunReloadLoopAsync` 中，`await _agentRuntime.ApplyMcpToolChangesAsync(...)` 与 `_logger.LogInformation(...)` 之间插入：

```csharp
_bindingCache.Clear();
_logger.LogDebug("Cleared capability binding cache after workspace MCP reload.");
```

(b) `RuntimeInitializationExtensions.RuntimeFactories.cs` 的 `StartMcpWorkspaceWatcher`：

```csharp
var watcher = new McpWorkspaceWatcherService(
    services.McpRegistry,
    agentRuntime,
    startup.WorkspacePath,
    app.Services.GetRequiredService<ILogger<McpWorkspaceWatcherService>>(),
    app.Services.GetRequiredService<McpConfigStore>(),
    app.Services.GetRequiredService<CapabilityBindingCache>());
```

- [ ] **Step 4: 运行测试确认 GREEN**

Run: `dotnet test src/OpenClaw.Tests -c Release --filter "FullyQualifiedName~GatewayRuntimeLifecycleTests|FullyQualifiedName~McpServerToolRegistryTests"`
Expected: 全绿；若 `WaitForConditionAsync` 超时失败，检查 watcher 循环是否在 `Clear()` 前抛异常（`ReloadWorkspaceServersAsync` 失败时不清缓存是有意行为——仅成功 reload 清空）。

- [ ] **Step 5: 提交**

```bash
git add src/OpenClaw.Gateway src/OpenClaw.Tests
git commit -m "$(cat <<'EOF'
feat(#232): clear capability binding cache on workspace MCP reload

Co-Authored-By: Claude Code <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: 文档 + 全量验证

**Files:**

- Modify: `docs/nacos-mcp-router.md`（Capability slots 章节 dynamic 条目）
- Modify: `docs/zh-CN/Nacos-MCP-Router与OpenClaw.NET-MetaSkill集成架构.md`（§5.3 表 dynamic 行、§7.3 失效机制行、§6 实现对照 note）

- [ ] **Step 1: 更新 EN 文档**

`docs/nacos-mcp-router.md` Capability slots 章节 binding modes 的 dynamic 条目（当前以 "The executor resolves the intent through the same `resolve_capability` core as #230..." 开头），替换为：

```
- **dynamic** — the executor resolves the intent through the same
  `resolve_capability` core as #230 (`search → add`, no LLM, no `use_tool`
  inside the resolver), then calls `use_tool` on the bound tool. Successful
  bindings are cached per session, keyed by a SHA-256 hash of the normalised
  intent (task_description + keywords + selection_policy); later slots in the
  same session reuse the binding until its TTL (default 300 s) expires or the
  workspace MCP config reloads. `resolve_capability` itself is never cached.
```

- [ ] **Step 2: 更新 zh 架构文档**

(a) §5.3 表 `binding: dynamic` 行的执行语义单元格改为：

```
执行时经 #230 Resolver 核心 `search → add` 解析绑定，再 `use_tool`；零 LLM 往返；绑定按会话缓存（intent 哈希键，TTL/reload 失效，#232）
```

(b) §7.3 表 `失效机制` 行改为：

```
| 失效机制 | TTL 过期（可配，默认 300s）+ mcp.json reload 成功清空（#232 已实现）；订阅 Nacos 配置变更事件（后续项，需 Nacos SDK 或 Router 通知能力） |
```

(c) §6 实现对照 note 替换为：

```
> 实现对照（2026-09-14，#230/#231/#232 已落地）：上图中动态槽位的 `search → add → use` 与静态槽位的 `add`（首次，幂等缓存）→ `use` 均为确定性代码路径；会话级绑定缓存（intent 哈希 + TTL/reload 失效）已实现，Nacos 变更事件订阅的失效联动仍属后续项。
```

- [ ] **Step 3: 全量测试**

Run: `dotnet test -c Release`
Expected: 与 #231 基线一致——全量通过，除已知 2 个环境性失败（`PluginCommandsTests...NativePluginDoesNotRunNpmLifecycleScripts` npm 临时目录问题、`CompanionCanvasUiTests...PreserveDraftUntilSend` CRLF 行尾问题，二者与 #232 无关，隔离运行仍失败）。

- [ ] **Step 4: 提交**

```bash
git add docs
git commit -m "$(cat <<'EOF'
docs(#232): record binding cache semantics (TTL + reload invalidation)

Co-Authored-By: Claude Code <noreply@anthropic.com>
EOF
)"
```

---

## Acceptance Criteria 映射

| AC | 覆盖 |
|---|---|
| 1. 同一 intent 二次解析 cache 命中，search/add 次数为 1 | Task 2：`Execute_Dynamic_SameSessionSecondCall_SkipsResolve`（Calls == [search, add, use, use]）+ e2e `DynamicSlot_SameSession_ResolvesOnceThenReusesBinding` |
| 2. TTL 过期后重解析 | Task 1：`TryGet_ExpiredEntry_Misses`（50ms TTL + 200ms 延迟） |
| 3. mcp.json reload 后缓存清空 | Task 3：`McpWorkspaceWatcherService_ReloadSuccess_ClearsCapabilityBindingCache` |
| 4. 静态绑定进程内只解析一次 | 既有 #231 测试已 pin：`Execute_Static_AddsOncePerExecutorInstance_ThenUsesToolEachCall` 与 `StaticSlot_UsesOnlyThreeRouterTools_AndHandlesProtocolFailures`（2 次执行 1 次 add）；执行器为 DI 单例即进程级，本次无代码改动 |
| 5. `dotnet test` 全量 | Task 4 Step 3，与 #231 基线比对 |

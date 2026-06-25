# 工具钩子（Tool Hooks）

工具钩子在 Agent 运行时循环中拦截每次工具调用 —— 在调用前和调用后 ——
实现审计日志、策略执行、自主模式门控、合约范围限制以及外部插件集成。

---

## 架构概览

```text
┌──────────────────────────────────────────────────────────────┐
│                     网关启动                                  │
│  CreateHooks()                                               │
│  ┌──────────────────────────────────────────────────────┐    │
│  │ 始终存在: AuditLogHook, AutonomyHook, ContractScopeHook │   │
│  │ + pluginHost.ToolHooks (BridgedToolHook 实例)         │    │
│  │ + nativeDynamicPluginHost.ToolHooks (自定义 IToolHook)│    │
│  └──────────────────────┬───────────────────────────────┘    │
└────────────────────────┬──────────────────────────────────────┘
                         │ IReadOnlyList<IToolHook>
                         ▼
┌──────────────────────────────────────────────────────────────┐
│                    AgentRuntime                               │
│  _hooks → OpenClawToolExecutor(_hooks, ...)                  │
└────────────────────────┬──────────────────────────────────────┘
                         │
                         ▼
┌──────────────────────────────────────────────────────────────┐
│              OpenClawToolExecutor.ExecuteAsync()              │
│                                                              │
│  1. 构造 ToolHookContext（sessionId, channelId, ...）         │
│  2. BEFORE: foreach hook in _hooks:                          │
│     - IToolHookWithContext? → BeforeExecuteAsync(ctx)        │
│     - 否则 → BeforeExecuteAsync(name, args)                   │
│     - 任一 hook 返回 false → 硬拒绝                            │
│  3. 执行实际工具                                               │
│  4. AFTER: foreach hook in _hooks:                           │
│     - AfterExecuteAsync(...), 异常安全                         │
└──────────────────────────────────────────────────────────────┘
```

**核心设计原则：**

- **无集中注册表** — Hook 就是一个扁平的 `IReadOnlyList<IToolHook>`。
- **广度优先执行** — 每个 hook 在 before 和 after 阶段都会对每次工具调用运行。
- **硬拒绝机制** — `BeforeExecuteAsync` 中任一 hook 返回 `false` 则跳过工具，并将拒绝消息返回给 LLM。
- **After-hook 安全失败** — after-hook 中的异常被捕获并记录，绝不向上传播。
- **桥接超时保护** — 插件桥接 hook 对 `BeforeExecuteAsync` 强制执行 5 秒超时，防止异常插件阻塞 Agent 循环。

---

## 核心接口

### `IToolHook`

定义于 `src/OpenClaw.Core/Abstractions/IToolHook.cs`：

```csharp
public interface IToolHook
{
    string Name { get; }

    ValueTask<bool> BeforeExecuteAsync(
        string toolName,
        string arguments,
        CancellationToken ct);

    ValueTask AfterExecuteAsync(
        string toolName,
        string arguments,
        string result,
        TimeSpan duration,
        bool failed,
        CancellationToken ct);
}
```

| 成员 | 阶段 | 返回值 | 行为 |
|------|------|--------|------|
| `Name` | — | `string` | 用于日志的可读标识。 |
| `BeforeExecuteAsync` | 调用前 | `ValueTask<bool>` | 返回 `false` 拒绝执行。LLM 收到：`"Tool execution denied by hook: {hookName}"`。 |
| `AfterExecuteAsync` | 调用后 | `ValueTask` | 每次工具执行后调用，即使工具失败也会调用。即发即弃，异常被捕获。 |

### `IToolHookWithContext`

定义于 `src/OpenClaw.Core/Abstractions/IToolHookWithContext.cs`：

扩展 `IToolHook`，为需要会话、频道或发送者元数据的 Hook 提供带 `ToolHookContext` 的重载：

```csharp
public interface IToolHookWithContext : IToolHook
{
    ValueTask<bool> BeforeExecuteAsync(
        ToolHookContext context,
        CancellationToken ct);

    ValueTask AfterExecuteAsync(
        ToolHookContext context,
        string result,
        TimeSpan duration,
        bool failed,
        CancellationToken ct);
}
```

### `ToolHookContext`

定义于 `src/OpenClaw.Core/Abstractions/ToolHookContext.cs`：

```csharp
public readonly record struct ToolHookContext(
    string SessionId,
    string ChannelId,
    string SenderId,
    string CorrelationId,
    string ToolName,
    string ArgumentsJson,
    bool IsStreaming);
```

`OpenClawToolExecutor` 在每个 hook 阶段前构造此上下文。实现 `IToolHookWithContext` 的 hook 直接接收它；普通 `IToolHook` hook 仅接收 `toolName` 和 `arguments`。

---

## 内置 Hook

三个内置 Hook 在网关启动时始终注册。它们按顺序在每次工具调用上运行。

### `AuditLogHook`（审计日志）

源码：`src/OpenClaw.Agent/AuditLogHook.cs`

为审计目的记录每一次工具执行。实现 `IToolHookWithContext`。

- `BeforeExecute` — 始终返回 `true`（永不拒绝）。
- `AfterExecute` — 记录成功/失败、耗时、结果长度以及会话/频道/发送者元数据。

**用途：** 可观测性与审计合规。每次工具调用都有迹可循。

### `AutonomyHook`（自主模式门控）

源码：`src/OpenClaw.Agent/AutonomyHook.cs`

作为硬拒绝层执行自主模式策略。实现 `IToolHook`。

| 策略 | 配置键 | 行为 |
|------|--------|------|
| `AutonomyMode = "readonly"` | `ToolingConfig.AutonomyMode` | 拒绝写入类工具（`shell`、`write_file` 等）。只读工具正常通过。 |
| `WorkspaceOnly` | `ToolingConfig.WorkspaceOnly` | 确保文件系统工具路径位于工作区根目录内。 |
| `ForbiddenPathGlobs` | `ToolingConfig.ForbiddenPathGlobs` | 拒绝匹配任一 glob 模式的文件路径或 shell 命令。 |
| `AllowShell` | `ToolingConfig.AllowShell` | 为 `false` 时拒绝所有 shell 工具调用。 |
| `AllowedShellCommandGlobs` | `ToolingConfig.AllowedShellCommandGlobs` | 设置后，仅允许匹配 glob 的 shell 命令。 |

**用途：** 安全门控。防止 Agent 运行任意命令或在允许路径之外写入文件。

### `ContractScopeHook`（合约范围限制）

源码：`src/OpenClaw.Agent/ContractScopeHook.cs`

对工具调用执行合约范围限制。实现 `IToolHookWithContext`。

| 策略 | 配置键 | 行为 |
|------|--------|------|
| `MaxToolCalls` | `ContractPolicy.MaxToolCalls` | 按会话跟踪工具调用次数，超出上限后拒绝。 |
| `AllowedPaths` | `ContractPolicy.AllowedPaths` | 将文件系统工具限制在明确允许的路径前缀内。 |
| 范围 shell 拒绝 | — | 在范围合约下运行时，完全拒绝 `shell` 和 `code_exec` 工具。 |

**用途：** 资源治理。防止范围会话中的工具滥用和路径逃逸。

---

## 插件 Hook

外部插件（Node.js 桥接或原生 .NET）可以注册自定义 Hook。

### 插件桥接（`BridgedToolHook`）

源码：`src/OpenClaw.Agent/Plugins/BridgedToolHook.cs`

通过 JSON-RPC 将 hook 事件转发到进程外的 Node.js 插件。

#### 注册

插件在其清单中声明事件订阅：

```json
{
  "eventSubscriptions": ["tool:before", "tool:after"]
}
```

通配符 `"tool:*"` 订阅两者。当桥接主机检测到这些订阅后，会为该插件创建 `BridgedToolHook`。

#### Before 阶段

1. 发送 `"hook_before"` JSON-RPC 请求，附带 `{ EventName, ToolName, Arguments }`。
2. **5 秒超时** —— 插件未响应时，默认**放行**并记录警告日志。
3. 插件处理函数返回 `true`/`false`。任一插件返回 `false` 即拒绝该工具。

#### After 阶段

1. 发送 `"hook_after"` JSON-RPC 请求，附带 `{ EventName, ToolName, Arguments, Result, DurationMs, Failed }`。
2. 单向即发即弃 —— 异常被静默捕获。
3. 插件处理函数的返回值被忽略。

#### JavaScript 插件 API（`api.on(...)`）

源码：`src/OpenClaw.Agent/Plugins/plugin-bridge.mjs`

```javascript
// tool:before —— 返回 false 拒绝
api.on("tool:before", (ctx) => {
  // ctx.toolName, ctx.arguments
  if (ctx.toolName === "shell") return false;
  return true;
});

// tool:after —— 返回值被忽略
api.on("tool:after", (ctx) => {
  // ctx.toolName, ctx.arguments, ctx.result, ctx.durationMs, ctx.failed
  console.log(`工具 ${ctx.toolName} 完成，耗时 ${ctx.durationMs}ms`);
});
```

| 兼容性 | 运行时 | 说明 |
|--------|--------|------|
| 有限支持 | 仅 JIT | AOT 阻止原生动态 Hook，但允许桥接 Hook。强制执行 5 秒超时。 |

### 原生动态插件

源码：`src/OpenClaw.Agent/Plugins/NativeDynamicPluginHost.cs`

进程内的 .NET 插件可以直接注册 Hook：

```csharp
public class MyPlugin : INativeDynamicPlugin
{
    public void Register(INativeDynamicPluginContext context)
    {
        context.RegisterHook(new MyCustomHook());
    }
}
```

Hook 实现 `IToolHook`，遵循与内置 Hook 相同的执行契约。

| 兼容性 | 运行时 | 说明 |
|--------|--------|------|
| 仅 JIT | JIT | 通过 `PluginCapabilityPolicy.Hooks` 在 AOT 中被阻止。 |

---

## 适配器级 Hook

### `SemanticKernelPolicyHook`

源码：`src/OpenClaw.SemanticKernelAdapter/SemanticKernelPolicyHook.cs`

治理 Semantic Kernel 相关工具（以 `sk_` 为前缀）。实现 `IToolHookWithContext`。

- 通过 glob 模式按工具允许/拒绝。
- 按发送者、按工具、按分钟的速率限制（内存内实现，定期清理）。
- 非 SK 工具直接放行。

---

## Hook 执行顺序

Hook 按 `IReadOnlyList<IToolHook>` 中的顺序执行，该顺序在网关启动时确定：

1. `AuditLogHook`
2. `AutonomyHook`
3. `ContractScopeHook`
4. 插件桥接 Hook（每个订阅插件一个，按注册顺序）
5. 原生动态插件 Hook（按注册顺序）

所有 Hook 在每次工具调用上都会运行。没有优先级系统，除硬拒绝外没有短路机制，Hook 层级也没有按工具的门控（只关心特定工具的 Hook 在内部自行过滤）。

---

## 配置

没有独立的 `HooksConfig` 类。Hook 行为通过相关配置节驱动：

| Hook | 配置节 | 关键字段 |
|------|--------|----------|
| `AutonomyHook` | `ToolingConfig` | `AutonomyMode`、`WorkspaceOnly`、`ForbiddenPathGlobs`、`AllowShell`、`AllowedShellCommandGlobs` |
| `ContractScopeHook` | `ContractPolicy` | `MaxToolCalls`、`AllowedPaths` |
| `SemanticKernelPolicyHook` | `SemanticKernelPolicyOptions` | 允许/拒绝 glob、速率限制 |
| 插件 Hook | `PluginsConfig` | 发现、能力门控 |

---

## 添加自定义 Hook

1. 实现 `IToolHook`（或 `IToolHookWithContext` 用于需要感知会话的 Hook）：

```csharp
public sealed class MyPolicyHook : IToolHook
{
    public string Name => "my_policy";

    public ValueTask<bool> BeforeExecuteAsync(
        string toolName, string arguments, CancellationToken ct)
    {
        // 返回 false 拒绝执行。
        return ValueTask.FromResult(true);
    }

    public ValueTask AfterExecuteAsync(
        string toolName, string arguments, string result,
        TimeSpan duration, bool failed, CancellationToken ct)
    {
        // 追踪指标、记录日志等。
        return ValueTask.CompletedTask;
    }
}
```

2. 在 `RuntimeInitializationExtensions.CreateHooks()` 中注册：

```csharp
private static IReadOnlyList<IToolHook> CreateHooks(...)
{
    var hooks = new List<IToolHook>
    {
        new AuditLogHook(logger),
        new AutonomyHook(toolingConfig),
        new ContractScopeHook(logger),
        new MyPolicyHook()  // ← 在此添加
    };
    // ... 追加插件 Hook ...
    return hooks;
}
```

Hook 注册是代码驱动的 —— 没有基于配置文件的注册方式。

---

## 相关文档

- [CAPABILITY_MATRIX.md](../CAPABILITY_MATRIX.md) — AOT/JIT Hook 支持矩阵。
- [COMPATIBILITY.md](../COMPATIBILITY.md) — `api.on(...)` 兼容性说明。
- [ARCHITECTURE_BOUNDARIES.md](../ARCHITECTURE_BOUNDARIES.md) — Hook 在架构中的位置。
- [GOAL_TECHNICAL_ARCHITECTURE.md](../GOAL_TECHNICAL_ARCHITECTURE.md) — Hook 触发模式。

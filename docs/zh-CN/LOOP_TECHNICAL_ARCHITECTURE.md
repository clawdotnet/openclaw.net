# OpenClaw.NET /loop 命令 — 技术架构文档

> 基于 TickerQ 的会话级定时循环提示词注入机制。用户设置 /loop 后，系统按指定间隔自动向会话注入预设提示词并推进 Agent 轮次，支持幂等覆盖和双层语义自毁；当前活跃 loop 条目存储在内存中，进程重启后不会保留。

- **状态:** 已实现（分支: `Feature/Codex-loop`）
- **提交:** `78d115e`、`bb58cf3`、`42acf7c`
- **总变更:** +1,033 行，涉及 11 个文件
- **测试:** 62 通过，0 失败

---

## 目录

1. [问题与动机](#1-问题与动机)
2. [架构总览](#2-架构总览)
3. [组件清单](#3-组件清单)
4. [Loop 生命周期状态机](#4-loop-生命周期状态机)
5. [TickerQ 调度引擎](#5-tickerq-调度引擎)
6. [双层语义终止检测](#6-双层语义终止检测)
7. [CLI 命令与聊天频道](#7-cli-命令与聊天频道)
8. [Gateway DI 与接线](#8-gateway-di-与接线)
9. [错误处理与并发安全](#9-错误处理与并发安全)
10. [OTel 上下文斩断](#10-otel-上下文斩断)
11. [设计与 /goal 的关系](#11-设计与-goal-的关系)
12. [NativeAOT 兼容性](#12-nativeaot-兼容性)
13. [测试策略](#13-测试策略)
14. [后续扩展](#14-后续扩展)

---

## 1. 问题与动机

### 定时自主监控需求

在日常开发与运维中，大量场景需要 Agent 按固定时间间隔自动执行任务：

- **构建健康检查：** 每 5 分钟检查 CI 状态，发现问题即时报告
- **日志轮询：** 每 30 秒扫描生产日志中的异常模式
- **周期性代码审查：** 每 2 小时审查最新提交的代码变更
- **环境状态监控：** 每 1 小时检查服务器资源使用率

传统方式需要用户手动反复输入相同指令，或依赖外部 cron 脚本——两者都脱离了 Agent 会话上下文，无法利用 Agent 的记忆、工具链和推理能力。

### /loop 是什么

`/loop` 是一个**定时循环提示词注入**命令。用户在会话中输入 `/loop 5m 检查构建状态` 后，系统会：

1. 将 interval 解析为标准 cron 表达式
2. 将 loop 条目写入内存调度注册表
3. 每次轮询发现条目到期时，将预设 prompt 作为系统消息注入当前会话
4. 推进 Agent 执行完整的工具调用循环，产生响应
5. 当任务完成时（模型声明或关键词匹配），自动从注册表移除 loop 条目

### 生态系统对比

| 特性 | Codex /loop | OpenClaw.NET /loop |
|-------|-------------|---------------------|
| 调度引擎 | 内存级定时器（随客户端消亡） | **TickerQ 分钟级轮询 + 内存 loop 注册表** |
| 重启恢复 | ❌ 丢失 | ❌ 活跃 loop 注册表重启后丢失（持久化待增强） |
| 幂等覆盖 | ✅ 单 session 唯一 | ✅ 单 session 唯一 |
| 语义自毁 | ✅ 关键词检测 | ✅ **双层检测（tool + 关键词）** |
| 触发渠道 | 仅 CLI/TUI | **CLI + 聊天频道** |
| 并发安全 | 无集群支持 | `ConcurrentDictionary` + 单条目锁 |
| 宿主语言 | Rust | **C# / .NET** |

---

## 2. 架构总览

```plaintext
┌─────────────────────────────────────────────────────────────────────┐
│                    用户 / 操作员 (CLI / 频道)                         │
│       /loop 5m 检查构建状态                                          │
└─────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────┐
│                   ChatCommandProcessor (/loop)                       │
│  schedule (带 interval + prompt) │ cancel │ status                   │
│  内建命令，SetLoopCallback 桥接到 Gateway                            │
└─────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────┐
│              OpenClaw.Core.Loops (独立调度子系统)                     │
│                                                                      │
│  ┌──────────────────┐   ┌──────────────────────────────┐           │
│  │ ClawLoopScheduler │──▶│ ConcurrentDictionary          │           │
│  │  · ScheduleLoop   │   │   sessionId → LoopEntry       │           │
│  │  · CancelLoop     │   │   (cron 表达式 + 状态追踪)     │           │
│  │  · GetLoopStatus  │   └──────────┬───────────────────┘           │
│  └────────▲─────────┘               │ 每分钟 poll                    │
│           │                         ▼                                │
│  ┌────────┴─────────┐   ┌──────────────────────────────┐           │
│  │ ILoopControlService│  │ AgentLoopJob                  │           │
│  │  · SignalComplete  │  │ [TickerFunction(              │           │
│  └────────▲─────────┘   │   "AgentLoopExecutor",         │           │
│           │              │   cronExpression: "* * * * *")]│           │
│  ┌────────┴─────────────┐──────────────────────────────┘           │
│  │ LoopTerminationDetector                                           │
│  │  路径 1 (主): loop_control tool → SignalComplete                 │
│  │  路径 2 (兜底): 响应文本关键词匹配 → SignalComplete              │
│  └─────────────────────────────────────────────────────┘            │
└─────────────────────────────────────────────────────────────────────┘
               │ 派发 turn
               ▼
┌─────────────────────────────────────────────────────────────────────┐
│                    OpenClaw.Agent                                     │
│  ┌──────────────────┐   ┌──────────────────────────────────────┐   │
│  │ LoopControlTool   │   │ AgentRuntime (无侵入)                  │   │
│  │ IToolWithContext  │   │  loop prompt 作为普通 InboundMessage   │   │
│  │ → ILoopControlSvc │   │  进入 MessagePipeline → RunAsync()     │   │
│  └──────────────────┘   └──────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────────┘
```

**关键设计原则：**

- **Core/Loops/** 是纯调度层，不依赖 Agent、Gateway，仅依赖 TickerQ + DI 抽象
- **AgentRuntime 零侵入**：loop prompt 通过 `MessagePipeline.InboundWriter` 注入，与普通用户消息走相同路径
- **Goal 路径不受影响**：`/loop` 和 `/goal` 是独立的调度入口，共享 AgentRuntime 但生命周期管理完全隔离

---

## 3. 组件清单

| 组件 | 路径 | 职责 |
|-----------|------|----------------|
| `AgentLoopRequestPayload` | `Core/Loops/AgentLoopRequestPayload.cs` | 强类型 JSON payload record（sessionId + prompt），AOT 安全的源生成序列化 |
| `IAgentLoopDispatcher` | `Core/Loops/IAgentLoopDispatcher.cs` | 派发接口：将 loop prompt 注入 session 并推进 turn |
| `ILoopControlService` | `Core/Loops/ILoopControlService.cs` | 终止信号接口：接收 tool 或 detector 的完成通知 |
| `ClawLoopScheduler` | `Core/Loops/ClawLoopScheduler.cs` | 调度门面：管理 `ConcurrentDictionary<string, LoopEntry>` 注册表，提供 Schedule/Cancel/Status + `IntervalToCron()` 静态方法 |
| `LoopEntry` | `Core/Loops/ClawLoopScheduler.cs` | 单个 loop 条目：sessionId、prompt、cron 表达式、NCrontab 解析的 `CrontabSchedule`、下次触发时间 |
| `AgentLoopJob` | `Core/Loops/AgentLoopJob.cs` | `[TickerFunction("AgentLoopExecutor", cronExpression: "* * * * *")]`：每分钟 poll 注册表，分发到期条目 |
| `LoopTerminationDetector` | `Core/Loops/LoopTerminationDetector.cs` | 双层终止检测：结构化 tool 信号（主）+ `FrozenSet<string>` 关键词匹配（兜底） |
| `LoopCommandParser` | `Core/Loops/LoopCommandParser.cs` | `[GeneratedRegex]` 编译期解析 `/loop <value><unit> <prompt>` 语法 |
| `LoopAction` / `LoopCommand` | `Core/Loops/LoopCommandParser.cs` | 命令模型枚举（Schedule/Cancel/Status/Invalid）和 POCO |
| `LoopControlTool` | `Agent/Tools/LoopControlTool.cs` | `IToolWithContext`：暴露给 LLM，模型可显式声明 `status="complete"` 终止 loop |
| `ChatCommandProcessor` | `Core/Pipeline/ChatCommandProcessor.cs` | 内建 `/loop` 命令路由 + `SetLoopCallback` 桥接到 Gateway |
| `CoreServicesExtensions` | `Gateway/Composition/` | DI 注册：`ClawLoopScheduler`、`AgentLoopJob`、`LoopTerminationDetector`、`LoopControlTool` |
| `RuntimeInitializationExtensions` | `Gateway/Composition/` | Loop callback 接线：解析命令 → 调用 `ClawLoopScheduler` → 返回响应文本 |

---

## 4. Loop 生命周期状态机

### 状态定义

| 状态 | 含义 | 触发条件 |
|-------|-------|---------|
| **Scheduled** | loop 已注册，等待定时触发 | `/loop <interval> <prompt>` |
| **Running** | 正在执行一个 loop turn | TickerQ 轮询 tick 发现到期条目 |
| **Overridden** | 被新的 `/loop` 命令替换 | 同一 session 再次 `/loop` |
| **Terminated** | 已自动或手动取消（终态） | 语义检测触发 或 `/loop cancel` |

### 转换图

```plaintext
                          /loop 5m <prompt>
                               │
                               ▼
                         ┌──────────┐
            ┌───────────│ Scheduled │◄──────────────┐
            │           └─────┬─────┘               │
            │    /loop cancel  │  轮询 tick           │ /loop 10m <new>
            │                 ▼                       │ (幂等覆盖)
            │           ┌─────────┐                  │
            │           │ Running  │─────────────────┘
            │           └────┬────┘
            │                │
            │    ┌───────────┼───────────┐
            │    │           │           │
            │    ▼           ▼           ▼
            │  模型调      关键词       正常
            │ loop_control  命中       完成
            │ (status=
            │  complete)
            │    │           │           │
            │    └───────────┴───────────┘
            │                │
            ▼                ▼
       ┌──────────────────────────┐
       │       Terminated          │
       │  (ConcurrentDictionary     │
       │   条目移除)                 │
       └──────────────────────────┘
```

### 关键规则

- **幂等覆盖：** 同一 sessionId 只能有一个活跃 loop。再次 `/loop` 时，`ConcurrentDictionary.AddOrUpdate` 原子替换旧条目
- **非抢占执行：** timer 触发时，prompt 通过 `MessagePipeline.InboundWriter` 写入会话管道。如果 session 正忙（持有会话锁），消息自然在 Channel 中排队，等待当前 turn 完成后消费
- **静默自毁：** 语义终止不主动通知用户（用户可能离线），仅记 Information 级别日志。手动 `/loop cancel` 时回显确认

---

## 5. TickerQ 调度引擎

### 架构选择

TickerQ 10.4.0 的公开 API 不提供 `ICronTickerManager` 动态注册接口。因此采用 **编译期 `[TickerFunction]` + 内存注册表** 的混合方案：

```plaintext
编译期                                运行时
┌──────────────────────────┐      ┌──────────────────────────┐
│ [TickerFunction(          │      │ ClawLoopScheduler         │
│   "AgentLoopExecutor",    │      │   ConcurrentDictionary    │
│   cronExpression:         │────────▶  sessionId → LoopEntry   │
│   "* * * * *")]           │ 每分钟 │   (cron + nextTrigger)   │
│ AgentLoopJob.ExecuteAsync │ tick   │                          │
└──────────────────────────┘      │ AgentLoopJob 每分钟 poll:  │
                                   │   var due = scheduler      │
                                   │     .GetDueEntries(now);   │
                                   │   foreach → DispatchAsync  │
                                   └──────────────────────────┘
```

### Cron 表达式生成

`ClawLoopScheduler.IntervalToCron()` 将用户友好格式转换为 6 字段 cron：

| 用户输入 | Cron 表达式 | 语义 |
|---------|-------------|------|
| `5m` | `*/5 * * * *` | 每 5 分钟 |
| `30s` | `*/30 * * * * *` | 30 秒 cron 表达式；实际 dispatch 由分钟级轮询观察 |
| `120s` | `*/2 * * * *` | 每 120 秒 = 每 2 分钟 |
| `1h` | `0 */1 * * *` | 每 1 小时（整点） |

### 重试与失败处理

TickerQ 作业级重试未在此层实现——`AgentLoopJob` 内部 dispatch 异常被 catch 并记录 Error 日志，不抛给 TickerQ。单次 dispatch 失败不影响下次 tick。设计理念：loop 是心跳机制，错过一次不应影响后续。

---

## 6. 双层语义终止检测

### 路径 1：结构化 Tool（主路径）

LLM 在响应中调用 `loop_control` tool 显式声明完成：

```json
{
  "name": "loop_control",
  "arguments": { "status": "complete" }
}
```

`LoopControlTool.ExecuteAsync` → `LoopTerminationDetector.OnToolCompleteAsync` → `ILoopControlService.SignalCompleteAsync` → `ClawLoopScheduler.CancelLoopAsync` → 从 `ConcurrentDictionary` 移除条目。

**外部验证：** tool 仅接受 `status="complete"`。任何其他值（`paused`、`done` 等）返回错误，不触发终止。

### 路径 2：关键词匹配（兜底）

`LoopTerminationDetector` 在模型响应文本中扫描预定义关键词（大小写不敏感）：

```csharp
private static readonly FrozenSet<string> TerminationKeywords = new[]
{
    "LOOP_TERMINATE",
    "DONE",
    "WORK_COMPLETE",
}.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
```

命中完整关键词即触发与路径 1 相同的终止流程；较长单词中的子串不匹配。未命中则不做任何操作，loop 正常等待下一次 tick。

### 防御设计

| 场景 | 行为 |
|-------|------|
| tool 调 `loop_control` 但被审批拒绝 | 视为未完成，loop 继续运行 |
| 响应文本含完整关键词但任务实际未完成 | 误杀风险（保守策略）。关键词集刻意精简，并按词边界匹配 |
| 两条路径同时触发 | `ConcurrentDictionary.TryRemove` 保证幂等，第二次调用无操作 |
| 响应既无 tool 也无关键词 | 不触发终止，正常等待下次 cron |

---

## 7. CLI 命令与聊天频道

### 命令参考

`/loop` 是 `ChatCommandProcessor` 中的**内建**命令，与 `/status`、`/goal`、`/help` 等并列。

| 命令 | 功能 |
|---------|------|
| `/loop <value><unit> <prompt>` | 启动或覆盖定时循环。unit: `s`（秒）、`m`（分钟）、`h`（小时） |
| `/loop cancel` | 手动取消当前 session 的 loop |
| `/loop stop` | `/loop cancel` 的别名 |
| `/loop status` | 查询当前 session 的 loop 状态 |

### 示例

```plaintext
用户: /loop 5m 检查 latest CI build 状态

系统: Loop started — interval: 5m, prompt: "检查 latest CI build 状态"

--- 5 分钟后 ---
[系统自动注入 prompt，Agent 执行工具调用，返回结果]

用户: /loop status

系统: Loop active — cron: */5 * * * *, prompt: "检查 latest CI build 状态", scheduled at: 2026-06-20T14:35:00.0000000Z

用户: /loop cancel

系统: Loop canceled.
```

### 命令解析

`LoopCommandParser.TryParse()` 使用 `[GeneratedRegex]` 编译期正则：

```csharp
[GeneratedRegex(@"^/loop\s+(?<value>\d+)\s*(?<unit>s|m|h)\s+(?<prompt>.+)$", RegexOptions.IgnoreCase)]
private static partial Regex LoopCommandRegex();
```

解析优先级：
1. 精确匹配 `/loop cancel` 或 `/loop stop` → `LoopAction.Cancel`
2. 精确匹配 `/loop status` → `LoopAction.Status`
3. 正则匹配 `<value><unit> <prompt>` → `LoopAction.Schedule`
4. 以上都不匹配 → `LoopAction.Invalid`
5. 不以 `/loop` 开头 → 返回 `null`（非 loop 命令，正常流转到 LLM）

### 多频道支持

Loop callback 在 `RuntimeInitializationExtensions` 中通过 `SetLoopCallback` 注册。`ChatCommandProcessor` 对来自任何渠道（CLI、Telegram、WhatsApp、Discord 等）的 `/loop` 消息统一调用此回调。无需渠道特定的适配代码。

---

## 8. Gateway DI 与接线

### 服务注册

在 `CoreServicesExtensions.AddOpenClawCoreServices()` 中：

```csharp
// Loop scheduling
services.AddSingleton<ClawLoopScheduler>();
services.AddSingleton<ILoopControlService>(sp => sp.GetRequiredService<ClawLoopScheduler>());
services.AddSingleton<LoopTerminationDetector>();
services.AddSingleton<AgentLoopJob>();

// Loop control tool (registered alongside other ITool implementations)
services.AddSingleton<ITool, LoopControlTool>();
```

### Callback 接线

在 `RuntimeInitializationExtensions.InitializeOpenClawRuntimeAsync()` 中，紧随 `SetCompactCallback` 之后：

```csharp
services.CommandProcessor.SetLoopCallback(async (session, text, ct) =>
{
    var scheduler = app.Services.GetRequiredService<ClawLoopScheduler>();
    var cmd = LoopCommandParser.TryParse(text);

    return cmd.Action switch
    {
        LoopAction.Cancel   => await CancelLoopAsync(scheduler, session.Id, ct),
        LoopAction.Status   => await GetLoopStatusAsync(scheduler, session.Id, ct),
        LoopAction.Schedule => await ScheduleLoopAsync(scheduler, session.Id, cmd.Interval!, cmd.Prompt!, ct),
        _                   => "Usage: /loop <interval> <prompt> ..."
    };
});
```

### AgentLoopJob 发现

TickerQ 通过 DI 容器自动发现标记了 `[TickerFunction]` 的类。`AgentLoopJob` 注册为 singleton 后，TickerQ 在启动时扫描并启动其 `ExecuteAsync` 方法上的 cron 定时器。

---

## 9. 错误处理与并发安全

### 边界场景

| 场景 | 行为 |
|-------|------|
| Session 被删除后 loop 触发 | `AgentLoopJob` dispatch 时 session 不存在 → 记 Warning 日志，静默跳过该条目（不移除——下次 tick 再检查） |
| 用户快速连续发多轮 `/loop` | 后覆盖前，最后一次生效。回显信息提示 interval 已更改 |
| `/loop` 在 `/goal` 激活时使用 | 允许，两者独立。goal 的自动续跑和 loop 的定时注入各自运作 |
| `loop_control` tool 被审批拒绝 | tool 返回错误信息，loop 不终止 |
| interval 为 0 或非法单位 | `IntervalToCron()` 抛出 `ArgumentException`，callback 捕获并回显 `Error: ...` |

### 并发安全

| 层级 | 机制 |
|------|------|
| `ClawLoopScheduler._entries` | `ConcurrentDictionary<string, LoopEntry>` — 线程安全的 AddOrUpdate/TryRemove |
| `LoopEntry.IsDue()` | 单条目内 `_nextOccurrence` 比较和更新由单条目锁保护 |
| Agent 层 | `AgentRuntime` 内部已有会话锁，同一 session 同一时刻只有一个 turn 执行 |
| TickerQ | `[TickerFunction]` 触发分钟级轮询作业；每个 loop 的状态仍保存在内存注册表中 |

---

## 10. OTel 上下文斩断

为防止 loop 多次迭代间形成嵌套 OpenTelemetry span 链（Codex 曾因此导致 34GB/天的日志膨胀），`AgentLoopJob.ExecuteAsync` 每次执行前显式斩断上下文：

```csharp
[TickerFunction("AgentLoopExecutor", cronExpression: "* * * * *")]
public async Task ExecuteAsync(TickerFunctionContext context, CancellationToken ct)
{
    // Cut OTel context to prevent nested span chains across loop iterations
    Activity.Current = null;

    var now = DateTimeOffset.UtcNow;
    var dueEntries = _scheduler.GetDueEntries(now);
    foreach (var entry in dueEntries)
    {
        await _dispatcher.DispatchAsync(entry.SessionId, entry.Prompt, ct);
    }
}
```

这确保每次 loop tick 的 span 深度始终为常数，无论 loop 已运行多少轮。

---

## 11. 设计与 /goal 的关系

`/loop` 和 `/goal` 是两条**独立但互补**的调度路径：

| 维度 | /loop | /goal |
|------|-------|-------|
| 触发方式 | 外部定时器注入 prompt | 模型停止时自动续跑 |
| 调度引擎 | TickerQ 分钟级轮询 + 内存注册表 | AgentRuntime 循环内联 |
| 生命周期 | Scheduled → Running → Terminated | Active → Paused/Blocked/BudgetLimited → Complete |
| 模型工具 | `loop_control`（仅 complete） | `get_goal`、`create_goal`、`update_goal` |
| 预算系统 | 无 | Token 预算 + 基线机制 |
| 持久化 | 内存 loop 注册表（持久化待增强） | InMemory + JSONL 历史 |

**共存规则：**
- 同一 session 可同时拥有 `/loop` 和 `/goal`
- `/loop cancel` 不影响 goal，`/goal clear` 不影响 loop
- 只有一个共享点：`ChatCommandProcessor` 同时路由两套命令

---

## 12. NativeAOT 兼容性

所有新增代码在设计上保持 AOT 兼容：

| 组件 | AOT 安全检查 |
|------|-------------|
| `LoopCommandParser` | 使用 `[GeneratedRegex]` 编译期正则，无运行时 `new Regex()` |
| `AgentLoopJob` | `[TickerFunction]` 由 TickerQ Source Generator 编译期处理 |
| `AgentLoopRequestPayload` | 使用 `System.Text.Json` + `[JsonPropertyName]` 源生成 |
| `LoopTerminationDetector` | `FrozenSet<string>` 是 AOT 安全的编译期集合 |
| `LoopControlTool` | 实现 `IToolWithContext` 接口（非反射注册，DI 显式 `AddSingleton`） |

无 `dynamic`、无 `MakeGenericType`、无未标注的 `[RequiresUnreferencedCode]`。

---

## 13. 测试策略

### 单元测试覆盖

| 被测组件 | 测试文件 | 验证点 |
|----------|---------|--------|
| `LoopCommandParser` | `LoopCommandParserTests.cs` | 合法 schedule 解析（含中文 prompt）；cancel/stop/status 精确匹配；非法输入返回 Invalid；非 loop 命令返回 null |
| `ClawLoopScheduler` | `ClawLoopSchedulerTests.cs` | Schedule → 条目存在；秒级 cron 可解析；非法 cron 统一为 `ArgumentException`；Cancel → 条目移除；SignalComplete → 通过显式接口取消；幂等覆盖；到期轮询；并发 `IsDue` 访问 |
| `LoopTerminationDetector` | `LoopControlToolTests.cs` | 完整关键词命中触发 SignalComplete；子串不触发；空/null 文本返回 false；OnToolComplete 触发 SignalComplete |
| `LoopControlTool` | `LoopControlToolTests.cs` | `status="complete"` 正确转发；非法 status 返回错误；无 session 上下文返回错误；工具元数据验证 |

### 测试统计数据

- **测试文件：** 3 个
- **测试方法：** 62 个（含继承自 Goal 相关命名匹配的测试）
- **通过率：** 100%
- **Mock 框架：** NSubstitute

---

## 14. 后续扩展

### 审批挂起协议（Phase 2）

当 loop turn 中触发审批阻塞（`ToolApprovalCallback` 返回 false 或等待人工决策）时：
- 利用 TickerQ 作业暂停/恢复能力
- 审批通过后自动唤醒 loop
- 解决 Codex 级"Token 焚烧"问题（无人值守时反复重试审批请求）

### REST API 管理端点

暴露 HTTP endpoint 供外部系统编程式管理 loop：
- `POST /api/loops` — 创建 loop
- `DELETE /api/loops/{sessionId}` — 取消 loop
- `GET /api/loops/{sessionId}` — 查询 loop 状态
- `GET /api/loops` — 列出所有活跃 loop

### 持久化增强

当前 loop 条目存储在内存 `ConcurrentDictionary` 中，重启后丢失。TickerQ 本身支持 EF Core 持久化——将 `LoopEntry` 序列化存入 TickerQ 数据库表，实现真正的跨重启持久化。

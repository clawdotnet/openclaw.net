# OpenClaw /loop Command Design

Date: 2026-06-20

## Summary

实现 OpenClaw 的 `/loop` 定时循环命令，对标 OpenAI Codex 的 `/loop` 原语。基于现有 TickerQ 10.3.0 调度引擎，在 `OpenClaw.Core/Loops/` 建立独立的调度子系统，支持 CLI 和聊天频道双渠道触发，提供幂等覆盖、语义自毁、跨重启持久化的完整生命周期管理。

`/goal`（目标驱动循环）已有 `AgentRuntimeGoalIntegration` + `GoalPromptTemplates` 地基，本期不做新建设，但 `/loop` 的 `ILoopControlService` 接口和 TickerQ 暂停/恢复管道为后续 `/goal` 增强预留扩展点。

## Goals

### P0: /loop 命令核心链路

用户通过 CLI 或聊天频道输入 `/loop <interval> <prompt>`，系统解析后在 TickerQ 中注册一个 cron 作业，按 interval 定时将 prompt 注入对应 session 并推进 agent turn。

核心链路必须：

- 使用 TickerQ 持久化 cron 作业，支持跨网关重启自动恢复
- 同一 session 下幂等覆盖：新 `/loop` 替换旧作业，不产生重复定时器
- 非抢占执行：timer 触发时若 session 正忙，prompt 进入消息队列等 idle 后派发
- 100% NativeAOT 兼容：Regex Source Gen、System.Text.Json 源生成、零反射

### P0: 语义自毁终止

loop 任务应能自动识别"工作已完成"并自我注销，防止无限空转消耗 Token。

双层检测策略：

1. **结构化 tool（主路径）**：模型调用 `loop_control(status="complete")` → `LoopControlTool` 转发到 `ILoopControlService` → `ClawLoopScheduler` 注销 TickerQ 作业
2. **关键词匹配（兜底）**：`LoopTerminationDetector` 扫描响应流文本，匹配预定义关键词（`LOOP_TERMINATE`、`任务完成`、`DONE` 等）时触发注销

两者任一触发即终止，静默执行（不通知用户，仅记日志）。

### P1: 手动生命周期命令

- `/loop cancel` — 手动取消当前 session 的 loop 任务，回显确认
- `/loop status` — 查询当前 session 的 loop 状态（interval、prompt、next trigger）

### Phase 2: 审批挂起协议（本期不做）

当 agent turn 中抛出 `ApprovalRequiredException` 时，TickerQ 作业自动 Pause；审批通过后 Resume。利用 `ITickerExceptionHandler` + TickerQ 作业状态属性实现。

### Phase 2: REST API（本期不做）

暴露 HTTP endpoint 供外部系统编程式管理 loop 任务，包一层 Controller 转发到 `ClawLoopScheduler`。

## Non-Goals

- 不实现 `/goal` 增强（审批挂起、token budget 硬限制、CAS 状态锁）
- 不改变 `AgentRuntime.RunAsync` 的核心逻辑
- 不引入新的持久化存储（复用 TickerQ 的 EF Core 存储）
- 不实现 loop 任务的跨 session 共享或模板化

## Architecture

### 新增文件

| 文件 | 位置 | 职责 |
|---|---|---|
| `AgentLoopRequestPayload.cs` | `OpenClaw.Core/Loops/` | 强类型 payload record，AOT 安全的 JSON 序列化契约 |
| `ClawLoopScheduler.cs` | `OpenClaw.Core/Loops/` | 调度门面：Register/Override/Cancel/GetStatus |
| `AgentLoopJob.cs` | `OpenClaw.Core/Loops/` | `[TickerFunction("AgentLoopExecutor")]` 标注的作业执行体 |
| `IAgentLoopDispatcher.cs` | `OpenClaw.Core/Loops/` | 接口：将 loop prompt 注入 session 并推进 turn |
| `ILoopControlService.cs` | `OpenClaw.Core/Loops/` | 接口：接收 tool 或 detector 的终止信号 |
| `LoopTerminationDetector.cs` | `OpenClaw.Core/Loops/` | 双层检测：tool 信号（主）+ 关键词（兜底） |
| `LoopCommandParser.cs` | `OpenClaw.Cli/Commands/` | `[GeneratedRegex]` 编译期解析 `/loop` 语法 |
| `LoopControlTool.cs` | `OpenClaw.Agent/Tools/` | 实现 `ITool`，暴露给模型以显式声明完成 |

### 组件关系

```
CLI / Channels (WhatsApp, Telegram, Discord...)
     │ 用户输入 /loop 5m 检查构建状态
     │ MessagePipeline 拦截 /loop 前缀
     ▼
┌─ OpenClaw.Core/Loops/ ─────────────────────────────────┐
│                                                          │
│  ClawLoopScheduler ──▶ ICronTickerManager (TickerQ 已有) │
│   · ScheduleLoopAsync         │ 定时触发                 │
│   · CancelLoopAsync           ▼                          │
│   · GetLoopStatus       AgentLoopJob                     │
│                    [TickerFunction("AgentLoopExecutor")]  │
│                         │   │                             │
│  ILoopControlService ◀──┘   │  IAgentLoopDispatcher      │
│   · SignalComplete           │  → AgentRuntime.RunAsync   │
│                         │                                │
│  LoopTerminationDetector                                 │
│   · 扫描响应流文本                                        │
│   · 监听 tool 信号                                       │
└─────────────────────────────────────────────────────────┘
     │ 派发 turn
     ▼
┌─ OpenClaw.Agent/Tools/ ────┐
│  LoopControlTool : ITool   │
│  → ILoopControlService     │
└────────────────────────────┘
```

### 分层原则

- **Core/Loops/** 纯调度层：不依赖 Agent、Gateway，仅依赖 TickerQ + DI 抽象
- **Gateway** 负责接线：实现 `IAgentLoopDispatcher`，注入到 `ClawLoopScheduler` 和 `AgentLoopJob`
- **Agent** 只加一个 Tool：`LoopControlTool` 是纯粹的 `ITool` 实现，不侵入 `AgentRuntime` 主循环
- **Goal 路径完全不受影响**：`/loop` 和 `/goal` 是不同的调度入口，共享 `AgentRuntime` 但生命周期管理独立

## Data Flow

### 阶段 1：注册

1. 用户在 CLI 或频道输入 `/loop 5m 检查构建状态`
2. `MessagePipeline` 检测 `/loop` 前缀，路由到 `LoopCommandParser`
3. `LoopCommandParser` 用 `[GeneratedRegex]` 解析出 interval（5m）和 prompt
4. 转换为 6 位 cron 表达式（秒级精度），构造 `AgentLoopRequestPayload`
5. `ClawLoopScheduler.ScheduleLoopAsync` 通过 `ICronTickerManager` 注册 `CronTicker`
   - `Function = "AgentLoopExecutor"`
   - `Expression = cronExpression`
   - `Request = TickerHelper.CreateTickerRequest(payload)`
   - `Retries = 2`, `RetryIntervals = [15, 45]`（指数退避）
6. 回显 `"✓ Loop 已启动，每 5 分钟触发"`（如为覆盖则提示替换信息）

### 阶段 2：定时触发

1. TickerQ 按 cron 表达式触发 `AgentLoopJob.ExecuteAgentLoopAsync`
2. 执行前 `Activity.Current = null` 斩断 OTel 上下文（防 span 嵌套）
3. 调用 `IAgentLoopDispatcher.DispatchAsync(sessionId, prompt)`
4. 内部获取 session、构造 user message、推进 `AgentRuntime.RunAsync`
5. 如果 session 正忙 → prompt 进入消息队列等待 idle
6. 如果 session 不存在 → 调用 `CancelLoopAsync` 自清理

### 阶段 3：语义终止

**主路径（tool）**：
1. 模型在响应中调用 `loop_control(status="complete")`
2. `LoopControlTool.ExecuteAsync` → `ILoopControlService.SignalComplete(sessionId)`
3. `LoopTerminationDetector` 收到信号 → `ClawLoopScheduler.CancelLoopAsync`
4. TickerQ 移除对应 `CronTicker`

**兜底路径（关键词）**：
1. `LoopTerminationDetector` 扫描响应流文本
2. 命中关键词 → 同主路径步骤 3-4
3. 未命中 → 不做任何操作，loop 正常等待下次触发

### 阶段 4：手动干预

- `/loop cancel` → `ClawLoopScheduler.CancelLoopAsync` → 回显确认
- `/loop status` → `ClawLoopScheduler.GetLoopStatusAsync` → 回显 interval/prompt/next trigger

## Key Behaviors

### 幂等覆盖

同一 sessionId 只能有一个活跃 loop。jobId 固定为 `LoopJob_{sessionId}`。再次 `/loop` 时 `ICronTickerManager` 的 update 操作覆盖同 ID 作业。回显 `"已更新: /loop 10m ... (替换了之前的 5m 设置)"`。

### 非抢占执行

TickerQ 触发时不中断正在执行的 turn。prompt 通过 session 的消息队列排队，等当前 turn 完成后自动消费。

### 静默自毁

语义自毁不主动通知用户（用户可能离线）。仅记 Information 级别日志。手动取消时回显确认。

### 重启恢复

TickerQ 在应用启动时自动从数据库加载所有 cron 作业（包括 loop 作业），无需手动重注册。`AgentLoopJob` 在首次触发时验证 session 仍存在。

## Error Handling

### TickerQ 作业失败

- `Retries=2` + `RetryIntervals=[15s, 45s]`（指数退避）
- 3 次均失败 → 作业进入 dead-letter 状态，记 ERROR 日志

### 并发安全

| 层级 | 机制 |
|---|---|
| TickerQ | EF Core 行锁（`SELECT ... FOR UPDATE`），同一 cron 作业只由一个实例领取 |
| Agent | `AgentRuntime` 内部并发保护，单 session 同一时刻只有一个 turn |
| ClawLoopScheduler | 覆盖写入幂等，即使多实例同时注册也只需一条生效 |

### 边界场景

| 场景 | 行为 |
|---|---|
| `/loop` 在 `/goal` 激活时调用 | 允许，两者独立 |
| Session 已被删除 | `AgentLoopJob` dispatch 时发现不存在 → `CancelLoopAsync` 自清理 |
| 用户快速连续发多轮 `/loop` | 后覆盖前，最后一次生效 |
| `loop_control` tool 被审批拒绝 | 视为未完成，loop 继续运行 |
| 响应无 tool 调用也无关键词 | 不触发终止，正常等待下次 cron |
| interval 为 0 或非法 | `LoopCommandParser` 失败 → 回显帮助信息 |

### OTel 上下文斩断

每次 `AgentLoopJob` 触发前执行 `Activity.Current = null`，确保多次 loop 触发之间不形成嵌套 span 链，防止 Codex 同款日志膨胀问题。

## Testing

### 单元测试

| 被测组件 | 测试内容 | Mock 依赖 |
|---|---|---|
| `LoopCommandParser` | 合法/非法语法；边界值（0m、999h）；中文 prompt | 无 |
| `ClawLoopScheduler` | 注册→cron 正确；覆盖替换旧作业；取消→Remove 调用；查询状态 | `ICronTickerManager` |
| `LoopTerminationDetector` | tool 信号触发取消；关键词命中触发；不命中不触发；双命中只取消一次 | `ClawLoopScheduler` |
| `AgentLoopJob` | dispatch 成功/失败行为；session 不存在自清理 | `IAgentLoopDispatcher` |
| `LoopControlTool` | `status=complete` 正确转发到 `ILoopControlService` | `ILoopControlService` |

### 集成测试

- 完整生命周期：解析→注册→触发→dispatch→tool→detector→注销
- 幂等覆盖：注册→再次注册→验证旧作业替换→取消
- 多 loop 隔离：sessionA 和 sessionB 的 loop 互不干扰
- 重启恢复：注册 loop→模拟重启→验证作业仍在 TickerQ 中

### 不需要的测试

- TickerQ 本身的 cron 触发、重试、行锁（第三方库自带覆盖）
- `AgentRuntime.RunAsync` turn 执行流程（已有现有测试）
- 聊天频道消息路由（已有现有测试）

## NativeAOT Compatibility

所有新增代码必须通过 `dotnet publish /p:PublishAot=true` 验证。检查要点：

- `LoopCommandParser` 使用 `[GeneratedRegex]` 而非 `new Regex()`
- `[TickerFunction]` 由 Source Generator 编译期处理
- `AgentLoopRequestPayload` 使用 `System.Text.Json` 源生成上下文
- 无 `dynamic`、`MakeGenericType`、未标注的 `[RequiresUnreferencedCode]`
- `ILoopControlService` 和 `IAgentLoopDispatcher` 接口通过 DI 注册（非反射扫描）

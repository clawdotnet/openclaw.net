# OpenClaw.NET 长时间持久会话（Long-Lived Persistent Sessions）

> **中文 | [English](../LONG_LIVED_PERSISTENT_SESSIONS.md)**
>
> 版本：2026-07-02 · 基于 OpenClaw.NET 源码分析

---

## 目录

1. [概述](#1-概述)
2. [会话核心模型](#2-会话核心模型)
3. [双层持久化架构](#3-双层持久化架构)
4. [会话生命周期](#4-会话生命周期)
5. [执行检查点与中断恢复](#5-执行检查点与中断恢复)
6. [后台持续执行](#6-后台持续执行)
7. [启动恢复——宕机自愈](#7-启动恢复宕机自愈)
8. [跨会话通信与子会话](#8-跨会话通信与子会话)
9. [历史压缩](#9-历史压缩)
10. [持久化 Token 审计](#10-持久化-token-审计)
11. [可选增强：Fractal Memory / MemPalace](#11-可选增强fractal-memory--mempalace)
12. [整体架构图](#12-整体架构图)
13. [关键源码索引](#13-关键源码索引)

---

## 1. 概述

OpenClaw.NET 的会话系统不是简单的"聊天历史记录存储"，而是一套**完整的长时间运行 AI Agent 生命周期管理机制**。它支持：

- 跨进程重启的多轮对话持久化
- AI Agent 后台任务持续执行（多批次自动续跑）
- 工具调用中途的检查点保存与精确恢复
- 网关宕机后的启动自愈
- 历史自动压缩、Token 审计追踪
- 会话间同步/异步通信与子 Agent 派生

核心设计哲学：**会话是状态，不是线程** — 它们只是带对话列表和配置覆盖的键值行，`SessionManager` 不拥有任何执行上下文。

---

## 2. 会话核心模型

会话由 `Session` 类（`src/OpenClaw.Core/Models/Session.cs`）定义：

### 2.1 身份与路由

| 字段 | 类型 | 说明 |
|------|------|------|
| `Id` | `string` | 会话唯一标识，默认格式 `channelId:senderId`，即"某用户在某通道上"唯一映射 |
| `ChannelId` | `string` | 通道标识（如 `webchat`、`whatsapp`、`teams`） |
| `SenderId` | `string` | 发送者标识 |
| `StableSessionBinding` | `StableSessionBindingInfo?` | 外部稳定绑定（跨平台会话关联） |

### 2.2 对话历史

```csharp
List<ChatTurn> History   // { Role, Content, Timestamp, ToolCalls? }
```

每轮 `ChatTurn` 包含角色（`user`/`assistant`/`system`）、正文、时间戳，以及可选的工具调用列表 `ToolInvocation`（工具名、参数、结果、耗时、失败码等）。

### 2.3 生命周期状态

双层状态机：

```
SessionState (会话级):     Active ──► Paused ──► Expired

SessionRunState (运行级):  Idle ──► Running ──► Continuing
                                │         │
                                ▼         ▼
                           Paused    Blocked
                           BudgetLimited
                           Completed / Failed
```

### 2.4 时间戳

| 字段 | 说明 |
|------|------|
| `CreatedAt` | 会话创建时间 |
| `LastActiveAt` | 最后活跃时间 —— 驱动过期清扫和容量淘汰的关键字段 |

### 2.5 Token 计量（线程安全）

全部使用 `Interlocked` 原子操作更新，无锁线程安全：

```csharp
long TotalInputTokens      // Interlocked.Read / Interlocked.Add
long TotalOutputTokens
long TotalCacheReadTokens
long TotalCacheWriteTokens
```

### 2.6 每会话覆盖配置

一个会话可以独立于网关全局配置，拥有自己的：

- `ModelOverride` — 模型覆盖（`/model` 命令设置）
- `ModelProfileId` — 命名模型配置文件
- `ReasoningEffort` — 推理强度（`/think` 命令设置）
- `RoutePresetId` — 工具预设
- `RouteAllowedTools` — 工具白名单
- `SystemPromptOverride` — 系统提示覆盖
- `ContractPolicy` — 合约执行限制策略
- `Delegation` / `DelegatedSessions` — 委托/子会话元数据

---

## 3. 双层持久化架构

```
┌──────────────────────────────────────────────────┐
│                 SessionManager                    │
│                                                  │
│  ┌────────────────┐        ┌──────────────────┐  │
│  │   _active       │        │   IMemoryStore    │  │
│  │  (Concurrent    │ ◄──►   │  (SQLite 默认)    │  │
│  │   Dictionary)   │        │                   │  │
│  │                 │        │  支持:             │  │
│  │  内存热缓存      │        │  - SQLite (默认)   │  │
│  │  快速读写        │        │  - MemPalace (JIT) │  │
│  └────────────────┘        └──────────────────┘  │
│                                                  │
│  容量控制:                                         │
│  - _maxSessions: 内存活跃上限                      │
│  - _timeout: 空闲超时（SessionTimeoutMinutes）     │
│  - _admissionGate: 单许可信号量，串行化准入         │
└──────────────────────────────────────────────────┘
```

### 3.1 热路径（快速准入）

```
新消息 → _active.TryGetValue(key)
  ├─ 命中 → 更新 LastActiveAt → 直接返回（无锁）
  └─ 未命中 → 获取 _admissionGate → 进入慢路径
```

### 3.2 慢路径（持久化回水合）

```
获取准入信号量 _admissionGate
  ├─ 再次检查 _active（双检锁模式）
  ├─ _store.GetSessionAsync(key)  ← 从 SQLite 回水合
  ├─ EnsureCapacityForAdmission()
  │   ├─ SweepExpiredActiveSessions()  先清扫过期会话
  │   └─ 仍超限 → 淘汰最旧 LastActiveAt 的会话
  └─ _active.TryAdd(key, session) → 返回
```

### 3.3 持久化写入

```csharp
PersistAsync(session, ct):
  获取每会话 SemaphoreSlim → 串行化写入
  _store.SaveSessionAsync → 3 次重试，指数退避
```

### 3.4 淘汰 ≠ 删除

从 `_active` 淘汰的会话**保留在 IMemoryStore 中**。下次消息到达时自动从持久化存储回水合——这是"长时间持久"的核心保障。

---

## 4. 会话生命周期

```
  ┌──────────┐
  │ 消息到达  │
  └────┬─────┘
       ▼
  ┌──────────────┐
  │ 1. 准入      │  GetOrCreateByIdAsync → 热路径/回水合/新建
  └──────┬───────┘
         ▼
  ┌──────────────┐
  │ 2. 活跃工作   │  每请求更新 LastActiveAt
  │              │  AgentRuntime 追加 ChatTurn 到 History
  │              │  原子更新 Token/缓存计数器
  └──────┬───────┘
         ▼
  ┌──────────────┐
  │ 3. 检查点     │  每批工具调用完成后写入 ExecutionCheckpoint
  └──────┬───────┘
         ▼
  ┌──────────────┐
  │ 4. 持久化     │  PersistAsync → IMemoryStore.SaveSessionAsync
  └──────┬───────┘
         ▼
  ┌──────────────┐
  │ 5. 过期/淘汰  │  超时 → SweepExpiredActiveSessions
  │              │  超容量 → RemoveActive（最旧优先）
  └──────────────┘
         │
  网关关闭时 ──► await 所有进行中的持久化任务 → 释放信号量
```

---

## 5. 执行检查点与中断恢复

这是长时间会话**最核心**的机制。运行时在每批工具调用完成后立即保存检查点，确保工具调用不会在恢复时重复执行。

### 5.1 检查点模型

```csharp
SessionExecutionCheckpoint {
    CheckpointId:      Guid 唯一标识
    Kind:              "tool_batch"         // 检查点类型
    State:             "ready_to_resume"    // | "completed" | "failed"
    Sequence:          递增序号
    Iteration:         当前迭代编号
    HistoryCount:      写入时的历史轮数
    CorrelationId:     关联追踪 ID
    CreatedAtUtc:      创建时间
    PersistedAtUtc:    持久化时间
    LastResumeAttemptAtUtc:  上次恢复尝试时间
    CompletedAtUtc:    完成时间
    CompletionReason:  完成原因
    ToolCalls: [
        { CallId, ToolName, ResultStatus, FailureCode, DurationMs,
          ArgumentsBytes, ResultBytes }
    ]
}
```

### 5.2 写入时机

```
AgentRuntime 执行循环中:
  LLM 返回工具调用 → 执行工具 → 追加 assistant[tool_use] 到 History
  → PersistToolBatchCheckpointAsync()  ← 此时写入检查点
  → 继续下一轮迭代
```

检查点只记录**已完成的工具批次边界**——这是第一个可以安全恢复的持久化点，确保恢复时不重复调用工具。

### 5.3 恢复逻辑

```
新消息到达 RunTurnAsync():
  TryGetResumableCheckpoint(session)
    ├─ null → 正常处理：追加 user turn → 压缩/裁剪 → 构建消息
    └─ 有检查点且 State=ready_to_resume:
         ├─ 不追加新的 user turn
         ├─ BuildMessages(exactLatestToolBatch: true)
         │   精确重建最后一个工具批次的 Assistant+Tool 消息对
         ├─ 插入恢复系统指令：
         │   "The previous execution was interrupted after completing
         │    a tool batch. Resume from here. Do NOT re-execute the
         │    already-completed tools listed above."
         └─ 如果用户消息不是裸 resume/continue:
              追加用户附注为额外 user 消息
```

**裸恢复触发词**：`resume`、`continue`、`/resume`、`/continue`

### 5.4 恢复安全性

- 工具调用的参数和结果保存在 `History` 的 `ToolInvocation` 中，在持久化存储中完整保留
- 恢复时从检查点元数据精确重建 `FunctionCallContent` + `FunctionResultContent` 消息对
- 检查点的 `CallId` 用于精确匹配 `ChatMessage` 中的工具调用和结果

---

## 6. 后台持续执行

当单轮达到最大迭代上限时，会话**不会终止**，而是自动转入后台分批次续跑。

### 6.1 触发条件

```
RunTurnAsync 达到 _maxIterations (默认 20)
  → 返回 AgentTurnResult {
      ShouldContinue: true,
      StopReason: BatchLimitReached,
      ContinuePrompt: "Continue working toward the active goal."
    }
```

### 6.2 后台初始化与续跑

```csharp
// Gateway 首次检测到需要续跑时:
BackgroundRun = new BackgroundRunMetadata {
    RunId, Objective, StartedAtUtc, LastContinuedAtUtc,
    ContinuationCount, ContinuationSequence,
    ConsecutiveNoProgressCount,  // 检测卡死
    TokenBudget, MaxContinuationTurns,
    LastCheckpointId, LastStopReason
};

// 写入内部续跑消息:
pipeline.WriteAsync(new InboundMessage {
    Type = "background_auto_continue",
    IsSystem = true
});
```

### 6.3 关键特性

| 特性 | 说明 |
|------|------|
| **独立并发控制** | `MaxConcurrentBackgroundTurns`（默认 3），与用户请求并发隔离 |
| **WebSocket 断开不取消** | Channel 客户端退出不影响后台 Agent 继续执行 |
| **Goal 驱动续跑** | 如有活跃 Goal，续跑提示自动包含 Goal 特定指令 |
| **卡死检测** | `ConsecutiveNoProgressCount` 追踪连续无进展轮次 |
| **预算控制** | `TokenBudget` + `MaxContinuationTurns` 防止无限消耗 |

### 6.4 Goal 自动续跑

```
AgentRuntime 循环中:
  LLM 返回文本（无工具调用）
    → GoalIntegration.EvaluateGoalContinuation()
      ├─ Goal 未完成 → 插入 goal_check system 消息 → continue 循环
      └─ Goal 完成 → 正常返回
```

---

## 7. 启动恢复——宕机自愈

`BackgroundSessionRecoveryWorker` 在网关启动时执行：

```
Gateway 启动
  → BackgroundSessionRecoveryWorker.RecoverOnceAsync()
    ├─ 检查 BackgroundExecution.Enabled && AutoResumeOnStartup
    ├─ 查询 IMemoryStore:
    │   ListBackgroundRunnableSessionsAsync(limit)
    │   WHERE RunState IN (Running, Continuing) AND Goal IS ACTIVE
    └─ 逐个入队：
        pipeline.WriteAsync(new InboundMessage {
            Type = "background_auto_resume",
            Text = "Resume the active background goal from the latest checkpoint.",
            IsSystem = true
        });
        + 可选 AutoResumeStaggerSeconds 交错启动
```

### 相关配置

```yaml
OpenClaw:
  BackgroundExecution:
    Enabled: true
    AutoResumeOnStartup: true
    AutoResumeStaggerSeconds: 5      # 恢复交错间隔
    AutoResumeMaxConcurrent: 3       # 恢复并发上限
```

---

## 8. 跨会话通信与子会话

所有会话间通信通过统一的 `MessagePipeline.InboundWriter` 管道路由。这意味着子会话、后台续跑、启动恢复——全部使用与用户消息相同的代码路径。

### 8.1 工具矩阵

| 工具 | 模式 | 超时 | 说明 |
|------|------|------|------|
| `sessions_spawn` | 异步（fire-and-forget） | — | 创建子会话，立即返回 session ID |
| `sessions_yield` | 同步（rendezvous） | 5-300s（默认 60s） | 向目标发消息，轮询等待回复 |
| `sessions send` | 异步 | — | 向已有会话发消息，立即返回 |
| `sessions list` | 查询 | — | 列出所有活跃会话 |
| `sessions history` | 查询 | — | 读取任意会话最近 N 轮历史 |

### 8.2 `sessions_yield` 内部机制

```
1. 死锁保护: targetSessionId == currentSessionId → 直接拒绝
2. 快照: target.History.Count
3. 入队: pipeline.WriteAsync(message)
4. 轮询: TryGetActiveById → 检测新 assistant turn
   轮询间隔: 500ms → 1s → 2s（退避）
5. 淘汰降级: 目标会话被淘汰时 → LoadAsync 从存储回退一次
6. 返回: 目标回复文本 或 超时消息
```

### 8.3 注册

所有会话工具统一在 `ToolPresetResolver` 的 `group:sessions` 预设中管理，操作员可一键启用/禁用整组。

---

## 9. 历史压缩

长时间会话的历史可能超出 LLM 上下文窗口，系统提供基于 LLM 的自动压缩：

### 9.1 参数

| 参数 | 默认值 | 说明 |
|------|--------|------|
| `enableCompaction` | `false` | 是否启用 |
| `compactionThreshold` | `40` | 触发压缩的历史轮数阈值 |
| `compactionKeepRecent` | `10` | 保留原文的最近轮数 |

### 9.2 压缩流程

```
History.Count > compactionThreshold
  → 保留最近 keepRecent 轮原文
  → 将旧轮次格式化为摘要请求
  → LLM 生成 2-3 句摘要（MaxOutputTokens=256, Temperature=0.3）
  → 移除旧轮次，插入 [Previous conversation summary: ...]
  → 压缩失败 → 自动回退到简单裁剪
```

### 9.3 注意事项

- 压缩不在迭代循环内执行（避免级联 LLM 调用）
- 只在每轮开始时执行一次
- 已有摘要会在下次压缩时被重新摘要（合并）

---

## 10. 持久化 Token 审计

每轮 Token 用量以追加式 JSONL 格式写入持久化账单：

### 10.1 账单模型

```json
{
  "CorrelationId": "a1b2c3d4...",
  "SessionId": "webchat:user123",
  "ChannelId": "webchat",
  "ProviderId": "openai",
  "ModelId": "gpt-4o",
  "InputTokens": 1234,
  "OutputTokens": 567,
  "CacheReadTokens": 200,
  "CacheWriteTokens": 100,
  "EstimatedInputTokensByComponent": { "system": 500, "history": 600, ... },
  "IsEstimated": false,
  "TimestampUtc": "2026-07-02T12:00:00Z"
}
```

### 10.2 文件路径

```
默认: {Memory.StoragePath}/audit/turn-token-usage.jsonl
```

### 10.3 三向可追踪

同一 `CorrelationId` 贯穿三个维度：

1. **结构化日志** — `[{CorrelationId}]` 前缀
2. **上游 Provider HTTP 头** — `X-OpenClaw-Correlation-Id`（可配置）
3. **JSONL 审计文件** — `CorrelationId` 字段

外部系统可以通过 `X-Request-Id` / `X-Trace-Id` HTTP 头注入自己的 Trace ID，网关原样传播为 `CorrelationId`。

### 10.4 估计与精确

- `IsEstimated: false` → Provider 返回了实际 usage
- `IsEstimated: true` → Provider 未返回 usage，使用基于消息长度的启发式估算

---

## 11. 可选增强：Fractal Memory / MemPalace

### 11.1 Fractal Memory

| 维度 | 说明 |
|------|------|
| **定位** | 结构化项目记忆（文件树模式：index/state/timeline/decisions/children/artifacts） |
| **接入方式** | MCP 协议，stdio 启动 `fractalmem-mcp` 命令 |
| **与 Core 会话关系** | 互补 —— Fractal Memory 管理项目知识，Core 管理会话状态 |
| **自动注入** | `AutoContextMode=auto` 时每轮自动搜索并注入紧凑上下文 |
| **写操作** | V1 为读优先，写操作需 `AllowWrites=true` + 审批 |

### 11.2 MemPalace

| 维度 | 说明 |
|------|------|
| **定位** | JIT-only 知识图谱增强型记忆后端 |
| **限制** | 不支持 NativeAOT，必须 JIT 模式 + 动态原生插件 |
| **存储模型** | Wings → Rooms → Drawers 三层命名空间 |
| **工具** | `mempalace_kg`：三元组 add/query/timeline |

---

## 12. 整体架构图

```
                        ┌──────────────────────────┐
                        │      外部消息入口          │
                        │  WebSocket / REST /       │
                        │  WhatsApp / Teams / CLI   │
                        └──────────┬───────────────┘
                                   │
                                   ▼
                        ┌──────────────────────┐
                        │    MessagePipeline    │
                        │   InboundWriter       │
                        │   (按 SessionId 路由)  │
                        └──────────┬───────────┘
                                   │
              ┌────────────────────┼────────────────────┐
              │                    │                    │
              ▼                    ▼                    ▼
    ┌──────────────┐    ┌──────────────┐    ┌──────────────┐
    │ Session A    │    │ Session B    │    │ Session C    │
    │ (用户对话)   │    │ (子 Agent)   │    │ (后台续跑)   │
    └──────┬───────┘    └──────┬───────┘    └──────┬───────┘
           │                   │                   │
           └───────────────────┼───────────────────┘
                               │
                               ▼
                    ┌────────────────────┐
                    │   SessionManager   │
                    │                    │
                    │  ┌──────────────┐  │
                    │  │ _active 热缓存│  │
                    │  └──────┬───────┘  │
                    │         │          │
                    │  ┌──────▼───────┐  │
                    │  │ IMemoryStore  │  │
                    │  │  持久化存储    │  │
                    │  └──────────────┘  │
                    └────────────────────┘
                               │
          ┌────────────────────┼────────────────────┐
          │                    │                    │
          ▼                    ▼                    ▼
  ┌──────────────┐   ┌──────────────┐   ┌──────────────┐
  │ 检查点系统   │   │ Token 审计    │   │ 历史压缩     │
  │ Checkpoint   │   │ JSONL Ledger  │   │ Compaction   │
  └──────────────┘   └──────────────┘   └──────────────┘
          │
          ▼
  ┌──────────────┐
  │ 启动恢复     │
  │ Recovery     │
  │ Worker       │
  └──────────────┘
```

---

## 13. 关键源码索引

| 组件 | 路径 |
|------|------|
| Session 模型 | `src/OpenClaw.Core/Models/Session.cs` |
| Session 管理模型 | `src/OpenClaw.Core/Models/SessionAdminModels.cs` |
| SessionManager | `src/OpenClaw.Core/Sessions/SessionManager.cs` |
| AgentRuntime（检查点、恢复、压缩） | `src/OpenClaw.Agent/AgentRuntime.cs` |
| AgentCheckpointManager | `src/OpenClaw.Agent/Runtime/AgentCheckpointManager.cs` |
| SessionsSpawnTool | `src/OpenClaw.Gateway/Tools/SessionsSpawnTool.cs` |
| SessionsYieldTool | `src/OpenClaw.Gateway/Tools/SessionsYieldTool.cs` |
| SessionsTool（list/history/send） | `src/OpenClaw.Agent/Tools/SessionsTool.cs` |
| BackgroundSessionRecoveryWorker | `src/OpenClaw.Gateway/Background/BackgroundSessionRecoveryWorker.cs` |
| GatewayInboundMessageWorker（续跑逻辑） | `src/OpenClaw.Gateway/Extensions/GatewayInboundMessageWorker.cs` |
| TurnTokenUsageAuditLog | `src/OpenClaw.Core/Observability/TurnTokenUsageAuditLog.cs` |
| ToolPresetResolver（sessions 预设注册） | `src/OpenClaw.Gateway/ToolPresetResolver.cs` |
| IGoalService | `src/OpenClaw.Core/Abstractions/IGoalService.cs` |

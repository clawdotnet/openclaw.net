# OpenClaw.NET Goal 机制 — 技术架构文档

> 会话级持久化目标机制。当模型在完成目标前停止时，运行时自动续跑。完整实现了上游 OpenClaw 的 Goal 语义。

- **状态:** 已实现（分支: `goal`）
- **提交:** `21e177b`、`02e9990`、`4c26f48`
- **总变更:** +2,716 行，涉及 20 个文件
- **测试:** 59 通过，0 失败

---

## 目录

1. [问题与动机](#1-问题与动机)
2. [架构总览](#2-架构总览)
3. [组件清单](#3-组件清单)
4. [6 状态状态机](#4-6-状态状态机)
5. [Token 预算系统](#5-token-预算系统)
6. [模型工具](#6-模型工具)
7. [运行时集成](#7-运行时集成)
   - [原生 AgentRuntime](#71-原生-agentruntime)
   - [MAF 适配器 (MafAgentRuntime)](#72-maf-适配器-mafagentruntime)
8. [CLI 命令](#8-cli-命令)
9. [TUI 显示](#9-tui-显示)
10. [Prompt 设计](#10-prompt-设计)
11. [外部验证](#11-外部验证)
12. [通道隔离](#12-通道隔离)
13. [DI 注册](#13-di-注册)
14. [NativeAOT 兼容性](#14-nativeaot-兼容性)
15. [测试策略](#15-测试策略)
16. [设计决策（来自评审）](#16-设计决策来自评审)
17. [后续扩展](#17-后续扩展)

---

## 1. 问题与动机

### "懒惰"模型问题

在长程 Agent 任务（修 bug、写文档、重构代码）中，大语言模型普遍存在过早停止的行为：

- **部分完成：** 模型完成部分工作后停下，剩余工作未完成
- **虚假胜利：** 模型在未验证完整范围的情况下宣称完成
- **范围收缩：** 模型用更简单的方案替代，不能完全满足目标

用户通过反复输入"继续"来弥补——这是一个手动、易错的循环。Goal 机制将其自动化。

### Goal 是什么

Goal 是一个会话级持久化目标机制。当模型停止（`toolCalls.Count == 0`）时，运行时自动：

1. 检查是否存在 active goal
2. 评估目标是否已达成
3. 如果**未达成**且在限制内 → 注入"Goal Check" prompt 并继续迭代循环
4. 如果**已达成**或**阻塞**或**预算耗尽** → 正常返回

这将 Agent 执行从单次交互转变为异步任务提交模式：发起后无需关注。

### 生态系统对比

| 特性 | Claude Code | Codex | OpenClaw | OpenClaw.NET |
|---------|-------------|-------|-------------------|--------------|
| 触发方式 | Hook on Stop | Hook on Stop | Hook on Stop | **运行时循环内联** |
| 状态数 | 3 | 5 | 6 | **6** |
| Token 预算 | ✅ | ✅ | ✅ | ✅（默认 128K） |
| 模型工具 | — | `update_goal` | 3 个工具 | **3 个工具** |
| 阻塞检测 | 隐式 | 3 次重复 | 3 次重复 | **3 次重复（文本哈希）** |
| 宿主语言 | TypeScript | TypeScript | Go/TS | **C# / .NET** |

---

## 2. 架构总览

```
┌─────────────────────────────────────────────────────────────────────┐
│                          用户 / 操作员                               │
│       /goal start "修复 CI"  +500k                                  │
└─────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────┐
│                   ChatCommandProcessor (/goal)                       │
│  start │ pause │ resume │ complete │ block │ clear │ status          │
│  内建命令，在会话管道中处理                                             │
└─────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────┐
│                     IGoalService (InMemoryGoalService)                │
│  ┌──────────┐  ┌──────────┐  ┌───────────┐  ┌───────────────────┐  │
│  │创建 Goal  │  │更新状态  │  │Token 追踪 │  │阻塞条件检测       │  │
│  └──────────┘  └──────────┘  └───────────┘  └───────────────────┘  │
│              ConcurrentDictionary<string, SessionGoal>               │
└─────────────────────────────────────────────────────────────────────┘
                  │                  │                  │
                  ▼                  ▼                  ▼
┌──────────────────┐  ┌──────────────────┐  ┌──────────────────────┐
│   模型工具 (3)    │  │ 运行时集成        │  │   TUI Footer         │
│   ─────────────   │  │   ──────────     │  │   ────────────       │
│  get_goal (只读)  │  │ 激活 prompt      │  │  状态文本            │
│  create_goal (写) │  │ 检查 prompt      │  │  进度条              │
│  update_goal (写) │  │ 预算门控         │  │  按状态显示          │
└──────────────────┘  │ 通道门控          │  └──────────────────────┘
                       │ 阻塞检测          │
                       └──────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────────┐
│                    AgentRuntime (轮次循环)                            │
│                                                                      │
│  for (i = 0; i < MaxIterations; i++)                                 │
│  {                                                                    │
│      messages = BuildMessages(session)                                │
│      if (Goal 激活中) → 在索引 1 注入激活 prompt                     │
│                                                                      │
│      response = await LLM_Call(messages)                              │
│                                                                      │
│      if (toolCalls.Count == 0)  // 模型停止                          │
│      {                                                                │
│          if (GoalIntegration.ShouldContinue(session, i))              │
│          {                                                            │
│              messages.Add(goal_check_prompt)                          │
│              continue;  // ← 自动续跑                                │
│          }                                                            │
│          return finalResponse;  // 正常停止                          │
│      }                                                                │
│                                                                      │
│      执行工具 → 将结果回送给 LLM                                      │
│  }                                                                    │
└─────────────────────────────────────────────────────────────────────┘
```

---

## 3. 组件清单

| 组件 | 路径 | 职责 |
|-----------|------|----------------|
| `GoalStatus` | `Core/Models/Goal/GoalStatus.cs` | 6 状态枚举 + 扩展方法（`IsPursuable`、`IsTerminal`、`ToDisplayName`、`FormatGoalFooterLine`、`FormatGoalProgressBar`） |
| `SessionGoal` | `Core/Models/Goal/SessionGoal.cs` | 数据模型：目标、预算、token、阻塞哈希、续跑计数、文本归一化 + SHA-256 哈希 |
| `GoalHistoryRecord` | `Core/Models/Goal/GoalHistoryRecord.cs` | AOT 兼容的序列化记录，用于 goal 历史 JSONL |
| `IGoalService` | `Core/Abstractions/IGoalService.cs` | 服务接口：CRUD、状态转换、token 追踪、阻塞检测、历史持久化 |
| `InMemoryGoalService` | `Core/Services/InMemoryGoalService.cs` | 线程安全 `ConcurrentDictionary` 实现。验证状态转换、从会话基线计算 token 用量、记录阻塞哈希、追加历史 JSONL |
| `GetGoalTool` | `Agent/Tools/GetGoalTool.cs` | `IToolWithContext`。只读：返回 goal 状态、目标、token、预算 |
| `CreateGoalTool` | `Agent/Tools/CreateGoalTool.cs` | `IToolWithContext`。创建 goal（目标 + 可选预算）。如果已有 goal 则失败 |
| `UpdateGoalTool` | `Agent/Tools/UpdateGoalTool.cs` | `IToolWithContext`。仅允许设置 `complete` 或 `blocked`。包含外部验证门控 |
| `AgentRuntimeGoalIntegration` | `Agent/Goal/AgentRuntimeGoalIntegration.cs` | 核心集成：激活 prompt 构建、续跑评估、预算检查、阻塞检测、通道门控 |
| `GoalPromptTemplates` | `Agent/Goal/GoalPromptTemplates.cs` | Prompt 构建器（轮次开始的激活 prompt、停止时的检查 prompt） |
| `ChatCommandProcessor` | `Core/Pipeline/ChatCommandProcessor.cs` | 内建 `/goal` 命令处理器：start/pause/resume/complete/block/clear/status |
| `GoalTuiExtensions` | `Tui/GoalTuiExtensions.cs` | TUI footer 格式化：状态行、进度条 |
| `MafAgentRuntime` | `MAFAdapter/MafAgentRuntime.cs` | MAF 运行时并行 Goal 集成：激活 prompt + 自动续跑循环 |

---

## 4. 6 状态状态机

### 状态定义

| 状态 | 含义 | 允许转换到 | 触发条件 |
|-------|-------|---------------|---------|
| **Active** | 正在推进目标 | Paused, Blocked, BudgetLimited, UsageLimited, Complete | 默认状态；`/goal resume` 恢复 |
| **Paused** | 操作员暂停 | Active | `/goal pause` |
| **Blocked** | 真实阻塞（3+ 轮） | Active | 模型或操作员标记 blocked |
| **BudgetLimited** | Token 预算耗尽 | Active | Token 使用量 >= 预算 |
| **UsageLimited** | 用量限制（预留） | Active | 未来系统级限制 |
| **Complete** | 目标已达成（终态） | — | `update_goal(complete)` 或 `/goal complete` |

### 转换图

```
                    ┌─────────────┐
         ┌─────────│   Active    │◄────────┐
         │         └──────┬──────┘         │
         │                │                │
    /goal pause    budget exceeded    /goal resume
         │                │                │
         ▼                ▼                │
   ┌─────────┐     ┌─────────────┐        │
   │ Paused  │     │BudgetLimited│        │
   └────┬────┘     └──────┬──────┘        │
        │                 │               │
        └─────────────────┘               │
              /goal resume                │
                                          │
        ┌─────────────────────────────────┘
        │
   update_goal blocked              update_goal complete
        │                                │
        ▼                                ▼
   ┌─────────┐                     ┌──────────┐
   │ Blocked │                     │ Complete │
   └────┬────┘                     └──────────┘
        │
   /goal resume
```

### 无效转换守卫

服务层对不允许的转换显式抛出 `InvalidOperationException`：

```
Active→Active        ✅（无操作）
Complete→Active      ❌（终态不可转换）
Complete→Paused      ❌（终态不可转换）
Paused→Blocked       ❌（必须先经过 Active）
```

### 阻塞检测算法

阻塞条件通过**空白符归一化后的模型助手轮次文本的精确哈希匹配**判定：

1. 归一化：`Trim()` + 将内部连续空白折叠为单个空格
2. 计算归一化文本的 SHA-256 哈希
3. 与 `SessionGoal` 上的 `LastBlockerHash` 比较
4. 如果匹配 → 递增 `ConsecutiveBlockerCount`
5. 如果 `ConsecutiveBlockerCount >= 3` → 自动转换为 `Blocked` 状态
6. 如果不匹配 → 将计数器重置为 1，更新 `LastBlockerHash`

这是一个**保守的启发式规则**：误报（不同的阻塞条件因巧合而匹配）被认为比漏报（相同的阻塞条件因改述而逃脱检测）更安全。

### 可续跑性

只有 `Active` 状态是可续跑的——运行时仅在 goal 处于 Active 状态时自动续跑。所有其他状态（Paused、Blocked、BudgetLimited、UsageLimited、Complete）都会使运行时正常返回。

---

## 5. Token 预算系统

### 基线机制

Token 预算基于 goal 创建时捕获的**会话基线**计算：

```csharp
// 创建 goal 时：记录会话当前 token 总数
var tokensAtStart = session.GetTotalTokens();
var goal = goalService.CreateGoal(sessionId, objective, tokenBudget, tokensAtStart);

// 每次检查时：用量 = 当前总数 - 基线
goalService.UpdateTokenUsage(sessionId, session.GetTotalTokens());
// goal.TokensUsed = sessionTotal - goal.TokensAtStart
```

这确保 goal 不会追溯计费创建之前消耗的 token。

### 预算执行

当 `goal.IsBudgetExceeded`（`TokensUsed >= TokenBudget`）时：

1. Goal 转换为 `BudgetLimited`
2. 当前轮次正常完成（返回已生成的文本）
3. 后续轮次不自动续跑（BudgetLimited 不可续跑）
4. 用户可通过 `/goal resume` 重新激活（可能增加预算）

### CLI 语法

通过 `ChatCommandProcessor.HandleGoalCommandAsync` 中的正则提取：

| 输入 | 解析结果 |
|-------|--------------|
| `/goal start fix bug +500k` | 预算 = 500,000 |
| `/goal start write docs spend 2M tokens` | 预算 = 2,000,000 |
| `/goal start refactor +1.5M` | 预算 = 1,500,000 |
| `/goal start debug issue` | 预算 = 0（无限制） |

### 默认预算

128K 输出 token（与 Claude Code 默认值一致）。

---

## 6. 模型工具

通过 `ITool` / `IToolWithContext` 接口向 LLM 暴露三个工具。

### get_goal（只读）

```json
{
  "name": "get_goal",
  "parameters": {},
  "description": "读取当前会话 goal：状态、目标、token 用量和预算。"
}
```

返回 goal 的状态、目标文本、token 用量和预算信息。实现 `IToolWithContext` 以从执行上下文获取会话 ID。

### create_goal（由用户指示创建）

```json
{
  "name": "create_goal",
  "parameters": {
    "objective": "要实现的目标",
    "token_budget": 500000
  }
}
```

仅当用户/系统 prompt 显式指示时成功。如果会话已有 goal，则返回错误。

### update_goal（仅 complete/blocked）

```json
{
  "name": "update_goal",
  "parameters": {
    "status": "complete|blocked",
    "note": "可选说明"
  }
}
```

**受限转换：** 模型**只能**设置 `complete` 或 `blocked`。不能暂停、恢复、清除或替换 goal。这些只能由操作员通过 CLI 操作。

**外部验证门控：** 在批准 `update_goal(status="complete")` 之前，工具执行合理性检查。如果验证失败，拒绝转换并重新注入 goal-check prompt。运行时的 `AgentRuntimeGoalIntegration.EvaluateGoalContinuation` 提供额外的基于迭代次数的验证。

### 权限边界

| 操作 | 模型 (/goal 工具) | 操作员 (/goal 命令) | 系统 |
|-----------|-------|-----------------|--------|
| 读取 goal | ✅ get_goal | ✅ status | ✅ |
| 创建 goal | ✅（需用户指示） | ✅ start | — |
| 标记完成 | ✅ update_goal | ✅ complete/done | — |
| 标记阻塞 | ✅ update_goal | ✅ block/blocked | — |
| 暂停 | ❌ | ✅ pause | — |
| 恢复 | ❌ | ✅ resume | ✅（BudgetLimited 检查后） |
| 清除 | ❌ | ✅ clear | ✅（/new、/reset 时） |
| BudgetLimited | — | — | ✅（预算超限时自动） |

---

## 7. 运行时集成

### 7.1 原生 AgentRuntime

原生 `AgentRuntime`（`src/OpenClaw.Agent/AgentRuntime.cs`）使用 `for` 循环遍历迭代次数：

```csharp
for (var i = 0; i < _maxIterations; i++)
{
    // 1. LLM 调用
    var response = await CallLlmAsync(messages);

    // 2. 提取工具调用
    var toolCalls = GetToolCalls(response);

    // 3. 如果模型停止
    if (toolCalls.Count == 0)
    {
        // ── Goal 续跑检查 ──
        if (_goalIntegration is not null)
        {
            _goalIntegration.UpdateGoalTokenUsage(session);
            var prompt = _goalIntegration.EvaluateGoalContinuation(
                session, i, _maxIterations, response.Text);

            if (prompt is not null)
            {
                messages.Add(new ChatMessage(ChatRole.System, prompt));
                continue;  // ← 自动续跑
            }
        }

        // 正常停止
        return response.Text;
    }

    // 4. 执行工具
    await ExecuteTools(toolCalls);
    // 回到步骤 1
}
```

**集成点：**

| 位置 | 代码位置 | 操作 |
|-------|----------|--------|
| 构造函数 | `AgentRuntime(...)` | 可选 `IGoalService` → 创建 `AgentRuntimeGoalIntegration` |
| 消息构建后 | `for` 循环前 | `messages.Insert(1, goalActivationPrompt)` — 在系统 prompt 之后 |
| 模型停止后 | 循环内的 `toolCalls.Count == 0` | `EvaluateGoalContinuation()` → 注入检查 prompt 或返回 |

### 7.2 MAF 适配器 (MafAgentRuntime)

Microsoft Agent Framework 适配器（`src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs`）委托给 `ChatClientAgent.RunAsync()`——该函数内部处理自己的工具循环。Goal 集成通过外层迭代循环包裹：

```csharp
for (var i = 0; i < _maxIterations; i++)
{
    // 1. 推入执行作用域
    using var scope = MafExecutionContextScope.Push(context);

    // 2. MAF agent 处理一轮（可能包含多个内部 LLM+工具周期）
    var response = await agent.RunAsync(messages, mafSession, options);

    // 3. 提取并记录响应
    session.History.Add(assistantTurn);

    // 4. ── Goal 续跑检查 ──
    if (_goalIntegration is not null)
    {
        _goalIntegration.UpdateGoalTokenUsage(session);
        var prompt = _goalIntegration.EvaluateGoalContinuation(
            session, i, _maxIterations, text);

        if (prompt is not null)
        {
            messages.Add(new ChatMessage(ChatRole.System, prompt));
            continue;  // ← 自动续跑
        }
    }

    // 正常完成
    return text;
}
```

**流式路径**遵循相同模式，但在循环中包裹 `Channel<AgentStreamEvent>` 生产者，在开始下一次迭代前清理上一次迭代的生产者。

**与原生运行时的关键差异：**

| 方面 | 原生 AgentRuntime | MafAgentRuntime |
|-------|-------------------|-----------------|
| 循环粒度 | 每次迭代一次 LLM 调用 | 每次迭代一次 `agent.RunAsync()`（可能包含多次内部 LLM 调用） |
| 工具循环 | 由 AgentRuntime 管理 | 由 ChatClientAgent 内部管理 |
| 执行作用域 | 不需要 | 每次迭代推入 `MafExecutionContextScope` |
| 流式处理 | `RunStreamingAsync` 并行续跑 | 用于循环包裹 Producer+Channel |

---

## 8. CLI 命令

`/goal` 命令是 `ChatCommandProcessor` 中的**内建**命令，而非动态注册。它与 `/status`、`/new`、`/model` 等一起注册在 `BuiltInCommands` 中。

### 命令参考

| 命令 | 别名 | 功能 |
|---------|-------|----------|
| `/goal` | `/goal status` | 显示当前 goal 详情 |
| `/goal start <目标>` | `/goal set`、`/goal create` | 创建新 goal，支持 `+Nk`/`+Nm` 预算语法 |
| `/goal pause [备注]` | — | 暂停 active goal |
| `/goal resume [备注]` | — | 恢复 paused/blocked/budget-limited goal |
| `/goal complete [备注]` | `/goal done` | 标记 goal 已完成 |
| `/goal block [备注]` | `/goal blocked` | 标记 goal 已阻塞 |
| `/goal clear` | — | 清除当前 goal |

### 关键约束

- **单 goal 限制：** 一个 session 同时只能有一个 goal。创建第二个会返回描述性错误
- **`/new` 和 `/reset` 清除 goal：** 这些命令意图开始新的会话上下文，会清除当前 goal
- **终态不可修改：** Complete 的 goal 不能被修改，必须先清除
- **模型工具与 CLI 能力分离：** CLI 支持完整生命周期；模型工具仅限 read/create/update(complete|blocked)

### 实现

`ChatCommandProcessor` 中的 `HandleGoalCommandAsync` 方法：

1. 解析子命令（`start`、`pause` 等）
2. 对于 `start`，通过正则从 `+Nk`/`+Nm` 或 `spend N tokens` 后缀解析预算
3. 通过 `IGoalService.UpdateStatus()` 验证状态转换
4. 返回人类可读的结果文本

---

## 9. TUI 显示

### Footer 格式

TUI footer 使用 `GoalStatusExtensions.FormatGoalFooterLine()` 显示 goal 状态：

| Goal 状态 | Footer 文本 |
|-----------|------------|
| Active（有预算） | `Pursuing goal (12k/50k)` |
| Active（无预算） | `Pursuing goal: fix CI for PR 874...` |
| Paused | `Goal paused (/goal resume)` |
| Blocked | `Goal blocked (/goal resume)` |
| BudgetLimited | `Goal unmet (50k/50k)` |
| UsageLimited | `Goal hit usage limits (/goal resume)` |
| Complete | `Goal achieved (42k)` |

### 进度条

当 goal 处于 Active 状态且有 token 预算时，进度条显示实时进展：

```
[========>           ] 45% (58k/128k)
```

进度条由 `GoalStatusExtensions.FormatGoalProgressBar()` 渲染，使用：
- 20 字符宽的进度条，`=` 填充，`>` 指针
- 百分比 = `tokensUsed / tokenBudget`
- 如果终端不支持 Unicode，回退为静态文本

---

## 10. Prompt 设计

### Goal 激活 Prompt

在**每个轮次开始**时（如果 goal 处于 active 状态）注入一次：

> **Active Goal**
> A session-scoped goal is now active with the following objective:
> `<objective>{{objective}}</objective>`
>
> **Your Behavior**
> - Treat the objective itself as your directive. Do NOT pause to ask the user what to do.
> - The system will automatically continue you if you stop before the goal is achieved.
> - When the goal is fully achieved, use the update_goal tool with status='complete'.
> - If you're genuinely blocked after repeated attempts, use update_goal with status='blocked'.
>
> **Completion Audit**
> Before declaring the goal complete, derive concrete requirements from the objective. For each requirement, identify authoritative evidence. Uncertain evidence means NOT achieved.

### Goal 检查 Prompt

在每次**自动续跑**时（模型停止后）注入：

> **Goal Check — Continue Working**
> You were working toward this objective: `<objective>{{objective}}</objective>`
>
> 1. REVIEW all work done so far
> 2. DETERMINE whether the objective has been FULLY achieved
> 3. If ACHIEVED → use update_goal tool with status='complete'
> 4. If NOT ACHIEVED → CONTINUE working without asking the user
>
> **Budget**: Used {{tokens_used}} / Budget {{token_budget}} / Remaining {{remaining_tokens}}
> **Fidelity**: Optimize for movement toward the requested end state. Do NOT substitute easier solutions.
> **Blocked Audit**: Only mark blocked after 3+ consecutive turns with the same blocker.
> Iteration: {{iteration}}/{{max_iterations}}

### 注入策略

- **激活 prompt：** `messages.Insert(1, ...)` — 放在系统 prompt 之后、用户消息之前，确保模型尽早看到目标指令
- **检查 prompt：** `messages.Add(...)` — 追加在模型响应之后，推动模型继续工作

---

## 11. 外部验证

在批准 `update_goal(status="complete")` 之前，运行时执行合理性检查：

1. **非工具执行中：** 模型的最后一个动作必须是面向用户的文本响应
2. **迭代次数 >= 2：** 防止在第 1 轮就立即声明"已完成"
3. **非工具链执行中：** Agent 不能在工具链执行过程中

如果验证失败，拒绝转换并重新注入 goal-check prompt。这解决了"模型不可信"前提与模型自我报告之间的张力：模型自我报告，但运行时交叉核验合理性。

---

## 12. 通道隔离

**决策来源：** CEO 评审中的外部声音（Codex）发现。

Goal 自动续跑仅在**交互式通道**中触发——即有人类操作员在场且能观察到续跑的会话。非交互式通道（HTTP API、webhook、定时任务）在第一次停止时正常返回。

在 `AgentRuntimeGoalIntegration` 中的实现：

```csharp
private static readonly HashSet<string> InteractiveChannelPrefixes =
    new(StringComparer.OrdinalIgnoreCase) { "cli", "tui", "terminal", "console", "companion" };

private static bool IsInteractiveChannel(string? channelId)
{
    if (string.IsNullOrWhiteSpace(channelId)) return true; // 默认视为交互式
    return InteractiveChannelPrefixes.Contains(channelId);
}
```

`Session.ChannelId` 字段用于门控决策。`AgentRuntime` 和 `MafAgentRuntime` 都应用此门控。

---

## 13. DI 注册

在 `CoreServicesExtensions.AddOpenClawCoreServices()` 中注册：

```csharp
// Goal 服务（单例，可选历史文件）
services.AddSingleton<IGoalService>(sp =>
{
    var startup = sp.GetRequiredService<GatewayStartupContext>();
    var logger = sp.GetRequiredService<ILogger<InMemoryGoalService>>();
    var historyPath = !string.IsNullOrEmpty(startup.Config.Memory.StoragePath)
        ? Path.Combine(Path.GetFullPath(startup.Config.Memory.StoragePath), "goal-history.jsonl")
        : null;
    return new InMemoryGoalService(logger, historyPath);
});

// Goal 工具（与其他 ITool 实现一起注册）
services.AddSingleton<ITool, GetGoalTool>();
services.AddSingleton<ITool, CreateGoalTool>();
services.AddSingleton<ITool, UpdateGoalTool>();
```

Goal 工具是 `ITool` 单例，可通过 `AgentRuntime`（通过构造函数注入 `IReadOnlyList<ITool>`）和 `MafAgentRuntime`（通过 `MafToolAdapter` 包装）自动使用。

---

## 14. NativeAOT 兼容性

项目面向 NativeAOT（AOT 编译的 .NET）。所有 Goal 代码遵循 source-generator friendly 模式：

| 风险 | 缓解措施 |
|------|-----------|
| JSON 序列化反射 | `GoalHistoryRecord` + `[JsonSerializable]` + `GoalJsonContext` 分部类 |
| 依赖注入 | 标准 `IServiceProvider` 解析 |
| 并发访问 | `ConcurrentDictionary` + 原子操作 |
| 无 `dynamic` 或运行时代码生成 | 所有类型静态已知 |
| 无反射 | 枚举上的扩展方法，而非基于反射的分发 |

`GoalJsonContext` 是一个小型源代码生成的序列化上下文：

```csharp
[JsonSerializable(typeof(GoalHistoryRecord))]
internal sealed partial class GoalJsonContext : JsonSerializerContext;
```

---

## 15. 测试策略

### 单元测试（5 个类，34 个测试）

| 测试类 | 文件 | 覆盖内容 |
|-----------|------|----------|
| `GoalStatusTests` | `GoalStatusTests.cs` | `IsPursuable`、`IsTerminal`、`ToDisplayName`（全部 6 个状态） |
| `SessionGoalTests` | `SessionGoalTests.cs` | 归一化、SHA-256 哈希、预算超限、最大目标长度 |
| `InMemoryGoalServiceTests` | `InMemoryGoalServiceTests.cs` | CRUD、状态转换（有效/无效）、token 追踪、阻塞检测（3+ 相同）、多会话隔离、预算超限流程 |
| `GoalPromptTemplatesTests` | `GoalPromptTemplatesTests.cs` | Footer 格式（所有状态）、进度条、激活 prompt、检查 prompt（有/无预算） |
| `AgentRuntimeGoalIntegrationTests` | `AgentRuntimeGoalIntegrationTests.cs` | Goal 激活 prompt（active/paused/无 goal）、续跑评估、**通道门控**（交互/非交互）、**预算超限** → BudgetLimited、**续跑上限** → 自动暂停、**最大迭代次数**守卫、token 用量追踪、**阻塞检测**（3 次连续） |

### 集成测试场景

1. **完整生命周期：** start → active → (auto-continue × N) → complete → clear
2. **预算超限：** start +500k → 工作到预算 → 自动转换为 BudgetLimited → resume
3. **阻塞审计：** start → 阻塞条件 × 3 → Blocked → resume → 解决 → complete
4. **会话重置：** `/new` 或 `/reset` 清除 goal
5. **通道门控：** `ChannelId=cli` 续跑 vs `ChannelId=gateway` 正常返回

### 测试框架

- **xUnit**（.NET 测试运行器）
- **NSubstitute**（模拟框架）
- 所有测试均为 `[Fact]`（当前覆盖无需 `[Theory]`）
- Session 实例需要 `SenderId`（Session 模型上的 `required` 字段）

---

## 16. 设计决策（来自评审）

在三阶段评审过程中做出的决策（CEO、设计、工程）：

| # | 决策 | 选择 | 来源 |
|---|----------|--------|--------|
| 1 | 实现方案 | 完整上游对等（方案 A） | CEO 评审 |
| 2 | 范围模式 | 范围扩展 | CEO 评审 |
| 3 | Goal 预算 vs 会话预算 | Goal 预算覆盖会话预算 | 工程评审 |
| 4 | 通道门控 | 按 `ChannelId` 门控 | 外部声音（Codex） |
| 5 | 工具依赖模式 | 构造函数注入 `IGoalService` | 工程评审 |
| 6 | 激活 prompt 位置 | 在最后的系统/recall 注入之后 | 工程评审 |
| 7 | 工具文件组织 | 每个工具一个文件（3 个文件） | 工程评审 |
| 8 | Token 记账可靠性 | 回退：2+ 次连续不可靠读数 → BudgetLimited | 外部声音 |
| 9 | 自动续跑检测 | 接受启发式规则（与上游一致） | 外部声音 |

### 已接受的扩展

1. **Goal 历史持久化：** 将完成的 goal 追加到 `~/.openclaw/goal-history.jsonl`（JSONL 格式）
2. **Goal 保真度审计（轻量级）：** 完成时的非阻塞文件变更和测试状态审计
3. **TUI 进度条：** 终端 footer 中的动画 `tokensUsed/tokenBudget` 显示

---

## 17. 后续扩展

1. **分布式 Goal 存储：** 将 `InMemoryGoalService` 替换为 Redis/SQL 后端，支持多实例部署
2. **评审 Agent：** 在 goal-check 时使用独立的评审 agent 验证完成质量
3. **Goal 历史浏览器：** 跨会话查看已完成 goal 的 UI
4. **Goal 模板：** 预定义模板（`/goal fix-bug`、`/goal write-docs`），带有调整后的审计 prompt
5. **子 Goal 支持：** 将大目标分解为可追踪的子目标
6. **外部验证增强：** 在批准"完成"前进行文件哈希快照比较

---

## 附录 A：验证检查清单

部署 Goal 代码变更后，使用此清单确认功能正常工作。

### 前提条件
- [ ] Gateway 服务已重新编译并重启
- [ ] `IGoalService` 已在 DI 中注册（`CoreServicesExtensions.cs`）
- [ ] `ChatCommandProcessor` 已注入 `IGoalService`（可选参数）
- [ ] `AgentRuntime` 构造函数接收 `IGoalService`（通过 `NativeAgentRuntimeFactory`）
- [ ] 至少一个运行时（原生或 MAF）已激活 Goal 集成

### 第一步：创建 Goal
1. 打开 **webchat** 或 **CLI/TUI**
2. 发送：`/goal start 测试 Goal 系统 +500k`
3. **预期：** 返回 `Goal created: "测试 Goal 系统" with budget 500000`
4. **如果失败：** 检查 `ChatCommandProcessor.HandleGoalCommandAsync` 是否可达且 `IGoalService` 不为 null

### 第二步：验证命令处理
1. 发送：`/goal`
2. **预期：** 显示 goal 详情（状态：Active、目标、token 用量、预算）
3. 发送：`/goal pause`
4. **预期：** 显示 `Goal paused`
5. 发送：`/goal resume`
6. **预期：** 显示 `Goal resumed`
7. 发送：`/goal clear`
8. **预期：** 显示 `Goal cleared`

### 第三步：验证自动续跑（核心功能）
1. 创建 goal：`/goal start 浏览代码库结构 +500k`
2. 发送实际任务：`列出顶层目录及其用途`
3. **预期：** 模型开始工作、读取文件，然后：
   - 如果模型在完成前停下 → 系统自动续跑（在历史中查找 `[goal_check:N]`）
   - 如果模型已完成 → 系统尊重完成状态
4. **在日志中检查：**
   - `Goal activation prompt injected`
   - `Goal auto-continue iteration N/M`
   - `[goal_check:N] Continue working toward objective...`

### 第四步：验证通道门控
1. **webchat** 中（ChannelId = `websocket`）：Goal 自动续跑**应工作**
2. **CLI/TUI** 中（ChannelId = `cli` / `tui`）：Goal 自动续跑**应工作**
3. 通过 **HTTP API**（ChannelId 不在交互式列表中）：Goal 自动续跑**不应触发**

### 第五步：验证预算执行
1. 创建小额预算的 goal：`/goal start 测试 +100`
2. 发送需要多次工具调用的任务
3. **预期：** Token 消耗后 Goal 转换为 `BudgetLimited`，模型正常停止
4. 验证：`/goal` 显示状态 `Budget Limited`

### 第六步：验证阻塞检测
1. 为不可能的任务创建 goal：`/goal start 删除不存在的文件`
2. 让模型尝试并失败 3 次（同样的错误）
3. **预期：** 3 次连续相同阻塞后，Goal 转换为 `Blocked`
4. 验证：`/goal` 显示状态 `Blocked`

### 第七步：验证会话重置清除 Goal
1. 创建 goal：`/goal start 临时任务`
2. 发送：`/new` 或 `/reset`
3. 发送：`/goal`
4. **预期：** `No active goal` — 会话重置已清除 goal

### 日志关键字调试

| 关键字 | 含义 | 何时查找 |
|---------|---------|-------------|
| `Goal activation prompt injected` | 激活 prompt 已注入消息列表 | 每轮有 active goal 时 |
| `Goal auto-continue iteration` | 自动续跑已触发 | 模型停下且有 active goal 时 |
| `Goal {SessionId} budget exceeded` | Token 预算已耗尽 | `TokensUsed >= TokenBudget` 时 |
| `Goal {SessionId} blocked after 3+` | 阻塞阈值已到达 | 3 次相同阻塞后 |
| `Goal {SessionId} auto-paused` | 每轮续跑上限已到 | 超过 `MaxContinuationsPerTurn`（10）后 |
| `Goal auto-continuation skipped` | 非交互式通道阻止续跑 | 非交互式通道有 active goal 时 |


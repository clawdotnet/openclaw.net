# 会话处理（Session Handling）

本文说明 OpenClaw 中 *session*（会话）是什么、生命周期如何管理，以及会话相关工具（`sessions_spawn`、`sessions_yield`、`sessions`）分别做什么。目标读者是希望理解运行时会话机制的贡献者与运维人员。

## 什么是会话

会话是网关路由对话状态的基本单元，定义见 [src/OpenClaw.Core/Models/Session.cs](../../src/OpenClaw.Core/Models/Session.cs)：

- **标识信息**：`Id`、`ChannelId`、`SenderId`。
- **对话历史**：`History`，按时间顺序保存 `ChatTurn`。
- **生命周期状态**：`SessionState`（`Active`、`Paused`、`Expired`）。
- **时间戳**：`CreatedAt`、`LastActiveAt`（会话过期判断依赖后者）。
- **会话级覆写**：模型、推理强度、工具预设、系统提示词、路由白名单、合同策略、委派元数据等。
- **Token 计数器**：`TotalInputTokens`、`TotalOutputTokens`、缓存读写 token，使用原子方式更新以保证并发安全。

默认会话键是 `channelId:senderId`，即“某个频道下某个发送者”默认对应一个会话。对子代理、定时任务、Webhook 等场景可以使用显式 `sessionId`。

## 会话所有者：`SessionManager`

会话状态由 [src/OpenClaw.Core/Sessions/SessionManager.cs](../../src/OpenClaw.Core/Sessions/SessionManager.cs) 统一管理，核心职责包括：

- 维护活动会话内存集合（并发字典）。
- 通过持久化存储读写会话（默认 SQLite 后端）。
- 按空闲超时进行清理。
- 按最大活动会话数进行准入与淘汰。
- 通过准入闸门避免并发准入时的容量竞态。

常用接口包括：默认/显式 ID 创建或加载会话、按 ID 查询活动会话、加载历史会话、列举活动会话、持久化会话、活动集移除、过期清理、准入前容量保障。

## 生命周期（按步骤）

1. **准入**：新消息或 `sessions_spawn` 触发 `GetOrCreateByIdAsync`，优先命中活动缓存，未命中则尝试从存储重建；必要时创建新会话。
2. **活动处理**：请求解析到会话后会刷新 `LastActiveAt`，回合写入 `History`，token 计数器累加。
3. **持久化**：会话在回合结束后写入存储，带重试与退避。
4. **长回合检查点**：多步工具执行时写入 `ExecutionCheckpoint`，用于重启后的可恢复执行。
5. **跨会话通信**：通过同一条入站消息管道按 `SessionId` 路由；会话不是线程，而是状态容器。
6. **过期与淘汰**：先清理超时会话，再按容量淘汰最久未活跃会话；淘汰仅移出内存，不删除持久化数据。
7. **释放**：进程关闭时等待后台持久化任务并释放会话锁资源。

## 会话相关工具

### `sessions_spawn`（异步触发）

网关工具定义： [src/OpenClaw.Gateway/Tools/SessionsSpawnTool.cs](../../src/OpenClaw.Gateway/Tools/SessionsSpawnTool.cs)

- 参数：必填 `prompt`，可选 `session_id`、`channel_id`。
- 行为：创建/获取目标会话并把消息写入入站管道。
- 返回：立即返回会话 ID，不等待子会话执行完成。

### `sessions_yield`（同步等待）

网关工具定义： [src/OpenClaw.Gateway/Tools/SessionsYieldTool.cs](../../src/OpenClaw.Gateway/Tools/SessionsYieldTool.cs)

- 参数：`session_id`、`message`，以及超时参数。
- 行为：向目标会话发送消息并轮询等待新的 assistant 回复。
- 返回：目标会话回复或超时结果。

### `sessions`（list/history/send）

Agent 工具定义： [src/OpenClaw.Agent/Tools/SessionsTool.cs](../../src/OpenClaw.Agent/Tools/SessionsTool.cs)

- `list`：列出活动会话。
- `history`：读取指定会话最近 N 轮历史。
- `send`：向指定会话发送消息并立即返回。

### 注册方式

上述工具归于同一会话工具组（`group:sessions`），定义见 [src/OpenClaw.Gateway/ToolPresetResolver.cs](../../src/OpenClaw.Gateway/ToolPresetResolver.cs)。

## 心智模型

- 会话是“状态单元”，不是“执行线程”。
- 检查点是恢复点，不是完整运行时快照。
- 所有会话消息都走同一入站管道。
- 过期/淘汰是内存层行为，不等于持久化删除。
- `spawn` 与 `yield` 的区别是异步触发 vs 同步等待。

## 每轮 Token 消费统计（Per-turn Token Accounting）

OpenClaw 会在每一轮（turn）把 token 使用量映射到多个观察层：回合上下文、会话累计、运行时累计、provider 聚合，以及（启用时）合同治理成本跟踪。

### 单轮统计如何发生

1. **建立回合上下文**：运行时先创建 `TurnContext`，承载该轮关联信息与观测数据。
2. **吸收 usage**：`AgentTurnAccounting` 在流式与非流式路径记录 usage，并规范化输入/输出/缓存字段。
3. **必要时估算回填**：当上游 provider 未返回 usage 时，运行时可使用估算值保持记账连续。
4. **多路写入**：同一轮 usage 同步写入以下位置：
   - 会话累计计数（`Session`）
   - 进程级累计计数（`RuntimeMetrics`）
   - provider/model 聚合与最近轮次（`ProviderUsageTracker`）
   - 合同治理成本（启用合同模式时）
5. **外部观察面读取这些计数**：`/status`、`/usage`、指标/管理接口、OpenAI 兼容 `usage` 字段均由这些计数投影得到。

### 去哪里看 token 统计

- **回合级**：`TurnContext` 摘要与回合日志。
- **会话累计**：`/status`、`/usage`。
- **运行时/provider 级**：`/metrics`、`/metrics/providers`。
- **运维排障视图**：`/admin/providers`、`/admin/sessions/{id}/timeline`。
- **兼容响应**：OpenAI 兼容 chat/responses 里的 `usage` 字段。

### 每次会话任务 token 消耗（持久化账本）

OpenClaw 现已把每轮（turn）token 用量写入追加型持久化账本，因此“每次会话任务消耗多少 token”可以在内存窗口之外长期追溯。

- **写入模型**：append-only JSONL（每行一条 turn 记录）。
- **默认路径**：`<Memory.StoragePath>/audit/turn-token-usage.jsonl`。
- **记录结构**：`TurnTokenUsageRecord`，包含 `SessionId`、`ChannelId`、`ProviderId`、`ModelId`、输入/输出/缓存 token、`EstimatedInputTokensByComponent`、`IsEstimated`、`TimestampUtc`。
- **执行链路**：turn 记账会发出 `ITurnTokenUsageObserver` 事件；网关默认使用组合 observer，同时写入 `ProviderUsageTracker`（有界 recent turns，适合近期排障）与 `TurnTokenUsageAuditLog`（持久化追加账本）。

运维说明：

- 持久化 JSONL 账本是逐轮/逐会话任务审计的长期依据。
- Dashboard 的 provider timeline 仍是“近期窗口视图”，不是长期账本。
- `IsEstimated=true` 表示上游未回传 usage，本轮 token 使用估算值补齐。

### 在 Dashboard 查看 token 统计

可视化入口位于 Dashboard 的 **Sessions** 页面（对应 [src/OpenClaw.Dashboard/Pages/Sessions.razor](../../src/OpenClaw.Dashboard/Pages/Sessions.razor)）：

1. 打开 Sessions 页，左侧会话列表会显示每条会话的 `Σ` 总 token（`input + output`）。
2. 点击任意会话后，右侧详情会出现 token 汇总卡片：
   - `Input tokens`
   - `Output tokens`
   - `Cache read tokens`
   - `Cache write tokens`
   - `Total tokens`
3. 在同一详情区向下可查看 **Provider token 时间线** 表格（来自 `/admin/sessions/{id}/timeline`），按 turn 展示：
   - 时间戳
   - Provider / Model
   - input/output/cache/total token

口径说明：

- 汇总卡片读取的是会话累计计数（`Session.Total*Tokens`）。
- 时间线是 provider recent turns 的有界窗口，主要用于排障与最近行为观察，不是长期审计账本。
- 当上游未回传 usage 时，部分 token 可能为估算值。

### 当前语义（重要）

- OpenAI 兼容 `usage` 目前是**会话累计视图**，不是单次请求增量。
- 某些路径 provider 若未回传 usage，token 可能是**估算值**而非计费原值。
- provider recent turns 是**有界内存窗口**，不是长期审计账本。
- 每轮持久化 token 审计可通过 `turn-token-usage.jsonl` 获取。
- `/status` 与 `/usage` 都是会话累计，不是“上一轮增量”。

### 关键实现锚点

- 记账入口： [src/OpenClaw.Agent/Runtime/AgentTurnAccounting.cs](../../src/OpenClaw.Agent/Runtime/AgentTurnAccounting.cs)
- 会话计数： [src/OpenClaw.Core/Models/Session.cs](../../src/OpenClaw.Core/Models/Session.cs)
- 回合上下文： [src/OpenClaw.Core/Observability/TurnContext.cs](../../src/OpenClaw.Core/Observability/TurnContext.cs)
- Provider 聚合： [src/OpenClaw.Core/Observability/ProviderUsageTracker.cs](../../src/OpenClaw.Core/Observability/ProviderUsageTracker.cs)
- Turn observer 协议： [src/OpenClaw.Core/Abstractions/ITurnTokenUsageObserver.cs](../../src/OpenClaw.Core/Abstractions/ITurnTokenUsageObserver.cs)
- Turn 记录模型： [src/OpenClaw.Core/Models/TurnTokenUsageRecord.cs](../../src/OpenClaw.Core/Models/TurnTokenUsageRecord.cs)
- 持久化 token 账本： [src/OpenClaw.Core/Observability/TurnTokenUsageAuditLog.cs](../../src/OpenClaw.Core/Observability/TurnTokenUsageAuditLog.cs)
- 运行时累计： [src/OpenClaw.Core/Observability/RuntimeMetrics.cs](../../src/OpenClaw.Core/Observability/RuntimeMetrics.cs)
- 会话命令输出： [src/OpenClaw.Core/Pipeline/ChatCommandProcessor.cs](../../src/OpenClaw.Core/Pipeline/ChatCommandProcessor.cs)
- 指标与管理接口： [src/OpenClaw.Gateway/Endpoints/DiagnosticsEndpoints.cs](../../src/OpenClaw.Gateway/Endpoints/DiagnosticsEndpoints.cs)、[src/OpenClaw.Gateway/Endpoints/AdminEndpoints.Runtime.cs](../../src/OpenClaw.Gateway/Endpoints/AdminEndpoints.Runtime.cs)、[src/OpenClaw.Gateway/Endpoints/AdminEndpoints.Sessions.cs](../../src/OpenClaw.Gateway/Endpoints/AdminEndpoints.Sessions.cs)
- Gateway observer 注入与透传： [src/OpenClaw.Gateway/Composition/CoreServicesExtensions.cs](../../src/OpenClaw.Gateway/Composition/CoreServicesExtensions.cs)、[src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.RuntimeFactories.cs](../../src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.RuntimeFactories.cs)
- OpenAI 兼容 usage 输出： [src/OpenClaw.Gateway/Endpoints/OpenAiEndpoints.ChatCompletions.cs](../../src/OpenClaw.Gateway/Endpoints/OpenAiEndpoints.ChatCompletions.cs)、[src/OpenClaw.Gateway/Endpoints/OpenAiEndpoints.Responses.cs](../../src/OpenClaw.Gateway/Endpoints/OpenAiEndpoints.Responses.cs)

## 相关文档

- [TOOLS_GUIDE.md](../TOOLS_GUIDE.md)：工具目录与预设组合。
- [USER_GUIDE.md](../USER_GUIDE.md)：运维视角的 provider、工具、频道与会话。
- [GLOSSARY.md](../GLOSSARY.md)：术语定义。
- [PROMPT_CACHING.md](../PROMPT_CACHING.md)：缓存读写 token 语义与 provider 缓存行为。

> 说明：本页是中文版会话参考。若中英文出现差异，以英文原文 [docs/SESSIONS.md](../SESSIONS.md) 与代码实现为准。

# Strategos Thompson Sampling 回灌（Sidecar ↔ 网关）设计方案

> 适用范围：本次会话后续要实现的 `P2-Thompson Sampling 回灌` 阶段。计划文件将由 writing-plans skill 派生。

**目标**：在 sidecar 上以 `SelectorBackedChatClient` 装饰器形式接入 Strategos 的 `IAgentSelector`，并通过 webhook 消费网关运行事件，将每次运行的成败反馈回 `IAgentSelector.RecordOutcomeAsync`，形成闭环的 Thompson Sampling 学习回路。

**架构**：三层组件 + 一处 DI 接线。

1. `SelectorBackedChatClient`（sidecar）— 实现 `IChatClient`，包装 `LlmMode` 选出的客户端。每次 `GetResponseAsync`/`GetStreamingResponseAsync` 调用，先用 `IAgentSelector.SelectAgentAsync` 选出一个 agent id，再按 id 路由到对应的内部客户端；选不出来时**回退到默认客户端**（保持工作流不停）。被选中的 agent id 同时写入 sidecar 端的 `RunIdAgentSelectionCache`（按 `(runId, stepName)` 索引），供稍后的 outcome 反馈回查。

2. `GatewayEventReceiver`（sidecar）— `IHostedService` + `POST /runtime-events` 端点。接收网关 webhook 推送的 `RuntimeEventEntry`，按 `Component == "workflow"` 与 `Action ∈ {run_started, run_completed, run_failed, response_sent}` 过滤，调用 `AgentOutcomeMapper` 转为 `(agentId, taskCategory, AgentOutcome)`，再用缓存里的 agentId 调 `IAgentSelector.RecordOutcomeAsync`。入口处按 `entry.Id` 去重。

3. `RuntimeEventWebhook`（网关）— 出站 webhook 客户端。在 `RuntimeEventStore.Append` 之后异步把同一份 `RuntimeEventEntry` POST 到配置的 sidecar URL，携带共享 bearer token。5xx 重试一次，401/403 停发，连接失败重试一次。

4. **DI 接线（sidecar `Program.cs`）**：把原先 `IChatClient` 直接注册改成 `SelectorBackedChatClient` 包装层；新增 `IAgentSelector` 单例（默认 `ThompsonSamplingAgentSelector` + `InMemoryBeliefStore`）；新增 `GatewayEventReceiver` HostedService。两侧都**默认关闭**，由配置打开。

**调用栈**（选 agent）：

```
ReviewWorkflow ─▶ SecurityReviewer.ExecuteAsync(state, ct)
                            └─▶ chat.GetResponseAsync<ReviewVerdict>(messages, ct)
                                  └─▶ SelectorBackedChatClient.GetResponseAsync
 ├─ Build AgentSelectionContext { WorkflowId, StepName="SecurityReviewer",
 │                                 TaskDescription=first user msg 截断,
 │                                 AvailableAgents=SelectorOptions.AvailableAgents }
 ├─ selector.SelectAgentAsync(context)
 │     on failure → log warning, return default inner client
 ├─ 查 RunIdAgentSelectionCache.Set((runId, stepName), agentId, category, ts)
 ├─ Pick agent-specific inner client
 │     "mock"         → LlmMode 选出的默认客户端
 │     "gpt-4o-mini"  → 真实 OpenAI 客户端（DirectOpenAI 模式）
 │     "claude-…"     → Anthropic 客户端（如已配置）
 └─ inner.GetResponseAsync<ReviewVerdict>(messages, ct)
```

**调用栈**（outcome 反馈）：

```
gateway MafDurableHttpWorkflowRunner 完成
 └─▶ _events.Append(new RuntimeEventEntry {
        Component = "workflow",
        Action    = "run_completed",
        Metadata  = { runId, stepName, score }    // 新增 stepName + score
    })
 ├─ (现有) JSONL 写入 —— 与今天完全相同
 └─ (新增) RuntimeEventWebhook → POST {sidecar-url}/runtime-events
        Authorization: Bearer <shared token>
        Body = RuntimeEventEntry (序列化后)

sidecar GatewayEventReceiver 收到
 ├─ 校验 token（401 if mismatch）
 ├─ 过滤 Component="workflow" 且 Action ∈ {run_started, completed, failed, response_sent}
 ├─ 跳过 id 重复（in-memory LRU 10k）
 ├─ AgentOutcomeMapper.Map(entry) → (agentId, taskCategory, AgentOutcome)
 │     agentId 从 RunIdAgentSelectionCache.TryGet(runId, stepName) 查出
 ├─ selector.RecordOutcomeAsync(agentId, category, outcome)
 │     on failure → log warning, drop
 └─ 200 OK
```

**技术栈**：.NET 10, ASP.NET Core minimal API, Strategos 2.10.0（含 `Strategos.Abstractions` 中的 `IAgentSelector`、`Strategos.Infrastructure.Selection` 中的 `ThompsonSamplingAgentSelector` + `InMemoryBeliefStore`）, xUnit v3 3.2.2, NSubstitute 5.3.0。

**父级设计**：[`docs/zh-CN/OpenClaw.NET集成Strategos方案.md`](../../zh-CN/OpenClaw.NET集成Strategos方案.md) §6 路线图中的 "Thompson Sampling 消费网关运行事件"；侧车 P0 设计 [`docs/superpowers/specs/2026-08-20-openclaw-strategos-p0-sidecar-host-design.md`](../specs/2026-08-20-openclaw-strategos-p0-sidecar-host-design.md) §7 的"接缝仍在侧车"原则；P2-Ontology MCP App 设计 [`docs/superpowers/specs/...ontology-mcp-app...md`](../specs/)（已落地，§3 的"侧车独立"延续至此）。

## 全局约束

- 目标框架 `net10.0`，C# 14。
- `TreatWarningsAsErrors=true`：本次新增的所有项目必须 0 警告。
- **侧车为 JIT 发布**（无 AOT）—— P0 已确立。
- **默认关闭**：`Strategos:Selector:Enabled` 与 `OpenClaw:RuntimeEvents:Webhook:Url` 均默认为空/未配置。两侧都关闭时，所有现有工作流、网关行为不变。延续 P2-Ontology "完全可选" 的口径。
- **不对外暴露新端口**：sidecar 的 `POST /runtime-events` 与现有 `POST /api/workflows/...` 同进程同端口（仍是 8080）。`/runtime-events` 单独鉴权（bearer token）。
- **不修改网关 JSONL 形状**：sidecar webhook 仅是 `RuntimeEventStore.Append` 的镜像，JSONL 仍是主记录（durable record），webhook 失败不影响 JSONL。
- **不动 `Steps/*.cs`**：装饰器把选 agent 逻辑放在 `IChatClient` 边界之外；评审者代码保持原样（与 P0 "评审者步骤不变" 一致）。
- **共享 bearer token 走 SecretResolver**：网关侧 `OpenClaw:RuntimeEvents:Webhook:TokenSecret`，sidecar 侧 `Strategos:Selector:Webhook:TokenSecret`，复用 P0 的 `SecretResolver`。
- **AP2 风格接入**：`IAgentSelector` 已是成熟接口，不重新设计。
- **缓存是 sidecar 本地内存**：进程崩溃后历史选择丢失，feedback 沉默——这是 Thompson Sampling 的天然属性，可接受。

## 文件结构

| 文件 | 角色 | 改动 |
|---|---|---|
| `samples/OpenClaw.StrategosWorkflowHost/Adapters/SelectorBackedChatClient.cs` | `IChatClient` 装饰器；选 agent + 路由 | 新建 |
| `samples/OpenClaw.StrategosWorkflowHost/Adapters/RunIdAgentSelectionCache.cs` | `(runId, stepName) → (agentId, category, ts)` 内存缓存，FIFO 淘汰 | 新建 |
| `samples/OpenClaw.StrategosWorkflowHost/Adapters/AgentOutcomeMapper.cs` | 纯函数 `RuntimeEventEntry → (agentId, category, outcome)?` | 新建 |
| `samples/OpenClaw.StrategosWorkflowHost/Adapters/GatewayEventReceiver.cs` | `IHostedService` + `POST /runtime-events` 端点；接收、过滤、去重、反馈 | 新建 |
| `samples/OpenClaw.StrategosWorkflowHost/Configuration/SelectorOptions.cs` | `Enabled`、`AvailableAgents[]`、`TaskCategory`、`WebhookTokenSecret`、`CacheSize`、`InnerClients: Dictionary<string, IChatClient>` | 新建 |
| `samples/OpenClaw.StrategosWorkflowHost/Program.cs` | 接线：`IAgentSelector` 单例、SelectorBacked 装饰器、Receiver HostedService | 修改 |
| `samples/OpenClaw.StrategosWorkflowHost/appsettings.json` | 新增 `Strategos:Selector:{Enabled=false, AvailableAgents=[]}` | 修改 |
| `samples/OpenClaw.StrategosWorkflowHost/appsettings.Development.json` | `Strategos:Selector:{Enabled=true, AvailableAgents=["mock","mock-fast"], TaskCategory="agent_review"}` | 修改 |
| `samples/OpenClaw.StrategosWorkflowHost.Tests/SelectorBackedChatClientTests.cs` | 装饰器 5 个单元测试 | 新建 |
| `samples/OpenClaw.StrategosWorkflowHost.Tests/RunIdAgentSelectionCacheTests.cs` | 缓存 4 个测试 | 新建 |
| `samples/OpenClaw.StrategosWorkflowHost.Tests/AgentOutcomeMapperTests.cs` | 映射 6 个测试 | 新建 |
| `samples/OpenClaw.StrategosWorkflowHost.Tests/GatewayEventReceiverTests.cs` | 接收器 5 个测试 | 新建 |
| `samples/OpenClaw.StrategosWorkflowHost.Tests/Integration/SelectorEndToEndTests.cs` | 端到端 1 个：真实 `ThompsonSamplingAgentSelector` + `InMemoryBeliefStore` | 新建 |
| `src/OpenClaw.Gateway/Composition/RuntimeEventWebhookExtensions.cs` | `AddRuntimeEventWebhook(IConfiguration)`：解析 URL + token + 注入 `RuntimeEventWebhook` 单例 | 新建 |
| `src/OpenClaw.Gateway/RuntimeEventWebhook.cs` | 接收 `RuntimeEventEntry`，异步 POST，5xx/连接失败重试一次 | 新建 |
| `src/OpenClaw.Gateway/Workflows/MafDurableHttpWorkflowRunner.cs` | `RecordEvent(runId, stepName, action, status, summary, score?)` 增加 `stepName` + `score` 参数；现有调用点补齐 `stepName` | 修改 |
| `src/OpenClaw.Tests/RuntimeEventWebhookTests.cs` | webhook 4 个测试：触发条件、URL 未设跳过、5xx 重试、body 形状 | 新建 |

> **note**: `Strategos.Selection`（包含 `AgentSelectionContext`、`AgentSelection`、`AgentOutcome` 类型）已存在且与 `IAgentSelector` 协作；我们直接复用其类型，不再造轮子。

## 关键决策摘要

| # | 决策点 | 选择 | 备选 / 为何不选 |
|---|---|---|---|
| 1 | 选 agent 的接缝位置 | `SelectorBackedChatClient` 装饰 `IChatClient` | (a) 改写每个 reviewer 步骤——侵入大；(b) `IAgentStepExecutor`——多一层抽象，收益不抵成本 |
| 2 | outcome 来源 | HTTP webhook `POST /runtime-events`（侧车端）+ `RuntimeEventWebhook`（网关端） | (a) 共享 JSONL——耦合文件路径与挂载语义；(b) Postgres 直读——把侧车绑到网关 DB |
| 3 | 触发事件 | 复用网关已发出的 `Component="workflow"` + `Action in {run_started, run_completed, run_failed, response_sent}` | (a) 新增 `Component="AgentSelection"` 事件——网关要写新事件类型；(c) 推全部事件——噪声大，污染 belief store |
| 4 | 失败策略 | 选 agent 失败→回退默认；outcome 失败→日志+丢弃 | (a) 抛错→工作流可用性绑死 selector；(c) 配置化——为假设中的问题加接口面 |
| 5 | agentId 传递 | 侧车本地 `RunIdAgentSelectionCache` 关联 runId+stepName | (a) 元数据塞 agentId——网关不知道侧车选了谁；(b) 同上但走请求体——增加协议复杂度 |
| 6 | 端口 | 复用 8080，新增 `/runtime-events` 路由 | 新端口——增加部署面（端口、health check、ingress 等） |
| 7 | 鉴权 | 共享 bearer token，走 SecretResolver | mTLS——sidecar 与网关通常同主机/同集群网络，token 足够；mTLS 是 P3 以后的工程 |
| 8 | 关闭默认 | 两端 `Enabled=false`/`Url=""` | 默认开——会污染所有现有工作流的 belief store，破坏现有 dev 用例 |

## Webhook 报文（网关 → sidecar）

```json
{
  "id": "evt_abc123...",
  "timestampUtc": "2026-08-21T15:30:00Z",
  "component": "workflow",
  "action": "run_completed",
  "summary": "...",
  "severity": "info",
  "metadata": {
    "runId": "abc123",
    "stepName": "SecurityReviewer",
    "score": "0.92"
  }
}
```

侧车端直接复用 `OpenClaw.Core.Models.RuntimeEventEntry` 类型——不引入新模型。

## 错误处理表

| 失败场景 | 侧车行为 | 网关行为 |
|---|---|---|
| `selector.SelectAgentAsync` 返回 `Result.Failure` | 日志 warning，转发到包装的默认客户端 | n/a |
| 包装的 inner client 抛错（网络错误等） | 原样向上抛——与今天一致 | n/a |
| webhook 5xx | n/a | 日志 warning，重试一次（1s 抖动）；放弃（JSONL 仍是 durable 记录） |
| webhook 401/403 | 返回 401；日志 warning；**不重试** | 停止发送（配置错误） |
| `RecordOutcomeAsync` 失败 | 日志 warning，丢弃该 outcome | n/a |
| 侧车不可达（连接拒绝） | n/a | 日志 warning，下次 Append 时再重试一次 |
| 元数据缺 `runId` 或 `stepName` | 缓存 miss，日志 debug，跳过 | 跳过本次 POST，日志 debug |

## 测试策略

| 文件 | 测试数 | 验证点 |
|---|---|---|
| `SelectorBackedChatClientTests.cs` | 5 | 选 agent 后路由到对应 inner；失败时回退；空候选时回退；流式 API 同样行为；选出的 agent id 写入 `ChatOptions.Metadata` |
| `RunIdAgentSelectionCacheTests.cs` | 4 | 写入/读取；FIFO 淘汰；并发写安全（`ConcurrentDictionary`）；miss 返回 null |
| `AgentOutcomeMapperTests.cs` | 6 | `run_started` → neutral；`run_completed` → success + score；`run_failed` → failure；`response_sent` → neutral；非 `workflow` 组件被忽略；缺 `runId`/`stepName` 返回 null |
| `GatewayEventReceiverTests.cs` | 5 | 有效 entry 接受并调 `RecordOutcomeAsync`；token 不匹配返 401；按 `id` 去重；非 `workflow` 组件忽略；`RecordOutcomeAsync` 失败不中断后续事件 |
| `Integration/SelectorEndToEndTests.cs` | 1 | 端到端闭环：装饰器选 agent → fake chat 返回响应 → 模拟 webhook 投递 outcome → `ThompsonSamplingAgentSelector` + `InMemoryBeliefStore` 信念更新 1 次 |
| `RuntimeEventWebhookTests.cs`（网关） | 4 | 配置后触发；URL 空时跳过；5xx 重试；body 字段（`component`/`action`/`metadata.runId`）正确 |

**测试侧的选择器**：不关心算法的测试用 `StubAgentSelector`（总是返回固定 agentId、记录 `RecordOutcomeAsync` 调用于断言）。端到端测试用真实 `ThompsonSamplingAgentSelector` + `InMemoryBeliefStore`，固定 `randomSeed: 42` 保证可重复（与 Strategos 自家 `ThompsonSamplingSelectorTests.cs:42` 一致）。

**LlmMode 集成**：`Mock` 模式下默认客户端仍是 `MockReviewChatClient`，`AvailableAgents = ["mock"]`，selector 永远选 `mock`——Thompson Sampling 跑但观察不到差异。集成测试用 NSubstitute fake 替换 inner client 来驱动算法。

**覆盖率估算**：~25 个新测试，0 个旧测试改动。装饰器、缓存、映射、接收器都是叠加的。

## 设计自审

**1. Spec 覆盖**：用户原始诉求"用 `IAgentSelector` 包装 `Steps/*.cs` agent 步骤，经 `RecordOutcomeAsync` 消费网关运行结果"对应：
- "用 `IAgentSelector` 包装 `Steps/*.cs` agent 步骤" → §架构点 1（`SelectorBackedChatClient`）+ §关键决策 1
- "经 `RecordOutcomeAsync` 消费网关运行结果" → §架构点 2 + §关键决策 2、3、5
✅ 全覆盖。

**2. 占位符扫描**：无 `TBD` / `TODO` / "待定"。每个文件、每个测试都有具体内容。

**3. 内部一致性**：
- `RuntimeEventEntry.Metadata` 字段：网关 `RecordEvent` 写入 `runId`+`stepName`+`score`；侧车 `AgentOutcomeMapper` 从同一处读取。一致。
- `SelectorOptions.InnerClients` 字典：装饰器按选中的 agentId 取 inner client；与 `AvailableAgents[]` 同源。命名一致。
- `Strategos:Selector:Webhook:TokenSecret`（侧车）与 `OpenClaw:RuntimeEvents:Webhook:TokenSecret`（网关）：两端各持一份，互相校验。一致。
- `MafDurableHttpWorkflowRunner.RecordEvent` 既有调用点（`RunAsync`、`RespondAsync`、`GetAsync`）都需要补 `stepName`——已在文件结构里点名。

**4. 作用域检查**：是否太宽？
- 4 个新文件（侧车）、4 个新文件（网关侧）+ 6 个测试文件 = 14 个文件改动 / 新建。
- 这次任务有清晰的边界：装饰器、缓存、映射、接收器 + 网关 webhook + runner RecordEvent 扩展。每个组件都有一句话职责。
- 不动：`Steps/*.cs`、sidecar 工作流编排、`IAgentSelector` 接口本身、Thompson Sampling 算法、belief store 实现、网关 `RuntimeEventStore` 行为。

**5. 歧义检查**：
- "agentId" 在 selector 和 webhook 里的语义：spec 里统一定义为"selector 选出的标识符（如 'mock'、'gpt-4o-mini'）"，与 LLM 模型名绑定。明确。
- "outcome"：成功 / 失败 / 中性（recency-only），三者枚举。明确。
- "neutral outcome" 是否更新 belief：是的，仅作为时间戳记录（`TaskCategoryClassifier` 内部用），不调整 Beta 参数。文中已说明。
- sidecar 关闭时 webhook 行为：sidecar 不监听端口 / 路由未注册——返回连接拒绝，网关重试一次后放弃。明确。

**已知执行期不确定性（非占位，有 fallback）**：
- `Strategos.Selection` 的具体类型 `AgentSelectionContext`、`AgentSelection`、`AgentOutcome` 字段名：执行时由实现者读 `/e/GitHub/strategos/src/Strategos.Selection/` 校对，本文给的字段名基于上下文合理推断。
- `ThompsonSamplingAgentSelector` 构造函数签名（belief store / classifier / logger / seed）需在执行时校对。Strategos 自己的测试 `ThompsonSamplingSelectorTests.cs:30` 给了实例。
- 网关 `RuntimeEventWebhook` 注入点：可能挂在 `RuntimeEventStore.Append` 内部（构造注入 `RuntimeEventWebhook`），或通过现有 `RuntimeMetrics` 旁的 DI 容器。执行时校对 `CoreServicesExtensions.cs` 现有组装顺序。
- `InMemoryBeliefStore` 是否线程安全：默认 `ConcurrentDictionary`，如果内部还有非并发结构需要包锁。执行时校对 `/e/GitHub/strategos/src/Strategos.Infrastructure/Selection/InMemoryBeliefStore.cs`。

---

请审阅此 spec。如果你想调整任何决策（特别是 §关键决策 表中的 8 项），告诉我；如果一切 OK，我就进入 writing-plans 阶段。
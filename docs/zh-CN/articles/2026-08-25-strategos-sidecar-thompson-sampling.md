# OpenClaw × Strategos：从侧车样例到 Thompson Sampling 反馈闭环

> 本文配套阅读：  
> - 设计规格：[`2026-08-20-openclaw-strategos-p0-sidecar-host-design.md`](../../superpowers/specs/2026-08-20-openclaw-strategos-p0-sidecar-host-design.md)（P0 侧车样例）  
> - 设计规格：[`2026-08-21-thompson-sampling-sidecar-feedback-design.md`](../../superpowers/specs/2026-08-21-thompson-sampling-sidecar-feedback-design.md)（P2 Thompson Sampling 反馈）  
> - 父级设计：[`OpenClaw.NET集成Strategos方案.md`](../../zh-CN/OpenClaw.NET集成Strategos方案.md)

## 一、为什么需要侧车

OpenClaw 把"重量级工作流引擎"刻意排除在核心之外——`OpenClaw.Core` 与 `OpenClaw.Gateway`（AOT 发布）只做编排、网关、模型适配；持久化、事件溯源、Saga、可补偿、审批闸门这些概念由外部后端承载。网关通过 `AgentWorkflowRegistry` 注册的 `IAgentWorkflowRunner` 来调用后端，唯一受支持的 Kind 是 `maf-durable-http`——把工作流托管职责完全委派给一个外部 HTTP 服务（参见 [`docs/workflow-backends.md`](../../workflow-backends.md)）。

这条边界带来的好处是 OpenClaw 二进制不会因为引入 Wolverine/Marten/Postgres 而被迫放弃 AOT；代价是"持久化、可恢复、带审批"的工作流必须由外部进程承担。P0 之前，外部进程的范式是 [`samples/OpenClaw.DurableAgentReview`](../../../samples/OpenClaw.DurableAgentReview/Program.cs)——一个内存 Mock，已经塑造了三端点的形状，但没有事件溯源、没有重启恢复、没有 Saga。

P0 的目标正是把"真实持久化"塞进同一组端点形状里：新建样例 [`samples/OpenClaw.StrategosWorkflowHost/`](../../../samples/OpenClaw.StrategosWorkflowHost/)，承载真实的 Strategos Saga 运行时（Wolverine + Marten + PostgreSQL），对外仍说 OpenClaw 既有的 `maf-durable-http` 契约——网关零差量，仅把 `BaseUrl` 从 5095 指到 5097。P0 只证集成可行，不交付 `Kind: strategos-http` 别名、Evidence 互链、Thompson Sampling、宿主 AOT——这些留给 P1/P2/P3。

P2 的目标则是在同一侧车样例上闭合 Thompson Sampling 学习回路：让网关在运行工作流时把每次步骤成败反馈给侧车的 agent selector，让 selector 在下一次选择时更倾向于"历史上成功率高"的 agent。本文聚焦于这条反馈回路的设计与实现。

## 二、整体拓扑

```
                       ┌──────────────────────────────────────┐
                       │ OpenClaw.Gateway  (AOT)               │
                       │                                      │
                       │  AgentWorkflowRegistry (按 Kind 分发) │
                       │   ├─ Kind=maf-durable-http           │
                       │   │    → MafDurableHttpWorkflowRunner│
                       │   │       ├─ RuntimeEventStore(JSONL)│
                       │   │       └─ RuntimeEventWebhook(P2) │
                       │   └─ Kind=strategos-http (P1 已落地)  │
                       │        → StrategosHttpWorkflowRunner │
                       │           └─ 组合 MafDurableHttp...   │
                       │              (仅重打 Kind 标签)       │
                       │                                      │
                       │  BaseUrl = http://127.0.0.1:5097     │
                       └────────────┬─────────────────────────┘
                                    │  POST /api/workflows/.../run
                                    │  GET  /api/workflows/.../status/{runId}
                                    │  POST /api/workflows/.../respond/{runId}
                                    ▼
        ┌───────────────────────────────────────────────────────────┐
        │ OpenClaw.StrategosWorkflowHost  (JIT 样例)                │
        │                                                           │
        │  ┌────────────────┐    ┌────────────────────┐              │
        │  │ 3 端点 (契约)   │───▶│ Strategos Saga     │              │
        │  └────────────────┘    │ (Roslyn 源生成)     │              │
        │                        │  + Wolverine        │              │
        │                  ┌─────┴──────────┐         │              │
        │                  │ IWorkflowStep  │ ── IChatClient (P2 包装)│
        │                  │ 评审者 (注入)   │         │              │
        │                  └────────────────┘         │              │
        │                  ┌────────────────────────┐ │              │
        │                  │ Marten (Postgres)      │◀┘              │
        │                  │ 事件流 + 聚合          │                 │
        │                  └────────────────────────┘                 │
        │                                                           │
        │  ┌──────────────────────┐  ┌──────────────────────────┐    │
        │  │ SelectorBackedChat    │  │ GatewayEventReceiver     │    │
        │  │ Client (P2 IChatClient│  │ POST /runtime-events     │    │
        │  │ 装饰器)               │  │ (P2)                     │    │
        │  └──────────┬───────────┘  └──────────┬───────────────┘    │
        │             │                         │                    │
        │  ┌──────────▼───────────┐  ┌──────────▼───────────────┐    │
        │  │ ThompsonSampling     │  │ AgentOutcomeMapper       │    │
        │  │ AgentSelector +      │◀─│ (P2 纯函数)              │    │
        │  │ InMemoryBeliefStore  │  └──────────────────────────┘    │
        │  └──────────────────────┘                                  │
        │                                                            │
        │  ┌──────────────────────┐                                  │
        │  │ RunIdAgentSelection   │ (P2 (runId, stepName) 缓存)    │
        │  │ Cache (P2)           │                                  │
        │  └──────────────────────┘                                  │
        └────────────────────────────────────────────────────────────┘
                                    │
                  ┌─────────────────┼─────────────────┐
                  ▼                 ▼                 ▼
            ┌──────────┐     ┌──────────────┐   ┌──────────────┐
            │ Mode=Mock│     │ Mode=Direct  │   │ Mode=Gateway │
            │ (固定    │     │ (任意 OpenAI │   │ →127.0.0.1:  │
            │  verdict)│     │  兼容端点)    │   │   18789/v1   │
            │ 默认     │     │              │   │              │
            └──────────┘     └──────────────┘   └──────────────┘
```

关键不变量：

- **网关零差量**：`AgentWorkflowRegistry` 继续接受 `maf-durable-http`，三端点（`POST .../run`、`GET .../status/{runId}`、`POST .../respond/{runId}`）字节级对齐。宿主以 **JIT、非 AOT** 发布——绕开 Wolverine/Marten 尚未完工的 AOT 故事。
- **`runId == saga WorkflowId`**：适配器从 `run_xxx` 解析 saga id，作为 Marten 事件流身份。
- **`PendingInputs` 仅在 `waiting_for_input` 时填充**，匹配 `MafDurableHttpWorkflowRunner` 预期。
- **Provider 密钥不离开网关**：`Mode=Gateway` 下 provider 密钥/预设/TokenJuice 在网关侧单一来源；宿主只持有调用网关 v1 端点的鉴权凭证。
- **默认关闭**：Thompson Sampling 两侧都默认关闭（`Strategos:Selector:Enabled=false`、`OpenClaw:RuntimeEvents:Webhook:Url=""`），关闭时与 P0 行为完全相同。

## 三、P0：把真实持久化塞进三端点形状

### 3.1 评审工作流的 Strategos DSL 定义

工作流主体 [`Workflows/ReviewWorkflow.cs`](../../../samples/OpenClaw.StrategosWorkflowHost/Workflows/ReviewWorkflow.cs) 是事件溯源 Saga，演示了 Strategos 提供的完整工作流拓扑原语：

```csharp
[Workflow("durable-agent-review", Persistence = PersistenceMode.EventSourced)]
public static partial class DurableAgentReviewWorkflowDefinition
{
    public static WorkflowDefinition<ReviewState> Definition =>
        Workflow<ReviewState>
            .Create("durable-agent-review")
            .StartWith<PlanExecutor>()
            .Fork(
                path => path.Then<SecurityReviewer>(),
                path => path.Then<ArchitectureReviewer>(),
                path => path.Then<CostReviewer>())
            .Join<AggregateReviews>()
            .Then<AssessConfidence>(step => step
                .RequireConfidence(0.85)
                .OnLowConfidence(alt => alt.Then<RequestHumanReview>()))
            .AwaitApproval<Operator>(approval => approval
                .WithContextFrom(s => s.AggregatedSummary ?? "Approval required.")
                .WithTimeout(TimeSpan.FromHours(4))
                .OnTimeout(esc => esc.EscalateTo<Admin>(a => a
                    .WithContextFrom(s => "Escalated after approval timeout."))))
            .Then<ExecuteApprovedAction>(step => step.Compensate<RevertApprovedAction>())
            .OnFailure(flow => flow.Then<NotifyFailure>())
            .Finally<EmitAuditTrace>();
}
```

这里演示的能力：

- **Fork / Join**：三评审者并行，独立返回各自的 verdict，再合并到 `AggregateReviews`。
- **置信度闸门**：`AssessConfidence` 返回聚合置信度，`RequireConfidence(0.85)` 在 step-config 上声明——`MockReviewChatClient` 输出 `Confidence=0.8`，因此确定性触发低置信度分支。
- **人在回路**：`AwaitApproval<Operator>` 暂停 Saga，持久化 `Request...ApprovalEvent`；网关的 `respond` 端点恢复 Saga。
- **超时升级**：`OnTimeout` 路由到 `EscalateTo<Admin>`，再开一个 `AwaitApproval<Admin>`。注意这是嵌套在审批 builder 内的（`IApprovalEscalationBuilder`），不是顶层 `Then<>` 步骤。
- **补偿**：`Compensate<RevertApprovedAction>()` 在 step-config 上声明，触发后 saga 进入补偿相位，适配器映射为 `failed`。
- **失败兜底**：`OnFailure` 必须在 `Finally` 之前，`NotifyFailure` 写入失败原因。
- **审计轨迹**：`Finally<EmitAuditTrace>()` 写入 `ExecutionResult`。

### 3.2 事件溯源状态

[`Workflows/ReviewState.cs`](../../../samples/OpenClaw.StrategosWorkflowHost/Workflows/ReviewState.cs) 实现 `IEventSourcedState<ReviewState>`，Marten 单流聚合约定：

```csharp
[WorkflowState]
public sealed record ReviewState : IEventSourcedState<ReviewState>
{
    public Guid Id { get; init; }
    public Guid WorkflowId { get; init; }
    public string UserRequest { get; init; } = "";
    public string Plan { get; init; } = "";
    public IReadOnlyList<ReviewVerdict> Reviews { get; init; } = [];
    public string? AggregatedSummary { get; init; }
    public double AggregateConfidence { get; init; }
    public HumanDecision? Decision { get; init; }
    public string? ExecutionResult { get; init; }
    public string? FailureReason { get; init; }
    public string CurrentPhase { get; init; } = "NotStarted";

    public static ReviewState Create(DurableAgentReviewStarted started) => started.InitialState;

    public ReviewState Apply(PlanExecutorCompleted e) =>
        e.UpdatedState with { CurrentPhase = "ExecutingPlan" };
    // ...每步 Completed 折叠

    public ReviewState ApplyEvent(IProgressEvent evt) => evt switch
    {
        PlanExecutorCompleted c => c.UpdatedState,
        SecurityReviewerCompleted c => c.UpdatedState,
        // ...
        _ => this,
    };
}
```

`{StepClassName}Completed` 事件由 Roslyn 源生成器产出（命名规则来自 `EventsEmitter.cs` 与 `EventSourcedAuditState.Create(EventSourcedHappyStarted)`），事件携带 `UpdatedState` 快照——折叠时直接 adopt 该快照并打上 `CurrentPhase` 印章。

### 3.3 评审者步骤：把 IChatClient 边界暴露给 P2

[`Steps/SecurityReviewer.cs`](../../../samples/OpenClaw.StrategosWorkflowHost/Steps/SecurityReviewer.cs) 是 P2 Thompson Sampling 包装的天然插入点：

```csharp
public sealed class SecurityReviewer(IChatClient chat) : IWorkflowStep<ReviewState>
{
    public async Task<StepResult<ReviewState>> ExecuteAsync(
        ReviewState state, StepContext context, CancellationToken cancellationToken)
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.System, "Return ONLY JSON: {role,verdict,summary,confidence}."),
            new ChatMessage(ChatRole.User, PromptBuilders.Security(state.Plan, state.UserRequest)),
        };
        // ← 这里塞入 (runId, stepName)，P2 装饰器按它做 outcome 关联
        var correlation = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["runId"] = state.Id.ToString(),
                ["stepName"] = context.StepName,
            },
        };
        var response = await chat.GetResponseAsync<ReviewVerdict>(
            messages, options: correlation, cancellationToken: cancellationToken);
        if (!response.TryGetResult(out var verdict) || verdict is null)
            throw new InvalidOperationException("LLM did not return a security verdict.");
        var stamped = verdict with { Role = "security" };
        return StepResult<ReviewState>.WithConfidence(
            state with { Reviews = state.Reviews.Append(stamped).ToList() },
            stamped.Confidence);
    }
}
```

三个关键设计：

1. **`IChatClient` 经主构造函数注入**：源生成器把步骤注册为 `AddTransient<{Step}>()`，DI 注入按调用解析。
2. **`ChatOptions.AdditionalProperties` 携带 `(runId, stepName)`**：这是 P2 outcome 关联的关联键——网关端不知道侧车选了哪个 agent，只能告诉侧车 `(runId, stepName)`，侧车需要回查当时的 agentId。
3. **`StepResult.WithConfidence`**：把 verdict 的 confidence 透传给 saga，供 `RequireConfidence` 判定。

`ArchitectureReviewer` 与 `CostReviewer` 结构相同，三个评审者并行时仅看到 fork 前的 state，各自只追加自己的 verdict，不互相干扰。

### 3.4 契约适配器：三端点 → Saga 命令/状态

[`Adapters/DurableHttpAdapter.cs`](../../../samples/OpenClaw.StrategosWorkflowHost/Adapters/DurableHttpAdapter.cs) 把 HTTP 形状翻译成 Marten 事件流读取与 Wolverine 命令发布：

```csharp
public sealed class DurableHttpAdapter(IMessageBus bus, IDocumentStore store)
{
    public async Task<AgentWorkflowRunResult> StartRunAsync(
        string workflowName, AgentWorkflowRequest request, CancellationToken ct)
    {
        var runId = $"run_{Guid.NewGuid():N}";
        if (!TryParseSagaId(runId, out var sagaId))
            throw new InvalidOperationException("Invalid runId format.");
        var initial = new ReviewState { Id = sagaId, WorkflowId = sagaId, UserRequest = request.Input };
        var cmd = new StartDurableAgentReviewCommand(sagaId, initial);
        await bus.PublishAsync(cmd, ct);  // Wolverine 事务发件箱
        return new AgentWorkflowRunResult { /* ... */ };
    }

    public async Task<AgentWorkflowRunSnapshot?> GetStatusAsync(
        string workflowName, string runId, CancellationToken ct)
    {
        if (!TryParseSagaId(runId, out var sagaId)) return null;
        await using var query = store.QuerySession();
        var state = await query.LoadAsync<ReviewState>(sagaId);  // 内联快照
        if (state is null) return null;
        var status = PhaseStatusMap.ToOpenClawStatus(state.CurrentPhase);
        var events = await query.Events.FetchStreamAsync(sagaId);  // 事件流审计
        // ...
    }
}
```

读侧用 `LoadAsync`（内联快照，快）+ `FetchStreamAsync`（审计流，完整）；状态映射 [`Adapters/PhaseStatusMap.cs`](../../../samples/OpenClaw.StrategosWorkflowHost/Adapters/PhaseStatusMap.cs) 是纯函数——`"AwaitingApproval" -> "waiting_for_input"` 等，把 Strategos 内部相位翻成 OpenClaw `AgentWorkflowStatuses` 常量（小写 `queued`/`running`/`waiting_for_input`/`completed`/`failed`/`cancelled`）。

写侧用 `IMessageBus.PublishAsync`：Wolverine 的发件箱模式保证命令与状态变更在同一事务内提交——Saga 启动后无需网关做幂等去重。

审批恢复走 `ResumeOperatorApprovalCommand([SagaIdentity] Guid WorkflowId, ApprovalDecision Decision, string? SelectedOptionId, string? Instructions)`——`[SagaIdentity]` 让 Wolverine 按 `WorkflowId` 路由到正确的 saga 实例，`ApprovalDecision` 是 `Strategos.Models` 枚举（`Approved/Rejected/Deferred`）。

### 3.5 LLM 模式：贡献者首次运行零密钥

[`Configuration/LlmMode.cs`](../../../samples/OpenClaw.StrategosWorkflowHost/Configuration/LlmMode.cs) 把 `LlmOptions` 解析为三种 `IChatClient`：

| 模式 | 网络出口 | 凭证来源 | 何时用 |
|---|---|---|---|
| `Mock`（默认） | 无 | 无 | 贡献者首次运行；CI kill-restart |
| `DirectOpenAI` | Provider API | `env:OPENAI_API_KEY` | 不跑 OpenClaw 直连 |
| `BackThroughGateway` | `127.0.0.1:18789/v1` | `env:OPENCLAW_GATEWAY_KEY` | 全链路，provider 密钥留网关 |

`OPENCLAW_GATEWAY_KEY` 是网关鉴权凭证（让宿主能进网关的门），不是 provider 密钥（provider 密钥永远留在网关）。Mock 模式固定返回 `ReviewVerdict{Verdict="review-required", Confidence=0.8}`——工作流确定性走到 `AssessConfidence`（0.8 < 0.85 → `AwaitApproval`），即便没有 LLM，审批闸门也被走过。

P0 不实现 Direct/Gateway 的具体 OpenAI 兼容 `IChatClient`——OpenClaw 与 Strategos 都不引 OpenAI SDK；Mock 模式覆盖全部 P0 验收，Direct/Gateway 的实现作为 README 标注的"需用户补全"项。

### 3.6 Fork 并发冲突（核验发现，关键）

三评审者并行 append 到同一 Marten 事件流，会触发乐观并发冲突（`ConcurrentUpdateException` / `EventStreamUnexpectedMaxEventIdException`）。[`Program.cs`](../../../samples/OpenClaw.StrategosWorkflowHost/Program.cs) 必须配置重试：

```csharp
opts.OnException(ex => ex is Marten.Exceptions.ConcurrentUpdateException
    || ex.GetType().Name.Contains("EventStreamUnexpected", StringComparison.Ordinal))
    .RetryWithCooldown(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(800));
```

缺此配置 Fork 路径会 stall saga——这是 Strategos 的真实生产配置（核验自 `EventSourcedHostFixture.cs:86-101`），不是 P0 假设。

### 3.7 Kill-Restart 测试：唯一能证明"状态恢复"的验收

P0 的核心验收测试 [`KillRestartTests`](../../../samples/OpenClaw.StrategosWorkflowHost.Tests/KillRestartTests.cs) 走完完整生命周期：

```
1. docker compose up -d postgres strategos-host
2. POST /run → 拿 runId, status=queued→running
3. 轮询 GET /status 直到 status=waiting_for_input
4. docker compose kill strategos-host          (模拟崩溃)
5. docker compose up -d strategos-host         (重启 — Marten 持有事件流)
6. GET /status/{runId} → status=waiting_for_input  ← 关键断言
7. POST /respond/{runId} {approved:true} → status=completed
8. 断言: 第 6 步的 Events 数组包含崩溃前的事件
```

第 6 步是关键——没有它，重启测试只证明"宿主起来了"，不证明"状态恢复了"。这正是 P0 与内存版样例的本质区别：内存 Mock 永远不会丢失 Saga，但只要进程一 kill，所有 saga 状态归零。

## 四、P2：Thompson Sampling 反馈闭环

P2 在 P0 之上加一层"学习回路"：让 agent selector 知道历史成败，下次选择时倾向成功概率高的 agent。

### 4.1 设计目标与边界

| 目标 | 边界（明确不做） |
|---|---|
| 用 `IAgentSelector` 包装 `Steps/*.cs` agent 步骤 | 不重写 reviewer 步骤 |
| 经 `RecordOutcomeAsync` 消费网关运行结果 | 不动 JSONL 形状（webhook 仅是 Append 的镜像） |
| 闭环 Thompson Sampling 学习 | 不引入 `Kind: strategos-http` 别名（P1） |
| 不破坏现有工作流 | 默认关闭，关闭时行为不变 |

### 4.2 三层组件

#### 4.2.1 `SelectorBackedChatClient` —— IChatClient 装饰器

[`Adapters/SelectorBackedChatClient.cs`](../../../samples/OpenClaw.StrategosWorkflowHost/Adapters/SelectorBackedChatClient.cs) 把"选 agent + 路由"放在 `IChatClient` 边界之外，不侵入评审者代码：

```csharp
public sealed class SelectorBackedChatClient : IChatClient
{
    private readonly IAgentSelector _selector;
    private readonly RunIdAgentSelectionCache _cache;
    private readonly IChatClient _defaultClient;
    private readonly IReadOnlyDictionary<string, IChatClient> _innerClients;
    private readonly SelectorOptions _options;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var inner = await ResolveInnerClientAsync(messages, options, cancellationToken);
        return await inner.GetResponseAsync(messages, options, cancellationToken);
    }

    private async Task<IChatClient> ResolveInnerClientAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        var context = BuildContext(messages);
        var selectionResult = await _selector.SelectAgentAsync(context, cancellationToken);

        if (!selectionResult.IsSuccess || selectionResult.Value is null)
        {
            _logger.LogWarning("Selector returned failure: {Error}. Falling back to default client.", selectionResult.Error);
            return _defaultClient;
        }

        var selected = selectionResult.Value.SelectedAgentId;
        if (!_innerClients.TryGetValue(selected, out var inner))
        {
            _logger.LogWarning("Selected agent {AgentId} has no InnerClient registered. Falling back to default client.", selected);
            return _defaultClient;
        }

        // 只在 (runId, stepName) 都存在时缓存，否则 outcome 永远回查不到
        if (TryGetCorrelationKey(options, out var runId, out var stepName))
        {
            _cache.Set(runId, stepName, selected, _options.TaskCategory);
        }

        return inner;
    }
}
```

**三个设计原则：**

1. **失败全部回退到 default client**：selector 返回 `Result.Failure`、选中的 agentId 没注册、inner client 抛错——所有路径都不抛错到评审者，而是返回 `defaultClient`。这是 P0"评审者步骤不变"的延续：selector 故障不能拖垮工作流可用性。
2. **关联键缺失不缓存**：如果 `ChatOptions.AdditionalProperties` 里没有 `runId` 或 `stepName`，缓存无意义（outcome 事件拿不到关联键，永远回查 miss），索性不缓存。
3. **`AgentSelectionContext` 故意不带 `WorkflowId`**：侧车的关联以 `(runId, stepName)` 为键，`WorkflowId` 用 `Guid.Empty` 占位——Strategos selector 不需要 saga 身份，它只需要"任务描述 + 可选 agent 列表"。

#### 4.2.2 `RunIdAgentSelectionCache` —— 侧车本地 FIFO 缓存

[`Adapters/RunIdAgentSelectionCache.cs`](../../../samples/OpenClaw.StrategosWorkflowHost/Adapters/RunIdAgentSelectionCache.cs) 是 outcome 关联的核心：

```csharp
public sealed class RunIdAgentSelectionCache
{
    private readonly ConcurrentDictionary<string, CachedSelection> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _insertionOrder = new();
    private readonly object _evictionLock = new();
    private readonly int _capacity;

    public RunIdAgentSelectionCache(int capacity = 10_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    public CachedSelection Set(string runId, string stepName, string agentId, string taskCategory)
    {
        var key = GetKey(runId, stepName);  // "{runId}{stepName}"
        var selection = new CachedSelection(agentId, taskCategory, DateTimeOffset.UtcNow);

        if (_entries.TryAdd(key, selection))
        {
            lock (_evictionLock)
            {
                _insertionOrder.Enqueue(key);
                EvictIfOverCapacity();
            }
        }
        else
        {
            _entries[key] = selection;  // 覆盖
        }
        return selection;
    }

    public CachedSelection? TryGet(string runId, string stepName)
        => _entries.TryGetValue(GetKey(runId, stepName), out var selection) ? selection : null;

    private void EvictIfOverCapacity()
    {
        while (_insertionOrder.Count > _capacity)
        {
            var oldest = _insertionOrder.Dequeue();
            _entries.TryRemove(oldest, out _);
        }
    }
}

public readonly record struct CachedSelection(string AgentId, string TaskCategory, DateTimeOffset SelectedAt);
```

**两个设计选择：**

- **容量驱动 FIFO 淘汰**（默认 10000 条）：超过容量从 `_insertionOrder` 队列头淘汰；`ConcurrentDictionary` 保证读路径无锁。Thompson Sampling 对"没有 outcome 的 selection"天然宽容——信念停在先验不更新，淘汰是安全的。
- **键是 `{runId}{stepName}` 字符串拼接**：简单到极致，避免自定义 `IEqualityComparer<CachedKey>`。`(runId, stepName)` 是唯一键，重复 set 覆盖值但保留位置（避免恶意刷键把队列填爆）。

**为什么是本地内存？** 侧车进程崩溃后历史选择丢失，feedback 沉默——这是 Thompson Sampling 的天然属性，可接受。如果上 Redis/Postgres 做持久化缓存，复杂度（序列化、过期、跨实例一致性）远超收益；且 selector 的本意就是"快速试错、长程学习"，单进程寿命足以累积足够样本。

#### 4.2.3 `AgentOutcomeMapper` —— 纯函数

[`Adapters/AgentOutcomeMapper.cs`](../../../samples/OpenClaw.StrategosWorkflowHost/Adapters/AgentOutcomeMapper.cs) 把 `RuntimeEventEntry` 翻译成 `(agentId, taskCategory, AgentOutcome)`：

```csharp
public sealed class AgentOutcomeMapper
{
    private static readonly HashSet<string> CompletedActions = new(StringComparer.Ordinal)
    {
        "run_completed",
        "run_failed",
    };

    public MappedOutcome? Map(RuntimeEventEntry entry, CancellationToken ct = default)
    {
        if (!string.Equals(entry.Component, "workflow", StringComparison.OrdinalIgnoreCase))
            return null;

        var metadata = entry.Metadata;
        if (metadata is null
            || !metadata.TryGetValue("runId", out var runId)
            || !metadata.TryGetValue("stepName", out var stepName))
        {
            _logger.LogDebug("Skipping runtime event {EventId}: missing runId or stepName in metadata.", entry.Id);
            return null;
        }

        if (!CompletedActions.Contains(entry.Action))
            return null;  // run_started / response_sent — 没有 pass/fail 信号

        var cached = _cache.TryGet(runId, stepName);
        if (cached is null)
        {
            _logger.LogDebug("Skipping runtime event {EventId}: no cached selection for runId={RunId} stepName={StepName}.",
                entry.Id, runId, stepName);
            return null;
        }

        var success = string.Equals(entry.Action, "run_completed", StringComparison.OrdinalIgnoreCase);
        var outcome = success ? AgentOutcome.Succeeded() : AgentOutcome.Failed();

        return new MappedOutcome(cached.Value.AgentId, cached.Value.TaskCategory, outcome);
    }
}

public readonly record struct MappedOutcome(string AgentId, string TaskCategory, AgentOutcome Outcome);
```

**为什么是纯函数？** 单元测试 6 个 case 都是"给定 entry，期望返回 mapped 或 null"，零状态、零 IO。映射策略：

- **组件过滤**：只关心 `Component == "workflow"`；`tool`/`session`/其他组件一律忽略。
- **动作过滤**：`run_started` 与 `response_sent` 没有成败信号，直接跳过；`run_completed` 与 `run_failed` 才映射。
- **关联键缺失 → 跳过**：`runId` 或 `stepName` 缺失时返回 null（不抛错）。
- **缓存 miss → 跳过**：侧车未记录选择（早于 sidecar 启动、或已被淘汰）时静默跳过。
- **缺失原因用 Debug 日志**：运行时高频路径，不需要 Warning/Error 级别刷屏。

#### 4.2.4 `GatewayEventReceiver` —— HTTP 入口

[`Adapters/GatewayEventReceiver.cs`](../../../samples/OpenClaw.StrategosWorkflowHost/Adapters/GatewayEventReceiver.cs) 处理入站 webhook：

```csharp
public sealed class GatewayEventReceiver
{
    private const int DedupCapacity = 10_000;
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);

    public async Task<IResult> HandleAsync(HttpContext context, CancellationToken cancellationToken)
    {
        // 1. Bearer token 校验（不匹配 → 401）
        if (_expectedBearerToken is not null)
        {
            var auth = context.Request.Headers.Authorization.ToString();
            if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                || !FixedTimeEquals(auth.Substring("Bearer ".Length).Trim(), _expectedBearerToken))
            {
                _logger.LogWarning("Rejecting /runtime-events: bearer token mismatch.");
                return Results.Unauthorized();
            }
        }

        // 2. 反序列化
        RuntimeEventEntry? entry;
        try { entry = await JsonSerializer.DeserializeAsync<RuntimeEventEntry>(context.Request.Body, ...); }
        catch (JsonException ex) { return Results.BadRequest(); }

        // 3. 按 entry.Id 去重
        if (!_seen.TryAdd(entry.Id, 0)) return Results.Ok();  // 已处理过
        TrimSeenIfOverCapacity();

        // 4. 映射 → 调 RecordOutcomeAsync
        var mapped = _mapper.Map(entry, cancellationToken);
        if (mapped is null) return Results.Ok();  // 被 mapper 过滤

        try
        {
            var outcome = await _selector.RecordOutcomeAsync(
                mapped.Value.AgentId, mapped.Value.TaskCategory, mapped.Value.Outcome, cancellationToken);
            if (!outcome.IsSuccess)
                _logger.LogWarning("RecordOutcomeAsync returned failure for agent {AgentId}: {Error}",
                    mapped.Value.AgentId, outcome.Error);
        }
        catch (Exception ex)
        {
            // selector 抛错必须吞掉，不能让 HTTP 循环中断
            _logger.LogWarning(ex, "RecordOutcomeAsync threw for agent {AgentId}; dropping outcome.", mapped.Value.AgentId);
        }

        return Results.Ok();
    }

    private static bool FixedTimeEquals(string a, string b) { /* 常量时间比较，防侧信道 */ }
}
```

**设计要点：**

- **按 `entry.Id` 去重**：网关在 `RecordEvent` 里用 `evt_{Guid:N}` 前 20 位作为 id；侧车收到重复 id 直接 200 OK 返回，不影响 webhook 重试语义。
- **去重集合 FIFO 淘汰**：`_seen` 超过 10000 条时把超出的旧 key 移除（不追求严格 LRU，并发场景下取近似即可）。
- **Bearer token 走 `FixedTimeEquals`**：常量时间字符串比较，防止侧信道时序攻击推断 token。
- **selector 抛错被吞**：HTTP 循环必须继续处理后续事件，单次 outcome 投递失败不能中断整条流水线。
- **路由仅在 `Enabled=true` 时挂载**：`SelectorServerBootstrap.MapSelectorEventEndpoint` 检查配置后决定是否 `MapPost("/runtime-events", ...)`；关闭时该路径返回 404 而不是 401——404 告诉网关"端点不存在，重试无意义"，更早停止浪费。

### 4.3 网关侧：`RuntimeEventWebhook` 出站客户端

[`src/OpenClaw.Gateway/RuntimeEventWebhook.cs`](../../../src/OpenClaw.Gateway/RuntimeEventWebhook.cs) 是网关端的镜像客户端，把 `RuntimeEventStore.Append` 的同一份 entry 推到侧车：

```csharp
public sealed class RuntimeEventWebhook
{
    public async Task SendAsync(RuntimeEventEntry entry, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Url)) return;

        var attempts = 0;
        while (true)
        {
            attempts++;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _options.Url);
                if (!string.IsNullOrWhiteSpace(_options.BearerToken))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.BearerToken);

                var json = JsonSerializer.Serialize(entry, CoreJsonContext.Default.RuntimeEventEntry);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                using var response = await _http.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode) return;

                var status = (int)response.StatusCode;
                if (status is 401 or 403)
                {
                    // 配置错误，不重试
                    _logger.LogWarning("RuntimeEventWebhook returned {StatusCode}; webhook will not retry (configuration error).", status);
                    return;
                }
                if (status is >= 500 || status is 429)
                {
                    // 服务器故障，重试一次
                    if (attempts >= 2) { /* 放弃 */ return; }
                    await Task.Delay(_options.RetryDelayMs, cancellationToken);
                    continue;
                }
                // 其他 4xx：侧车拒绝 payload，重试无用
                _logger.LogDebug("RuntimeEventWebhook returned {StatusCode} for entry {EventId}; dropping.", status, entry.Id);
                return;
            }
            catch (HttpRequestException ex) { /* 连接失败，重试一次 */ }
            catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested) { return; }
        }
    }
}
```

**重试策略表：**

| 失败 | 行为 |
|---|---|
| 5xx / 429 | 重试 1 次，间隔 `RetryDelayMs`（默认 1s） |
| 401 / 403 | 配置错误，停发 |
| 其他 4xx | 侧车拒绝 payload，重试无用 |
| `HttpRequestException` | 连接失败，重试 1 次 |
| 2xx | 完成 |

webhook 失败**不影响 JSONL 写入**——`MafDurableHttpWorkflowRunner.RecordEvent` 先调 `_events.Append(entry)`，再 fire-and-forget 推 webhook。即便侧车挂了，durable 记录仍在；恢复时手动 replay 即可。

### 4.4 网关侧扩展：`RecordEvent` 增加 `stepName` + `score`

[`src/OpenClaw.Gateway/Workflows/MafDurableHttpWorkflowRunner.cs`](../../../src/OpenClaw.Gateway/Workflows/MafDurableHttpWorkflowRunner.cs) 在原有 `RecordEvent(runId, action, status, summary)` 基础上增加 `stepName` + `score` 两个参数，并把"步骤级事件"作为独立的 webhook 触发点：

```csharp
private void RecordEvent(string runId, string action, string status, string summary,
    string? stepName = null, double? score = null)
{
    var metadata = new Dictionary<string, string>
    {
        ["backendId"] = BackendId,
        ["workflowId"] = WorkflowId,
        ["runId"] = runId,
        ["status"] = status
    };
    if (!string.IsNullOrWhiteSpace(stepName)) metadata["stepName"] = stepName;
    if (score.HasValue) metadata["score"] = score.Value.ToString(CultureInfo.InvariantCulture);

    var entry = new RuntimeEventEntry { /* Component = "workflow", Action = action, Metadata = metadata, ... */ };
    _events.Append(entry);

    if (_webhook is not null)
    {
        // fire-and-forget，webhook 自己处理重试与吞错
        _ = Task.Run(async () =>
        {
            try { await _webhook.SendAsync(entry).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "RuntimeEventWebhook.SendAsync threw for entry {EventId}.", entry.Id); }
        });
    }
}
```

而步骤级事件由 `RecordStepEvents` 从 saga 的 `*Completed/*Failed` 事件类型中拆出 `stepName`：

```csharp
private void RecordStepEvents(string runId, IReadOnlyList<AgentWorkflowEvent> workflowEvents, string status)
{
    foreach (var evt in workflowEvents)
    {
        if (string.Equals(evt.Type, "status", StringComparison.OrdinalIgnoreCase)) continue;  // 跳过 StreamAsync 注入的 status
        if (string.IsNullOrWhiteSpace(evt.Type)) continue;
        if (!_lastRecordedStepEventIds.TryAdd(evt.Id, 0)) continue;  // 按 evt.Id 去重

        var action = ResolveStepAction(evt.Type);
        if (action is null) continue;

        var bareStepName = StripStepSuffix(evt.Type);  // "SecurityReviewerCompleted" → "SecurityReviewer"
        RecordEvent(
            runId,
            action,
            status,
            string.IsNullOrWhiteSpace(evt.Summary) ? $"Step '{bareStepName}' completed." : evt.Summary,
            stepName: bareStepName);
    }
}

private static string? ResolveStepAction(string eventType)
{
    if (eventType.EndsWith("Completed", StringComparison.Ordinal)) return "run_completed";
    if (eventType.EndsWith("Failed", StringComparison.Ordinal) || eventType.EndsWith("Faulted", StringComparison.Ordinal))
        return "run_failed";
    return null;
}
```

**为什么拆出 stepName？** 网关看到的是 Strategos saga 发出的"`{StepClassName}Completed`"事件类型——为了侧车 outcome 关联需要拆出裸 stepName（`SecurityReviewerCompleted` → `SecurityReviewer`），与 `Steps/*.cs` 里 `context.StepName` 完全对齐。

**为什么 webhook 在 `RecordStatus` 与 `RecordStepEvents` 都触发？** 工作流级 status 变化（`waiting_for_input`/`completed`/`failed`）与步骤级事件（每个评审者完成 / 失败）都产生一条 `RuntimeEventEntry`。侧车只关心 `run_completed` / `run_failed` + 关联键命中，所以工作流级 status 会被 mapper 过滤掉（动作不对应 `CompletedActions`），不会污染 belief store。

### 4.5 接线（`Program.cs`）

[`samples/OpenClaw.StrategosWorkflowHost/Program.cs`](../../../samples/OpenClaw.StrategosWorkflowHost/Program.cs) 把这两层串起来：

```csharp
// LLM-mode-aware IChatClient. Mock by default; other modes throw at startup (see LlmMode).
var llmOptions = builder.Configuration.GetSection("Llm").Get<LlmOptions>() ?? new LlmOptions();
var llmLogger = NullLogger<LlmMode>.Instance;
var chat = LlmClientFactory.Create(llmOptions, llmLogger);

// Selector surface: when Strategos:Selector:Enabled is true, replaces the
// direct IChatClient registration with a SelectorBackedChatClient that
// routes calls through Thompson Sampling. When disabled (default), this
// is a no-op for the IChatClient registration — same behavior as P0/P2.
SelectorServerBootstrap.AddSelectorServer(opts.Services, builder.Configuration, chat);
if (builder.Configuration.GetValue($"{SelectorOptions.SectionName}:Enabled", false))
{
    opts.Services.AddSingleton<IChatClient>(sp => sp.GetRequiredService<SelectorBackedChatClient>());
}
else
{
    opts.Services.AddSingleton(chat);
    opts.Services.AddSingleton<IChatClient>(chat);
}
```

`SelectorServerBootstrap.AddSelectorServer` 注册 selector 单例、缓存、mapper、receiver、装饰器；`MapSelectorEventEndpoint` 仅在 `Enabled=true` 时挂载 `POST /runtime-events`。

### 4.6 端到端调用栈

**选 agent：**

```
ReviewWorkflow ─▶ SecurityReviewer.ExecuteAsync(state, ct)
                            └─▶ chat.GetResponseAsync<ReviewVerdict>(messages, options, ct)
                                  └─▶ SelectorBackedChatClient.GetResponseAsync
 ├─ Build AgentSelectionContext { WorkflowId=Guid.Empty, StepName="StrategosChat",
 │                                 TaskDescription=first user msg 截断,
 │                                 AvailableAgents=["mock","mock-fast"] }
 ├─ selector.SelectAgentAsync(context)
 │     on failure → log warning, return default inner client
 ├─ 查 RunIdAgentSelectionCache.Set((runId, stepName), agentId, category, ts)
 ├─ Pick agent-specific inner client
 │     "mock"         → MockReviewChatClient
 │     "mock-fast"    → NSubstitute fake
 │     "gpt-4o-mini"  → DirectOpenAI 客户端（如已配置）
 └─ inner.GetResponseAsync<ReviewVerdict>(messages, options, ct)
```

**outcome 反馈：**

```
gateway MafDurableHttpWorkflowRunner 完成
 └─▶ RecordStepEvents(...)  (每个 *Completed/*Failed 步骤都触发)
      └─▶ _events.Append(new RuntimeEventEntry {
             Component = "workflow",
             Action    = "run_completed" | "run_failed",
             Metadata  = { runId, stepName="SecurityReviewer", status, backendId, workflowId }
         })
 ├─ (现有) JSONL 写入 —— 与今天完全相同
 └─ (新增) RuntimeEventWebhook → POST {sidecar-url}/runtime-events
        Authorization: Bearer <shared token>
        Body = RuntimeEventEntry (CoreJsonContext.Default.RuntimeEventEntry 序列化)

sidecar GatewayEventReceiver.HandleAsync(ctx, ct)
 ├─ 校验 token（401 if mismatch）
 ├─ 反序列化 RuntimeEventEntry
 ├─ 跳过 id 重复（in-memory LRU 10k）
 ├─ AgentOutcomeMapper.Map(entry) → (agentId, taskCategory, AgentOutcome)?
 ├─ selector.RecordOutcomeAsync(agentId, category, outcome)
 │     on failure → log warning, drop
 └─ 200 OK
```

## 五、关键设计决策与权衡

| # | 决策点 | 选择 | 备选 | 理由 |
|---|---|---|---|---|
| 1 | 选 agent 的接缝位置 | `SelectorBackedChatClient` 装饰 `IChatClient` | (a) 改写每个 reviewer 步骤；(b) `IAgentStepExecutor` | 装饰器对评审者代码零侵入；`IChatClient` 边界天然分离"模型调用"与"模型选择" |
| 2 | outcome 来源 | HTTP webhook `POST /runtime-events` | (a) 共享 JSONL；(b) Postgres 直读 | webhook 是 Append 的镜像，不破坏 durable 记录；Postgres 直读把侧车绑到网关 DB |
| 3 | 触发事件 | 复用 `Component="workflow"` + `Action ∈ {run_completed, run_failed}` | (a) 新增 `Component="AgentSelection"` 事件；(b) 推全部事件 | 复用现有事件源零网关新增类型；过滤减小污染 |
| 4 | 失败策略 | 选 agent 失败→回退默认；outcome 失败→日志+丢弃 | (a) 抛错；(b) 配置化策略 | 故障隔离到边界外：selector 故障不能拖垮工作流可用性 |
| 5 | agentId 传递 | 侧车本地 `RunIdAgentSelectionCache` 关联 `(runId, stepName)` | (a) 元数据塞 agentId；(b) 请求体传递 | 网关不知道侧车选了谁；侧车自行维护关联 |
| 6 | 端口 | 复用 8080，新增 `/runtime-events` 路由 | 新端口 | 新端口会引入部署面（health check、ingress、端口冲突） |
| 7 | 鉴权 | 共享 bearer token，走 SecretResolver | mTLS | sidecar 与网关通常同主机/同集群网络，token 足够；mTLS 是 P3+ 的工程 |
| 8 | 关闭默认 | 两端 `Enabled=false` / `Url=""` | 默认开 | 默认开会污染所有现有工作流的 belief store，破坏现有 dev 用例 |

### 5.1 "失败回退"为何是必须的

装饰器在 `ResolveInnerClientAsync` 里三次可能失败（selector 返回 `Result.Failure`、选中的 agentId 没注册、inner client 抛错），全部走"返回 `defaultClient`"而不是抛错。原因：

1. **工作流可用性不应绑死 selector**：Thompson Sampling 是优化层，不是正确性层；selector 故障时仍要走完工作流。
2. **`defaultClient` 必然可用**：`MockReviewChatClient` 是确定性实现，永远能返回 verdict；`DirectOpenAI` 模式失败时至少保留 mock 回退——这就是"fallback"语义的价值。
3. **审计轨迹完整**：即便 selector 全程失败，工作流仍然产出 `*Completed` 事件，`NotifyFailure` / `EmitAuditTrace` 照常执行。

### 5.2 "装饰器"为何优于"步骤包装"

| 维度 | `SelectorBackedChatClient` 装饰 | 包装每个 reviewer 步骤 |
|---|---|---|
| 侵入性 | 0（评审者代码不变） | 5 个 reviewer × DI 改造 |
| 流式 API 支持 | 透传 `GetStreamingResponseAsync` | 需分别实现 |
| 配置可逆性 | 关闭 `Enabled` 即恢复 P0 | 需回滚步骤 DI |
| 单测覆盖 | 装饰器 5 测试 + E2E 1 测试 | 每步骤 × N 测试 |

装饰器把策略与机制分离：评审者只懂"我要 chat"，装饰器懂"我要先选 agent 再 chat"。这是 OpenClaw 一贯的边界纪律——核心/网关不感知 Strategos，评审者不感知 selector。

### 5.3 为何 JSONL 仍是 durable 记录，webhook 仅是镜像

`MafDurableHttpWorkflowRunner.RecordEvent` 先调 `_events.Append(entry)`，再 fire-and-forget 推 webhook。JSONL 写入失败抛 warning，webhook 失败也抛 warning，但**两者互不影响**：

- JSONL 写入失败：监控告警 + 后续重放依赖旁路
- Webhook 失败：belief store 少一次 outcome 更新，但 belief 停在先验不崩溃

如果颠倒顺序（先 webhook 再 Append），webhook 失败会导致 JSONL 也不写——durable 记录丢失。这是 P0/P2 设计的硬约束：**JSONL 是 durable 记录，webhook 是 best-effort 镜像**。

### 5.4 端到端测试：信念真的更新了

[`tests/Integration/SelectorEndToEndTests.cs`](../../../samples/OpenClaw.StrategosWorkflowHost.Tests/Integration/SelectorEndToEndTests.cs) 验证闭环：

```csharp
[Fact]
public async Task Selection_Then_Outcome_Webhook_Updates_Thompson_Belief_By_One()
{
    // 用真实的 ThompsonSamplingAgentSelector + InMemoryBeliefStore，randomSeed: 42
    var beliefStore = new InMemoryBeliefStore(beliefLogger);
    var selector = new ThompsonSamplingAgentSelector(beliefStore, new TaskCategoryClassifier(), selectorLogger, randomSeed: 42);
    var cache = new RunIdAgentSelectionCache();

    // 两个 inner client：一个 good、一个 bad
    var decorator = new SelectorBackedChatClient(selector, cache, goodInner, ..., options, ...);

    // 1. 第一次 chat 调用：selector 选 agent，decorator 路由，cache 记录
    var firstResponse = await decorator.GetResponseAsync(...);
    var selected = cache.TryGet("run-e2e-1", "SecurityReviewer");

    // 2. 记录前信念观察数
    var beforeBelief = (await beliefStore.GetBeliefAsync(selected.Value.AgentId, "General", ct)).Value;
    var beforeObservations = beforeBelief.ObservationCount;

    // 3. 启动 sidecar 测试服务器，发 webhook
    var mapper = new AgentOutcomeMapper(cache, ...);
    var receiver = new GatewayEventReceiver(mapper, selector, expectedBearerToken: "secret", ...);
    using var host = new HostBuilder().ConfigureWebHost(web => { /* MapPost /runtime-events */ }).Build();
    await host.StartAsync();

    var entry = new RuntimeEventEntry { /* Component="workflow", Action="run_completed", ... */ };
    using var req = new HttpRequestMessage(HttpMethod.Post, "/runtime-events") { Content = JsonContent.Create(entry) };
    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
    using var resp = await client.SendAsync(req, ct);

    // 4. 断言：信念观察数 +1，success outcome 把 mean 拉高
    Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    var afterBelief = (await beliefStore.GetBeliefAsync(selected.Value.AgentId, "General", ct)).Value;
    Assert.Equal(beforeObservations + 1, afterBelief.ObservationCount);
    Assert.True(afterBelief.Mean >= beforeBelief.Mean);
}
```

测试用真实 `ThompsonSamplingAgentSelector` + `InMemoryBeliefStore`，固定 `randomSeed: 42` 保证可重复（与 Strategos 自家 `ThompsonSamplingSelectorTests.cs:42` 一致）。整条链路走完：

1. 装饰器选 agent + 缓存写入 → 1 次 selection
2. HTTP webhook 触发 `RecordOutcomeAsync` → 1 次 outcome 更新
3. 信念观察数从 0 → 1，mean 因 success outcome 上升

这是 P2 的核心验收：**"运行经验真的进了 selector 的脑子"**，而不是仅仅日志里写一行"recorded outcome"。

## 六、测试策略与质量门

| 测试文件 | 测试数 | 验证点 |
|---|---|---|
| `SelectorBackedChatClientTests.cs` | 5 | 选 agent 后路由到对应 inner；失败时回退；空候选时回退；流式 API 同样行为；缺 `ChatOptions.AdditionalProperties` 不缓存 |
| `RunIdAgentSelectionCacheTests.cs` | 4 | 写入/读取；FIFO 淘汰；并发写安全（`ConcurrentDictionary`）；miss 返回 null |
| `AgentOutcomeMapperTests.cs` | 6 | `run_completed` → success；`run_failed` → failure；`run_started`/`response_sent` → null；非 `workflow` 组件忽略；缺 `runId`/`stepName` 返回 null；缓存 miss 返回 null |
| `GatewayEventReceiverTests.cs` | 5 | 有效 entry 接受并调 `RecordOutcomeAsync`；token 不匹配返 401；按 `id` 去重；非 `workflow` 组件忽略；`RecordOutcomeAsync` 抛错不中断后续事件 |
| `Integration/SelectorEndToEndTests.cs` | 1 | 端到端闭环：装饰器选 → fake chat → 模拟 webhook → 信念观察数 +1 |
| `RuntimeEventWebhookTests.cs`（网关） | 4 | 配置后触发；URL 空时跳过；5xx 重试；body 字段（`component`/`action`/`metadata.runId/stepName`）正确 |

**测试侧的选择器**：单元测试用 `StubAgentSelector`（总是返回固定 agentId、记录 `RecordOutcomeAsync` 调用于断言），端到端测试用真实 `ThompsonSamplingAgentSelector` + `InMemoryBeliefStore`。

**LlmMode 集成**：`Mock` 模式下默认客户端是 `MockReviewChatClient`，`AvailableAgents=["mock"]`，selector 永远选 `mock`——Thompson Sampling 在跑但观察不到差异（同一 agent 反复成功/失败，信念曲线不动）。集成测试用 NSubstitute fake 替换 inner client 驱动算法：好客户端成功/坏客户端失败的信念对比。

**测试侧立场**：所有测试都不直接依赖 Strategos selector 实现细节——通过 `IAgentSelector` 接口契约测试，这意味着 selector 实现可替换（`ThompsonSamplingAgentSelector` → `UCB1AgentSelector` → `RandomAgentSelector`）而不破坏测试。这是接口隔离的收益。

## 七、P1：网关注册表按 Kind 分发（已落地）

P0 阶段网关只支持 `maf-durable-http` 一种 kind；一旦宿主样例稳定，P1 把 `strategos-http` 提升为一等后端类型——配置里写哪种 Kind，网关就调度哪种 runner。当前 [`AgentWorkflowRegistry`](../../../src/OpenClaw.Gateway/Workflows/AgentWorkflowRegistry.cs) 已经按 Kind 分发：

```csharp
internal sealed class AgentWorkflowRegistry : IDisposable
{
    private readonly Dictionary<string, IAgentWorkflowRunner> _runners;

    public AgentWorkflowRegistry(
        GatewayConfig config,
        RuntimeEventStore events,
        ILoggerFactory loggerFactory,
        RuntimeEventWebhook? webhook = null)
    {
        _runners = new Dictionary<string, IAgentWorkflowRunner>(StringComparer.OrdinalIgnoreCase);
        if (!config.Workflows.Enabled) return;

        foreach (var (backendId, backendConfig) in config.Workflows.Backends)
        {
            if (string.IsNullOrWhiteSpace(backendId) || !backendConfig.Enabled) continue;

            var kind = string.IsNullOrWhiteSpace(backendConfig.Kind)
                ? AgentWorkflowBackendKinds.MafDurableHttp
                : backendConfig.Kind.Trim();

            _runners[backendId.Trim()] = kind switch
            {
                AgentWorkflowBackendKinds.MafDurableHttp => new MafDurableHttpWorkflowRunner(
                    backendId.Trim(), backendConfig, events, webhook,
                    loggerFactory.CreateLogger<MafDurableHttpWorkflowRunner>()),
                AgentWorkflowBackendKinds.StrategosHttp => new StrategosHttpWorkflowRunner(
                    backendId.Trim(), backendConfig, events, webhook,
                    loggerFactory.CreateLogger<StrategosHttpWorkflowRunner>()),
                _ => throw new InvalidOperationException(
                    $"Unsupported workflow backend kind '{kind}' for backend '{backendId}'.")
            };
        }
    }
    // ...
}
```

而 [`StrategosHttpWorkflowRunner`](../../../src/OpenClaw.Gateway/Workflows/StrategosHttpWorkflowRunner.cs) 走的是组合而非继承——因为 `MafDurableHttpWorkflowRunner` 是 `internal sealed`，无法继承：

```csharp
internal sealed class StrategosHttpWorkflowRunner : IAgentWorkflowRunner, IDisposable
{
    private readonly MafDurableHttpWorkflowRunner _inner;

    public StrategosHttpWorkflowRunner(
        string backendId,
        WorkflowBackendConfig config,
        RuntimeEventStore events,
        RuntimeEventWebhook? webhook,
        ILogger<StrategosHttpWorkflowRunner> logger)
    {
        _inner = new MafDurableHttpWorkflowRunner(
            backendId, config, events, webhook,
            NullLogger<MafDurableHttpWorkflowRunner>.Instance);
        BackendId = _inner.BackendId;
        WorkflowId = _inner.WorkflowId;
    }

    public AgentWorkflowBackendSummary GetSummary()
    {
        // 重打 Kind 标签: 内部仍走 maf-durable-http 协议，但 summary 上报 "strategos-http"
        var innerSummary = _inner.GetSummary();
        return new AgentWorkflowBackendSummary
        {
            Id = innerSummary.Id,
            Kind = AgentWorkflowBackendKinds.StrategosHttp,  // ← 关键:覆盖
            WorkflowName = innerSummary.WorkflowName,
            DisplayName = innerSummary.DisplayName,
            Enabled = innerSummary.Enabled,
        };
    }

    public Task<AgentWorkflowRunResult> RunAsync(...)
        => _inner.RunAsync(...);
    public Task<AgentWorkflowRunSnapshot> GetAsync(...)
        => _inner.GetAsync(...);
    public Task<AgentWorkflowRunSnapshot> RespondAsync(...)
        => _inner.RespondAsync(...);
    public IAsyncEnumerable<AgentWorkflowEvent> StreamAsync(...)
        => _inner.StreamAsync(...);
    public void Dispose() => _inner.Dispose();
}
```

### 7.1 P1 的设计动机

为什么需要 `strategos-http` 这个独立 Kind？线上契约字节级一致（仍走 `maf-durable-http` 三端点形状），但**对外语义**不一样：

- **观察性**：summary/events/runtime metadata 上报 Kind=`"strategos-http"`，运维/UI 可以区分"通用 durable-http 后端"与"Strategos 持久化后端"。
- **可演化**：未来 Strategos 独有协议特性（如查询本体、检索 Evidence Bundle）可以叠加在 `"strategos-http"` runner 上，不污染通用 `"maf-durable-http"` 路径。
- **零侵入**：已部署的 `maf-durable-http` 配置无需改动；新增后端类型仅多一行注册表分支（`switch` 增加一条 arm）。

### 7.2 P1 与 P2 的协作

P2 的 webhook 注入点对两个 Kind 一视同仁：`AgentWorkflowRegistry` 构造时拿到 `RuntimeEventWebhook?`，把它传给两个 runner（`MafDurableHttpWorkflowRunner` 直接持有；`StrategosHttpWorkflowRunner` 通过 `_inner` 间接持有）。这意味着：

- 配置 `Kind=maf-durable-http` + `OpenClaw:RuntimeEvents:Webhook:Url=...` → webhook 直发侧车
- 配置 `Kind=strategos-http` + 同上 → 同一份 webhook 行为，只是 summary 上 Kind 字段为 `"strategos-http"`

P0/P2 设计保持的"接缝纪律"在 P1 落地后仍然成立——`StrategosHttpWorkflowRunner` 是**纯标签层**，所有 HTTP/JSONL/webhook 行为来自 `MafDurableHttpWorkflowRunner`。

### 7.3 P1 未交付项

`StrategosHttpWorkflowRunner.GetSummary()` 仅在 Kind 字段上重打标签。它**不**额外提供：

- Strategos 专有的 summary 字段（如 saga 类型、Wolverine handler 列表、Marten 投影）
- Strategos 专有的运行时 API（如查询当前 saga 状态、列出 in-flight workflows）
- Strategos 专有的 Evidence Bundle 端点（这些跟随 P2a 一起落地）

这些都属于 P1.1+ 的范围——当前 P1 严格遵循"组合而非扩展"原则，避免在网关侧复制 maf-durable-http 的实现细节。

## 八、未来工作与边界

| 阶段 | 范围 | 与本文关系 |
|---|---|---|
| **P1.1** | `StrategosHttpWorkflowRunner` 暴露 Strategos 专有 summary/运行时端点 | 在 P1 标签层之上扩展，不动 `MafDurableHttpWorkflowRunner` |
| **P2a**（已部分落地） | Evidence Bundle ↔ Marten 互链；`status` 响应的 `OutputPayload`/`Events` 与 `EmitAuditTrace` 步骤关联 | 适配器层即接缝；`FetchStreamAsync` 已就绪 |
| **P2b**（已落地） | Ontology MCP App：把宿主的本体 MCP 服务器注册为 OpenClaw MCP App | `OntologyServerBootstrap` + `OntologyAppManifestWriter` |
| **P3** | 宿主 AOT 发布；进程内嵌 | 等待 Wolverine/Marten AOT 完工 |
| **P4+** | mTLS 鉴权替换 bearer token；Redis-backed selection cache（跨进程持久化） | 当前规模无需 |

## 九、经验总结

P0 + P2 这一对组合展示了"接缝纪律 + 反馈回路"如何在不破坏 AOT 边界的前提下，把学习能力引入工作流：

- **接缝纪律**：网关只懂 `maf-durable-http`，侧车说同样契约；评审者只懂 `IChatClient`，装饰器在边界外选 agent。任何一层都可以独立替换，不拖动其他层。
- **失败隔离**：selector/webhook/cache 的故障都通过"回退默认 / 静默跳过 / 日志丢弃"三种策略被吸收，不污染工作流可用性。
- **镜像而非主导**：JSONL 仍是 durable 记录，webhook 是 best-effort 镜像；Append 与 Send 的顺序确保 webhook 失败不影响 JSONL。
- **默认关闭**：所有 Thompson Sampling / webhook 配置默认关，关闭时与 P0/P1 行为完全一致——给贡献者零冲击的演进路径。
- **可观测**：信念观察数 / Mean / observation count 全部可读，端到端测试断言 `ObservationCount + 1` 把"运行经验进脑子"这件事变成可验证的事实。

这套设计模式——**侧车承载真实持久化、装饰器承载策略选择、HTTP webhook 承载反馈回路、JSONL 仍是 durable 主记录**——可以推广到其他 agent 学习场景：RAG 检索器在线学习、规划器策略更新、工具选择 Thompson Sampling 都可套用同一框架。

---

## 附录 A：文件清单

### 侧车（samples/OpenClaw.StrategosWorkflowHost/）

| 文件 | 角色 | 阶段 |
|---|---|---|
| `Program.cs` | WebApplication + UseWolverine + 3 端点 + IChatClient 注册 + Selector 接线 | P0 + P2 |
| `Configuration/LlmMode.cs` | Mock/DirectOpenAI/BackThroughGateway 三模式 + IChatClient 工厂 | P0 |
| `Configuration/MockReviewChatClient.cs` | 固定 verdict mock 客户端 | P0 |
| `Configuration/SelectorOptions.cs` | Thompson Sampling 配置：Enabled/AvailableAgents/TaskCategory/InnerClients/CacheSize/Webhook | P2 |
| `Configuration/SelectorServerBootstrap.cs` | 接线：注册 selector/cache/mapper/receiver/decorator + MapSelectorEventEndpoint | P2 |
| `Configuration/OntologyGraphFactory.cs` / `OntologyOptions.cs` / `OntologyServerBootstrap.cs` / `Adapters/OntologyAppManifest*.cs` | P2b MCP App | P2b |
| `Workflows/ReviewState.cs` | 事件溯源状态 + ApplyEvent 折叠 | P0 |
| `Workflows/ReviewWorkflow.cs` | Strategos DSL：Fork/Join/RequireConfidence/AwaitApproval/Compensate/Finally | P0 |
| `Workflows/Models/ReviewVerdict.cs` / `HumanDecision.cs` | DTO | P0 |
| `Workflows/ApproverMarker.cs` | `Operator` / `Admin` 自声明 marker | P0 |
| `Steps/PlanExecutor.cs` / `SecurityReviewer.cs` / `ArchitectureReviewer.cs` / `CostReviewer.cs` / `AggregateReviews.cs` / `AssessConfidence.cs` / `ExecuteApprovedAction.cs` / `RevertApprovedAction.cs` / `EmitAuditTrace.cs` / `NotifyFailure.cs` / `RequestHumanReview.cs` / `PromptBuilders.cs` | 手写 `IWorkflowStep<ReviewState>` 步骤类 | P0 |
| `Adapters/DurableHttpAdapter.cs` | 三端点 ↔ Saga 命令/状态 | P0 |
| `Adapters/PhaseStatusMap.cs` | Strategos 相位 → OpenClaw 状态（纯函数） | P0 |
| `Adapters/PendingInputBuilder.cs` | `AwaitingApproval` → `AgentWorkflowPendingInput` | P0 |
| `Adapters/EvidenceBundleParser.cs` | Evidence 解析（P2a） | P2a |
| `Adapters/SelectorBackedChatClient.cs` | IChatClient 装饰器：选 agent + 路由 | P2 |
| `Adapters/RunIdAgentSelectionCache.cs` | `(runId, stepName)` 内存缓存 | P2 |
| `Adapters/AgentOutcomeMapper.cs` | `RuntimeEventEntry` → `(agentId, category, outcome)` 纯函数 | P2 |
| `Adapters/GatewayEventReceiver.cs` | `POST /runtime-events` 端点 | P2 |
| `tests/SelectorBackedChatClientTests.cs` | 装饰器 5 测试 | P2 |
| `tests/RunIdAgentSelectionCacheTests.cs` | 缓存 4 测试 | P2 |
| `tests/AgentOutcomeMapperTests.cs` | 映射 6 测试 | P2 |
| `tests/GatewayEventReceiverTests.cs` | 接收器 5 测试 | P2 |
| `tests/Integration/SelectorEndToEndTests.cs` | 端到端闭环 1 测试 | P2 |

### 网关（src/OpenClaw.Gateway/）

| 文件 | 角色 | 阶段 |
|---|---|---|
| `Workflows/MafDurableHttpWorkflowRunner.cs` | 既有 `maf-durable-http` 后端 runner；扩展 `RecordEvent` 增加 `stepName`/`score`；新增 `RecordStepEvents` 从 saga `*Completed/*Failed` 事件拆 stepName | P2 |
| `Workflows/AgentWorkflowRegistry.cs` | 后端类型注册：按 Kind 分发 maf-durable-http / strategos-http | P0 + P1 |
| `Workflows/StrategosHttpWorkflowRunner.cs` | P1 别名 runner（组合而非继承） | P1（已落地） |
| `RuntimeEventStore.cs` | JSONL Append 与 Query（不变） | P0 |
| `RuntimeEventWebhook.cs` | 出站 webhook 客户端（5xx 重试、401/403 停发） | P2 |
| `Composition/RuntimeEventWebhookExtensions.cs` | `AddRuntimeEventWebhook` DI 接线 | P2 |
| `Composition/CoreServicesExtensions.cs` | `AddRuntimeEventWebhook` 调用点 | P2 |

## 附录 B：核验记录

P0 的 8 项 Strategos API 待核验项经 4 个并行研究 agent + 直接读 `E:/GitHub/strategos` 源码全部解决：

1. **NuGet 包 + 命名空间** — `LevelUp.Strategos.*`（MinVer，2.10.0）；C# 命名空间根 `Strategos`。
2. **`Workflow<T>.Create()` 构建 API** — 返回 `IWorkflowBuilder<TState>`；`Fork(params Action<IForkPathBuilder>[])`、`Join<T>` 在 `IForkJoinBuilder`、`RequireConfidence(double)`、`OnLowConfidence(Action<IBranchBuilder>)`、`Compensate<T>` 在 `IStepConfiguration`、`OnFailure` 须在 `Finally` 前。
3. **步骤模型** — `AgentStepBase<TState,TResult>` sealed 不可继承；步骤是手写 `IWorkflowStep<TState>`（`ExecuteAsync(TState, StepContext, CancellationToken) -> Task<StepResult<TState>>`），主构造函数注入 DI；`StepContext` 非泛型无 `Services`/`RaiseAsync`。
4. **状态/事件/归约** — 事件溯源模式用 `IEventSourcedState<TState>.ApplyEvent` + Marten 单流聚合约定；`[Append]`/`[Merge]` 用于文档模式（**无 `[Snapshot]`**）。
5. **Wolverine/Marten** — `builder.Host.UseWolverine` + `AddMarten(...).IntegrateWithWolverine().ApplyAllDatabaseChangesOnStartup()` + 生成器 `Add{Pascal}Workflow()`；`IMessageBus.PublishAsync` 发命令；读 `LoadAsync` + `FetchStreamAsync`（非 `AggregateStreamAsync`）。
6. **审批恢复** — `Resume{Point}ApprovalCommand([SagaIdentity] Guid WorkflowId, ApprovalDecision, string? SelectedOptionId, string? Instructions)`；`ApprovalDecision` 枚举（`Approved/Rejected/Deferred`）。
7. **OpenClaw 常量** — `AgentWorkflowStatuses.*` 小写（`queued`/`running`/`waiting_for_input`/`completed`/`failed`/`cancelled`）；`AgentWorkflowBackendKinds.MafDurableHttp="maf-durable-http"`；`CoreJsonContext` 注册全部工作流 DTO；`SecretResolver` 支持 `env:`/`raw:`（无 `ref:`）。
8. **`OperationStatusResponse`** — `OpenClaw.Core.Models`，字段 `Success`/`Message`/`Error`/`Mode`（mutable `set`）。404 响应复用此类型。

P2 的关联项：

- **`Strategos.Selection`** 类型（`AgentSelectionContext`、`AgentSelection`、`AgentOutcome`、`TaskCategoryClassifier`、`TaskCategory`、`InMemoryBeliefStore`、`ThompsonSamplingAgentSelector`）— 全部按 `LevelUp.Strategos.Infrastructure.Selection` 命名空间消费。
- **`IAgentSelector`** 接口位于 `Strategos.Abstractions`，方法签名 `SelectAgentAsync(AgentSelectionContext, CancellationToken) -> Task<Result<AgentSelection>>` 与 `RecordOutcomeAsync(string agentId, string taskCategory, AgentOutcome outcome, CancellationToken) -> Task<Result<Unit>>`。
# P0 规格：OpenClaw.StrategosWorkflowHost Sidecar 样例

> **状态：** 设计（brainstorming 完成，待生成实施计划）
> **父设计：** [`docs/zh-CN/OpenClaw.NET集成Strategos方案.md`](../../zh-CN/OpenClaw.NET集成Strategos方案.md)
> **档位：** P0 — Sidecar 宿主样例，**零网关代码改动**
> **日期：** 2026-08-20

## 1. 目标

新建样例 `samples/OpenClaw.StrategosWorkflowHost/`，承载一个真实的 Strategos saga 运行时（Wolverine + Marten + PostgreSQL），并对外说 OpenClaw.NET 既有的 `maf-durable-http` 工作流后端契约——**不改 `OpenClaw.Core`、`OpenClaw.Gateway`、`AgentWorkflowRegistry` 的任何一行**。

这证明集成设计的核心命题：接缝（`IAgentWorkflowRunner` + 三端点 HTTP 契约）已经存在，Strategos sidecar 可以填满它，让 OpenClaw 获得持久化、事件溯源、可补偿、带审批闸门的工作流能力。

**P0 只证明集成可行。** 它不交付 `Kind: "strategos-http"`（P1）、Evidence Bundle 互链 / MCP 本体 App / Thompson Sampling 回灌（P2）、进程内嵌（P3）。

## 2. 背景

- OpenClaw.NET 刻意把重量级工作流引擎排除在核心之外（[`docs/ARCHITECTURE_BOUNDARIES.md`](../../ARCHITECTURE_BOUNDARIES.md)："Core should not become a product-specific workflow engine"）。唯一受支持的后端类型是 `maf-durable-http`，通过 HTTP 委派给外部持久化宿主（[`docs/workflow-backends.md`](../../workflow-backends.md)）。
- 网关的 `AgentWorkflowRegistry`（[`src/OpenClaw.Gateway/Workflows/AgentWorkflowRegistry.cs`](../../../src/OpenClaw.Gateway/Workflows/AgentWorkflowRegistry.cs)）对任何非 `maf-durable-http` 的 kind 直接抛错。P0 复用这个确切的 kind 字符串——网关零差量。
- 现有样例 `samples/OpenClaw.DurableAgentReview`（[`Program.cs`](../../../samples/OpenClaw.DurableAgentReview/Program.cs)）是一个内存版 mock，已经塑造了三个契约端点的形状。P0 宿主把真实持久化塞进同样的端点。
- Strategos 是一个 .NET 10 库：用流式 C# DSL 声明工作流，由 Roslyn 源生成器在编译期降级为类型安全的 Wolverine saga，落在 Marten 事件流上。MVP 已完成（据集成设计引用 `docs/design.md`）。Wolverine/Marten 的 AOT 支持官方标注"进行中"（[wolverinefx.io AOT](https://wolverinefx.io/guide/aot)），故宿主以 **JIT、非 AOT** 发布——符合 OpenClaw 的 AOT/JIT 边界纪律。

## 3. 架构与拓扑

P0 新增 **三个组件，外加一个可选组件**：

```
                 ┌───────────────────────────────────┐
                 │  OpenClaw.Gateway  (AOT, 不变)     │
                 │  配置: Kind=maf-durable-http       │
                 │  BaseUrl=http://127.0.0.1:5097    │
                 └──────────────┬────────────────────┘
                                │  POST/GET/POST (既有契约)
                                ▼
   ┌────────────────────────────────────────────────────────┐
   │  OpenClaw.StrategosWorkflowHost  (JIT 样例, 新增)        │
   │                                                        │
   │  ┌─────────────┐   ┌──────────────┐   ┌─────────────┐  │
   │  │ ASP.NET Core│──▶│ Strategos     │──▶│  Marten     │  │
   │  │ 3 端点      │   │ saga 运行时   │   │ (Postgres)  │  │
   │  │ (契约)      │   │ + Wolverine   │   │ 事件存储    │  │
   │  └─────────────┘   └──────┬───────┘   └─────────────┘  │
   │                            │                            │
   │                     ┌──────┴───────┐                    │
   │                     │ AgentSteps   │                    │
   │                     │ (IChatClient)│                    │
   │                     └──────┬───────┘                    │
   └────────────────────────────┼───────────────────────────┘
                                │
              ┌─────────────────┼─────────────────┐
              ▼                 ▼                 ▼
        ┌──────────┐     ┌──────────────┐   ┌──────────────┐
        │ Mode=Mock│     │ Mode=Direct  │   │ Mode=Gateway │
        │ (固定    │     │ (任意 OpenAI │   │ →127.0.0.1:  │
        │  verdict)│    │  兼容端点)    │   │   18789/v1   │
        │ 默认     │     │              │   │ (provider    │
        └──────────┘     └──────────────┘   │  密钥留在网关)│
                                             └──────────────┘
   (可选第4组件: Postgres, 经 docker compose 启动)
```

**关键不变量（已对照代码核验）：**
- **网关零差量。** `AgentWorkflowRegistry` 继续接受 `maf-durable-http`；P0 只把 `BaseUrl` 从 5095 指到 5097。改动在用户配置里，不在仓库代码里。
- 三端点（`POST .../run`、`GET .../status/{runId}`、`POST .../respond/{runId}`）与 `MafDurableHttpWorkflowRunner` 发出的、以及 `samples/OpenClaw.DurableAgentReview` 服务的内容字节级一致。
- 宿主以 **JIT、非 AOT** 发布——绕开未完工的 Wolverine/Marten AOT 故事，符合 AOT/JIT 边界（"JIT、动态或重插件表面应显式且可选"）。
- `Mode=Gateway` 下 provider 密钥（OpenAI/Claude 等的 API key）不离开网关：宿主注册一个指向网关 OpenAI 兼容端点的 `IChatClient`，provider 密钥/预设/TokenJuice 在网关侧单一来源。注意：宿主调用网关 v1 端点时需要一个**网关鉴权凭证**（区别于 provider 密钥），见 §5 的 `OPENCLAW_GATEWAY_KEY` 说明。默认 `Mode=Mock` 意味着贡献者 `dotnet run` 不需要任何密钥。

**P0 明确不做：** `Kind: strategos-http` 别名（P1）；Evidence/Marten 互链、本体 MCP App、Thompson Sampling 回灌（P2）；进程内嵌（P3）。

## 4. 项目布局

分层单职责结构（选定方案），C# 14，文件作用域命名空间 + 主构造函数（匹配 [`CONTRIBUTING.md`](../../../CONTRIBUTING.md) 代码风格）。每个文件单一职责：

```
samples/OpenClaw.StrategosWorkflowHost/
├─ OpenClaw.StrategosWorkflowHost.csproj      # net10.0, JIT 发布; PackageRefs: LevelUp.Strategos.*, WolverineFX, Marten
├─ Program.cs                                  # ~100 LOC: 启动 DI + MapPost/MapGet/MapPost 三端点
├─ appsettings.json                            # 3 个 LLM 模式 profile + Postgres 连接串占位
├─ appsettings.Development.json                # Mock 默认 + localhost:5432 postgres
├─ docker-compose.yml                          # 2 服务: postgres + strategos-host (gateway 可选, 用户自跑)
├─ Dockerfile                                   # 宿主多阶段 JIT 构建
├─ README.md                                    # 3 条运行路径 (Mock / Direct / Gateway) + kill-restart 步骤
│
├─ Configuration/
│  └─ LlmMode.cs                                # 枚举: Mock | DirectOpenAI | BackThroughGateway; 读 "Strategos:Llm:Mode"
│
├─ Workflows/
│  ├─ ReviewState.cs                            # [WorkflowState] 不可变 record + [Append]/[Snapshot] 归约器; IWorkflowState
│  ├─ ReviewWorkflow.cs                         # static Create() → Workflow<ReviewState> DSL
│  ├─ Models/
│  │  ├─ ReviewVerdict.cs                       # 评审输出 record (Role, Verdict, Summary, Confidence)
│  │  └─ HumanDecision.cs                       # Approved/Rejected + ActorId + Comment
│  └─ Reviewers/
│     ├─ PlanExecutor.cs                        # AgentStep: 计划步骤
│     ├─ SecurityReviewer.cs                    # AgentStep: 安全评审 (IChatClient)
│     ├─ ArchitectureReviewer.cs                # AgentStep: 架构评审 (IChatClient)
│     ├─ CostReviewer.cs                        # AgentStep: 成本评审 (IChatClient)
│     ├─ AggregateReviews.cs                    # Join 目标: 合并三路 verdict → AggregatedSummary
│     ├─ AssessConfidence.cs                    # RequireConfidence(0.85) 闸门; OnLowConfidence → AwaitApproval<Operator>
│     ├─ ExecuteApprovedAction.cs               # 终端执行; Compensate<RevertApprovedAction>
│     ├─ RevertApprovedAction.cs                # 补偿处理器
│     ├─ EmitAuditTrace.cs                      # .Finally: 把事件流摘要写入 OutputPayload
│     └─ NotifyFailure.cs                       # OnFailure: 失败通知步骤
│
├─ Adapters/
│  ├─ DurableHttpAdapter.cs                    # run/status/respond 契约映射 (相位↔六态, PendingInputs 填充)
│  ├─ PhaseStatusMap.cs                         # Strategos 相位枚举 → OpenClaw 状态 (§4.2 表的代码形式)
│  └─ PendingInputBuilder.cs                    # AwaitingApproval → AgentWorkflowPendingInput{PortId,Payload} 构造器
│
└─ tests/ (同级测试项目)
   ├─ OpenClaw.StrategosWorkflowHost.Tests.csproj
   ├─ PhaseStatusMapTests.cs                    # 单元: 每个相位映射到唯一六态之一
   ├─ PendingInputBuilderTests.cs                # 单元: 审批上下文 → 端口 payload 形状
   ├─ MockReviewChatClientTests.cs                # 单元: mock 客户端返回可解析 JSON, 三角色产出不同 verdict
   ├─ DurableHttpAdapterTests.cs                 # 集成: run/status/respond 往返 (WebApplicationFactory + Mock LLM + Marten testcontainer)
   └─ KillRestartTests.cs                       # CI 自动化 kill-restart (§7)
```

**布局理由：** `Program.cs` 保持薄；`Adapters/` 承载契约逻辑，P1 的 `StrategosHttpWorkflowRunner` 将与之对照；`Reviewers/*.cs` 一步一文件，使 P2 的 Thompson Sampling 包装器有显而易见的插入点；`PhaseStatusMap.cs` 是纯函数，单元覆盖 100%。

匹配现有样例的 C# 习惯用法：`WebApplication.CreateSlimBuilder`、经 `ConfigureHttpJsonOptions` 接入 `CoreJsonContext.Default`（即便宿主是 JIT，AOT 安全的 DTO 仍走 `OpenClaw.Core` 的序列化器往返）。

## 5. LLM 模式配置

三个模式 profile，启动时解析为一个 `IChatClient` 注册。

### `appsettings.json`

```json
{
  "ConnectionStrings": {
    "Postgres": "Host=localhost;Port=5432;Database=strategos;Username=strategos;Password=strategos"
  },
  "Strategos": {
    "Llm": {
      "Mode": "Mock",
      "Direct": {
        "Endpoint": "https://api.openai.com/v1",
        "ApiKeySecret": "env:OPENAI_API_KEY",
        "Model": "gpt-4o-mini"
      },
      "Gateway": {
        "Endpoint": "http://127.0.0.1:18789/v1",
        "ApiKeySecret": "env:OPENCLAW_GATEWAY_KEY",
        "Model": "deepseek-v4-flash"
      }
    },
    "Approval": {
      "TimeoutHours": 4,
      "OnTimeout": "EscalateToAdmin"
    }
  }
}
```

### 模式选项

```csharp
public enum LlmMode { Mock, DirectOpenAI, BackThroughGateway }

public sealed class LlmOptions
{
    public LlmMode Mode { get; init; } = LlmMode.Mock;
    public LlmEndpointOptions Direct { get; init; } = new();
    public LlmEndpointOptions Gateway { get; init; } = new();
}

public sealed class LlmEndpointOptions
{
    public string Endpoint { get; init; } = "";
    public string? ApiKeySecret { get; init; }
    public string Model { get; init; } = "";
}
```

### DI 注册

```csharp
builder.Services.AddSingleton(sp =>
{
    var opts = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
    return opts.Mode switch
    {
        LlmMode.Mock               => new MockReviewChatClient(),
        LlmMode.DirectOpenAI        => BuildOpenAiClient(opts.Direct),
        LlmMode.BackThroughGateway   => BuildOpenAiClient(opts.Gateway),
        _ => throw new InvalidOperationException($"Unknown LLM mode '{opts.Mode}'.")
    };
});

static IChatClient BuildOpenAiClient(LlmEndpointOptions o)
{
    if (string.IsNullOrWhiteSpace(o.Endpoint))
        throw new InvalidOperationException("LLM Endpoint is required for non-Mock modes.");
    var key = SecretResolver.Resolve(o.ApiKeySecret, ...);  // 复用 OpenClaw.Core.Security
    if (string.IsNullOrEmpty(key))
        throw new InvalidOperationException($"LLM ApiKeySecret '{o.ApiKeySecret}' resolved empty.");
    return new OpenAIClient(new ApiKeyCredential(key),
        new OpenAIClientOptions { Endpoint = new Uri(o.Endpoint) })
        .AsChatClient(o.Model);
}
```

| 模式 | 何时用 | 网络出口 | 凭证来源 | 评审者行为 |
|---|---|---|---|---|
| **Mock**（默认） | 贡献者首次运行；CI kill-restart | 无 | 无 | 固定 `ReviewVerdict{Verdict="review-required", Confidence=0.8}`——工作流确定性地到达 `AssessConfidence`（0.8 < 0.85 → `AwaitApproval`），即便没有 LLM，审批闸门也被走过 |
| **DirectOpenAI** | 不跑 OpenClaw、直连真实 LLM | Provider API | `env:OPENAI_API_KEY` | 真实 LLM verdict |
| **BackThroughGateway** | 全链路：provider 密钥/预设/TokenJuice 留在网关 | `127.0.0.1:18789/v1`（仅回环） | `env:OPENCLAW_GATEWAY_KEY` | 评审者调网关；网关调真实 provider |

**关键设计细节：**
- `MockReviewChatClient` 是一个真正的 `IChatClient` 实现——它把*适配器路径*（Strategos agent step → `IChatClient` → verdict 解析）端到端走通，所以 Mock 不是空操作；它与真实路径的验证方式不同。`SecretResolver.Resolve` 复用自 `OpenClaw.Core.Security`（与 `MafDurableHttpWorkflowRunner` 同源），不引入新的密钥处理。模式在启动时解析一次（单例）；错误配置在 DI 构建期失败（"不支持的形态应快速失败"）。

**`OPENCLAW_GATEWAY_KEY` 语义澄清（避免误读）：**
此处的 `OPENCLAW_GATEWAY_KEY` 是**网关鉴权凭证**——宿主调用网关 v1 端点时用于身份认证的令牌，**不是 provider 密钥**（OpenAI/Claude 等的 API key 永远留在网关，不出网关进程）。两者职责分离：网关鉴权凭证让宿主"能进网关的门"；provider 密钥在网关内部决定"调哪个上游模型"。若网关对回环 v1 端点不强制鉴权（P0 可接受的简化），则 README 允许省略此凭证；否则贡献者需在本地设置该环境变量。README 将明确这一点。

## 6. 工作流定义（Strategos DSL）

### 状态（`ReviewState.cs`）

```csharp
using LevelUp.Strategos;  // [WorkflowState], IWorkflowState, [Append]/[Snapshot] — 待核验命名空间

[WorkflowState]
public record ReviewState : IWorkflowState
{
    public Guid WorkflowId { get; init; }
    public string UserRequest { get; init; } = "";
    public string Plan { get; init; } = "";

    [Append]  // 归约器重放 ReviewCompleted 事件, 累积
    public ImmutableList<ReviewVerdict> Reviews { get; init; } = [];

    public string? AggregatedSummary { get; init; }
    public decimal AggregateConfidence { get; init; }

    [Snapshot]
    public HumanDecision? Decision { get; init; }

    public string? ExecutionResult { get; init; }
    public string? FailureReason { get; init; }
}
```

### 定义（`ReviewWorkflow.cs`）

```csharp
public static class ReviewWorkflow
{
    public static WorkflowDefinition<ReviewState> Create() =>
        Workflow<ReviewState>
            .Create("durable-agent-review")
            .StartWith<PlanExecutor>()
            .Fork(
                flow => flow.Then<SecurityReviewer>(),
                flow => flow.Then<ArchitectureReviewer>(),
                flow => flow.Then<CostReviewer>())
            .Join<AggregateReviews>()
            .Then<AssessConfidence>(step => step
                .RequireConfidence(0.85m)
                .OnLowConfidence(flow => flow
                    .AwaitApproval<Operator>(options => options
                        .WithTimeout(TimeSpan.FromHours(4))
                        .OnTimeout(f => f.Then<EscalateToAdmin>()))))
            .Then<ExecuteApprovedAction>()
                .Compensate<RevertApprovedAction>()
            .Finally<EmitAuditTrace>()
            .OnFailure(flow => flow.Then<NotifyFailure>());
}
```

此定义证明 Strategos 的 MVP 能力拓扑，且不过度扩展：`Fork`/`Join`（并行三评审 + 合并）；`RequireConfidence` + `OnLowConfidence` → `AwaitApproval`（置信度闸门 + 人在回路 + 超时升级路由）；`.Compensate<>`（失败回滚）；`.Finally<>` + `.OnFailure`（审计轨迹 + 失败通知）。

**`EscalateToAdmin` 是一个桩步骤**，不是完整的二级审批闸门——P0 只验证升级*路由*存在，不构建多级审批（按工作流范围决策推迟到 P2c）。它记录"已升级，等待管理员"并重新进入 `AwaitApproval`，使 kill-restart 测试能覆盖一个非默认审批者路径。

### 评审者步骤（`Reviewers/*.cs`）

```csharp
public sealed class SecurityReviewer : AgentStep<ReviewState>   // 待核验基类名
{
    public override async Task ExecuteAsync(StepContext<ReviewState> ctx, CancellationToken ct)
    {
        var chat = ctx.Services.GetRequiredService<IChatClient>();   // 待核验 DI 访问模式
        var prompt = BuildSecurityPrompt(ctx.State.Plan, ctx.State.UserRequest);
        var response = await chat.CompleteAsync(prompt, ct);     // 待核验 IChatClient 表面
        var verdict = ReviewVerdict.Parse("security", response.Message.Text);
        await ctx.RaiseAsync(new ReviewCompleted(verdict));     // 待核验事件上报 API; [Append] 归约器由源生成器发射
    }
}
```

评审者步骤经 `ctx.Services`（scoped DI）解析 `IChatClient`，不走构造函数注入——Strategos 源生成器发射这些处理器，构造函数签名受其发射模式约束。`ReviewCompleted` 是源生成的事件，触发 `[Append]` 归约器；归约器是发射的，非手写。

**不做：** 不注册 `IAgentSelector`/Thompson Sampling（P2c）；不注册 `BudgetGuard`/`WorkflowBudget`/`LoopDetector`（Strategos Infrastructure-pack 强化项，非正确性所需）；不用 RAG DSL / 上下文装配 DSL（Strategos 延迟的消费者责任特性；P0 用简单字符串拼装 prompt）。

## 7. 契约适配器与数据流

### 相位 → 状态映射（`PhaseStatusMap.cs`，纯函数）

```csharp
public static class PhaseStatusMap
{
    public static string ToOpenClawStatus(string phase) => phase switch
    {
        "NotStarted"        => AgentWorkflowStatuses.Queued,
        "AwaitingApproval"   => AgentWorkflowStatuses.WaitingForInput,
        "Completed"          => AgentWorkflowStatuses.Completed,
        "Failed"            => AgentWorkflowStatuses.Failed,
        "Compensated"        => AgentWorkflowStatuses.Failed,   // 补偿完成 → failed 并附原因
        "Cancelled"         => AgentWorkflowStatuses.Cancelled,
        _ when phase.StartsWith("Executing", StringComparison.Ordinal) => AgentWorkflowStatuses.Running,
        _ => AgentWorkflowStatuses.Running  // 未知相位安全回落为 running, 记日志
    };
}
```

状态经 `MafDurableHttpWorkflowRunner.NormalizeStatus` 转小写，故 `AgentWorkflowStatuses.*` 常量必须是这些小写字符串（`queued`、`running`、`waiting_for_input`、`completed`、`failed`、`cancelled`）。待核验 `src/OpenClaw.Core/Models/WorkflowModels.cs` 中的确切字符串。

### 端点映射（`Program.cs`）

```csharp
app.MapPost("/api/workflows/{workflow}/run", async (string workflow, HttpContext ctx, DurableHttpAdapter adapter) =>
{
    var request = await adapter.ReadRequestAsync(ctx);
    var run = await adapter.StartRunAsync(workflow, request, ctx.RequestAborted);
    return Results.Json(run, CoreJsonContext.Default.AgentWorkflowRunResult, statusCode: 202);
});

app.MapGet("/api/workflows/{workflow}/status/{runId}", async (string workflow, string runId, DurableHttpAdapter adapter, HttpContext ctx) =>
{
    var snapshot = await adapter.GetStatusAsync(workflow, runId, ctx.RequestAborted);
    return snapshot is null
        ? Results.NotFound(new OperationStatusResponse { Success = false, Error = "Workflow run not found." })
        : Results.Json(snapshot, CoreJsonContext.Default.AgentWorkflowRunSnapshot);
});

app.MapPost("/api/workflows/{workflow}/respond/{runId}", async (string workflow, string runId, HttpContext ctx, DurableHttpAdapter adapter) =>
{
    var response = await adapter.ReadResponseAsync(ctx);
    var snapshot = await adapter.RespondAsync(workflow, runId, response, ctx.RequestAborted);
    return snapshot is null
        ? Results.NotFound(new OperationStatusResponse { Success = false, Error = "Workflow run not found." })
        : Results.Json(snapshot, CoreJsonContext.Default.AgentWorkflowRunSnapshot);
});
```

### 适配器内部

```csharp
public sealed class DurableHttpAdapter
{
    // run: AgentWorkflowRequest → StartReviewCommand (Wolverine 发件箱入队)
    public async Task<AgentWorkflowRunResult> StartRunAsync(string workflowName, AgentWorkflowRequest req, CancellationToken ct)
    {
        var runId = $"run_{Guid.NewGuid():N}";
        if (!TryParseSagaId(runId, out var sagaId))               // runId == saga WorkflowId
            throw new InvalidOperationException("Invalid runId format.");  // 刚生成, 不应失败; 防御
        var cmd = new StartReviewCommand(sagaId, req.Input, req.Metadata);
        await _bus.SendAsync(cmd, ct);                              // Wolverine 事务发件箱

        return new AgentWorkflowRunResult
        {
            BackendId = req.Metadata?.GetValueOrDefault("backendId") ?? "durable-review",
            WorkflowId = workflowName,
            RunId = runId,
            Status = AgentWorkflowStatuses.Queued,
            Events = [],
            Metadata = BuildMetadata(req)
        };
    }

    // status: Marten 投影 + 相位映射 + PendingInputs 填充.
    // saga/投影不存在时返回 null; 端点把 null → HTTP 404 (匹配 samples/OpenClaw.DurableAgentReview 行为).
    public async Task<AgentWorkflowRunSnapshot?> GetStatusAsync(string workflowName, string runId, CancellationToken ct)
    {
        if (!TryParseSagaId(runId, out var sagaId))
            return null;
        var projection = await _session.LoadAsync<ReviewProjection>(sagaId, ct);
        if (projection is null)
            return null;

        var status = PhaseStatusMap.ToOpenClawStatus(projection.CurrentPhase);
        var events = await _session.Events.AggregateStreamAsync<ReviewState>(sagaId, ct);
        var eventSummaries = BuildEventSummaries(events);

        return new AgentWorkflowRunSnapshot
        {
            WorkflowId = workflowName,
            RunId = runId,
            BackendId = projection.BackendId ?? "durable-review",
            Status = status,
            Output = projection.Output,
            OutputPayload = projection.OutputPayload,
            PendingInputs = status == AgentWorkflowStatuses.WaitingForInput
                ? PendingInputBuilder.Build(projection.ApprovalContext)
                : [],
            Events = eventSummaries,
            Metadata = BuildMetadata(projection)
        };
    }

    // respond: AgentWorkflowResponse → saga 恢复消息 → 重读状态
    public async Task<AgentWorkflowRunSnapshot?> RespondAsync(string workflow, string runId, AgentWorkflowResponse resp, CancellationToken ct)
    {
        if (!TryParseSagaId(runId, out var sagaId))
            return null;
        var msg = new ApprovalReceived(sagaId, resp.Approved == true, resp.ActorId, resp.Comment);
        await _bus.SendAsync(msg, ct);
        return await GetStatusAsync(workflow, runId, ct);
    }
}
```

### 数据流（完整 run 生命周期）

```
Gateway.RunAsync ──POST/run──▶ DurableHttpAdapter.StartRunAsync
                                    │
                          StartReviewCommand ──Wolverine 发件箱──▶ Postgres
                                    ▼                                    ▼
                          PlanExecutor → SecurityReviewer/Architecture/Cost
                              (IChatClient: Mock/Direct/Gateway)            │
                                    ▼                                       │
                          ReviewCompleted ──[Append] 归约器──▶ Marten 事件流
                                    ▼
                          AggregateReviews → AssessConfidence (0.8 < 0.85)
                                    ▼
                          AwaitApproval<Operator> — saga 暂停 (持久化)
                                    │
Gateway.GetAsync ──GET/status──▶ GetStatusAsync → Marten 投影
                                    │   phase="AwaitingApproval" → "waiting_for_input"
                                    │   PendingInputs 从 ApprovalContext 填充
                                    ▼
                          (网关经 StreamAsync 轮询, ~2s — 既有 runner)
                                    │
Gateway.RespondAsync ─POST/respond─▶ DurableHttpAdapter.RespondAsync
                                    │   ApprovalReceived 消息
                                    ▼
                          saga 恢复 → ExecuteApprovedAction → EmitAuditTrace
                                    ▼
                          Completed → "completed", OutputPayload 携带审计轨迹 + 事件流版本号
```

**不变量（契约正确性所在）：**
- **`runId` == saga `WorkflowId`**——适配器从 `run_xxx` 解析 saga id（匹配样例的 `run_{Guid:N}` 格式）。`TryParseSagaId` 在 runId 格式非法时返回 false → 端点返回 404。在 `DurableHttpAdapterTests` 中测试。
- **`PendingInputs` 仅在 `waiting_for_input` 时填充**——匹配 `MafDurableHttpWorkflowRunner` 的预期与样例行为。
- **`status` 读两个 Marten 源**：投影读模型（当前相位，快）+ 事件流聚合（事件摘要，时间旅行）。事件映射为 `AgentWorkflowEvent` DTO，其 `Payload` 是 `JsonElement`——天然 AOT 安全。
- **`respond` 不直接恢复 saga**——它发布一条 `ApprovalReceived` 消息，saga 经其 Wolverine 处理器处理，保留发件箱 exactly-once。
- **BackendId 往返**：网关发 `metadata["backendId"]`；适配器读回；缺失时回退 `"durable-review"`（匹配样例的 `ResolveBackendId` 回退）。
- **未找到 runId → 404**：与 `samples/OpenClaw.DurableAgentReview` 的 `Results.NotFound` 行为一致；网关的 `MafDurableHttpWorkflowRunner` 把非 2xx 视为错误（契约一致）。

## 8. 错误处理

1. **工作流级补偿（Strategos 侧）。** `ExecuteApprovedAction` 失败 → `.Compensate<RevertApprovedAction>` 运行，发射 `Compensated` 事件。适配器映射为 `failed`，`Error` 字段携带失败步骤 + 补偿轨迹摘要。这发生在 saga 内部，在 HTTP 错误路径之外——网关在下一次 `status` 轮询时看到 `failed`。
2. **LLM 调用失败（agent 步骤）。** `IChatClient.CompleteAsync` 抛异常 → 步骤抛 → Wolverine 重试重新投递（确切策略是 Strategos 配置任务，见 §10）。可配置重试用尽后，saga → `Failed` → 适配器映射 `failed`。Mock 模式不走此路径；集成测试用指向桩端点的 `DirectOpenAI` 触发它。
3. **HTTP/适配器级错误（网关可见）。** 非 2xx 抛 `InvalidOperationException("Workflow backend '{id}' returned HTTP {code}")`——与网关已有预期完全一致的格式。网关记日志；适配器不重试 HTTP 调用（那是网关的职责，据设计 §4.1）。
4. **审批超时升级。** `AwaitApproval` 带 `WithTimeout(4h)` → `EscalateToAdmin`。适配器把审批超时映射为持续的 `waiting_for_input`，但 `PortId` 不同（`operator-approval` → `admin-approval`），使网关的 `respond` 循环自然把新审批呈现给正确的审批者类型。

## 9. 测试

| 测试 | 类型 | 位置 | 验证 |
|---|---|---|---|
| `PhaseStatusMapTests` | 单元（无 IO） | 同级测试项目 | 每个 Strategos 相位 → 唯一 OpenClaw 状态；默认分支；Compensated 边界 |
| `PendingInputBuilderTests` | 单元 | 同级 | 审批上下文 → 正确 `PortId`/`Payload`；仅 `waiting_for_input` 填充 |
| `MockReviewChatClientTests` | 单元 | 同级 | mock 返回可解析 JSON；三角色产出不同 verdict |
| `DurableHttpAdapterTests` | 集成（`WebApplicationFactory` + Mock LLM + PostgreSQL testcontainer 里的 Marten） | 同级 | 完整 run→status→respond 往返；`runId`==`sagaId` 不变量；`BackendId` 往返；404 路径 |
| `KillRestartTests` | CI 自动化（`docker compose` + 进程 kill） | 同级，以 xUnit 编排 docker | **验收测试**——见下 |

### `KillRestartTests` 流程

```
1. docker compose up -d postgres strategos-host      (宿主在 127.0.0.1:5097)
2. POST /run → 拿 runId, status=queued→running
3. 轮询 GET /status 直到 status=waiting_for_input      (saga 暂停在 AwaitApproval)
4. docker compose kill strategos-host                  (模拟崩溃)
5. docker compose up -d strategos-host                 (重启 — Marten 持有事件流)
6. GET /status/{runId} → status=waiting_for_input      (状态从最后持久化相位恢复)
7. POST /respond/{runId} {approved:true} → status=completed
8. 断言: 第 6 步的 Events 数组包含崩溃前的事件 (事件流连续性)
```

第 6 步是关键断言——没有它，重启测试只证明"宿主起来了"，不证明"状态恢复了"。

### 测试位置偏离（在 PR 中披露）

[`CONTRIBUTING.md`](../../../CONTRIBUTING.md) 说测试放在 `src/OpenClaw.Tests/`。**提议偏离：** Strategos 宿主测试作为**同级测试项目** `samples/OpenClaw.StrategosWorkflowHost.Tests/`，不进 `src/OpenClaw.Tests/`。理由：(a) 测试需要 PostgreSQL testcontainer 和宿主二进制，而 `src/OpenClaw.Tests/` 中面向核心/网关 AOT 通道的单元测试不需要这些；(b) 测试与样例同置匹配 OpenClaw 的样例自包含惯例；(c) 防止 `Microsoft.Extensions.AI` + Wolverine + Marten 测试依赖泄漏进 `src/OpenClaw.Tests/`（后者属于核心/网关 AOT 通道）。在 PR 描述中按 review-checklist 的扩展 PR 清单（"扩展边界是否显式？"）披露。

## 10. 待核验项（实施须解决）

上文标"待核验"的每一项 Strategos API 表面都是一个具体的实施任务：

1. **`LevelUp.Strategos` NuGet 包名 + 命名空间**——确切 `using`、包 ID、版本（设计称 GA 2.7.0；在 `Directory.Packages.props` 条目中固定）。
2. **`Workflow<T>.Create()` 构建器 API**——`.Fork()`/`.Join<>()`/`.Then<>()`/`.AwaitApproval<>()`/`.Compensate<>()`/`.Finally<>()`/`.OnFailure()` 的确切签名（设计 §1.2 引 11 个构建器）。
3. **`AgentStep<TState>` 基类 + `StepContext`**——`ExecuteAsync` 签名、DI 解析模式（`ctx.Services`？）、事件上报 API（`ctx.RaiseAsync`？）。
4. **`[WorkflowState]`/`[Append]`/`[Snapshot]` 特性 + `IWorkflowState`**——确切命名空间；归约器是否自动发射。
5. **Wolverine `IMessageBus.SendAsync` + 事务发件箱配置**——如何向 saga 发命令。
6. **Marten 投影 + `Events.AggregateStreamAsync`**——投影注册、时间旅行读 API。
7. **`AgentWorkflowStatuses`/`AgentWorkflowBackendKinds` 常量值**——核验 `src/OpenClaw.Core/Models/WorkflowModels.cs` 中的确切小写字符串。
8. **`OperationStatusResponse` 类型**——适配器 404 响应复用此类型（匹配样例 `Program.cs:25` 等），核验其命名空间与字段。

每一项都成为计划中的一个任务（writing-plans 阶段），附具体核验方法：读 Strategos 源/文档，或写一个在承诺构建路径前失败的探索性测试。

## 11. 验收标准（P0 出口）

- [ ] 样例 `dotnet build` 零警告（[`CONTRIBUTING.md`](../../../CONTRIBUTING.md)："No warnings"）。
- [ ] `dotnet run --project samples/OpenClaw.StrategosWorkflowHost`（Mock 模式）启动并在 :5097 监听，无需 LLM 密钥。
- [ ] 网关配置 `Kind=maf-durable-http`、`BaseUrl=http://127.0.0.1:5097`、`WorkflowName=durable-agent-review`；`openclaw.run_workflow` 到达 `waiting_for_input`。
- [ ] `KillRestartTests` 第 6 步通过——**重启后状态恢复**。
- [ ] `respond`（approved）到达 `completed`，带 `OutputPayload` 审计轨迹。
- [ ] `PhaseStatusMapTests`、`PendingInputBuilderTests`、`DurableHttpAdapterTests`、`MockReviewChatClientTests` 通过。
- [ ] README 文档化 Mock/Direct/Gateway 三条路径 + kill-restart 步骤。
- [ ] Apache-2.0 NOTICE/THIRD-PARTY 条目已加（设计 §7 许可义务）。
- [ ] PR 描述披露测试位置偏离。

**P0 明确不交付：** `Kind: strategos-http`（P1）；Evidence Bundle 互链 / MCP App / Thompson Sampling（P2）；宿主 AOT 发布（JIT 是 P0 终态，推迟到 P3）。

## 12. 风险

| 风险 | 严重度 | 缓解 |
|---|---|---|
| Strategos API 表面与 DSL 草图不符（构建器/方法名） | 中 | §10 待核验项是计划的第一批任务；探索性测试在承诺构建路径前快速失败 |
| PostgreSQL 成为贡献者本地新基础设施依赖 | 中 | 集成整体可选；`docker compose up -d postgres` 一键；README 明示"无 Postgres = 无此后端"；Mock 模式仍需 Postgres（为真实持久化证明而接受的取舍） |
| Strategos 延迟特性（上下文装配 DSL、RAG、`OnFailure` 发射器、自动投影）需自建 | 中 | P0 用简单字符串拼装 prompt；这些缝隙恰好是 OpenClaw 专属适配逻辑的位置（据设计 §7） |
| NuGet 包来源/版本治理（`LevelUp.` 前缀，lvlup-sw 上游） | 中 | 固定到 NuGet 稳定版而非 git 子模块；跟进上游 release；留意 v2.7.0 前后行为变更 |
| 测试位置偏离被评审者驳回 | 低 | PR 描述披露理由；若评审者坚持，回退到 `src/OpenClaw.Tests/` 并以 testcontainer 隔离 |

## 13. 与后续阶段的关系

- **P1** 事后把 `Kind: "strategos-http"` 升级为一等后端类型（一行注册表分支 + `StrategosHttpWorkflowRunner` 组合既有 `MafDurableHttpWorkflowRunner`）。P0 宿主已说契约，P1 纯粹是网关侧别名 + runner。
- **P2a**（Evidence Bundle ↔ Marten 互链）插入 `status` 响应的 `OutputPayload`/`Events` 与 `EmitAuditTrace` 步骤——适配器层即接缝。
- **P2b**（本体 MCP App）把宿主的本体 MCP 服务器注册为 OpenClaw MCP App。
- **P2c**（Thompson Sampling 回灌）用 `IAgentSelector` 包装 `Reviewers/*.cs` agent 步骤，经 `RecordOutcomeAsync` 消费网关运行结果。
- **P3** 跟踪 Wolverine/Marten AOT 注解成熟度；待"进行中"标注移除后重估 `strategos-inproc`。

---

## 附录 A：参考文档

- 集成设计：[`docs/zh-CN/OpenClaw.NET集成Strategos方案.md`](../../zh-CN/OpenClaw.NET集成Strategos方案.md)
- 架构边界：[`docs/ARCHITECTURE_BOUNDARIES.md`](../../ARCHITECTURE_BOUNDARIES.md)
- 工作流后端契约：[`docs/workflow-backends.md`](../../workflow-backends.md)
- 贡献规范：[`CONTRIBUTING.md`](../../../CONTRIBUTING.md)、[`docs/maintainers/review-checklist.md`](../../maintainers/review-checklist.md)
- Wolverine AOT 状态：[wolverinefx.io/guide/aot](https://wolverinefx.io/guide/aot)

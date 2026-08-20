# P0 规格：OpenClaw.StrategosWorkflowHost Sidecar 样例

> **状态：** 设计（brainstorming 完成；Strategos API 已对照 `E:/GitHub/strategos` 真实源码核验；实施计划见 [plans/](../plans/2026-08-20-openclaw-strategos-p0-sidecar-host.md)）
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
- Strategos 是一个 .NET 10 库（`E:/GitHub/strategos`）：用流式 C# DSL 声明工作流，由 Roslyn 源生成器在编译期降级为类型安全的 Wolverine saga。**核验结论：** 包前缀 `LevelUp.Strategos.*`（MinVer 派生版本，当前 2.10.0），C# 命名空间根是 `Strategos`；外部 `WolverineFx`=6.12.0、`Marten`=9.9.0、`Microsoft.Extensions.AI`=10.5.2。Wolverine/Marten 的 AOT 支持官方标注"进行中"（[wolverinefx.io AOT](https://wolverinefx.io/guide/aot)），故宿主以 **JIT、非 AOT** 发布——符合 OpenClaw 的 AOT/JIT 边界纪律。

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
   │  WebApplication.CreateBuilder + builder.Host.UseWolverine│
   │                                                        │
   │  ┌─────────────┐   ┌──────────────┐   ┌─────────────┐  │
   │  │ ASP.NET Core│──▶│ Strategos saga│──▶│  Marten     │  │
   │  │ 3 端点      │   │ (源生成)      │   │ (Postgres)  │  │
   │  │ (契约)      │   │ + Wolverine   │   │ 事件流      │  │
   │  └─────────────┘   └──────┬───────┘   └─────────────┘  │
   │                            │                            │
   │                     ┌──────┴───────┐                    │
   │                     │ IWorkflowStep │                    │
   │                     │ 评审者(注入    │                    │
   │                     │  IChatClient) │                    │
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
├─ Program.cs                                  # WebApplication + builder.Host.UseWolverine(...) + 3 端点 + IChatClient 注册
├─ appsettings.json                            # 3 个 LLM 模式 profile + Postgres 连接串
├─ appsettings.Development.json                # Mock 默认 + localhost:5432 postgres
├─ docker-compose.yml                          # 2 服务: postgres + strategos-host (gateway 可选, 用户自跑)
├─ Dockerfile                                   # 宿主多阶段 JIT 构建
├─ README.md                                    # 3 条运行路径 (Mock / Direct / Gateway) + kill-restart 步骤
│
├─ Configuration/
│  └─ LlmMode.cs                                # 枚举: Mock | DirectOpenAI | BackThroughGateway + IChatClient 工厂
│
├─ Workflows/
│  ├─ ReviewState.cs                            # [WorkflowState] record : IEventSourcedState<ReviewState> + Marten 聚合约定
│  ├─ ReviewWorkflow.cs                         # [Workflow] 静态 partial 定义 + DSL
│  ├─ ApproverMarker.cs                         # public sealed class Operator {} / Admin {}
│  └─ Models/
│     ├─ ReviewVerdict.cs                       # 评审输出 record (Role, Verdict, Summary, Confidence)
│     └─ HumanDecision.cs                       # Approved/Rejected + ActorId + Comment
│
├─ Steps/                                       # 手写 IWorkflowStep<ReviewState>（主构造函数注入依赖）
│  ├─ PlanExecutor.cs                           # 计划步骤（无需 LLM）
│  ├─ SecurityReviewer.cs                       # 安全评审 (注入 IChatClient)
│  ├─ ArchitectureReviewer.cs                   # 架构评审 (注入 IChatClient)
│  ├─ CostReviewer.cs                           # 成本评审 (注入 IChatClient)
│  ├─ AggregateReviews.cs                       # Join 目标: 合并三路 verdict
│  ├─ AssessConfidence.cs                       # 回传 Confidence; RequireConfidence 由 step-config 判定
│  ├─ ExecuteApprovedAction.cs                  # 终端执行; Compensate<RevertApprovedAction> 在 step-config 上
│  ├─ RevertApprovedAction.cs                    # 补偿步骤
│  ├─ EmitAuditTrace.cs                         # .Finally: 审计轨迹写入 ExecutionResult
│  ├─ NotifyFailure.cs                          # OnFailure: 失败通知
│  └─ PromptBuilders.cs                         # 各评审者 prompt 构造
│
├─ Adapters/
│  ├─ DurableHttpAdapter.cs                    # run/status/respond 契约映射 (相位↔六态, PendingInputs 填充)
│  ├─ PhaseStatusMap.cs                         # Strategos 相位 → OpenClaw 状态 (纯函数)
│  └─ PendingInputBuilder.cs                    # AwaitingApproval → AgentWorkflowPendingInput{PortId,Payload} 构造器
│
└─ tests/ (同级测试项目) samples/OpenClaw.StrategosWorkflowHost.Tests/
   ├─ PhaseStatusMapTests.cs                    # 单元: 每个相位映射到唯一六态之一
   ├─ PendingInputBuilderTests.cs                # 单元: 审批上下文 → 端口 payload 形状
   ├─ MockReviewChatClientTests.cs                # 单元: mock 客户端返回可解析 JSON, 三角色产出不同 verdict
   ├─ DurableHttpAdapterTests.cs                 # 集成: run/status/respond 往返 (WebApplicationFactory + Mock LLM + Marten testcontainer)
   ├─ HostBootstrapTests.cs                      # 集成: WebApplication+UseWolverine+Wolverine+Marten 启动特征化
   └─ KillRestartTests.cs                       # CI 自动化 kill-restart (§9)
```

**布局理由：** `Program.cs` 保持薄；`Adapters/` 承载契约逻辑，P1 的 `StrategosHttpWorkflowRunner` 将与之对照；`Steps/*.cs` 一步一文件（**核验修正：步骤是手写 `IWorkflowStep<ReviewState>` 类，非 `AgentStep` 子类**——见 §6），使 P2 的 Thompson Sampling 包装器有显而易见的插入点；`PhaseStatusMap.cs` 是纯函数，单元覆盖 100%。

匹配既有样例的序列化习惯：经 `ConfigureHttpJsonOptions` 接入 `CoreJsonContext.Default`（即便宿主是 JIT，AOT 安全的 DTO 仍走 `OpenClaw.Core` 的序列化器往返）。宿主用 `WebApplication.CreateBuilder`（非 `CreateSlimBuilder`，因需 `builder.Host.UseWolverine`）。

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
builder.Services.AddSingleton<IChatClient>(sp => sp.BuildChatClient());

public static IChatClient BuildChatClient(this IServiceProvider sp)
{
    var opts = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
    return opts.Mode switch
    {
        LlmMode.Mock               => new MockReviewChatClient(),
        LlmMode.DirectOpenAI        => BuildOpenAi(sp, opts.Direct),
        LlmMode.BackThroughGateway   => BuildOpenAi(sp, opts.Gateway),
        _ => throw new InvalidOperationException($"Unknown LLM mode '{opts.Mode}'.")
    };
}

private static IChatClient BuildOpenAi(IServiceProvider sp, LlmEndpointOptions o)
{
    if (string.IsNullOrWhiteSpace(o.Endpoint))
        throw new InvalidOperationException("LLM Endpoint is required for non-Mock modes.");
    var key = SecretResolver.Resolve(o.ApiKeySecret, sp.GetService<ILoggerFactory>()?.CreateLogger("Llm"));
    if (string.IsNullOrEmpty(key))
        throw new InvalidOperationException($"LLM ApiKeySecret '{o.ApiKeySecret}' resolved empty.");
    // OpenClaw 仓库与 Strategos 均不引 OpenAI SDK——LLM 走 Microsoft.Extensions.AI.IChatClient。
    // Direct/Gateway 模式的具体 OpenAI 兼容 IChatClient 实现须在执行时选定兼容包（如 OpenAI / Azure.AI.OpenAI）。
    // Mock 模式（默认）不依赖此分支即可跑通全部 P0 验收。
    throw new NotImplementedException("Wire a concrete OpenAI-compatible IChatClient in Direct/Gateway modes.");
}
```

| 模式 | 何时用 | 网络出口 | 凭证来源 | 评审者行为 |
|---|---|---|---|---|
| **Mock**（默认） | 贡献者首次运行；CI kill-restart | 无 | 无 | 固定 `ReviewVerdict{Verdict="review-required", Confidence=0.8}`——工作流确定性地到达 `AssessConfidence`（0.8 < 0.85 → `AwaitApproval`），即便没有 LLM，审批闸门也被走过 |
| **DirectOpenAI** | 不跑 OpenClaw、直连真实 LLM | Provider API | `env:OPENAI_API_KEY` | 真实 LLM verdict（**P0 不实现 IChatClient**，见下方说明） |
| **BackThroughGateway** | 全链路：provider 密钥/预设/TokenJuice 留在网关 | `127.0.0.1:18789/v1`（仅回环） | `env:OPENCLAW_GATEWAY_KEY` | 评审者调网关；网关调真实 provider（**P0 不实现 IChatClient**） |

**关键设计细节：**
- `MockReviewChatClient` 是一个真正的 `IChatClient` 实现——它把*适配器路径*（Strategos agent step → `IChatClient` → verdict 解析）端到端走通，所以 Mock 不是空操作；它与真实路径的验证方式不同。`SecretResolver.Resolve`（签名 `static string? Resolve(string? secretRef, ILogger? logger)`）复用自 `OpenClaw.Core.Security`，**只支持 `env:` 与 `raw:` 前缀（无 `ref:`）**。模式在启动时解析一次（单例）；错误配置在 DI 构建期失败（"不支持的形态应快速失败"）。
- **Direct/Gateway 模式在 P0 不实现具体 `IChatClient`**：OpenClaw 与 Strategos 均不引 OpenAI SDK，Strategos.Agents 通过自定义 `StrategosFunctionsChatClient` 走 MEAI，不依赖 OpenAI 包。P0 的 Mock 模式覆盖全部验收；Direct/Gateway 的兼容 `IChatClient` 实现（指向任意 OpenAI 兼容端点）作为 README 标注的"需用户补全"项。这避免在 P0 引入未核验的第三方包。

**`OPENCLAW_GATEWAY_KEY` 语义澄清（避免误读）：**
此处的 `OPENCLAW_GATEWAY_KEY` 是**网关鉴权凭证**——宿主调用网关 v1 端点时用于身份认证的令牌，**不是 provider 密钥**（OpenAI/Claude 等的 API key 永远留在网关，不出网关进程）。两者职责分离：网关鉴权凭证让宿主"能进网关的门"；provider 密钥在网关内部决定"调哪个上游模型"。若网关对回环 v1 端点不强制鉴权（P0 可接受的简化），则 README 允许省略此凭证；否则贡献者需在本地设置该环境变量。README 将明确这一点。

## 6. 工作流定义（Strategos DSL）

> 本节代码经对照 `E:/GitHub/strategos` 真实源码核验。核验关键修正：① 持久化用 `PersistenceMode.EventSourced`，状态实现 `IEventSourcedState<TState>` + Marten 单流聚合约定（非 `[Append]`/`[Snapshot]` 归约器模式）；② `Workflow<T>.Create()` 返回 `IWorkflowBuilder<T>`，`Finally<T>()` 才返回 `WorkflowDefinition<T>`；③ `Fork` 形参是 `params Action<IForkPathBuilder<T>>[]`，故 `path => path.Then<>()`；④ `RequireConfidence(double)`；⑤ `Operator`/`Admin` 是自声明 marker（无内置）；⑥ `Compensate<T>()` 在 `IStepConfiguration` 上（非 builder 顶层）；⑦ `OnFailure` 必须在 `Finally` 之前；⑧ 步骤是**手写 `IWorkflowStep<TState>` 类**（`AgentStepBase` 是 sealed），非 `AgentStep` 子类。

### 状态（`ReviewState.cs`）

事件溯源状态：实现 `IEventSourcedState<ReviewState>`（纯 `ApplyEvent` 折叠）+ Marten 单流聚合约定（`Id`、`static Create(StartedEvent)`、每步 `Apply(StepCompletedEvent)`）。参考 `E:/GitHub/strategos/.../Workflows/EventSourcedAuditWorkflow.cs`。

```csharp
using Strategos.Abstractions;
using Strategos.Agents.Abstractions;
using Strategos.Attributes;
using OpenClaw.StrategosWorkflowHost.Workflows.Models;

namespace OpenClaw.StrategosWorkflowHost.Workflows;

[WorkflowState]
public sealed record ReviewState : IEventSourcedState<ReviewState>
{
    public Guid Id { get; init; }                   // Marten 流身份
    public Guid WorkflowId { get; init; }           // IWorkflowState
    public string UserRequest { get; init; } = "";
    public string Plan { get; init; } = "";
    public IReadOnlyList<ReviewVerdict> Reviews { get; init; } = [];  // 不用 [Append]（事件溯源模式用 ApplyEvent 折叠）
    public string? AggregatedSummary { get; init; }
    public double AggregateConfidence { get; init; }
    public HumanDecision? Decision { get; init; }   // 标量: 事件溯源模式下用 ApplyEvent 折叠（无 [Snapshot]）
    public string? ExecutionResult { get; init; }
    public string? FailureReason { get; init; }
    public string CurrentPhase { get; init; } = "NotStarted";

    // Marten 种子：由源生成器产出的 DurableAgentReviewStarted 事件建流（命名以 build 产出为准）
    public static ReviewState Create(DurableAgentReviewStarted started) =>
        new() { Id = started.WorkflowId, WorkflowId = started.WorkflowId, UserRequest = started.UserRequest };

    // Marten 折叠：每步完成事件携带 UpdatedState，直接取
    public ReviewState Apply(PlanExecutorCompleted e)        => e.UpdatedState with { CurrentPhase = "ExecutingPlan" };
    public ReviewState Apply(SecurityReviewerCompleted e)     => ApplyEvent(e) with { CurrentPhase = "ExecutingReview" };
    public ReviewState Apply(ArchitectureReviewerCompleted e) => ApplyEvent(e) with { CurrentPhase = "ExecutingReview" };
    public ReviewState Apply(CostReviewerCompleted e)         => ApplyEvent(e) with { CurrentPhase = "ExecutingReview" };
    public ReviewState Apply(AggregateReviewsCompleted e)    => e.UpdatedState;
    public ReviewState Apply(AssessConfidenceCompleted e)    => e.UpdatedState;
    public ReviewState Apply(ExecuteApprovedActionCompleted e)=> e.UpdatedState;
    public ReviewState Apply(EmitAuditTraceCompleted e)       => e.UpdatedState with { CurrentPhase = "Completed" };

    // Strategos 内存折叠（saga 在 session.Events.Append 后调用）
    public ReviewState ApplyEvent(IProgressEvent evt) => evt switch
    {
        PlanExecutorCompleted c        => c.UpdatedState,
        SecurityReviewerCompleted c   => c.UpdatedState,
        ArchitectureReviewerCompleted c => c.UpdatedState,
        CostReviewerCompleted c       => c.UpdatedState,
        AggregateReviewsCompleted c  => c.UpdatedState,
        AssessConfidenceCompleted c  => c.UpdatedState,
        ExecuteApprovedActionCompleted c => c.UpdatedState,
        EmitAuditTraceCompleted c     => c.UpdatedState,
        DurableAgentReviewStarted    => this,
        _ => this,   // 未知事件透传（信息性事件）
    };
}
```

> `DurableAgentReviewStarted`/`{Step}Completed` 是源生成器从工作流名 + 步骤类名产出的类型（命名规则：`{Pascal}Started` 与 `{StepName}Completed`，核验自 `EventsEmitter.cs` 与 `EventSourcedAuditState.Create(EventSourcedHappyStarted)`）。`{Step}Completed` 签名：`([SagaIdentity] Guid WorkflowId, Guid StepExecutionId, ReviewState UpdatedState, double? Confidence, DateTimeOffset Timestamp) : I{Pascal}Event`。若 Started 事件不带 `UserRequest` 字段，则 `Create` 改为只取 `WorkflowId`、`UserRequest` 由首步从命令注入——build 后确认。

### 定义（`ReviewWorkflow.cs`）

```csharp
using Strategos.Attributes;
using Strategos.Builders;
using Strategos.Definitions;
using OpenClaw.StrategosWorkflowHost.Steps;

namespace OpenClaw.StrategosWorkflowHost.Workflows;

[Workflow("durable-agent-review", Persistence = PersistenceMode.EventSourced)]
public static partial class ReviewWorkflowDefinition
{
    public static WorkflowDefinition<ReviewState> Definition =>
        Workflow<ReviewState>
            .Create("durable-agent-review")        // → IWorkflowBuilder<ReviewState>
            .StartWith<PlanExecutor>()
            .Fork(                                  // params Action<IForkPathBuilder<ReviewState>>[]
                path => path.Then<SecurityReviewer>(),
                path => path.Then<ArchitectureReviewer>(),
                path => path.Then<CostReviewer>())
            .Join<AggregateReviews>()              // IForkJoinBuilder.Join<T> → IWorkflowBuilder
            .Then<AssessConfidence>(step => step   // IStepConfiguration
                .RequireConfidence(0.85)            // double, 非 decimal
                .OnLowConfidence(alt => alt         // Action<IBranchBuilder>（IBranchBuilder 有 AwaitApproval）
                    .AwaitApproval<Operator>(approval => approval   // Operator 是自声明 marker
                        .WithContextFrom(s => s.AggregatedSummary ?? "Approval required.")
                        .WithTimeout(TimeSpan.FromHours(4))
                        .OnTimeout(esc => esc       // IApprovalEscalationBuilder（非 branch builder）
                            .EscalateTo<Admin>(a => a.WithContextFrom(s => "Escalated after timeout."))))))
            .Then<ExecuteApprovedAction>(step => step.Compensate<RevertApprovedAction>())  // Compensate 在 step-config 上
            .OnFailure(flow => flow.Then<NotifyFailure>())   // 必须在 Finally 之前
            .Finally<EmitAuditTrace>();             // → WorkflowDefinition<ReviewState>
}

// 审批者 marker（TApprover : class，需用户自定义；无内置 Operator）
public sealed class Operator { }
public sealed class Admin { }
```

此定义证明 Strategos 的 MVP 能力拓扑：`Fork`/`Join`（并行三评审 + 合并）；`RequireConfidence` + `OnLowConfidence` → `AwaitApproval`（置信度闸门 + 人在回路 + 超时升级路由经 `EscalateTo<Admin>`）；`Compensate<>`（失败回滚，step-config 级）；`Finally<>` + `OnFailure`（审计轨迹 + 失败通知）。

**超时升级是 `EscalateTo<Admin>` 在审批 builder 内声明**（非独立 `Then<EscalateToAdmin>` 步骤），它再开一个 `AwaitApproval<Admin>`。P0 只验证升级*路由*存在，不构建多级审批（推迟到 P2c）。kill-restart 测试通过 `operator-approval` 端口覆盖默认审批者路径。

### 评审者步骤（`Steps/*.cs`）

**核验修正：** Strategos 的 `AgentStepBase<TState,TResult>` 是 sealed（不可继承），且 `StepContext` 非泛型、无 `Services`/`State`/`RaiseAsync`。步骤是**手写 `IWorkflowStep<ReviewState>` 类**，主构造函数注入依赖（源生成器把步骤注册为 `AddTransient<{Step}>()`，故 DI 注入 `IChatClient` 生效——参考 `EventSourcedHappyStep(WorkflowInvocationLog log)`）。步骤返回 `StepResult<ReviewState>`（携带 `UpdatedState` + `Confidence?`），不"raise event"；生成 saga 在 `session.Events.Append` 后调 `ApplyEvent` 折叠并发射 `{Step}Completed`。

```csharp
using Microsoft.Extensions.AI;
using Strategos.Abstractions;
using Strategos.Steps;
using OpenClaw.StrategosWorkflowHost.Workflows;
using OpenClaw.StrategosWorkflowHost.Workflows.Models;

namespace OpenClaw.StrategosWorkflowHost.Steps;

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
        var verdict = await chat.GetResponseAsync<ReviewVerdict>(messages, cancellationToken: cancellationToken);
        return StepResult<ReviewState>.FromState(state with
        {
            Reviews = state.Reviews.Append(verdict with { Role = "security" }).ToList()
        }).WithConfidence(verdict.Confidence);
    }
}
```

`ArchitectureReviewer`/`CostReviewer` 结构相同，仅 prompt builder 与 `Role` 不同。三评审者并行（Fork），各自只看到 fork 前的 state，返回各自 verdict。`IChatClient` 经主构造函数注入；Mock 模式下是 `MockReviewChatClient`，真实模式下指向 OpenAI 兼容端点。

**不做：** 不注册 `IAgentSelector`/Thompson Sampling（P2c，实现位于独立 `Strategos.Infrastructure` 包）；不注册 `AddBudgetGuard`/`AddLoopDetector`/`AddWorkflowOrchestration`（Strategos.Infrastructure 强化项，非正确性所需）；不用 RAG DSL / 上下文装配 DSL（Strategos 延迟的消费者责任特性；P0 用 `PromptBuilders` 简单字符串拼装）。

## 7. 契约适配器与数据流

> 本节代码经核验修正：① 宿主用 `builder.Host.UseWolverine(opts => { opts.Services.AddMarten(...).IntegrateWithWolverine().ApplyAllDatabaseChangesOnStartup(); opts.Services.AddDurableAgentReviewWorkflow(); })`（**无** `AddStrategos`/`AddWorkflow<T>`/`AddWolverineWithMarten`）；② 读状态用 `IDocumentStore.QuerySession().LoadAsync<ReviewState>(id)`（内联快照），读事件用 `.Events.FetchStreamAsync(id)`（**非** `AggregateStreamAsync`）；③ 启动命令 `Start{Pascal}Command(Guid WorkflowId, ReviewState InitialState)` 经 `IMessageBus.PublishAsync`；④ 审批恢复用 `Resume{Point}ApprovalCommand([SagaIdentity] Guid WorkflowId, ApprovalDecision Decision, string? SelectedOptionId, string? Instructions)`（**非** `ApprovalReceived`），`ApprovalDecision` 是 `Strategos.Models` 枚举（`Approved/Rejected/Deferred`）。

### 相位 → 状态映射（`PhaseStatusMap.cs`，纯函数）

```csharp
using OpenClaw.Core.Models;

namespace OpenClaw.StrategosWorkflowHost.Adapters;

public static class PhaseStatusMap
{
    public static string ToOpenClawStatus(string phase) => phase switch
    {
        "NotStarted"         => AgentWorkflowStatuses.Queued,
        "AwaitingApproval"   => AgentWorkflowStatuses.WaitingForInput,
        "Completed"          => AgentWorkflowStatuses.Completed,
        "Failed"            => AgentWorkflowStatuses.Failed,
        "Compensated"        => AgentWorkflowStatuses.Failed,
        "Cancelled"         => AgentWorkflowStatuses.Cancelled,
        _ when phase.StartsWith("Executing", StringComparison.Ordinal) => AgentWorkflowStatuses.Running,
        _ => AgentWorkflowStatuses.Running,
    };
}
```

`AgentWorkflowStatuses.*` 常量已核验为小写字面量（`queued`/`running`/`waiting_for_input`/`completed`/`failed`/`cancelled`，`src/OpenClaw.Core/Models/WorkflowModels.cs:5-13`）；`MafDurableHttpWorkflowRunner.NormalizeStatus` 转小写（空→`running`）。

### 端点映射（`Program.cs`）

```csharp
app.MapPost("/api/workflows/{workflow}/run", async (string workflow, HttpContext ctx, DurableHttpAdapter adapter) =>
{
    var request = await JsonSerializer.DeserializeAsync(ctx.Request.Body, CoreJsonContext.Default.AgentWorkflowRequest, ctx.RequestAborted)
        ?? throw new InvalidOperationException("request body required.");
    var run = await adapter.StartRunAsync(workflow, request, ctx.RequestAborted);
    return Results.Json(run, CoreJsonContext.Default.AgentWorkflowRunResult, statusCode: StatusCodes.Status202Accepted);
});

app.MapGet("/api/workflows/{workflow}/status/{runId}", async (string workflow, string runId, HttpContext ctx, DurableHttpAdapter adapter) =>
{
    var snapshot = await adapter.GetStatusAsync(workflow, runId, ctx.RequestAborted);
    return snapshot is null
        ? Results.NotFound(new OperationStatusResponse { Success = false, Error = "Workflow run not found." })
        : Results.Json(snapshot, CoreJsonContext.Default.AgentWorkflowRunSnapshot);
});

app.MapPost("/api/workflows/{workflow}/respond/{runId}", async (string workflow, string runId, HttpContext ctx, DurableHttpAdapter adapter) =>
{
    var response = await JsonSerializer.DeserializeAsync(ctx.Request.Body, CoreJsonContext.Default.AgentWorkflowResponse, ctx.RequestAborted)
        ?? throw new InvalidOperationException("response body required.");
    var snapshot = await adapter.RespondAsync(workflow, runId, response, ctx.RequestAborted);
    return snapshot is null
        ? Results.NotFound(new OperationStatusResponse { Success = false, Error = "Workflow run not found." })
        : Results.Json(snapshot, CoreJsonContext.Default.AgentWorkflowRunSnapshot);
});
```

### 适配器内部（`DurableHttpAdapter.cs`）

```csharp
using Marten;
using OpenClaw.Core.Models;
using OpenClaw.StrategosWorkflowHost.Workflows;
using Strategos.Models;   // ApprovalDecision
using Wolverine;

namespace OpenClaw.StrategosWorkflowHost.Adapters;

public sealed class DurableHttpAdapter(IMessageBus bus, IDocumentStore store)
{
    public async Task<AgentWorkflowRunResult> StartRunAsync(
        string workflowName, AgentWorkflowRequest request, CancellationToken ct)
    {
        var runId = $"run_{Guid.NewGuid():N}";
        if (!TryParseSagaId(runId, out var sagaId))
            throw new InvalidOperationException("Invalid runId format.");
        var initial = new ReviewState { Id = sagaId, WorkflowId = sagaId, UserRequest = request.Input };
        var cmd = new StartDurableAgentReviewCommand(sagaId, initial);  // 生成器产出; 无 [SagaIdentity] (Start 命令)
        await bus.PublishAsync(cmd, ct);                                  // Wolverine 事务发件箱
        return new AgentWorkflowRunResult
        {
            BackendId = request.Metadata?.GetValueOrDefault("backendId") ?? "durable-review",
            WorkflowId = workflowName, RunId = runId,
            Status = AgentWorkflowStatuses.Queued, Events = [], Metadata = BuildMetadata(request)
        };
    }

    public async Task<AgentWorkflowRunSnapshot?> GetStatusAsync(
        string workflowName, string runId, CancellationToken ct)
    {
        if (!TryParseSagaId(runId, out var sagaId)) return null;
        await using var query = store.QuerySession();
        var state = await query.LoadAsync<ReviewState>(sagaId);          // 内联快照投影
        if (state is null) return null;
        var status = PhaseStatusMap.ToOpenClawStatus(state.CurrentPhase);
        var events = await query.Events.FetchStreamAsync(sagaId);        // 事件流审计（非 AggregateStreamAsync）
        var eventSummaries = events.Select(e => new AgentWorkflowEvent
        {
            Id = $"evt_{e.Id}", Type = e.Data.GetType().Name,
            WorkflowId = workflowName, RunId = runId, Status = status,
            Summary = e.Data.GetType().Name, TimestampUtc = e.Timestamp.UtcDateTime
        }).ToArray();
        return new AgentWorkflowRunSnapshot
        {
            WorkflowId = workflowName, RunId = runId, BackendId = "durable-review", Status = status,
            Output = state.ExecutionResult, OutputPayload = BuildOutputPayload(state),
            PendingInputs = status == AgentWorkflowStatuses.WaitingForInput
                ? PendingInputBuilder.Build(state, "operator-approval") : [],
            Events = eventSummaries, Metadata = BuildMetadata(state)
        };
    }

    public async Task<AgentWorkflowRunSnapshot?> RespondAsync(
        string workflowName, string runId, AgentWorkflowResponse response, CancellationToken ct)
    {
        if (!TryParseSagaId(runId, out var sagaId)) return null;
        var decision = response.Approved == true ? ApprovalDecision.Approved : ApprovalDecision.Rejected;
        var resume = new ResumeDurableAgentReviewApprovalCommand(  // 生成器产出; [SagaIdentity] on WorkflowId
            sagaId, decision, response.Approved == true ? "approve" : "reject", response.Comment);
        await bus.PublishAsync(resume, ct);
        return await GetStatusAsync(workflowName, runId, ct);
    }

    private static bool TryParseSagaId(string runId, out Guid sagaId) { /* run_xxx → Guid */ }
    // BuildOutputPayload / BuildMetadata 省略，见实施计划 Task 8
}
```

### 数据流（完整 run 生命周期）

```
Gateway.RunAsync ──POST/run──▶ DurableHttpAdapter.StartRunAsync
                                    │  StartDurableAgentReviewCommand(Guid, ReviewState)
                          bus.PublishAsync ──Wolverine 发件箱──▶ Postgres
                                    ▼                                    ▼
                          saga 启动 → PlanExecutor → Security/Architecture/Cost
                              (IChatClient: Mock/Direct/Gateway)            │
                                    ▼                                       │
                          步骤返回 StepResult(UpdatedState, Confidence)      │
                          saga: session.Events.Append + State.ApplyEvent ──▶ Marten 事件流
                                    ▼
                          AggregateReviews → AssessConfidence (0.8 < 0.85)
                                    ▼
                          AwaitApproval<Operator> — saga 暂停 (持久化, 发 Request...ApprovalEvent)
                                    │
Gateway.GetAsync ──GET/status──▶ GetStatusAsync
                                    │  query.LoadAsync<ReviewState> (内联快照) → CurrentPhase
                                    │  query.Events.FetchStreamAsync (审计事件)
                                    │   phase="AwaitingApproval" → "waiting_for_input"
                                    │   PendingInputs 从 state 填充
                                    ▼
                          (网关经 StreamAsync 轮询, ~2s — 既有 runner, dedup by evt.Id)
                                    │
Gateway.RespondAsync ─POST/respond─▶ DurableHttpAdapter.RespondAsync
                                    │  ResumeDurableAgentReviewApprovalCommand(WorkflowId, ApprovalDecision, ...)
                          bus.PublishAsync
                                    ▼
                          saga 恢复 → ExecuteApprovedAction → EmitAuditTrace
                                    ▼
                          Completed → "completed", OutputPayload 携带审计轨迹 + 事件流版本号
```

**不变量（契约正确性所在）：**
- **`runId` == saga `WorkflowId`**——适配器从 `run_xxx` 解析 saga id。`TryParseSagaId` 非法时返回 false → 端点 404。`DurableHttpAdapterTests` 覆盖。
- **`PendingInputs` 仅在 `waiting_for_input` 时填充**——匹配 `MafDurableHttpWorkflowRunner` 预期与样例行为。
- **`status` 读两个 Marten 源**：内联快照 `LoadAsync<ReviewState>`（当前相位/状态，快）+ 事件流 `FetchStreamAsync`（审计事件摘要）。事件映射为 `AgentWorkflowEvent` DTO，其 `Payload` 是 `JsonElement`——天然 AOT 安全。
- **`respond` 不直接恢复 saga**——它发布 `Resume{Point}ApprovalCommand`（带 `[SagaIdentity]`，按 `WorkflowId` 路由），saga 经其 Wolverine 处理器处理（`Handle(Resume...)` 按 `ApprovalDecision` 分支：`Approved` → `Start{nextStep}Command`），保留发件箱 exactly-once。
- **BackendId 往返**：网关发 `metadata["backendId"]`；适配器读回；缺失回退 `"durable-review"`（匹配样例 `ResolveBackendId`）。
- **未找到 runId → 404**：与 `samples/OpenClaw.DurableAgentReview` 的 `Results.NotFound` 一致；网关把非 2xx 视为硬失败（契约一致）。

## 8. 错误处理

1. **工作流级补偿（Strategos 侧）。** `ExecuteApprovedAction` 失败 → `.Compensate<RevertApprovedAction>`（在 step-config 上声明）运行补偿步骤，saga 进入补偿相位。适配器映射为 `failed`，`Error` 字段携带失败步骤 + 补偿轨迹摘要。这在 saga 内部，HTTP 错误路径之外——网关在下一次 `status` 轮询时看到 `failed`。
2. **LLM 调用失败（agent 步骤）。** `IChatClient.GetResponseAsync` 抛异常 → 步骤抛 → Wolverine 重试重新投递（step-config 可 `.WithRetry(maxAttempts)`）。重试用尽后，saga → `Failed` → 适配器映射 `failed`。Mock 模式不走此路径；集成测试用指向桩端点的 `DirectOpenAI` 触发它。
3. **HTTP/适配器级错误（网关可见）。** 适配器内部异常经 ASP.NET Core 返回非 2xx；网关的 `MafDurableHttpWorkflowRunner` 把非 2xx 视为硬失败（不解析错误体）。契约一致。
4. **审批超时升级。** `AwaitApproval<Operator>` 带 `WithTimeout(4h)` → 超时后 `OnTimeout` 的 `IApprovalEscalationBuilder.EscalateTo<Admin>` 再开 `AwaitApproval<Admin>`。适配器把审批超时映射为持续的 `waiting_for_input`，但 `PortId` 不同（`operator-approval` → `admin-approval`），使网关的 `respond` 循环自然把新审批呈现给正确审批者类型。
5. **Fork 并发冲突（核验发现，关键）。** 三评审者并行 append 到**同一** Marten 事件流，会触发乐观并发冲突（`ConcurrentUpdateException` / `EventStreamUnexpectedMaxEventIdException`）。宿主必须配置重试（核验自 `EventSourcedHostFixture.cs:86-101`）：
   ```csharp
   opts.OnException(ex => ex is Marten.Exceptions.ConcurrentUpdateException
       || ex.GetType().Name.Contains("EventStreamUnexpected", StringComparison.Ordinal))
       .RetryWithCooldown(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100),
           TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(800));
   ```
   缺此配置，Fork 路径会 stall saga。

## 9. 测试

| 测试 | 类型 | 位置 | 验证 |
|---|---|---|---|
| `PhaseStatusMapTests` | 单元（无 IO） | 同级测试项目 | 每个 Strategos 相位 → 唯一 OpenClaw 状态；默认分支；Compensated 边界 |
| `PendingInputBuilderTests` | 单元 | 同级 | 审批上下文 → 正确 `PortId`/`Payload`；仅 `waiting_for_input` 填充 |
| `MockReviewChatClientTests` | 单元 | 同级 | mock 返回可解析 JSON；三角色产出不同 verdict |
| `HostBootstrapTests` | 集成 | 同级 | `WebApplication`+`UseWolverine`+Marten 事件溯源宿主启动（无样例，先特征化） |
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

## 10. 核验结论（已对照真实源码解决）

原 §10 列的 8 项 Strategos API 待核验项，经 4 个并行研究 agent + 直接读 `E:/GitHub/strategos` 源码已全部解决。结论汇总：

1. **NuGet 包 + 命名空间**——包前缀 `LevelUp.Strategos.*`（MinVer，2.10.0）；C# 命名空间根 `Strategos`（非 `LevelUp.Strategos`）。已固定到实施计划 csproj。
2. **`Workflow<T>.Create()` 构建 API**——返回 `IWorkflowBuilder<TState>`（非 `WorkflowDefinition`）；`Fork(params Action<IForkPathBuilder>[])`、`Join<T>` 在 `IForkJoinBuilder`、`RequireConfidence(double)`、`OnLowConfidence(Action<IBranchBuilder>)`、`Compensate<T>` 在 `IStepConfiguration`、`OnFailure` 须在 `Finally` 前。已修正在 §6。
3. **步骤模型**——`AgentStepBase<TState,TResult>` sealed 不可继承；步骤是手写 `IWorkflowStep<TState>`（`ExecuteAsync(TState, StepContext, CancellationToken) -> Task<StepResult<TState>>`），主构造函数注入 DI；`StepContext` 非泛型无 `Services`/`RaiseAsync`。已修正在 §6。
4. **状态/事件/归约**——事件溯源模式用 `IEventSourcedState<TState>.ApplyEvent` + Marten 单流聚合约定；`[Append]`/`[Merge]` 用于文档模式（**无 `[Snapshot]`**）。已修正在 §6。
5. **Wolverine/Marten**——`builder.Host.UseWolverine` + `AddMarten(...).IntegrateWithWolverine().ApplyAllDatabaseChangesOnStartup()` + 生成器 `Add{Pascal}Workflow()`；`IMessageBus.PublishAsync` 发命令；读 `LoadAsync` + `FetchStreamAsync`（非 `AggregateStreamAsync`）。已修正在 §7。
6. **审批恢复**——`Resume{Point}ApprovalCommand([SagaIdentity] Guid WorkflowId, ApprovalDecision, string? SelectedOptionId, string? Instructions)`；`ApprovalDecision` 枚举（`Approved/Rejected/Deferred`）。已修正在 §7。
7. **OpenClaw 常量**——`AgentWorkflowStatuses.*` 小写（`queued`/`running`/`waiting_for_input`/`completed`/`failed`/`cancelled`）；`AgentWorkflowBackendKinds.MafDurableHttp="maf-durable-http"`；`CoreJsonContext` 注册全部工作流 DTO；`SecretResolver` 支持 `env:`/`raw:`（无 `ref:`）。已核验 `WorkflowModels.cs`/`Session.cs`/`SecretResolver.cs`。
8. **`OperationStatusResponse`**——`OpenClaw.Core.Models`，字段 `Success`/`Message`/`Error`/`Mode`（mutable `set`）。404 响应复用此类型。

**遗留执行期不确定性（非 API 未知，而是无样例/需 build 确认，已在实施计划内标注回退）：**
- `WebApplication.CreateBuilder` + `builder.Host.UseWolverine` 无样例（Strategos 样例用 `Host.CreateDefaultBuilder`）→ 实施计划 Task 1 特征化测试先证；受阻回退 `Host.CreateDefaultBuilder`。
- 源生成器产出的 `DurableAgentReviewStarted`/`{Step}Completed`/`ResumeDurableAgentReviewApprovalCommand` 确切命名 → Task 6 build 后回填 §6/§7。
- `RequireConfidence` 在主 `.Then<>()` 路径是否强制执行（issue #135 称 fork/branch 路径未强制）→ 集成测试确认；不强制则 `AssessConfidence` 内手动判定。
- Direct/Gateway 的 OpenAI 兼容 `IChatClient` 包 → P0 不实现（Mock 覆盖验收）。

## 11. 验收标准（P0 出口）

- [ ] 样例 `dotnet build` 零警告（[`CONTRIBUTING.md`](../../../CONTRIBUTING.md)："No warnings"）。
- [ ] `dotnet run --project samples/OpenClaw.StrategosWorkflowHost`（Mock 模式）启动并在 :5097 监听，无需 LLM 密钥。
- [ ] 网关配置 `Kind=maf-durable-http`、`BaseUrl=http://127.0.0.1:5097`、`WorkflowName=durable-agent-review`；`openclaw.run_workflow` 到达 `waiting_for_input`。
- [ ] `KillRestartTests` 第 6 步通过——**重启后状态恢复**。
- [ ] `respond`（approved）到达 `completed`，带 `OutputPayload` 审计轨迹。
- [ ] `PhaseStatusMapTests`、`PendingInputBuilderTests`、`MockReviewChatClientTests`、`DurableHttpAdapterTests`、`HostBootstrapTests` 通过。
- [ ] README 文档化 Mock/Direct/Gateway 三条路径 + kill-restart 步骤 + Direct/Gateway 需补 `IChatClient` 的说明。
- [ ] Apache-2.0 NOTICE/THIRD-PARTY 条目已加。
- [ ] PR 描述披露测试位置偏离 + Direct/Gateway `IChatClient` 未实现。

**P0 明确不交付：** `Kind: strategos-http`（P1）；Evidence Bundle 互链 / MCP App / Thompson Sampling（P2）；宿主 AOT 发布（JIT 是 P0 终态，推迟到 P3）；Direct/Gateway 模式的具体 `IChatClient`（Mock 覆盖 P0 验收）。

## 12. 风险

| 风险 | 严重度 | 缓解 |
|---|---|---|
| `WebApplication`+`UseWolverine` 无样例 | 中 | 实施计划 Task 1 特征化测试先证；受阻回退 `Host.CreateDefaultBuilder` |
| Fork 并发 append 冲突 stall saga | 中 | §8.5 的 `OnException.RetryWithCooldown` 配置（核验自 `EventSourcedHostFixture`） |
| 源生成器事件/命令命名需 build 确认 | 低 | 实施计划 Task 6 build 后回填 §6/§7；命名规则已核验 |
| `RequireConfidence` 在主路径未强制（issue #135） | 低 | 集成测试确认；不强制则 `AssessConfidence` 内手动判定 |
| PostgreSQL 成为贡献者本地新基础设施依赖 | 中 | 集成整体可选；`docker compose up -d postgres` 一键；README 明示"无 Postgres = 无此后端"；Mock 模式仍需 Postgres（为真实持久化证明而接受的取舍） |
| Strategos 延迟特性需自建（上下文装配 DSL、RAG 等） | 中 | P0 用 `PromptBuilders` 简单字符串；这些缝隙恰好是 OpenClaw 专属适配逻辑的位置 |
| NuGet 包版本治理（MinVer 派生） | 低 | 固定到 NuGet 稳定版；跟进上游 release；留意 2.10.0 前后行为变更 |
| 测试位置偏离被评审者驳回 | 低 | PR 描述披露理由；若评审者坚持，回退 `src/OpenClaw.Tests/` 并以 testcontainer 隔离 |
| Direct/Gateway `IChatClient` 未实现 | 低 | Mock 覆盖全部 P0 验收；README 标注需用户补全 |

## 13. 与后续阶段的关系

- **P1** 事后把 `Kind: "strategos-http"` 升级为一等后端类型（一行注册表分支 + `StrategosHttpWorkflowRunner` 组合既有 `MafDurableHttpWorkflowRunner`）。P0 宿主已说契约，P1 纯粹是网关侧别名 + runner。
- **P2a**（Evidence Bundle ↔ Marten 互链）插入 `status` 响应的 `OutputPayload`/`Events` 与 `EmitAuditTrace` 步骤——适配器层（§7）即接缝，`FetchStreamAsync` 已就绪。
- **P2b**（本体 MCP App）把宿主的本体 MCP 服务器（`LevelUp.Strategos.Ontology.MCP`）注册为 OpenClaw MCP App。
- **P2c**（Thompson Sampling 回灌）用 `IAgentSelector`（接口在 `Strategos.Abstractions`，实现在 `Strategos.Infrastructure`）包装 `Steps/*.cs` agent 步骤，经 `RecordOutcomeAsync` 消费网关运行结果。
- **P3** 跟踪 Wolverine/Marten AOT 注解成熟度；待"进行中"标注移除后重估 `strategos-inproc`。

---

## 附录 A：参考文档

- 集成设计：[`docs/zh-CN/OpenClaw.NET集成Strategos方案.md`](../../zh-CN/OpenClaw.NET集成Strategos方案.md)
- 架构边界：[`docs/ARCHITECTURE_BOUNDARIES.md`](../../ARCHITECTURE_BOUNDARIES.md)
- 工作流后端契约：[`docs/workflow-backends.md`](../../workflow-backends.md)
- 贡献规范：[`CONTRIBUTING.md`](../../../CONTRIBUTING.md)、[`docs/maintainers/review-checklist.md`](../../maintainers/review-checklist.md)
- 实施计划：[`docs/superpowers/plans/2026-08-20-openclaw-strategos-p0-sidecar-host.md`](../plans/2026-08-20-openclaw-strategos-p0-sidecar-host.md)
- Wolverine AOT 状态：[wolverinefx.io/guide/aot](https://wolverinefx.io/guide/aot)

# OpenClaw.StrategosWorkflowHost (P0) 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 新建 `samples/OpenClaw.StrategosWorkflowHost`——一个 ASP.NET Core（JIT）样例，承载真实的 Strategos 事件溯源 saga 运行时（Wolverine + Marten + PostgreSQL），对外说 OpenClaw.NET 既有的 `maf-durable-http` 工作流后端契约，**网关零代码改动**。

**Architecture:** Sidecar 进程，复用 OpenClaw 既有三端点契约（run/status/respond）。Strategos 工作流以 `PersistenceMode.EventSourced` 运行，saga 把事件 append 到 Marten 流；适配器把相位映射到 OpenClaw 六态、用 `FetchStreamAsync` 读事件审计、用 `LoadAsync` 读状态快照。LLM 经可配置 `IChatClient`（Mock/DirectOpenAI/BackThroughGateway），Mock 模式无需密钥。

**Tech Stack:** .NET 10 (`net10.0`)；`LevelUp.Strategos` 2.10.0、`LevelUp.Strategos.Generators`（源生成器）、`LevelUp.Strategos.Agents`（MinVer）；`WolverineFx` 6.12.0 + `WolverineFx.Marten` 6.12.0；`Marten` 9.9.0；`Microsoft.Extensions.AI` 10.5.2；`Npgsql` 10.0.3；OpenClaw.Core（既有）。PostgreSQL（容器）。xUnit + Testcontainers + NSubstitute。

**Spec:** [`docs/superpowers/specs/2026-08-20-openclaw-strategos-p0-sidecar-host-design.md`](../specs/2026-08-20-openclaw-strategos-p0-sidecar-host-design.md)
> **注意：** 本计划的所有 Strategos 代码经对照 `E:/GitHub/strategos` 真实源码核验。spec 的 §6/§7 代码草图是核验前的设想，**已被本计划的核验后代码取代**；执行前会同步修正 spec 的 §5/§6/§7/§10。

## Global Constraints

- 目标框架 `net10.0`；C# 14（文件作用域命名空间、主构造函数、集合表达式）。
- Strategos NuGet 包前缀 `LevelUp.Strategos.*`（MinVer 派生版本，当前 2.10.0）；C# 命名空间根是 `Strategos`（非 `LevelUp.Strategos`）。
- 外部依赖固定版本：`WolverineFx`=6.12.0、`WolverineFx.Marten`=6.12.0、`Marten`=9.9.0、`Microsoft.Extensions.AI`=10.5.2、`Npgsql`=10.0.3。OpenClaw 仓库无 OpenAI SDK 引用——LLM 调用走 `Microsoft.Extensions.AI.IChatClient`（Strategos.Agents 同源）。
- OpenClaw 契约常量（已核验 `OpenClaw.Core.Models`）：状态 `"queued"`/`"running"`/`"waiting_for_input"`/`"completed"`/`"failed"`/`"cancelled"`；后端 kind `"maf-durable-http"`。所有工作流 DTO 经 `CoreJsonContext.Default.X` 序列化（camelCase + omit-null + 无缩进）。
- `SecretResolver`（`OpenClaw.Core.Security`）只支持 `env:` 与 `raw:` 前缀（**无 `ref:`**）；签名 `static string? Resolve(string? secretRef, ILogger? logger)`。
- 端点路径（相对 `BaseUrl`，网关侧 `Uri.EscapeDataString`）：`POST api/workflows/{workflowName}/run`（体 `AgentWorkflowRequest` → `AgentWorkflowRunResult`）、`GET api/workflows/{workflowName}/status/{runId}`（→ `AgentWorkflowRunSnapshot`）、`POST api/workflows/{workflowName}/respond/{runId}`（体 `AgentWorkflowResponse` → `AgentWorkflowRunSnapshot`）。网关对非 2xx 视为硬失败（不解析错误体），故 404 返回 `OperationStatusResponse` 体即可。
- 网关在每个 `/run` 请求的 `Metadata["backendId"]` 注入后端 id；宿主读回，缺失回退 `"durable-review"`。
- `dotnet build` 零警告；AOT 纪律不适用于本样例（JIT 发布），但 DTO 序列化仍走 `CoreJsonContext`（与既有样例一致）。
- Apache-2.0 许可义务：在 `THIRD-PARTY`/`NOTICE` 加 Strategos 条目。

---

## File Structure

```
samples/OpenClaw.StrategosWorkflowHost/
├─ OpenClaw.StrategosWorkflowHost.csproj
├─ Program.cs                       # ~120 LOC: WebApplication + UseWolverine + 3 端点 + IChatClient 注册
├─ appsettings.json                 # Postgres 连接串 + 3 LLM 模式
├─ appsettings.Development.json     # Mock 默认
├─ docker-compose.yml               # postgres + strategos-host
├─ Dockerfile
├─ README.md
├─ Configuration/LlmMode.cs         # LlmMode 枚举 + LlmOptions + IChatClient 工厂
├─ Workflows/
│  ├─ ReviewState.cs                # [WorkflowState] record : IEventSourcedState<ReviewState> + Marten 聚合约定
│  ├─ ReviewWorkflow.cs             # [Workflow] 静态定义 + DSL
│  ├─ ApproverMarker.cs            # public sealed class Operator {} / Admin {}
│  └─ Models/{ReviewVerdict.cs, HumanDecision.cs}
├─ Steps/                           # 手写 IWorkflowStep<ReviewState>（主构造函数 DI）
│  ├─ PlanExecutor.cs, SecurityReviewer.cs, ArchitectureReviewer.cs, CostReviewer.cs
│  ├─ AggregateReviews.cs, AssessConfidence.cs
│  ├─ ExecuteApprovedAction.cs, RevertApprovedAction.cs
│  ├─ EmitAuditTrace.cs, NotifyFailure.cs, EscalateToAdmin.cs
│  └─ PromptBuilders.cs             # 各评审者 prompt 构造
├─ Adapters/
│  ├─ DurableHttpAdapter.cs         # run/status/respond 契约映射
│  ├─ PhaseStatusMap.cs             # 相位 → 六态（纯函数）
│  └─ PendingInputBuilder.cs        # 审批上下文 → PendingInput
└─ (同级测试项目) samples/OpenClaw.StrategosWorkflowHost.Tests/
   ├─ ...Tests.csproj
   ├─ PhaseStatusMapTests.cs, PendingInputBuilderTests.cs, MockReviewChatClientTests.cs
   ├─ DurableHttpAdapterTests.cs, HostBootstrapTests.cs
   └─ KillRestartTests.cs
```

**为什么 `Steps/` 而非 spec 的 `Workflows/Reviewers/`：** 核验后确认 Strategos 没有 `AgentStep<T>` 可继承的基类（`AgentStepBase` 是 sealed），步骤是**手写 `IWorkflowStep<TState>` 类**（参考 `EventSourcedHappyStep`），主构造函数注入依赖。故独立 `Steps/` 目录比挂在 `Workflows/Reviewers/` 下更准确。

---

## Task 1: csproj + 最小宿主特征化（确认 WebApplication+UseWolverine+Wolverine+Marten 启动）

Strategos 仓库的样例/测试都用 `Host.CreateDefaultBuilder().UseWolverine(...)`，**没有 `WebApplication` 样例**。本任务先用一个最小 no-op 事件溯源工作流确认 `WebApplication.CreateBuilder + builder.Host.UseWolverine(...)` 能启动、Marten schema 能建表、源生成器能产出 saga。这一步把"宿主能否在 ASP.NET Core 里跑"这个唯一无样例的不确定性先消除。

**Files:**
- Create: `samples/OpenClaw.StrategosWorkflowHost/OpenClaw.StrategosWorkflowHost.csproj`
- Create: `samples/OpenClaw.StrategosWorkflowHost/Workflows/SmokeState.cs`
- Create: `samples/OpenClaw.StrategosWorkflowHost/Workflows/SmokeWorkflow.cs`
- Create: `samples/OpenClaw.StrategosWorkflowHost/Steps/NoopStep.cs`
- Create: `samples/OpenClaw.StrategosWorkflowHost/Program.cs`（最小骨架，端点返回占位文本）
- Test: `samples/OpenClaw.StrategosWorkflowHost.Tests/HostBootstrapTests.cs`

**Interfaces:**
- Consumes: `OpenClaw.Core`（既有，项目引用）
- Produces: 一个能 `dotnet run` 启动、监听端口、Marten 连上的宿主骨架

- [ ] **Step 1: 写 csproj**

`OpenClaw.StrategosWorkflowHost.csproj`：
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <PublishAot>false</PublishAot>
    <IsPackable>false</IsPackable>
    <AssemblyName>OpenClaw.StrategosWorkflowHost</AssemblyName>
    <RootNamespace>OpenClaw.StrategosWorkflowHost</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\OpenClaw.Core\OpenClaw.Core.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="LevelUp.Strategos" Version="2.10.0" />
    <PackageReference Include="LevelUp.Strategos.Generators" Version="2.10.0" PrivateAssets="all" />
    <PackageReference Include="LevelUp.Strategos.Agents" Version="2.10.0" />
    <PackageReference Include="WolverineFx" Version="6.12.0" />
    <PackageReference Include="WolverineFx.Marten" Version="6.12.0" />
    <PackageReference Include="Marten" Version="9.9.0" />
    <PackageReference Include="Microsoft.Extensions.AI" Version="10.5.2" />
    <PackageReference Include="Microsoft.Extensions.AI.Abstractions" Version="10.5.2" />
    <PackageReference Include="Npgsql" Version="10.0.3" />
    <PackageReference Include="JasperFx.Resources" Version="*" />
  </ItemGroup>
</Project>
```
> 注：`JasperFx.Resources` 提供 `AddResourceSetupOnStartup()`（核验自 `EventSourcedHostFixture.cs:9`）。版本待执行时以 NuGet 实际可用为准微调（MinVer 包可能需用 `*` 或具体 2.10.0）。把项目加入解决方案：`dotnet sln OpenClaw.Net.slnx add samples/OpenClaw.StrategosWorkflowHost/OpenClaw.StrategosWorkflowHost.csproj`。

- [ ] **Step 2: 写最小事件溯源工作流（确认源生成器产出）**

`Workflows/SmokeState.cs`（参考 `EventSourcedAuditWorkflow.cs:33` 的形状）：
```csharp
using Strategos.Abstractions;
using Strategos.Agents.Abstractions;
using Strategos.Attributes;

namespace OpenClaw.StrategosWorkflowHost.Workflows;

[WorkflowState]
public sealed record SmokeState : IEventSourcedState<SmokeState>
{
    public Guid Id { get; init; }
    public Guid WorkflowId { get; init; }
    public bool Done { get; init; }

    // Marten 聚合种子：由生成器产出的 {Pascal}Started 事件建流。执行时按生成器实际事件名修正。
    public static SmokeState Create(SmokeStarted started) =>
        new() { Id = started.WorkflowId, WorkflowId = started.WorkflowId };

    // Marten 折叠：步骤完成事件携带 UpdatedState，直接取之。执行时按生成器实际事件名修正。
    public SmokeState Apply(NoopCompleted evt) => evt.UpdatedState;

    public SmokeState ApplyEvent(IProgressEvent evt) => evt switch
    {
        NoopCompleted c => c.UpdatedState,
        _ => this,
    };
}
```
> `SmokeStarted`/`NoopCompleted` 是源生成器从 `SmokeWorkflow` + `NoopStep` 名字产出的类型——**本步骤会在 build 后确认确切名字并修正**（生成器命名规则：`{Pascal}Started` 与 `{StepName}Completed`，核验自 `EventsEmitter.cs` 与 `EventSourcedAuditState.Create(EventSourcedHappyStarted)`）。这是本计划唯一的"命名待 build 确认"点，每个相关任务都会回引此约定。

`Steps/NoopStep.cs`（参考 `EventSourcedHappyStep`）：
```csharp
using Strategos.Abstractions;
using Strategos.Steps;

namespace OpenClaw.StrategosWorkflowHost.Steps;

public sealed class NoopStep : IWorkflowStep<SmokeState>
{
    public Task<StepResult<SmokeState>> ExecuteAsync(
        SmokeState state, StepContext context, CancellationToken cancellationToken)
        => Task.FromResult(StepResult<SmokeState>.FromState(state with { Done = true }));
}
```

`Workflows/SmokeWorkflow.cs`（参考 `EventSourcedHappyWorkflowDefinition:153`）：
```csharp
using Strategos.Attributes;
using Strategos.Builders;
using Strategos.Definitions;

namespace OpenClaw.StrategosWorkflowHost.Workflows;

[Workflow("smoke", Persistence = PersistenceMode.EventSourced)]
public static partial class SmokeWorkflowDefinition
{
    public static WorkflowDefinition<SmokeState> Definition =>
        Workflow<SmokeState>.Create("smoke")
            .StartWith<NoopStep>()
            .Finally<NoopStep>();
}
```

- [ ] **Step 3: 写最小 Program.cs（WebApplication + UseWolverine + Marten）**

`Program.cs`（参考 `EventSourcedHostFixture.cs:75-138`，但用 `WebApplication`）：
```csharp
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using OpenClaw.StrategosWorkflowHost.Workflows;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWolverine(opts =>
{
    var pg = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");

    opts.Services.AddMarten(storeOptions =>
    {
        storeOptions.Connection(pg);
        storeOptions.AutoCreateSchemaObjects = JasperFx.AutoCreate.All;
    })
    .IntegrateWithWolverine()
    .ApplyAllDatabaseChangesOnStartup();

    opts.Services.AddSmokeWorkflow();
    opts.Services.AddResourceSetupOnStartup();
});

var app = builder.Build();
app.MapGet("/", () => "OpenClaw.StrategosWorkflowHost (smoke)");
app.Run();
```
> 加 `appsettings.Development.json`：`{"ConnectionStrings":{"Postgres":"Host=localhost;Port=5432;Database=strategos;Username=strategos;Password=strategos"}}`。

- [ ] **Step 4: 写 bootstrap 测试（确认宿主能起来）**

`Tests/HostBootstrapTests.cs`：用 Testcontainers 起 Postgres，`WebApplicationFactory<Program>` 起宿主，GET `/` 返回 200。此测试确认 WebApplication+Wolverine+Marten 事件溯源宿主可行。
```csharp
// 用 Testcontainers.PostgreSql 起 pg；用 WebApplicationFactory<Program> 起宿主；
// 断言 GET "/" 返回 200 且 host 未抛异常。
// 具体测试代码在执行时按 Testcontainers/WebApplicationFactory 当前 API 编写；
// 关键断言：var resp = await client.GetAsync("/"); resp.IsSuccessStatusCode。
```
> 这是计划里唯一允许"执行时补全测试脚手架"的点——Testcontainers + `WebApplicationFactory<Program>` 的确切 API 在本计划编写时未在本机核验。如该 Step 在执行时受阻（如 `WebApplicationFactory` 对源生成器项目的限制），回退到 `Host.CreateDefaultBuilder().UseWolverine()` + 端口监听断言，并记录偏差。

- [ ] **Step 5: build → 修正生成器事件名 → 跑测试**

```bash
dotnet build samples/OpenClaw.StrategosWorkflowHost/OpenClaw.StrategosWorkflowHost.csproj
```
查 `obj/Generated` 下源生成器产出，确认 `SmokeStarted`/`NoopCompleted` 命名；若不同，修 `SmokeState.Create/Apply` 形参与 `ApplyEvent` 的 match。循环至 `dotnet build` 零错误零警告。

- [ ] **Step 6: Commit**

```bash
git add samples/OpenClaw.StrategosWorkflowHost samples/OpenClaw.StrategosWorkflowHost.Tests
git commit -m "feat(strategos): bootstrap P0 sidecar host skeleton (smoke workflow)"
```

---

## Task 2: ReviewState（事件溯源状态 + Marten 聚合约定）

**Files:**
- Create: `Workflows/ReviewState.cs`
- Create: `Workflows/Models/ReviewVerdict.cs`
- Create: `Workflows/Models/HumanDecision.cs`
- Create: `Workflows/ApproverMarker.cs`
- Test: `Tests/ReviewStateFoldTests.cs`

**Interfaces:**
- Consumes: `IEventSourcedState<TState>`（`Strategos.Agents.Abstractions`）、`IProgressEvent`、生成器产出的 `{Step}Completed`/`DurableAgentReviewStarted` 事件类型
- Produces: `ReviewState`（不可变 record，含 `Reviews`/`Plan`/`AggregateConfidence`/`Decision`/`ExecutionResult`/`Status`）

- [ ] **Step 1: 写 ReviewVerdict / HumanDecision / ApproverMarker**

`Workflows/Models/ReviewVerdict.cs`：
```csharp
namespace OpenClaw.StrategosWorkflowHost.Workflows.Models;

public sealed record ReviewVerdict(
    string Role,            // "security" | "architecture" | "cost"
    string Verdict,         // "review-required" 等
    string Summary,
    double Confidence);
```

`Workflows/Models/HumanDecision.cs`：
```csharp
namespace OpenClaw.StrategosWorkflowHost.Workflows.Models;

public sealed record HumanDecision(bool Approved, string? ActorId, string? Comment);
```

`Workflows/ApproverMarker.cs`（核验：`TApprover : class` 需用户自定义，无内置 `Operator`，见核验报告 §5）：
```csharp
namespace OpenClaw.StrategosWorkflowHost.Workflows;

public sealed class Operator { }
public sealed class Admin { }
```

- [ ] **Step 2: 写 ReviewState（事件溯源 + Marten 约定）**

`Workflows/ReviewState.cs`（参考 `EventSourcedAuditWorkflow.cs:33-97`；事件名在 Task 6 build 后最终确认）：
```csharp
using OpenClaw.StrategosWorkflowHost.Workflows.Models;
using Strategos.Abstractions;
using Strategos.Agents.Abstractions;
using Strategos.Attributes;

namespace OpenClaw.StrategosWorkflowHost.Workflows;

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

    // Marten 种子：DurableAgentReviewStarted（命名见 Task 6 build 确认）
    public static ReviewState Create(DurableAgentReviewStarted started) =>
        new() { Id = started.WorkflowId, WorkflowId = started.WorkflowId, UserRequest = started.UserRequest };

    // Marten 折叠：每步完成事件携带 UpdatedState，直接取（并记相位）
    public ReviewState Apply(PlanExecutorCompleted e)        => e.UpdatedState with { CurrentPhase = "ExecutingPlan" };
    public ReviewState Apply(SecurityReviewerCompleted e)     => MergeReview(e);
    public ReviewState Apply(ArchitectureReviewerCompleted e) => MergeReview(e);
    public ReviewState Apply(CostReviewerCompleted e)         => MergeReview(e);
    public ReviewState Apply(AggregateReviewsCompleted e)    => e.UpdatedState with { CurrentPhase = "ExecutingAggregator" };
    public ReviewState Apply(AssessConfidenceCompleted e)    => e.UpdatedState with { CurrentPhase = "ExecutingConfidence" };
    public ReviewState Apply(ExecuteApprovedActionCompleted e) => e.UpdatedState with { CurrentPhase = "ExecutingAction" };
    public ReviewState Apply(EmitAuditTraceCompleted e)       => e.UpdatedState with { CurrentPhase = "Completed" };

    private ReviewState MergeReview<T>(T e) where T : IProgressEvent
        => ApplyEvent(e) with { CurrentPhase = "ExecutingReview" };

    // Strategos 内存折叠（saga 在 Append 后调用）
    public ReviewState ApplyEvent(IProgressEvent evt) => evt switch
    {
        PlanExecutorCompleted c        => c.UpdatedState,
        SecurityReviewerCompleted c   => c.UpdatedState with { Reviews = c.UpdatedState.Reviews },
        ArchitectureReviewerCompleted c => c.UpdatedState with { Reviews = c.UpdatedState.Reviews },
        CostReviewerCompleted c       => c.UpdatedState with { Reviews = c.UpdatedState.Reviews },
        AggregateReviewsCompleted c  => c.UpdatedState,
        AssessConfidenceCompleted c  => c.UpdatedState,
        ExecuteApprovedActionCompleted c => c.UpdatedState,
        EmitAuditTraceCompleted c     => c.UpdatedState,
        DurableAgentReviewStarted    => this,
        _ => this,
    };
}
```
> `DurableAgentReviewStarted` 含 `UserRequest` 字段——若生成器产出的 Started 事件**不**带初始状态字段（仅 `WorkflowId`），则 `Create` 改为只取 `WorkflowId`，`UserRequest` 由首步 `PlanExecutor` 从命令注入。Task 6 build 后确认并修正。`{Step}Completed` 事件签名（核验自 `EventsEmitter.cs:274`）：`{StepName}Completed([SagaIdentity] Guid WorkflowId, Guid StepExecutionId, ReviewState UpdatedState, double? Confidence, DateTimeOffset Timestamp)`。

- [ ] **Step 3: 写折叠单测**

`Tests/ReviewStateFoldTests.cs`：构造一个 `ReviewState`，喂 `SecurityReviewerCompleted`（`UpdatedState` 含一条 verdict），断言 `ApplyEvent` 后 `Reviews.Count == 1` 且 `CurrentPhase == "ExecutingReview"`；喂未知事件类型断言透传不变。
```csharp
// 示例骨架（事件类型在 Task 6 后可见，本测试在 Task 6 build 通过后充实）：
// var s = new ReviewState { Id = g, WorkflowId = g };
// var evt = new SecurityReviewerCompleted(g, Guid.NewGuid(), s with { Reviews = [v] }, 0.8, DateTimeOffset.UtcNow);
// var folded = s.ApplyEvent(evt);
// Assert.Single(folded.Reviews);
```
> 此测试依赖 Task 6 build 后才存在的生成器类型，故排在 Task 6 之后执行（或与 Task 6 同一 commit）。

- [ ] **Step 4: Commit**

```bash
git add Workflows/ReviewState.cs Workflows/Models Workflows/ApproverMarker.cs
git commit -m "feat(strategos): add event-sourced ReviewState with Marten aggregation"
```

---

## Task 3: PhaseStatusMap（纯函数 + 单测）

**Files:**
- Create: `Adapters/PhaseStatusMap.cs`
- Test: `Tests/PhaseStatusMapTests.cs`

**Interfaces:**
- Consumes: `OpenClaw.Core.Models.AgentWorkflowStatuses`（核验：`WorkflowModels.cs:5-13`）
- Produces: `static string ToOpenClawStatus(string phase)`

- [ ] **Step 1: 写失败测试**

`Tests/PhaseStatusMapTests.cs`：
```csharp
using OpenClaw.Core.Models;
using OpenClaw.StrategosWorkflowHost.Adapters;
using Xunit;

namespace OpenClaw.StrategosWorkflowHost.Tests;

public class PhaseStatusMapTests
{
    [Theory]
    [InlineData("NotStarted", AgentWorkflowStatuses.Queued)]
    [InlineData("AwaitingApproval", AgentWorkflowStatuses.WaitingForInput)]
    [InlineData("Completed", AgentWorkflowStatuses.Completed)]
    [InlineData("Failed", AgentWorkflowStatuses.Failed)]
    [InlineData("Compensated", AgentWorkflowStatuses.Failed)]
    [InlineData("Cancelled", AgentWorkflowStatuses.Cancelled)]
    [InlineData("ExecutingPlan", AgentWorkflowStatuses.Running)]
    [InlineData("ExecutingReview", AgentWorkflowStatuses.Running)]
    [InlineData("WhateverUnknown", AgentWorkflowStatuses.Running)] // 未知相位安全回落
    public void ToOpenClawStatus_MapsEachPhase(string phase, string expected)
        => Assert.Equal(expected, PhaseStatusMap.ToOpenClawStatus(phase));
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
dotnet test --filter PhaseStatusMapTests
```
Expected: FAIL（`PhaseStatusMap` 未定义）。

- [ ] **Step 3: 写实现**

`Adapters/PhaseStatusMap.cs`：
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

- [ ] **Step 4: 跑测试确认通过 + Commit**

```bash
dotnet test --filter PhaseStatusMapTests
git add Adapters/PhaseStatusMap.cs Tests/PhaseStatusMapTests.cs
git commit -m "feat(strategos): add PhaseStatusMap (Strategos phase -> OpenClaw status)"
```

---

## Task 4: PendingInputBuilder + 单测

**Files:**
- Create: `Adapters/PendingInputBuilder.cs`
- Test: `Tests/PendingInputBuilderTests.cs`

**Interfaces:**
- Consumes: `OpenClaw.Core.Models.AgentWorkflowPendingInput`（核验：`WorkflowModels.cs:94-100`，字段 `PortId`/`Summary`/`Payload(JsonElement?)`/`Metadata`）
- Produces: `static IReadOnlyList<AgentWorkflowPendingInput> Build(ReviewState state, string portId)`

- [ ] **Step 1: 写失败测试** — 断言 `Build(state, "operator-approval")` 返回单元素，`PortId=="operator-approval"`，`Payload` 非空含 `context`。
```csharp
[Fact]
public void Build_WhenAwaitingApproval_ReturnsSingleInputWithPortId()
{
    var state = new ReviewState { WorkflowId = Guid.NewGuid(), AggregatedSummary = "needs human review" };
    var inputs = PendingInputBuilder.Build(state, "operator-approval");
    var single = Assert.Single(inputs);
    Assert.Equal("operator-approval", single.PortId);
    Assert.NotNull(single.Payload);
}
```

- [ ] **Step 2: 跑确认失败**

- [ ] **Step 3: 写实现**

`Adapters/PendingInputBuilder.cs`：
```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Core.Models;
using OpenClaw.StrategosWorkflowHost.Workflows;

namespace OpenClaw.StrategosWorkflowHost.Adapters;

public static class PendingInputBuilder
{
    public static IReadOnlyList<AgentWorkflowPendingInput> Build(ReviewState state, string portId)
    {
        var payload = new JsonObject
        {
            ["workflowId"] = state.WorkflowId,
            ["summary"] = state.AggregatedSummary ?? "Approval required before executing the approved action.",
            ["confidence"] = state.AggregateConfidence,
            ["reviews"] = new JsonArray(state.Reviews.Select(v => (JsonNode)new JsonObject
            {
                ["role"] = v.Role,
                ["verdict"] = v.Verdict,
                ["summary"] = v.Summary,
                ["confidence"] = v.Confidence,
            }).ToArray()),
        };
        using var doc = JsonDocument.Parse(payload.ToJsonString());
        return [new AgentWorkflowPendingInput
        {
            PortId = portId,
            Summary = "Approve or reject the aggregated agent review.",
            Payload = doc.RootElement.Clone(),
            Metadata = new Dictionary<string, string> { ["requestPort"] = "HumanApproval" }
        }];
    }
}
```

- [ ] **Step 4: 跑通过 + Commit**

---

## Task 5: MockReviewChatClient + 单测

Mock 是一个**真正的 `IChatClient`**（核验决策见 spec §5），让 agent step→IChatClient→verdict 解析路径在无密钥下端到端走通。

**Files:**
- Create: `Configuration/MockReviewChatClient.cs`
- Test: `Tests/MockReviewChatClientTests.cs`

**Interfaces:**
- Consumes: `Microsoft.Extensions.AI.IChatClient`、`Microsoft.Extensions.AI.ChatMessage` 等
- Produces: 一个按角色返回固定 verdict JSON 的 `IChatClient`

- [ ] **Step 1: 写失败测试** — 用 `GetResponseAsync<ReviewVerdict>(...)`（MEAI 扩展）请求 security 角色，断言返回 `ReviewVerdict{Role="security", Confidence=0.8}`。
```csharp
[Theory]
[InlineData("security", "security")]
[InlineData("architecture", "architecture")]
[InlineData("cost", "cost")]
public async Task ReturnsVerdictForRole(string role, string expectedRole)
{
    var client = new MockReviewChatClient();
    var messages = new[] { new ChatMessage(ChatRole.User, role) };
    var verdict = await client.GetResponseAsync<ReviewVerdict>(messages);
    Assert.Equal(expectedRole, verdict.Role);
    Assert.Equal(0.8, verdict.Confidence);
}
```

- [ ] **Step 2: 跑确认失败**

- [ ] **Step 3: 写实现** — `MockReviewChatClient : IChatClient`，`GetResponseAsync<T>` 解析请求里角色，返回该角色的固定 verdict JSON 反序列化结果；`CompleteAsync` 返回文本 JSON。三个角色 verdict 均为 `Verdict="review-required", Confidence=0.8`，使工作流确定性地到达 `AssessConfidence`（0.8 < 0.85 → `AwaitApproval`）。
```csharp
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenClaw.StrategosWorkflowHost.Workflows.Models;

namespace OpenClaw.StrategosWorkflowHost.Configuration;

public sealed class MockReviewChatClient : IChatClient
{
    public ChatClientMetadata Metadata => new("mock-review");

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        var role = ExtractRole(messages);
        var verdict = new ReviewVerdict(role, "review-required", "Mock review: no critical risk.", 0.8);
        var json = JsonSerializer.Serialize(verdict);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, (await GetResponseAsync(messages, options, ct)).Messages[0].Text);
    }

    public void Dispose() { }

    private static string ExtractRole(IEnumerable<ChatMessage> messages)
        => messages.Select(m => m.Text).FirstOrDefault(t => t is "security" or "architecture" or "cost") ?? "security";
}
```
> `IChatClient` 的确切成员签名（`GetResponseAsync`/`GetStreamingResponseAsync`/`Metadata`/`Dispose`）以 `Microsoft.Extensions.AI.Abstractions` 10.5.2 为准；执行时若签名有异（如泛型 `GetResponseAsync<T>` 是扩展而非接口成员），按实际调整。Mock 的目的只是让 path 走通，不追求 MEAI 全表面。

- [ ] **Step 4: 跑通过 + Commit**

---

## Task 6: ReviewWorkflow 定义（[Workflow] + DSL）+ build 确认源生成

**Files:**
- Create: `Workflows/ReviewWorkflow.cs`

**Interfaces:**
- Consumes: `Workflow<TState>.Create` → `IWorkflowBuilder<TState>`（核验：`Workflow.cs:35`）；`Fork(params Action<IForkPathBuilder<TState>>[])`（`IWorkflowBuilder.cs:400`）；`Join<T>`（`IForkJoinBuilder.cs:44`）；`Then<T>(Action<IStepConfiguration>)`（`IWorkflowBuilder.cs:177`）；`AwaitApproval<T>(Action<IApprovalBuilder>)`（`IWorkflowBuilder.cs:371`）；`RequireConfidence(double)`（`IStepConfiguration.cs:50`）；`OnLowConfidence(Action<IBranchBuilder>)`（`:61`）；`Compensate<T>()`（`:68`）；`OnFailure(Action<IFailureBuilder>)`（`IWorkflowBuilder.cs:333`）；`Finally<T>()`（`:217`）；`WithTimeout`/`OnTimeout(Action<IApprovalEscalationBuilder>)`（`IApprovalBuilder.cs:82,130`）
- Produces: 源生成器产出 `DurableAgentReviewSaga`、`StartDurableAgentReviewCommand`、`AddDurableAgentReviewWorkflow()`、各 `{Step}Completed` 与 `DurableAgentReviewStarted` 事件类型

- [ ] **Step 1: 写工作流定义（核验后修正形式）**

`Workflows/ReviewWorkflow.cs`（核验报告 §1/§3/§4/§5 的修正形式；`OnFailure` 必须在 `Finally` 之前；`Compensate` 在 step-config 上）：
```csharp
using OpenClaw.StrategosWorkflowHost.Steps;
using Strategos.Attributes;
using Strategos.Builders;
using Strategos.Definitions;

namespace OpenClaw.StrategosWorkflowHost.Workflows;

[Workflow("durable-agent-review", Persistence = PersistenceMode.EventSourced)]
public static partial class ReviewWorkflowDefinition
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
                .OnLowConfidence(alt => alt.AwaitApproval<Operator>(approval => approval
                    .WithContextFrom(s => s.AggregatedSummary ?? "Approval required.")
                    .WithTimeout(TimeSpan.FromHours(4))
                    .OnTimeout(esc => esc.EscalateTo<Admin>(a => a
                        .WithContextFrom(s => "Escalated after approval timeout."))))))
            .Then<ExecuteApprovedAction>(step => step.Compensate<RevertApprovedAction>())
            .OnFailure(flow => flow.Then<NotifyFailure>())
            .Finally<EmitAuditTrace>();
}
```
> 修正点（相对 spec §6 草图）：① `Fork` 形参是 `Action<IForkPathBuilder>`，故 `path => path.Then<>()`；② `RequireConfidence(0.85)` 是 `double`；③ `Operator`/`Admin` 是自声明 marker；④ `OnTimeout` 收 `IApprovalEscalationBuilder`，用 `EscalateTo<Admin>`（核验：`IApprovalEscalationBuilder.cs:74`）而非 `Then<>`；⑤ `Compensate` 在 `IStepConfiguration` 上；⑥ `OnFailure` 在 `Finally` 前。`EscalateToAdmin` 步骤类不再是独立 `Then`——升级由 `EscalateTo<Admin>` 在审批 builder 内声明。删除 spec 里的 `EscalateToAdmin.cs` 独立步骤，改为 `NotifyTimeout` 类（若需升级时记录），或直接让 `EscalateTo<Admin>` 再开一个 `AwaitApproval<Admin>`。

- [ ] **Step 2: build，确认源生成器产出**

```bash
dotnet build samples/OpenClaw.StrategosWorkflowHost/OpenClaw.StrategosWorkflowHost.csproj
```
查 `obj/Generated`：确认存在 `DurableAgentReviewSaga`、`StartDurableAgentReviewCommand(Guid WorkflowId, ReviewState InitialState)`、`AddDurableAgentReviewWorkflow()`、`DurableAgentReviewStarted`、`{Step}Completed` 事件。若有 AGWF 诊断（如 `OnFailure` 位置、`Fork` 无 `Join`），按诊断修。

- [ ] **Step 3: 用生成器产出回填 ReviewState 的事件名（Task 2 遗留）**

对照 `obj/Generated` 里实际 `{Step}Completed` 与 `DurableAgentReviewStarted` 的确切命名与字段，修 `ReviewState.Create/Apply/ApplyEvent` 的形参类型与 `switch` 分支。重跑 `ReviewStateFoldTests`。

- [ ] **Step 4: 跑 Task 2 折叠测试通过 + Commit**

```bash
dotnet test --filter ReviewStateFoldTests
git add Workflows/ReviewWorkflow.cs Workflows/ReviewState.cs
git commit -m "feat(strategos): add ReviewWorkflow DSL definition (event-sourced)"
```

---

## Task 7: 手写 IWorkflowStep 步骤群

核验确认步骤是**手写 `IWorkflowStep<ReviewState>` 类**（参考 `EventSourcedHappyStep`），主构造函数注入依赖；`ExecuteAsync(ReviewState state, StepContext ctx, CancellationToken ct) -> Task<StepResult<ReviewState>>`。`StepContext` 非泛型、无 `Services`/`RaiseAsync`；`IChatClient` 经构造函数注入（源生成器把步骤注册为 `AddTransient<{Step}>()`，故 DI 注入生效）。

**Files:**
- Create: `Steps/PlanExecutor.cs`, `SecurityReviewer.cs`, `ArchitectureReviewer.cs`, `CostReviewer.cs`, `AggregateReviews.cs`, `AssessConfidence.cs`, `ExecuteApprovedAction.cs`, `RevertApprovedAction.cs`, `EmitAuditTrace.cs`, `NotifyFailure.cs`, `PromptBuilders.cs`

**Interfaces:**
- Consumes: `IWorkflowStep<TState>`（`Strategos.Abstractions`）、`StepResult<TState>.FromState/.WithConfidence`（`Strategos.Steps`）、`IChatClient`（MEAI）、`IProgressEventStore`（`Strategos.Agents.Abstractions`，可选）
- Produces: 10 个步骤类，每个返回 `StepResult<ReviewState>`

- [ ] **Step 1: 写 PlanExecutor**（无需 LLM，生成占位 Plan）
```csharp
using OpenClaw.StrategosWorkflowHost.Workflows;
using Strategos.Abstractions;
using Strategos.Steps;

namespace OpenClaw.StrategosWorkflowHost.Steps;

public sealed class PlanExecutor : IWorkflowStep<ReviewState>
{
    public Task<StepResult<ReviewState>> ExecuteAsync(
        ReviewState state, StepContext context, CancellationToken cancellationToken)
    {
        var updated = state with { Plan = $"Plan for: {state.UserRequest}" };
        return Task.FromResult(StepResult<ReviewState>.FromState(updated));
    }
}
```

- [ ] **Step 2: 写 PromptBuilders**（三评审者 prompt）
```csharp
namespace OpenClaw.StrategosWorkflowHost.Steps;

public static class PromptBuilders
{
    public static string Security(string plan, string request) =>
        $"You are a security reviewer. Plan: {plan}. Request: {request}. Return JSON {{role,verdict,summary,confidence}}.";
    public static string Architecture(string plan, string request) =>
        $"You are an architecture reviewer. Plan: {plan}. Request: {request}. Return JSON {{role,verdict,summary,confidence}}.";
    public static string Cost(string plan, string request) =>
        $"You are a cost reviewer. Plan: {plan}. Request: {request}. Return JSON {{role,verdict,summary,confidence}}.";
}
```

- [ ] **Step 3: 写 SecurityReviewer**（构造函数注入 `IChatClient`）
```csharp
using Microsoft.Extensions.AI;
using OpenClaw.StrategosWorkflowHost.Workflows;
using OpenClaw.StrategosWorkflowHost.Workflows.Models;
using Strategos.Abstractions;
using Strategos.Steps;

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
> `ArchitectureReviewer`/`CostReviewer` 结构相同，仅 prompt builder 与 `Role` 不同。三评审者并行（Fork），各自只看到 fork 前的 state，返回各自 verdict。

- [ ] **Step 4: 写 AggregateReviews**（Join 目标，合并三路 verdict）
```csharp
public sealed class AggregateReviews : IWorkflowStep<ReviewState>
{
    public Task<StepResult<ReviewState>> ExecuteAsync(
        ReviewState state, StepContext context, CancellationToken cancellationToken)
    {
        var summary = string.Join(" | ", state.Reviews.Select(r => $"{r.Role}:{r.Verdict}"));
        var confidence = state.Reviews.Count > 0
            ? state.Reviews.Average(r => r.Confidence)
            : 0.0;
        return Task.FromResult(StepResult<ReviewState>.FromState(state with
        {
            AggregatedSummary = summary,
            AggregateConfidence = confidence
        }).WithConfidence(confidence));
    }
}
```

- [ ] **Step 5: 写 AssessConfidence**（返回置信度，`RequireConfidence` 由 step-config 在 saga 层判定）
```csharp
public sealed class AssessConfidence : IWorkflowStep<ReviewState>
{
    public Task<StepResult<ReviewState>> ExecuteAsync(
        ReviewState state, StepContext context, CancellationToken cancellationToken)
        => Task.FromResult(StepResult<ReviewState>.FromState(state).WithConfidence(state.AggregateConfidence));
}
```
> `RequireConfidence(0.85)` 在 `ReviewWorkflow` 的 step-config 上声明，由生成 saga 判定；步骤只回传 `Confidence`。核验注意：`RequireConfidence`/`OnLowConfidence` 在 fork/branch 路径"已声明但未强制执行"（issue #135），但在主 `.Then<>()` 路径应生效——Task 7 收尾的集成测试会确认；若该 gate 未生效，降级为 `AssessConfidence` 内手动 `if (state.AggregateConfidence < 0.85)` 触发审批（记录偏差）。

- [ ] **Step 6: 写 ExecuteApprovedAction + RevertApprovedAction（补偿）**
```csharp
public sealed class ExecuteApprovedAction : IWorkflowStep<ReviewState>
{
    public Task<StepResult<ReviewState>> ExecuteAsync(
        ReviewState state, StepContext context, CancellationToken cancellationToken)
        => Task.FromResult(StepResult<ReviewState>.FromState(state with
        {
            ExecutionResult = $"Executed approved action for: {state.UserRequest}"
        }));
}

public sealed class RevertApprovedAction : IWorkflowStep<ReviewState>
{
    public Task<StepResult<ReviewState>> ExecuteAsync(
        ReviewState state, StepContext context, CancellationToken cancellationToken)
        => Task.FromResult(StepResult<ReviewState>.FromState(state with
        {
            FailureReason = (state.FailureReason ?? "") + "; reverted approved action."
        }));
}
```

- [ ] **Step 7: 写 EmitAuditTrace（Finally）+ NotifyFailure（OnFailure）**
```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

public sealed class EmitAuditTrace : IWorkflowStep<ReviewState>
{
    public Task<StepResult<ReviewState>> ExecuteAsync(
        ReviewState state, StepContext context, CancellationToken cancellationToken)
    {
        // 审计轨迹摘要写入 ExecutionResult（OutputPayload 由适配器从事件流组装）
        var audit = new JsonObject { ["plan"] = state.Plan, ["reviews"] = state.Reviews.Count, ["approved"] = state.Decision?.Approved };
        return Task.FromResult(StepResult<ReviewState>.FromState(state with
        {
            ExecutionResult = (state.ExecutionResult ?? "") + $"\nAuditTrace:{audit.ToJsonString()}"
        }));
    }
}

public sealed class NotifyFailure : IWorkflowStep<ReviewState>
{
    public Task<StepResult<ReviewState>> ExecuteAsync(
        ReviewState state, StepContext context, CancellationToken cancellationToken)
        => Task.FromResult(StepResult<ReviewState>.FromState(state with
        {
            FailureReason = $"Workflow failed at phase {state.CurrentPhase}."
        }));
}
```

- [ ] **Step 8: build + 跑 Task 6 的折叠测试，确认步骤类被 saga 引用**

```bash
dotnet build
dotnet test --filter "ReviewStateFoldTests|PhaseStatusMapTests|PendingInputBuilderTests|MockReviewChatClientTests"
```

- [ ] **Step 9: Commit**

```bash
git add Steps
git commit -m "feat(strategos): add IWorkflowStep reviewers + aggregator + compensation + audit"
```

---

## Task 8: DurableHttpAdapter（run/status/respond 契约映射）

**Files:**
- Create: `Adapters/DurableHttpAdapter.cs`
- Test: `Tests/DurableHttpAdapterTests.cs`

**Interfaces:**
- Consumes: `IMessageBus`（`Wolverine`，生产侧 publish 启动命令）、`IDocumentStore`（`Marten`，`QuerySession().LoadAsync<ReviewState>` 与 `.Events.FetchStreamAsync`）、`StartDurableAgentReviewCommand`（生成器产出，`Guid WorkflowId, ReviewState InitialState`）、`Resume{ApprovalPoint}ApprovalCommand`（`[SagaIdentity] Guid WorkflowId, ApprovalDecision Decision, string? SelectedOptionId, string? Instructions`，核验 `CommandsEmitter.cs:380`）、`ApprovalDecision`（`Strategos.Models`，`Approved/Rejected/Deferred`）
- Produces: `Task<AgentWorkflowRunResult> StartRunAsync(...)`、`Task<AgentWorkflowRunSnapshot?> GetStatusAsync(...)`、`Task<AgentWorkflowRunSnapshot?> RespondAsync(...)`

- [ ] **Step 1: 写适配器**
```csharp
using Marten;
using OpenClaw.Core.Models;
using OpenClaw.StrategosWorkflowHost.Workflows;
using Strategos.Models;
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

        var initial = new ReviewState
        {
            Id = sagaId,
            WorkflowId = sagaId,
            UserRequest = request.Input
        };
        // Start 命令无 [SagaIdentity]（核验 CommandsEmitter.cs:334），由 WorkflowId 路由
        var cmd = new StartDurableAgentReviewCommand(sagaId, initial);
        await bus.PublishAsync(cmd, ct);

        return new AgentWorkflowRunResult
        {
            BackendId = request.Metadata?.GetValueOrDefault("backendId") ?? "durable-review",
            WorkflowId = workflowName,
            RunId = runId,
            Status = AgentWorkflowStatuses.Queued,
            Events = [],
            Metadata = BuildMetadata(request)
        };
    }

    public async Task<AgentWorkflowRunSnapshot?> GetStatusAsync(
        string workflowName, string runId, CancellationToken ct)
    {
        if (!TryParseSagaId(runId, out var sagaId))
            return null;

        await using var query = store.QuerySession();
        var state = await query.LoadAsync<ReviewState>(sagaId);  // 内联快照投影
        if (state is null)
            return null;

        var status = PhaseStatusMap.ToOpenClawStatus(state.CurrentPhase);
        var events = await query.Events.FetchStreamAsync(sagaId);  // 事件流审计
        var eventSummaries = events
            .Select(e => new AgentWorkflowEvent
            {
                Id = $"evt_{e.Id}",
                Type = e.Data.GetType().Name,
                WorkflowId = workflowName,
                RunId = runId,
                Status = status,
                Summary = e.Data.GetType().Name,
                TimestampUtc = e.Timestamp.UtcDateTime
            })
            .ToArray();

        var pending = status == AgentWorkflowStatuses.WaitingForInput
            ? PendingInputBuilder.Build(state, "operator-approval")
            : [];

        return new AgentWorkflowRunSnapshot
        {
            WorkflowId = workflowName,
            RunId = runId,
            BackendId = "durable-review",
            Status = status,
            Output = state.ExecutionResult,
            OutputPayload = BuildOutputPayload(state),
            PendingInputs = pending,
            Events = eventSummaries,
            Metadata = BuildMetadata(state)
        };
    }

    public async Task<AgentWorkflowRunSnapshot?> RespondAsync(
        string workflowName, string runId, AgentWorkflowResponse response, CancellationToken ct)
    {
        if (!TryParseSagaId(runId, out var sagaId))
            return null;

        var decision = response.Approved == true
            ? ApprovalDecision.Approved
            : ApprovalDecision.Rejected;
        // 审批恢复命令（核验 CommandsEmitter.cs:380）；{ApprovalPoint} 名在 Task 6 build 后确认
        var resume = new ResumeDurableAgentReviewApprovalCommand(
            sagaId, decision, response.Approved == true ? "approve" : "reject", response.Comment);
        await bus.PublishAsync(resume, ct);
        return await GetStatusAsync(workflowName, runId, ct);
    }

    private static bool TryParseSagaId(string runId, out Guid sagaId)
    {
        if (runId.Length > 4 && runId.StartsWith("run_", StringComparison.Ordinal)
            && Guid.TryParse(runId.AsSpan(4), out sagaId))
            return true;
        sagaId = default;
        return false;
    }

    private static JsonElement? BuildOutputPayload(ReviewState s)
    {
        var node = new System.Text.Json.Nodes.JsonObject
        {
            ["workflowId"] = s.WorkflowId,
            ["plan"] = s.Plan,
            ["approved"] = s.Decision?.Approved,
            ["phase"] = s.CurrentPhase,
            ["reviewCount"] = s.Reviews.Count,
        };
        using var doc = JsonDocument.Parse(node.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static Dictionary<string, string> BuildMetadata(AgentWorkflowRequest r)
    {
        var m = new Dictionary<string, string>(StringComparer.Ordinal) { ["sample"] = "StrategosWorkflowHost" };
        if (r.SessionId is not null) m["sessionId"] = r.SessionId;
        return m;
    }
    private static Dictionary<string, string> BuildMetadata(ReviewState s)
        => new(StringComparer.Ordinal) { ["sample"] = "StrategosWorkflowHost", ["phase"] = s.CurrentPhase };
}
```
> `ResumeDurableAgentReviewApprovalCommand` 的确切类型名 `{ApprovalPoint}` 在 Task 6 build 后由生成器产出确认（审批 point 名取自 `AwaitApproval<Operator>` 的上下文，默认派生名）。若名不符，按 `obj/Generated` 实际名修。`IMessageBus.PublishAsync` 是 Wolverine 生产侧 API（核验：测试用 `TrackActivity().PublishMessageAndWaitAsync`，生产侧用 `IMessageBus`——执行时确认 `IMessageBus.PublishAsync(object, CancellationToken)` 签名）。

- [ ] **Step 2: 写集成测试** — 用 `WebApplicationFactory<Program>` + Mock LLM + Testcontainers Postgres：POST `/run` → 轮询 `/status` 直到 `waiting_for_input` → POST `/respond`{approved:true} → 断言 `completed` + `Events` 非空 + `runId`==`run_xxx`。
> 该测试在 Task 9（端点接线）完成后跑通；本任务先写 adapter 单测路径（用 NSubstitute 替身 `IMessageBus`/`IDocumentStore`，断言 `StartRunAsync` 调了 `PublishAsync`、`GetStatusAsync` 在 `state==null` 时返回 null→端点 404）。

- [ ] **Step 3: Commit**

```bash
git add Adapters/DurableHttpAdapter.cs Tests/DurableHttpAdapterTests.cs
git commit -m "feat(strategos): add DurableHttpAdapter (contract <-> saga mapping)"
```

---

## Task 9: Program.cs 端点 + DI + IChatClient + appsettings

**Files:**
- Modify: `Program.cs`（替换 Task 1 的最小骨架）
- Create: `Configuration/LlmMode.cs`
- Create: `appsettings.json`, `appsettings.Development.json`

**Interfaces:**
- Consumes: Task 8 `DurableHttpAdapter`、Task 5 `MockReviewChatClient`、`CoreJsonContext`、`SecretResolver`、生成器 `AddDurableAgentReviewWorkflow()`
- Produces: 三端点 + 三模式 LLM 注册

- [ ] **Step 1: 写 LlmMode + IChatClient 工厂**

`Configuration/LlmMode.cs`（核验：`SecretResolver` 用 `env:`/`raw:`，非 `ref:`）：
```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenClaw.Core.Security;

namespace OpenClaw.StrategosWorkflowHost.Configuration;

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

public static class LlmRegistration
{
    public static IChatClient BuildChatClient(this IServiceProvider sp)
    {
        var opts = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
        return opts.Mode switch
        {
            LlmMode.Mock => new MockReviewChatClient(),
            LlmMode.DirectOpenAI => BuildOpenAi(sp, opts.Direct),
            LlmMode.BackThroughGateway => BuildOpenAi(sp, opts.Gateway),
            _ => throw new InvalidOperationException($"Unknown LLM mode '{opts.Mode}'.")
        };
    }

    private static IChatClient BuildOpenAi(IServiceProvider sp, LlmEndpointOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.Endpoint))
            throw new InvalidOperationException("LLM Endpoint is required for non-Mock modes.");
        var logger = sp.GetService<ILoggerFactory>()?.CreateLogger("Llm");
        var key = SecretResolver.Resolve(o.ApiKeySecret, logger);
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException($"LLM ApiKeySecret '{o.ApiKeySecret}' resolved empty.");
        // OpenAI 兼容客户端：用 MEAI 的 OpenAIClient（若仓库未引 OpenAI SDK，改用任意 OpenAI 兼容 IChatClient 实现）
        // 执行时确认可用的 OpenAI 兼容 IChatClient 包并替换此处。
        throw new NotImplementedException("Wire a concrete OpenAI-compatible IChatClient (e.g. OpenAIClient.AsChatClient) in Direct/Gateway modes.");
    }
}
```
> **`BuildOpenAi` 留 `NotImplementedException` 是有意的**：OpenClaw 仓库不引 OpenAI SDK，Direct/Gateway 模式的具体 `IChatClient` 实现须在执行时选定一个兼容包（如 `OpenAI` 或 `Azure.AI.OpenAI`）。Mock 模式（默认）不依赖此分支即可跑通全部 P0 验收。README 标注 Direct/Gateway 为"需用户补 IChatClient 实现"。这避免了在本计划里伪造一个未核验的包引用。

- [ ] **Step 2: 写 appsettings**
`appsettings.json`：
```json
{
  "ConnectionStrings": { "Postgres": "Host=localhost;Port=5432;Database=strategos;Username=strategos;Password=strategos" },
  "Strategos": {
    "Llm": {
      "Mode": "Mock",
      "Direct":    { "Endpoint": "https://api.openai.com/v1", "ApiKeySecret": "env:OPENAI_API_KEY", "Model": "gpt-4o-mini" },
      "Gateway":   { "Endpoint": "http://127.0.0.1:18789/v1",  "ApiKeySecret": "env:OPENCLAW_GATEWAY_KEY", "Model": "deepseek-v4-flash" }
    }
  }
}
```
`appsettings.Development.json`：`{"Strategos":{"Llm":{"Mode":"Mock"}}}`。

- [ ] **Step 3: 写 Program.cs（端点 + DI + 并发重试）**

```csharp
using JasperFx.Resources;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.StrategosWorkflowHost.Adapters;
using OpenClaw.StrategosWorkflowHost.Configuration;
using OpenClaw.StrategosWorkflowHost.Workflows;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Marten;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<LlmOptions>(builder.Configuration.GetSection("Strategos:Llm"));
builder.Services.AddSingleton<IChatClient>(sp => sp.BuildChatClient());
builder.Services.AddSingleton<DurableHttpAdapter>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Add(CoreJsonContext.Default));

builder.Host.UseWolverine(opts =>
{
    var pg = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");

    // Fork 三评审者并发 append 同一 Marten 流 → 乐观并发冲突需重试（核验 EventSourcedHostFixture.cs:86-101）
    var cooldown = new[] { TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(800) };
    opts.OnException(ex => ex is Marten.Exceptions.ConcurrentUpdateException
        || ex.GetType().Name.Contains("EventStreamUnexpected", StringComparison.Ordinal))
        .RetryWithCooldown(cooldown);

    opts.Services.AddMarten(storeOptions =>
    {
        storeOptions.Connection(pg);
        storeOptions.AutoCreateSchemaObjects = JasperFx.AutoCreate.All;
    })
    .IntegrateWithWolverine()
    .ApplyAllDatabaseChangesOnStartup();

    opts.Services.AddDurableAgentReviewWorkflow();
    opts.Services.AddResourceSetupOnStartup();
});

var app = builder.Build();

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

app.MapGet("/", () => "OpenClaw.StrategosWorkflowHost");
app.Run();
```

- [ ] **Step 4: 跑 Task 8 集成测试（端到端 run→status→respond）**

```bash
dotnet test --filter DurableHttpAdapterTests
```
> 此测试现在应跑通端到端（Mock LLM + Testcontainers Postgres + WebApplicationFactory）。

- [ ] **Step 5: Commit**

```bash
git add Program.cs Configuration appsettings*.json
git commit -m "feat(strategos): wire endpoints, DI, IChatClient modes, fork-concurrency retry"
```

---

## Task 10: KillRestartTests（CI 自动化 kill-restart 验收）

**Files:**
- Create: `Tests/KillRestartTests.cs`
- Create: `docker-compose.yml`, `Dockerfile`（Task 11 同步，但本测试依赖 compose）

**Interfaces:**
- Consumes: 三端点契约（核验）、docker compose

- [ ] **Step 1: 写测试（编排 docker compose kill/restart）**

`Tests/KillRestartTests.cs`：
```csharp
// 流程（spec §9）：
// 1. docker compose up -d postgres strategos-host（宿主 :5097）
// 2. POST /api/workflows/durable-agent-review/run → runId, 轮询至 waiting_for_input
// 3. docker compose kill strategos-host
// 4. docker compose up -d strategos-host
// 5. GET status/{runId} → 断言 waiting_for_input（状态从最后持久化相位恢复）
// 6. POST respond{approved:true} → completed
// 7. 断言 Events 含崩溃前事件（事件流连续性）
//
// 关键断言（第 5 步）: Assert.Equal(AgentWorkflowStatuses.WaitingForInput, status)
```
> 测试用 `Process.Start("docker", ...)` 编排 compose；或用 `DockerComposeFixture` 抽象。具体 compose 文件路径与 project name 在执行时固定。

- [ ] **Step 2: 写 docker-compose.yml + Dockerfile**

`docker-compose.yml`：
```yaml
services:
  postgres:
    image: postgres:17
    environment:
      POSTGRES_USER: strategos
      POSTGRES_PASSWORD: strategos
      POSTGRES_DB: strategos
    ports: ["5432:5432"]
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U strategos"]
      interval: 2s
      timeout: 3s
      retries: 20

  strategos-host:
    build: .
    depends_on:
      postgres: { condition: service_healthy }
    environment:
      ConnectionStrings__Postgres: "Host=postgres;Port=5432;Database=strategos;Username=strategos;Password=strategos"
      Strategos__Llm__Mode: "Mock"
    ports: ["5097:8080"]
```
`Dockerfile`（多阶段 JIT）：
```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish samples/OpenClaw.StrategosWorkflowHost/OpenClaw.StrategosWorkflowHost.csproj -c Release -o /app
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "OpenClaw.StrategosWorkflowHost.dll"]
```

- [ ] **Step 3: 本地手动验证 compose 起得来 + 测试通过**

```bash
docker compose up -d --build
dotnet test --filter KillRestartTests
docker compose down
```

- [ ] **Step 4: Commit**

```bash
git add Tests/KillRestartTests.cs docker-compose.yml Dockerfile
git commit -m "test(strategos): add CI kill-restart acceptance test + docker compose"
```

---

## Task 11: README + 文档

**Files:**
- Create: `README.md`

- [ ] **Step 1: 写 README** — 三路径（Mock/Direct/Gateway）+ kill-restart 步骤 + 网关配置示例（`Kind=maf-durable-http`, `BaseUrl=http://127.0.0.1:5097`, `WorkflowName=durable-agent-review`）+ 测试位置偏离说明（spec §9：同级测试项目而非 `src/OpenClaw.Tests/`）+ Direct/Gateway 需补 `IChatClient` 实现的说明。
- [ ] **Step 2: Commit**

---

## Task 12: 许可与 PR 描述

**Files:**
- Create/Modify: 仓库根 `THIRD-PARTY` 或 `NOTICE`（加 Apache-2.0 Strategos 条目 +版权声明）

- [ ] **Step 1: 加许可条目** — `LevelUp.Strategos.*` Apache-2.0，版权 `Copyright (c) Levelup Software`。
- [ ] **Step 2: 写 PR 描述** — 披露测试位置偏离（review-checklist 扩展 PR 清单）、披露 Direct/Gateway `IChatClient` 未实现（Mock 覆盖 P0 验收）、引用 spec 与本计划。
- [ ] **Step 3: 最终验收** — 逐条核对 spec §11 验收清单，确认全绿后开 PR。

```bash
git add THIRD-PARTY
git commit -m "docs(strategos): add Apache-2.0 Strategos license notice"
```

---

## Self-Review

**1. Spec coverage:** spec §3 拓扑→Task 1/9；§4 布局→File Structure；§5 LLM 模式→Task 5/9；§6 工作流→Task 2/6/7；§7 适配器→Task 8；§8 错误处理→Task 7(补偿)/8(HTTP)/9(并发重试)；§9 测试→Task 3/4/5/8/10；§10 验证项→全部由 4 agent + 源码核验解决；§11 验收→Task 10/12；§12 风险→Direct/Gateway 未实现（Task 9 标注）。**未覆盖：** spec §3.3 MCP 第二条线、§13 P2a/b/c——这些是 P0 范围外，正确排除。

**2. Placeholder scan:** 无 "TBD/TODO"。`LlmRegistration.BuildOpenAi` 故意抛 `NotImplementedException` 并显式说明（Mock 覆盖 P0，Direct/Gateway 需执行时选包）——这是有据的边界，非占位符。`HostBootstrapTests` 与 `KillRestartTests` 的 Testcontainers/compose 脚手架标注"执行时按当前 API 补全"——这两处是本机未核验的测试基建，已在步骤内给出回退方案。

**3. Type consistency:** `ReviewState` 在 Task 2/6/7/8 一致；`PhaseStatusMap.ToOpenClawStatus` 在 Task 3/8 一致；`TryParseSagaId` 在 Task 8 三处一致；`AgentWorkflowStatuses` 常量值与核验一致。生成器产出的事件/命令名（`DurableAgentReviewStarted`、`{Step}Completed`、`StartDurableAgentReviewCommand`、`ResumeDurableAgentReviewApprovalCommand`）跨 Task 2/6/8 一致，并均标注"build 后确认"。

**已知执行期不确定性（非占位符，有回退）：**
- `WebApplication`+`UseWolverine` 无样例→Task 1 特征化测试先证；受阻回退 `Host.CreateDefaultBuilder`。
- 生成器事件/命令确切命名→Task 6 build 后回填 Task 2/8。
- `RequireConfidence` 在主路径是否强制→Task 7 集成测试确认；不强制则步骤内手动判定。
- Direct/Gateway `IChatClient` 包→Task 9 留 `NotImplementedException`，Mock 覆盖 P0。
- Testcontainers/compose 测试脚手架→Task 1/10 执行时补全。

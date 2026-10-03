# OpenClaw MetaSkill 调用 API 实施计划

> **供执行代理使用：** 必须使用 `superpowers:subagent-driven-development`（推荐）或 `superpowers:executing-plans`，逐项执行本计划。使用复选框（`- [ ]`）跟踪步骤。

**目标：** 新增经过身份验证的 Gateway API，通过真实的 AgentRuntime DAG 执行器调用指定 MetaSkill，并使用持久化幂等账本安全地重放请求。

**架构：** 在 `IAgentRuntime` 中新增公开的直接调用方法，并分别由 Native 实现 `AgentRuntime` 和 Microsoft Agent Framework 实现 `MafAgentRuntime` 提供实现。专用 Gateway 服务先执行现有的集成认证及会话所有权检查，再调用该方法。将调用方作用域内的幂等键、请求指纹、调用状态和终态结果持久化到已配置的 Gateway memory storage 路径；进程重启后，绝不自动重试遗留为运行中的调用。

**技术栈：** .NET 10、C# 14、ASP.NET Core Minimal APIs、`System.Text.Json` 源生成、xUnit、NSubstitute。

## 全局约束

- 目标框架为 .NET 10、C# 版本为 14，并遵循 `E:\GitHub\openclaw.net\AGENTS.md`。
- 新增内容须兼容 NativeAOT；新增持久化数据和 HTTP DTO 使用 JSON 源生成元数据，不使用基于反射的序列化。
- 复用 Gateway 现有的认证、调用方身份、会话所有权、会话锁和持久化约定；新 endpoint 不得经由 `/messages` 或外部 workflow runner 执行。
- 同一调用方、同一幂等键和同一请求返回原调用记录；同一幂等键对应不同请求时返回 HTTP 409。
- 并发重复请求不得启动第二次 DAG 执行。启动时只将没有活动 invocation lease 的持久化 `Running` 记录转为 `Uncertain`；仍由其他 Gateway 进程持有 lease 的调用必须保持 `Running`，不得自动重新执行。
- 不得声称在任意进程或机器故障下都能 exactly-once 执行。在 DAG 执行前持久化请求声明，只能提供持久化去重和明确的不确定状态。
- Gateway 幂等记录的保留期不得短于 DrasiWake outbox 的最长重试期限；该值必须可配置，并向运维人员说明。
- 测试使用 xUnit/NSubstitute，并遵循仓库测试命名约定 `MethodName_WhenCondition_ShouldExpectedBehavior`。

---

### 任务 1：在运行时契约中公开显式 MetaSkill 执行能力

**文件：**

- Modify: `E:\GitHub\openclaw.net\src\OpenClaw.Agent\IAgentRuntime.cs`
- Modify: `E:\GitHub\openclaw.net\src\OpenClaw.Agent\AgentRuntime.cs`
- Modify: `E:\GitHub\openclaw.net\src\OpenClaw.MicrosoftAgentFrameworkAdapter\MafAgentRuntime.cs`
- Test: `E:\GitHub\openclaw.net\src\OpenClaw.Tests\AgentRuntimeTests.cs`
- Test: `E:\GitHub\openclaw.net\src\OpenClaw.Tests\MafAdapterTests.cs`

**接口：**

- 在 `IAgentRuntime` 中添加：`Task<string> InvokeMetaSkillAsync(Session session, string skillName, string? input, CancellationToken cancellationToken = default);`
- `AgentRuntime` 和 `MafAgentRuntime` 都必须将调用直接转发至各自现有的 `ExecuteMetaSkillWithCallerContextAsync(session, skillName, input, cancellationToken, callerCredentialContext: null)` 路径。不得构造用户消息、解析 trigger phrase，也不得通过开启另一轮 agent turn 来调用 `meta_invoke` 工具。
- 此方法返回现有 MetaSkill DAG 执行器的同类结果字符串，并保留当前对技能禁用、技能不存在、取消及工具策略的处理行为。

- [x] **步骤 1：为两种运行时实现添加行为测试**

在现有 Native 和 MAF 运行时测试类中添加名为 `InvokeMetaSkillAsync_WhenSkillIsExplicit_ShouldExecuteThatMetaSkillDag` 的测试。配置一个加载了两个 MetaSkill 的运行时，并让它们返回不同结果；按名称调用第二个技能，断言返回第二个技能的结果且第一个技能未执行。断言中还要包含传入的 input，以证明直接调用方法会原样转发输入。

- [x] **步骤 2：运行针对性测试，确认当前因缺少接口而编译失败**

在 `E:\GitHub\openclaw.net` 目录下运行：

```powershell
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~InvokeMetaSkillAsync_WhenSkillIsExplicit_ShouldExecuteThatMetaSkillDag"
```

预期结果：测试构建因 `IAgentRuntime` 及其实现尚未公开 `InvokeMetaSkillAsync` 而失败。

- [x] **步骤 3：新增公开接口成员，并转发给现有的直接执行器**

在 `IAgentRuntime` 和两种运行时类中使用以下签名：

```csharp
Task<string> InvokeMetaSkillAsync(
    Session session,
    string skillName,
    string? input,
    CancellationToken cancellationToken = default);
```

每种实现都应将调用转发给现有的上下文 MetaSkill 执行器，并传入 null caller-credential context。保留私有 DAG 实现作为 MetaSkill 解析、策略执行和实际运行的唯一责任方。

- [x] **步骤 4：重新运行针对性测试**

运行步骤 2 中的命令。

预期结果：两种运行时的测试均通过，证明可以按指定名称直接执行技能，并传入给定 input。

---

### 任务 2：新增调用模型、保留期配置和持久化账本

**文件：**

- Create: `E:\GitHub\openclaw.net\src\OpenClaw.Gateway\Models\MetaInvocationModels.cs`
- Create: `E:\GitHub\openclaw.net\src\OpenClaw.Gateway\MetaInvocationJsonContext.cs`
- Create: `E:\GitHub\openclaw.net\src\OpenClaw.Gateway\MetaInvocationStore.cs`
- Modify: `E:\GitHub\openclaw.net\src\OpenClaw.Core\Models\GatewayConfig.cs`
- Modify: `E:\GitHub\openclaw.net\src\OpenClaw.Core\Validation\ConfigValidator.cs`
- Modify: `E:\GitHub\openclaw.net\src\OpenClaw.Gateway\Composition\SecurityServicesExtensions.cs`
- Modify: `E:\GitHub\openclaw.net\src\OpenClaw.Gateway\appsettings.json`
- Test: `E:\GitHub\openclaw.net\src\OpenClaw.Tests\MetaInvocationStoreTests.cs`
- Test: `E:\GitHub\openclaw.net\src\OpenClaw.Tests\ConfigValidatorTests.cs`

**接口：**

- 在 `GatewayConfig` 中添加 `MetaInvocations` 属性，类型为 `MetaInvocationsConfig`；`RetentionDays` 默认值为 `30`，且必须验证其值至少为 `1`。
- `MetaInvocationStatus` 只包含 `Running`、`Completed` 和 `Uncertain` 三种状态。
- `MetaInvocationRecord` 保存 `CallerId`、`IdempotencyKey`、`RequestHash`、`InvocationId`、`Status`、可空的 `Result`、可空的 `Error` 和 `CreatedAtUtc`。
- `MetaInvocationClaimKind` 只包含 `Started`、`Existing` 和 `Conflict` 三种类型。`MetaInvocationClaim` 同时包含 claim 类型及对应记录。
- `MetaInvocationStore` 提供以下方法：`BeginOrGetAsync(string callerId, string idempotencyKey, string requestHash, CancellationToken cancellationToken)`、`CompleteAsync(string callerId, string idempotencyKey, string result, CancellationToken cancellationToken)`、`MarkUncertainAsync(string callerId, string idempotencyKey, string error, CancellationToken cancellationToken)` 和 `MarkInterruptedInvocationsUncertainAsync(CancellationToken cancellationToken)`。
- 账本文件为 `${GatewayConfig.Memory.StoragePath}/meta-invocations.json`；只能使用 `MetaInvocationJsonContext` 对其进行序列化。
- 每条运行中的调用持有以 `InvocationId` 区分的独占 lease 文件；服务启动时探测 lease，仅恢复没有活动 lease 的 `Running` 记录。完成、不确定或进程释放时关闭 lease。

- [x] **步骤 1：先编写账本和配置验证测试**

添加以下测试用例：

```csharp
[Fact]
public async Task BeginOrGetAsync_WhenKeyAndRequestMatch_ShouldReturnExistingRecord()

[Fact]
public async Task BeginOrGetAsync_WhenKeyMatchesButRequestDiffers_ShouldReturnConflict()

[Fact]
public async Task BeginOrGetAsync_WhenCalledConcurrently_ShouldCreateOnlyOneInvocation()

[Fact]
public async Task MarkInterruptedInvocationsUncertainAsync_WhenStoreIsReopened_ShouldNotMakeRunningInvocationRunnable()

[Fact]
public async Task MarkInterruptedInvocationsUncertainAsync_WhenAnotherProcessOwnsLease_ShouldKeepInvocationRunning()

[Fact]
public async Task CompleteAsync_WhenStoreIsReopened_ShouldReplayPersistedResult()
```

并发测试使用多个 task，以相同调用方、幂等键和请求哈希调用 `BeginOrGetAsync`，并断言只有一个 claim 为 `Started`，且所有结果只有一个 `InvocationId`。另添加配置验证测试：拒绝 `RetentionDays = 0`，接受 `RetentionDays = 30`。

- [x] **步骤 2：运行针对性测试，确认其按预期失败**

在 `E:\GitHub\openclaw.net` 目录下运行：

```powershell
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~MetaInvocationStoreTests|FullyQualifiedName~ConfigValidatorTests"
```

预期结果：由于配置类型和账本契约尚不存在，构建或测试失败。

- [x] **步骤 3：实现强类型记录、JSON 源生成元数据和配置**

按“接口”小节定义准确的状态和 claim 枚举。添加 `MetaInvocationsConfig.RetentionDays = 30`，并在 `GatewayConfig` 中添加一个初始化为新配置对象的 `MetaInvocations` 属性；验证器需覆盖 `RetentionDays >= 1`。同时在 `src/OpenClaw.Gateway/appsettings.json` 中添加 `MetaInvocations` 节，并将 `RetentionDays` 设为 `30`。

为持久化记录集合和调用记录声明 `MetaInvocationJsonContext` 源生成元数据。不得增加基于反射的序列化回退路径。

- [x] **步骤 4：实现持久化 claim 和状态更新账本**

使用调用方作用域内的复合键 `(CallerId, IdempotencyKey)`。`BeginOrGetAsync` 必须在一次串行化的账本变更中完成以下操作：创建并持久化新的 `Running` 记录；请求哈希相同时返回 `Existing`；请求哈希不同时返回 `Conflict`。每次状态迁移都必须原子持久化，采用 `src/OpenClaw.Core/Actions/DurableActionJournal.cs` 使用的文件锁和 JSON 原子写入方式。

账本初始化时，在 Gateway 接受请求之前检查每条持久化 `Running` 记录的 invocation lease；只有无法由其他进程取得活动 lease 的调用才改为 `Uncertain` 并持久化。绝不清理 `Running` 记录。只在后续 claim 时清理年龄达到 `RetentionDays` 的记录；已完成和不确定记录均采用同一配置的保留期。使用 `Memory.StoragePath` 和 `MetaInvocations.RetentionDays` 配置，将账本注册为 Gateway singleton。

- [x] **步骤 5：重新运行账本和配置测试**

运行步骤 2 中的命令。

预期结果：所有匹配测试通过，包括重新打开同一路径的账本后确认已完成结果可重放、被中断调用仍处于不确定状态。

---

### 任务 3：新增专用的幂等 MetaSkill endpoint

**文件：**

- Create: `E:\GitHub\openclaw.net\src\OpenClaw.Gateway\MetaInvocationService.cs`
- Modify: `E:\GitHub\openclaw.net\src\OpenClaw.Gateway\Composition\IntegrationApiFacade.cs`
- Modify: `E:\GitHub\openclaw.net\src\OpenClaw.Gateway\Endpoints\IntegrationEndpoints.cs`
- Modify: `E:\GitHub\openclaw.net\src\OpenClaw.Gateway\Models\MetaInvocationModels.cs`
- Modify: `E:\GitHub\openclaw.net\src\OpenClaw.Gateway\MetaInvocationJsonContext.cs`
- Test: `E:\GitHub\openclaw.net\src\OpenClaw.Tests\GatewayAdminEndpointTests.cs`

**接口：**

- 新增 `POST /api/integration/meta-invocations`，使用 `Idempotency-Key` header 和以下 JSON body：`{ "skill": "<explicit-skill-name>", "input": "<input-or-null>", "sessionId": "<owned-session-id>" }`。
- 添加 `MetaSkillInvocationRequest(string Skill, string? Input, string SessionId)` 和 `MetaInvocationResponse(Guid InvocationId, MetaInvocationStatus Status, string? Result, string? Error, DateTimeOffset CreatedAtUtc)`。
- 服务接收已认证的稳定调用方 ID、幂等键、请求和执行委托。将固定顺序的源生成 JSON 表示中的准确请求字段 `(Skill, Input, SessionId)` 通过 SHA-256 计算指纹。
- 响应映射：首次完成或重放已完成请求时返回 HTTP 200；相同请求的执行中重复调用返回 HTTP 202，且使用相同 `InvocationId` 和 `Running` 状态；同一幂等键对应不同请求时返回 HTTP 409；调用状态不确定时返回 HTTP 409 和 `Uncertain` 状态。使用相同 key 和 body 再次 POST 即可查询状态或重放结果；不新增独立轮询路由。

- [x] **步骤 1：添加显式路由和持久幂等的 endpoint 测试**

在 `GatewayAdminEndpointTests.cs` 中添加针对性测试，覆盖以下行为：

```csharp
[Fact]
public async Task MetaInvocationEndpoint_WhenSkillIsNamed_ShouldInvokeRuntimeDirectly()

[Fact]
public async Task MetaInvocationEndpoint_WhenSameKeyAndBodyAreReplayed_ShouldReturnOriginalResult()

[Fact]
public async Task MetaInvocationEndpoint_WhenSameKeyHasDifferentBody_ShouldReturnConflict()

[Fact]
public async Task MetaInvocationEndpoint_WhenDuplicateArrivesDuringExecution_ShouldNotInvokeRuntimeTwice()

[Fact]
public async Task MetaInvocationEndpoint_WhenCallerDoesNotOwnSession_ShouldRejectBeforeClaimingKey()
```

使用 Gateway 现有的 endpoint 测试宿主，并替代 `IAgentRuntime`。断言 `InvokeMetaSkillAsync` 收到明确的技能名和 input；断言普通消息队列及 workflow runner 均未被调用；并断言并发重复请求只会调用运行时一次。

- [x] **步骤 2：只运行新增的 endpoint 测试，确认其按预期失败**

在 `E:\GitHub\openclaw.net` 目录下运行：

```powershell
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~MetaInvocationEndpoint"
```

预期结果：由于路由和 facade 操作尚不存在，新增测试失败。

- [x] **步骤 3：实现服务和 endpoint 调用流程**

路由必须先使用现有的 `AuthorizeAndConsume` 路径，并传入 `endpointScope: "integration.mutate"` 和 `requireCsrf: true`，之后才能读取或声明幂等键。验证幂等键存在且长度有上限、`Skill` 和 `SessionId` 非空；在返回任何已存储记录之前，先确认调用方有权访问所请求的 session。

facade/service 必须解析现有且归调用方所有的 session，并使用现有的每 session 锁。在调用 `IAgentRuntime.InvokeMetaSkillAsync` 前先调用 `MetaInvocationStore.BeginOrGetAsync`。只有 claim 类型为 `Started` 时才能执行 DAG。返回结果前，必须先持久化 `Completed` 状态和执行结果。相同请求返回其已持久化记录或执行中记录，不再调用运行时；请求不匹配则返回 409。如果 claim 持久化后执行抛出异常或被取消，则持久化 `Uncertain` 并返回该持久化状态；不得自动再次执行。

通过 `MetaInvocationJsonContext` 序列化新的请求和响应。不得改变 `/api/integration/messages` 或 `/api/integration/workflows/{workflowId}/runs` 的行为。

- [x] **步骤 4：运行针对性的 endpoint 测试**

运行步骤 2 中的命令。

预期结果：五项 endpoint 测试全部通过；无权访问 session 时不占用调用方的幂等键；重放返回已存储结果；相同请求的并发调用只执行一次 DAG。

---

### 任务 4：编写 HTTP 契约文档并进行跨模块验证

**文件：**

- Create: `E:\GitHub\openclaw.net\docs\integrations\meta-skill-invocation-api.md`
- Test: `E:\GitHub\openclaw.net\src\OpenClaw.Tests\AgentRuntimeTests.cs`
- Test: `E:\GitHub\openclaw.net\src\OpenClaw.Tests\MafAdapterTests.cs`
- Test: `E:\GitHub\openclaw.net\src\OpenClaw.Tests\MetaInvocationStoreTests.cs`
- Test: `E:\GitHub\openclaw.net\src\OpenClaw.Tests\GatewayAdminEndpointTests.cs`

**接口：**

- 记录准确的 `POST /api/integration/meta-invocations` URL、Bearer/browser 认证与 CSRF 要求、请求字段、`Idempotency-Key`、四种 HTTP 结果映射、调用方作用域及进程重启中断调用的处理行为。
- 记录 `Gateway.MetaInvocations.RetentionDays` 配置及其默认值 `30`，并要求运维人员将其设置为不短于 DrasiWake outbox 的最长重试期限。说明不确定结果需要调用方或运维人员协调处理，不会自动重试。

- [x] **步骤 1：新增面向运维人员的 API 文档**

文档应包含一个带有 `skill`、`input`、`sessionId` 和 `Idempotency-Key` 的请求示例；展示已完成响应的结构，并为 `200`、`202` 和 `409` 提供简明状态表。明确说明该路由直接执行指定的 MetaSkill DAG，相同请求会重放原结果，且此行为不构成 exactly-once 保证。

- [x] **步骤 2：运行组合行为测试**

在 `E:\GitHub\openclaw.net` 目录下运行：

```powershell
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~InvokeMetaSkillAsync_WhenSkillIsExplicit_ShouldExecuteThatMetaSkillDag|FullyQualifiedName~MetaInvocationStoreTests|FullyQualifiedName~MetaInvocationEndpoint|FullyQualifiedName~ConfigValidatorTests"
```

预期结果：所有匹配的运行时、持久化、endpoint 和配置测试均通过。

- [x] **步骤 3：构建解决方案并检查最终差异**

在 `E:\GitHub\openclaw.net` 目录下运行：

```powershell
dotnet build OpenClaw.Net.slnx --configuration Release --no-restore
git diff --check
```

预期结果：解决方案构建无错误，`git diff --check` 不报告空白字符错误。检查最终差异，确认 API 不会经由普通消息或外部 workflow 执行；启动时会将中断调用转为 `Uncertain`；保留期仍可配置。

---

## 计划自审

- **规格覆盖：** 任务 1 和任务 3 覆盖显式指定目标 MetaSkill 执行。任务 2 和任务 3 覆盖 Gateway 持久化幂等、同键重放、请求不匹配、并发重复抑制、重启后不确定状态及可配置保留期。全局约束和任务 4 覆盖 Bridge 重试窗口的保留期要求及有边界的可靠性声明。现有认证、调用方身份、会话所有权、会话锁和源生成序列化均已列为明确实现要求。
- **占位符检查：** 不含 `TBD`、`TODO`、待定设计决策或未说明的测试行为。每项任务都列出了具体文件、接口、针对性测试名称、命令和预期结果。
- **类型一致性：** endpoint 请求使用 `MetaSkillInvocationRequest`，响应使用 `MetaInvocationResponse`，持久化状态使用 `MetaInvocationRecord` 和 `MetaInvocationStatus`；服务通过 `MetaInvocationStore` 声明调用，并调用任务 1 新增的运行时方法。所有状态映射均对应同一组账本状态。

实施交接：修改 OpenClaw.NET 源码前，先按 writing-plans skill 选择一种计划执行方式。

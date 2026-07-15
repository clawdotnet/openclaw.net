# ActionAdapter HTTP Connector 桥接设计说明

- 文档日期：2026-07-15
- 设计状态：待评审
- 适用范围：OpenClaw.NET，补全 ActionExecuteTool → ActionAdapter → 业务 API 全链路
- 文档语言：中文

## 1. 背景与问题定义

当前 MetaSkill 已支持 DAG 消费临时图（`load_temporary_graph`）并通过 LLM 推理产出
ActionProposal。`ActionExecuteTool` 已实现提案归一化（`ActionProposalBuilder`）、策略判级
（`ActionPolicyEngine`）和治理落证（`governanceMapping`）。`ActionAdapter` 已编码并单测通过，
支持 preCheck/execution/rollback/幂等语义。

**断点：** `ActionExecuteTool` 从未调用 `ActionAdapter`。原因：
1. `ActionAdapter` 需要 `IActionAdapterConnector`，但无生产实现
2. `ActionAdapter` 未被 DI 注册
3. `ActionExecuteTool` 不持有 `ActionAdapter` 引用

目标：补齐 HTTP Connector 生产实现，接线 ActionExecuteTool → ActionAdapter → HTTP API，
一次性覆盖完整决策矩阵（low→自动执行，medium→审批执行，high/critical→仅提案）。

## 2. 设计目标与非目标

### 2.1 目标

1. 实现 `HttpActionAdapterConnector`：通用 HTTP Connector，将 `ActionCall` 映射为 HTTP 请求
2. 接线 `ActionExecuteTool`：DI 注入 `ActionAdapter`，在 proceed 路径触发实际执行
3. 建立 `ActionAdapterConfig` 配置模型：Connector 白名单、认证、超时、重试
4. 覆盖完整决策矩阵：low→自动执行，medium→审批执行，high/critical→仅提案
5. 提供 e2e 测试：完整链路 load_temporary_graph → action_execute → HTTP mock → 验证回写
6. 向后兼容：不传 adapter 时 ActionExecuteTool 行为不变

### 2.2 非目标

1. 不实现异步作业编排（V1 为同步请求-响应）
2. 不实现分布式幂等（InMemoryActionIdempotencyRegistry 足够单节点）
3. 不改变 CLI/MCP/HTTP 入口语义（它们通过 IntegrationApiFacade 间接使用）
4. 不改变非 action_execute 路径的 MetaSkill 行为
5. 不引入第三方 HTTP 库（使用 `IHttpClientFactory`）

## 3. 方案选择

**方案 A：HTTP Connector（采用）**

通用 HTTP Connector，配置驱动，一个实现覆盖所有 Connector 系统。
- 优点：最小代码量（~250 行），配置驱动，与现有 ActionCall 模型天然映射
- 缺点：需要约定 HTTP API 的 URL 模板规范

**方案 B：插件式 Connector（不采用）**

每个业务系统各自实现 `IActionAdapterConnector`。
- 优点：可针对每个系统做定制化适配
- 缺点：前期工作量大，不适合首版

**方案 C：进程/文件 Connector（不采用）**

通过本地进程调用或文件落点。
- 优点：零网络依赖
- 缺点：不符合"调用业务 API"的设计意图

## 4. 总体架构

```
ActionExecuteTool (DI injected)
  ├── ActionProposalBuilder.Normalize()
  ├── ActionPolicyEngine.Evaluate()
  │     ├── low → proceed_execute
  │     ├── medium → require_approval
  │     ├── high/critical → proposal_only
  │     └── unknown_connector → policy_denied
  └── ActionAdapter.ExecuteAsync(proposal)
        ├── IActionIdempotencyRegistry.TryRegister()
        ├── preCheck:  IActionAdapterConnector.InvokeAsync()
        ├── execution: IActionAdapterConnector.InvokeAsync()
        └── rollback:  IActionAdapterConnector.InvokeAsync()
              │
              └── HttpActionAdapterConnector
                    ├── ConnectorDefinition (来自 ActionAdapterConfig)
                    ├── IHttpClientFactory
                    └── HTTP Response → ActionAdapterStepResult
```

### 决策矩阵

| 风险级别 | PolicyDecision | Tool Status | Adapter 是否调用 |
|---------|---------------|-------------|-----------------|
| low | proceed_execute | execution_completed / execution_failed | 是 |
| medium | require_approval | pending_approval（首次）/ execution_completed（审批后） | 首次否，审批后是 |
| high | proposal_only | proposal_only | 否 |
| critical | proposal_only | proposal_only | 否 |
| unknown | policy_denied | failed (policy_denied) | 否 |

### 审批执行流程

```
第一次调用（无审批 payload）:
  action_execute({ proposal, decision: "require_approval" })
    → policy engine: require_approval
    → 返回 { status: "pending_approval", governanceMapping: {...} }
    → 操作员审批

第二次调用（带审批 payload）:
  action_execute({
    proposal,
    decision: "proceed",
    approval: { approver, decisionAt, decisionReason, ticketRef }
  })
    → ConnectorActionContractValidator 校验审批字段
    → policy engine 仍可能返回 require_approval（保护性降级）
    → 但 contractOverride.decision="proceed" 覆盖决策
    → ActionAdapter.ExecuteAsync() 实际执行
    → 返回 { status: "execution_completed", governanceMapping: {...} }
```

## 5. HttpActionAdapterConnector 设计

### 5.1 文件

- 新建：`src/OpenClaw.Agent/Actions/HttpActionAdapterConnector.cs`

### 5.2 核心映射

```
ActionCall.Call  → {Connector}.{Operation}（如 "crm.updateCustomerTier"）
ActionCall.Args  → JSON Request Body（序列化为 UTF-8 JSON）
```

解析规则：
1. 从 `Call` 提取 `system`（第一个 `.` 之前的部分）
2. 从 `ActionAdapterConfig.Connectors` 查找 `system` 对应的 `ConnectorDefinition`
3. 提取 `operation`（第一个 `.` 之后的部分）
4. 验证 `operation` 在 `ConnectorDefinition.AllowedCalls` 白名单中
5. 构造 URL：`{BaseUrl}/{operation}`
6. 发送 POST 请求，Body 为 `Args` 的 JSON 序列化

### 5.3 响应映射

| HTTP Status | ActionAdapterStepResult |
|------------|------------------------|
| 2xx (200-299) | `Success = true` |
| 4xx | `Success = false, ResultCode = "connector_error"` |
| 5xx | `Success = false, ResultCode = "connector_error"` |
| Timeout | `Success = false, ResultCode = "connector_unavailable"` |
| DNS/Connection error | `Success = false, ResultCode = "connector_unavailable"` |

失败时在 ResultCode 中附带简短的 HTTP status 和响应体摘要（截断至 256 字符）。

### 5.4 安全约束

1. 仅允许配置中 `AllowedCalls` 白名单的 operation
2. `Call` 中不得包含 `sql`/`db`/`database` 关键词（与 ActionProposalBuilder 双重防护）
3. `Args` 序列化前做深拷贝，不直接传引用
4. 使用 `IHttpClientFactory` 管理连接池，避免 socket 耗尽

### 5.5 接口

```csharp
internal sealed class HttpActionAdapterConnector : IActionAdapterConnector
{
    public HttpActionAdapterConnector(
        IOptions<ActionAdapterConfig> config,
        IHttpClientFactory httpClientFactory,
        ILogger<HttpActionAdapterConnector> logger);

    public ValueTask<ActionAdapterStepResult> InvokeAsync(
        ActionCall step, CancellationToken cancellationToken);
}
```

## 6. ActionExecuteTool 接线改造

### 6.1 文件

- 修改：`src/OpenClaw.Agent/Tools/ActionExecuteTool.cs`
- 修改：`src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.RuntimeFactories.cs`
- 修改：`src/OpenClaw.Gateway/Composition/IntegrationApiFacade.cs`

### 6.2 构造函数变化

```csharp
// 旧：无依赖
internal ActionExecuteTool(IActionPolicyEngine policyEngine)

// 新：可选注入 ActionAdapter
internal ActionExecuteTool(
    IActionPolicyEngine policyEngine,
    ActionAdapter? adapter = null)
```

不传 adapter 时行为完全不变——所有路径都返回决策结果而不执行。
这保证 CLI/MCP/HTTP 入口在配置 `Enabled: false` 时的安全降级。

### 6.3 ExecuteAsync 分流逻辑

在 `contractOverride.Decision == "proceed"` 分支中，新增 adapter 调用：

```csharp
if (contractOverride.Decision == "proceed" && _adapter is not null)
{
    var adapterResult = await _adapter.ExecuteAsync(normalized.Proposal, ct);
    return BuildExecutionResult(adapterResult, callerDecision, governanceMapping);
}
```

`BuildExecutionResult` 根据 `ActionAdapterResult.Status` 映射：
- `succeeded` → `"execution_completed"`
- `rolled_back` → `"execution_rolled_back"`
- `rollback_failed` → `"execution_failed"`
- 其他 → `"execution_failed"`

### 6.4 向后兼容分析

| 场景 | 影响 |
|------|------|
| adapter = null（配置关闭） | proposal_only / pending_approval 照常返回，不执行 |
| adapter 注入（配置开启） | proceed/require_approval 路径触发 Adapter |
| CLI `connector execute` | 不变——通过 HTTP client → Integration API → Facade |
| MCP `execute_connector_action` | 不变——通过 IntegrationApiFacade |
| 旧 MetaSkill（无 action_execute） | 不变 |
| `IntegrationApiFacade` 内部 `new ActionExecuteTool()` | 需改为注入或传入 adapter |

### 6.5 IntegrationApiFacade 改造

`ExecuteConnectorActionAsync` 需接收注入的 `ActionAdapter`：

```csharp
// 构造函数新增参数
private readonly ActionAdapter? _actionAdapter;

// ExecuteConnectorActionAsync 中
var tool = _actionAdapter is not null
    ? new ActionExecuteTool(new ActionPolicyEngine(), _actionAdapter)
    : new ActionExecuteTool();
var resultJson = await tool.ExecuteAsync(..., ct);
```

## 7. 配置模型

### 7.1 文件

- 新建：`src/OpenClaw.Core/Models/ActionAdapterConfigModels.cs`

### 7.2 类型定义

```csharp
public sealed class ActionAdapterConfig
{
    public bool Enabled { get; init; }
    public string DefaultDecisionMode { get; init; } = "risk-tiered";
    public int IdempotencyWindowMinutes { get; init; } = 60;
    public int MaxExecutionSteps { get; init; } = 10;
    public int MaxRollbackSteps { get; init; } = 5;
    public bool DenyUnknownConnector { get; init; } = true;
    public bool RequireEvidence { get; init; } = true;
    public Dictionary<string, RiskLevelPolicy> RiskPolicies { get; init; } = [];
    public Dictionary<string, ConnectorDefinition> Connectors { get; init; } = [];
}

public sealed class RiskLevelPolicy
{
    public string Decision { get; init; } = "proposal_only";
    public bool RequireApproval { get; init; }
}

public sealed class ConnectorDefinition
{
    public string BaseUrl { get; init; } = "";
    public ConnectorAuthConfig Auth { get; init; } = new();
    public int TimeoutSeconds { get; init; } = 30;
    public string[] AllowedCalls { get; init; } = [];
    public int RetryCount { get; init; } = 0;
}

public sealed class ConnectorAuthConfig
{
    public string Type { get; init; } = "Bearer";
    // "Bearer" → Authorization: Bearer <token>
    // "ApiKey" → X-API-Key: <token>
    // "None" → 无认证头
    public string TokenEnv { get; init; } = "";
    // 从环境变量读取 token 值
    public string HeaderName { get; init; } = "";
    // Type=ApiKey 时自定义 header 名称，默认 X-API-Key
}
```

### 7.3 配置 JSON 示例

```json
{
  "Harness": {
    "ActionAdapter": {
      "Enabled": true,
      "DefaultDecisionMode": "risk-tiered",
      "IdempotencyWindowMinutes": 60,
      "MaxExecutionSteps": 10,
      "MaxRollbackSteps": 5,
      "Connectors": {
        "crm": {
          "BaseUrl": "https://crm.example.com/api/v1",
          "Auth": { "Type": "Bearer", "TokenEnv": "CRM_API_TOKEN" },
          "TimeoutSeconds": 30,
          "AllowedCalls": ["updateCustomerTier", "createCase", "addNote"]
        },
        "stripe": {
          "BaseUrl": "https://api.stripe.com/v1",
          "Auth": { "Type": "Bearer", "TokenEnv": "STRIPE_API_KEY" },
          "AllowedCalls": ["refundCharge", "updateSubscription"]
        },
        "slack": {
          "BaseUrl": "https://slack.com/api",
          "Auth": { "Type": "Bearer", "TokenEnv": "SLACK_BOT_TOKEN" },
          "AllowedCalls": ["chat.postMessage", "chat.update"]
        }
      }
    }
  }
}
```

### 7.4 安全降级

1. `Enabled: false` → 全部 proposal_only，adapter 不实例化
2. `DefaultDecisionMode: "proposal-only"` → 全部 proposal_only，忽略 risk tier
3. `DenyUnknownConnector: true`（默认） → 未知 system 返回 policy_denied
4. 环境变量 Token 未设置 → `InvokeAsync` 返回 `Failure("connector_unavailable")`

## 8. DI 注册

### 8.1 文件

- 修改：`src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.RuntimeFactories.cs`

### 8.2 注册代码

在 `CreateBuiltInTools` 之前注册服务，在工具列表中使用注入的 `ActionExecuteTool`：

```csharp
// 在 Gateway Startup 中注册
services.Configure<ActionAdapterConfig>(
    config.GetSection("Harness:ActionAdapter"));

var adapterConfig = config.GetSection("Harness:ActionAdapter")
    .Get<ActionAdapterConfig>() ?? new ActionAdapterConfig();

if (adapterConfig.Enabled)
{
    services.AddSingleton<IActionAdapterConnector, HttpActionAdapterConnector>();
    services.AddSingleton<IActionIdempotencyRegistry, InMemoryActionIdempotencyRegistry>();
    services.AddSingleton<ActionAdapter>();
    services.AddSingleton<ActionExecuteTool>(sp =>
        new ActionExecuteTool(
            new ActionPolicyEngine(),
            sp.GetRequiredService<ActionAdapter>()));
}
else
{
    services.AddSingleton<ActionExecuteTool>(_ =>
        new ActionExecuteTool(new ActionPolicyEngine()));
}
```

工具列表中 `new ActionExecuteTool()` 替换为从 DI 获取的实例。

### 8.3 IntegrationApiFacade 注入

```csharp
// IntegrationApiFacade 构造函数新增参数
private readonly ActionAdapter? _actionAdapter;

// 从 DI 获取
var actionAdapter = services.GetService<ActionAdapter>();
```

## 9. 测试策略

### 9.1 单元测试

| 测试文件 | 状态 | 内容 |
|---------|------|------|
| `ActionProposalBuilderTests.cs` | 已有，不变 | 提案归一化、DB 写拦截 |
| `ActionExecuteToolTests.cs` | 已有，扩展 | 新增 adapter 注入后 execute 路径 |
| `ActionAdapterTests.cs` | 已有，不变 | FakeConnector 测试 preCheck/execution/rollback |
| `ActionPolicyEngineTests.cs` | 待新增 | 完整决策矩阵覆盖 |

### 9.2 集成测试（新增）

| 测试文件 | 内容 |
|---------|------|
| `HttpActionAdapterConnectorTests.cs` | Mock HTTP Server，验证 call→HTTP 映射、白名单拦截、超时/重试、认证头 |

### 9.3 E2E 测试（新增）

| 测试 | 验证内容 |
|------|---------|
| `FullPipelineE2ETests.cs` | `load_temporary_graph → action_execute(proceed, adapter注入) → Mock HTTP Server → 验证请求正确到达 → 验证 governanceMapping 包含 executionResult` |
| `FullPipeline_RequireApproval` | 首次调用返回 `pending_approval`，二次调用（带审批 payload）触发执行 |
| `FullPipeline_LowRisk_AutoExecute` | low risk proposal + proceed → 自动执行，无需审批 |
| `FullPipeline_ConfigDisabled` | `Enabled: false` 时全部 proposal_only |

### 9.4 E2E 测试骨架

```csharp
[Fact]
public async Task FullPipeline_LoadTempGraph_Infer_Execute_Writeback()
{
    // 1. 创建临时图文件（JSON-LD）
    var workspace = CreateTempDir();
    var graphPath = Path.Combine(workspace, "quality-slice.jsonld");
    await File.WriteAllTextAsync(graphPath, BuildQualitySliceJson());

    // 2. load_temporary_graph 读取
    var graphTool = new LoadTemporaryGraphTool(new ToolingConfig
    {
        AllowedReadRoots = [workspace]
    });
    var graphResult = await graphTool.ExecuteAsync(
        JsonSerializer.Serialize(new { path = graphPath }), ct);
    // 验证：status=ok, payload_json 包含 @type 节点

    // 3. 模拟 LLM 推理产出 ActionProposal
    var proposal = BuildProposal(targetSystem: "crm", riskLevel: "low");

    // 4. 启动 Mock HTTP Server（接收回写请求）
    using var mockServer = new MockHttpServer();
    mockServer.Expect("POST", "/updateCustomerTier", 200, "{\"ok\":true}");

    // 5. 配置 ActionAdapter + MockServer URL
    var config = Options.Create(new ActionAdapterConfig
    {
        Enabled = true,
        Connectors = new()
        {
            ["crm"] = new ConnectorDefinition
            {
                BaseUrl = mockServer.Url,
                AllowedCalls = ["updateCustomerTier"]
            }
        }
    });
    var httpClientFactory = new TestHttpClientFactory();
    var connector = new HttpActionAdapterConnector(config, httpClientFactory, NullLogger);
    var adapter = new ActionAdapter(connector, new InMemoryActionIdempotencyRegistry());
    var tool = new ActionExecuteTool(new ActionPolicyEngine(), adapter);

    // 6. action_execute 调用
    var args = JsonSerializer.Serialize(new
    {
        proposal = JsonSerializer.Serialize(proposal),
        decision = "proceed"
    });
    var result = await tool.ExecuteAsync(args, ct);

    // 7. 验证结果
    using var resultDoc = JsonDocument.Parse(result);
    Assert.Equal("execution_completed",
        resultDoc.RootElement.GetProperty("status").GetString());

    // 8. 验证 Mock Server 收到正确的 HTTP 请求
    var receivedRequest = mockServer.ReceivedRequests.Single();
    Assert.Equal("POST", receivedRequest.Method);
    Assert.Equal("/updateCustomerTier", receivedRequest.Path);

    // 9. 验证 governanceMapping 完整
    var mapping = resultDoc.RootElement.GetProperty("governanceMapping");
    Assert.NotEmpty(mapping.GetProperty("harnessContractId").GetString());
    Assert.NotEmpty(mapping.GetProperty("pevId").GetString());
}
```

## 10. 文件变更汇总

| 操作 | 文件 | 说明 |
|------|------|------|
| 新建 | `src/OpenClaw.Agent/Actions/HttpActionAdapterConnector.cs` | HTTP Connector 实现 |
| 新建 | `src/OpenClaw.Core/Models/ActionAdapterConfigModels.cs` | 配置模型 |
| 修改 | `src/OpenClaw.Agent/Tools/ActionExecuteTool.cs` | 新增 ActionAdapter 注入、执行分流 |
| 修改 | `src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.RuntimeFactories.cs` | DI 注册 |
| 修改 | `src/OpenClaw.Gateway/Composition/IntegrationApiFacade.cs` | 注入 ActionAdapter |
| 修改 | `src/OpenClaw.Core/Models/Session.cs` | 新增配置模型的 JsonContext 源生成声明 |
| 新建 | `src/OpenClaw.Tests/HttpActionAdapterConnectorTests.cs` | HTTP Connector 单测 |
| 新建 | `src/OpenClaw.Tests/ActionPolicyEngineTests.cs` | 完整决策矩阵测试 |
| 修改 | `src/OpenClaw.Tests/ActionExecuteToolTests.cs` | 扩展 adapter 注入路径测试 |
| 新建 | `src/OpenClaw.Tests/FullPipelineE2ETests.cs` | E2E 全链路测试 |
| 修改 | `docs/zh-CN/meta-skills.md` | 补充自动执行与审批执行示例 |

## 11. 验收标准

1. low risk + proceed → adapter 执行成功，Mock Server 收到正确请求
2. medium risk + 无审批 → 返回 pending_approval，不执行
3. medium risk + 带审批 payload → 执行成功
4. high/critical → proposal_only，不执行
5. 未知 connector → policy_denied
6. `Enabled: false` → adapter 未注入，全部 proposal_only
7. execution 步骤失败 → rollback 触发 → rolled_back
8. rollback 也失败 → rollback_failed
9. 同 idempotencyKey 重复调用 → idempotency_conflict
10. 全链路 governanceMapping 包含 executionResult 字段
11. 现有测试全量通过，无回归

## 12. 风险与缓解

1. **风险：HTTP Connector 语义与业务 API 返回格式不一致**
   - 缓解：Connector 只关心 HTTP 2xx/4xx/5xx，不解析业务语义；业务逻辑在 MetaSkill 推理层完成
2. **风险：Token 泄露**
   - 缓解：Token 仅从环境变量读取，不写入配置文件或日志
3. **风险：幂等窗口重启丢失**
   - 缓解：V1 使用 InMemory 注册表，重启后重置；后续可升级为持久化
4. **风险：连接池耗尽**
   - 缓解：使用 `IHttpClientFactory`，由框架管理连接池生命周期

## 13. 结论

本设计以最小增量补齐了 MetaSkill 推理到业务 API 回写的最后一段链路：

1. HttpActionAdapterConnector 提供了通用、配置驱动的 HTTP API 调用能力
2. ActionExecuteTool 通过可选 DI 注入 ActionAdapter，在不传 adapter 时行为不变
3. 覆盖完整决策矩阵（low→自动，medium→审批，high/critical→仅提案）
4. E2E 测试通过 Mock HTTP Server 验证全链路可达
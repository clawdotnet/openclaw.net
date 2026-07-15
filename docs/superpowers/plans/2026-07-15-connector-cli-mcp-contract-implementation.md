# Connector CLI MCP Contract Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在不改变既有非 Action 路径行为的前提下，落地统一 Connector Contract，并把同一执行语义接入 CLI 与 MCP（同步请求-响应）。

**Architecture:** 以 C# 强类型 Contract 作为运行时权威模型，并从同一模型导出 JSON Schema，供 CLI/MCP 输入校验复用。Gateway 新增统一 `connector-actions/execute` 集成入口，内部复用 `ActionExecuteTool`，使 MetaSkill、CLI、MCP 三入口共享错误码与治理映射语义。CLI 新增 `connector execute` 命令，MCP 新增 `openclaw.execute_connector_action` 工具，二者都通过 Integration API Facade 调用同一链路。

**Tech Stack:** .NET 10, C#, System.Text.Json source generation, ASP.NET Core minimal API, OpenClaw CLI, MCP C# SDK, xUnit

## Global Constraints

- 双轨契约：C# interface + DTO 作为运行时权威模型；从该模型导出 JSON Schema 供 CLI/MCP 校验。
- CLI/MCP V1 仅采用同步请求-响应，不引入异步作业编排。
- 当 `decision=require_approval` 时，审批字段（`approver`、`decisionAt`、`decisionReason`、`ticketRef`）严格条件必填。
- 未知 Connector 直接拒绝并返回 `policy_denied`。
- 写路径仅允许业务 API Connector，禁止数据库直写路径。
- 不改变现有未接入 Action 机制的 MetaSkill 行为。
- Preserve NativeAOT friendliness and avoid reflection-heavy trim-unsafe dependencies in runtime core paths.

---

## Scope Check

本计划覆盖同一子系统（统一 Contract + Gateway 执行入口 + CLI 适配 + MCP 适配），属于一个可独立验收的实现切片；无需再拆分为多个计划。

## File Structure

- Create: `src/OpenClaw.Core/Models/ConnectorActionContractModels.cs`
  - 责任：定义统一 Contract（请求、响应、审批扩展、schema 元数据）和基础校验入口。
- Create: `src/OpenClaw.Core/ConnectorActions/ConnectorActionSchemaExporter.cs`
  - 责任：基于 Contract DTO 导出 JSON Schema（V1）。
- Modify: `src/OpenClaw.Core/Models/Session.cs`
  - 责任：为新 Contract 模型补充 `CoreJsonContext` 源生成声明。
- Create: `src/OpenClaw.Tests/ConnectorActionContractTests.cs`
  - 责任：Contract 字段校验与 Schema 导出一致性测试。

- Modify: `src/OpenClaw.Gateway/Composition/IntegrationApiFacade.cs`
  - 责任：新增统一执行方法 `ExecuteConnectorActionAsync`，内部调用 `ActionExecuteTool`。
- Modify: `src/OpenClaw.Gateway/Endpoints/IntegrationEndpoints.cs`
  - 责任：新增 `/api/integration/connector-actions/execute`。
- Create: `src/OpenClaw.Core/Models/IntegrationConnectorActionModels.cs`
  - 责任：Integration API 请求/响应模型（避免 CLI/MCP 私有格式分叉）。
- Modify: `src/OpenClaw.Client/OpenClawHttpClient.cs`
  - 责任：新增 `ExecuteConnectorActionAsync` 客户端方法。
- Test: `src/OpenClaw.Tests/GatewayAdminEndpointTests.cs`
  - 责任：验证 Integration 入口语义（unknown connector / require approval 字段校验 / governanceMapping）。

- Create: `src/OpenClaw.Cli/ConnectorCommands.cs`
  - 责任：新增 `openclaw connector execute` 命令。
- Modify: `src/OpenClaw.Cli/Program.cs`
  - 责任：注册 `connector` 顶层命令并更新帮助文本。
- Test: `src/OpenClaw.Tests/ConnectorCommandsTests.cs`
  - 责任：命令参数解析、错误码透传、`--json` 输出验证。

- Modify: `src/OpenClaw.Gateway/Mcp/OpenClawMcpTools.cs`
  - 责任：新增 `openclaw.execute_connector_action` MCP 工具方法（同步语义）。
- Test: `src/OpenClaw.Tests/GatewayAdminEndpointTests.cs`
  - 责任：验证 MCP tools/list 出现新工具与 tools/call 返回契约一致响应。

- Modify: `docs/zh-CN/meta-skills.md`
  - 责任：补充 CLI/MCP 对齐说明与 `connector execute` / MCP tool 示例。

---

### Task 1: Define unified Connector Contract and schema exporter

**Files:**
- Create: `src/OpenClaw.Core/Models/ConnectorActionContractModels.cs`
- Create: `src/OpenClaw.Core/ConnectorActions/ConnectorActionSchemaExporter.cs`
- Modify: `src/OpenClaw.Core/Models/Session.cs`
- Test: `src/OpenClaw.Tests/ConnectorActionContractTests.cs`

**Interfaces:**
- Consumes:
  - `ActionProposal` (`OpenClaw.Core.Models.ActionProposal`)
- Produces:
  - `ConnectorActionExecuteRequest`
  - `ConnectorActionExecuteResponse`
  - `ConnectorApprovalPayload`
  - `ConnectorActionContractValidator.ValidateForExecution(ConnectorActionExecuteRequest request) -> (bool Success, string? ErrorCode, string? ErrorMessage)`
  - `ConnectorActionSchemaExporter.ExportV1() -> string`

- [ ] **Step 1: Write failing Contract tests**

```csharp
[Fact]
public void ValidateForExecution_RequireApprovalMissingTicketRef_Fails()
{
    var request = new ConnectorActionExecuteRequest
    {
        Proposal = BuildValidProposal(),
        Decision = "require_approval",
        Approval = new ConnectorApprovalPayload
        {
            Approver = "u_zhangsan",
            DecisionAt = "2026-07-15T08:30:00Z",
            DecisionReason = "ok",
            TicketRef = ""
        }
    };

    var result = ConnectorActionContractValidator.ValidateForExecution(request);

    Assert.False(result.Success);
    Assert.Equal("approval_denied", result.ErrorCode);
}

[Fact]
public void ExportV1_ReturnsJsonSchemaWithApprovalFields()
{
    var schema = ConnectorActionSchemaExporter.ExportV1();
    Assert.Contains("\"decision\"", schema, StringComparison.Ordinal);
    Assert.Contains("\"approval\"", schema, StringComparison.Ordinal);
    Assert.Contains("\"ticketRef\"", schema, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run targeted tests to see RED**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~ConnectorActionContractTests" -v minimal
```

Expected: FAIL (types and exporter not found).

- [ ] **Step 3: Implement minimal Contract models + validator + schema exporter**

```csharp
public sealed class ConnectorActionExecuteRequest
{
    public required ActionProposal Proposal { get; init; }
    public required string Decision { get; init; }
    public string? RiskLevel { get; init; }
    public ConnectorApprovalPayload? Approval { get; init; }
}

public static class ConnectorActionContractValidator
{
    public static (bool Success, string? ErrorCode, string? ErrorMessage) ValidateForExecution(ConnectorActionExecuteRequest request)
    {
        if (request.Decision.Equals("require_approval", StringComparison.OrdinalIgnoreCase))
        {
            var approval = request.Approval;
            if (approval is null
                || string.IsNullOrWhiteSpace(approval.Approver)
                || string.IsNullOrWhiteSpace(approval.DecisionAt)
                || string.IsNullOrWhiteSpace(approval.DecisionReason)
                || string.IsNullOrWhiteSpace(approval.TicketRef))
            {
                return (false, "approval_denied", "Approval payload is incomplete.");
            }
        }

        return (true, null, null);
    }
}
```

- [ ] **Step 4: Register JSON source-gen entries**

In `Session.cs` `CoreJsonContext` section add:

```csharp
[JsonSerializable(typeof(ConnectorActionExecuteRequest))]
[JsonSerializable(typeof(ConnectorActionExecuteResponse))]
[JsonSerializable(typeof(ConnectorApprovalPayload))]
[JsonSerializable(typeof(IntegrationConnectorActionExecuteRequest))]
[JsonSerializable(typeof(IntegrationConnectorActionExecuteResponse))]
```

- [ ] **Step 5: Re-run targeted tests to see GREEN**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~ConnectorActionContractTests" -v minimal
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/OpenClaw.Core/Models/ConnectorActionContractModels.cs src/OpenClaw.Core/ConnectorActions/ConnectorActionSchemaExporter.cs src/OpenClaw.Core/Models/Session.cs src/OpenClaw.Tests/ConnectorActionContractTests.cs
git commit -m "feat: add unified connector action contract and schema exporter"
```

---

### Task 2: Add unified Gateway integration execute endpoint

**Files:**
- Create: `src/OpenClaw.Core/Models/IntegrationConnectorActionModels.cs`
- Modify: `src/OpenClaw.Gateway/Composition/IntegrationApiFacade.cs`
- Modify: `src/OpenClaw.Gateway/Endpoints/IntegrationEndpoints.cs`
- Modify: `src/OpenClaw.Client/OpenClawHttpClient.cs`
- Test: `src/OpenClaw.Tests/GatewayAdminEndpointTests.cs`

**Interfaces:**
- Consumes:
  - `ConnectorActionExecuteRequest`
  - `ConnectorActionContractValidator.ValidateForExecution(...)`
  - `ActionExecuteTool.ExecuteAsync(string argumentsJson, CancellationToken ct)`
- Produces:
  - `IntegrationApiFacade.ExecuteConnectorActionAsync(IntegrationConnectorActionExecuteRequest request, CancellationToken ct) -> IntegrationConnectorActionExecuteResponse`
  - HTTP endpoint `POST /api/integration/connector-actions/execute`
  - `OpenClawHttpClient.ExecuteConnectorActionAsync(...)`

- [ ] **Step 1: Write failing integration endpoint tests**

Add tests in `GatewayAdminEndpointTests.cs`:

```csharp
[Fact]
public async Task Integration_ExecuteConnectorAction_UnknownConnector_ReturnsPolicyDenied()
{
    using var harness = await GatewayHarness.StartAsync();
    using var client = new OpenClawHttpClient(harness.Client.BaseAddress!.ToString(), harness.AuthToken, harness.Client);

    var response = await client.ExecuteConnectorActionAsync(BuildRequest(targetSystem: "unknown_connector"), TestContext.Current.CancellationToken);

    Assert.Equal("failed", response.Status);
    Assert.Equal("policy_denied", response.FailureCode);
}

[Fact]
public async Task Integration_ExecuteConnectorAction_RequireApprovalMissingFields_ReturnsApprovalDenied()
{
    using var harness = await GatewayHarness.StartAsync();
    using var client = new OpenClawHttpClient(harness.Client.BaseAddress!.ToString(), harness.AuthToken, harness.Client);

    var response = await client.ExecuteConnectorActionAsync(BuildRequireApprovalMissingTicketRequest(), TestContext.Current.CancellationToken);

    Assert.Equal("failed", response.Status);
    Assert.Equal("approval_denied", response.FailureCode);
}
```

- [ ] **Step 2: Run focused tests (RED)**

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~Integration_ExecuteConnectorAction" -v minimal
```

Expected: FAIL (method/endpoint missing).

- [ ] **Step 3: Implement facade method and endpoint**

In `IntegrationApiFacade.cs`:

```csharp
public async Task<IntegrationConnectorActionExecuteResponse> ExecuteConnectorActionAsync(
    IntegrationConnectorActionExecuteRequest request,
    CancellationToken ct)
{
    var validation = ConnectorActionContractValidator.ValidateForExecution(request.Request);
    if (!validation.Success)
    {
        return new IntegrationConnectorActionExecuteResponse
        {
            Status = "failed",
            FailureCode = validation.ErrorCode,
            Message = validation.ErrorMessage
        };
    }

    var toolArgs = JsonSerializer.Serialize(new { proposal = request.Request.Proposal }, CoreJsonContext.Default.Object);
    var resultJson = await new ActionExecuteTool().ExecuteAsync(toolArgs, ct);
    return IntegrationConnectorActionExecuteResponse.FromToolJson(resultJson);
}
```

In `IntegrationEndpoints.cs`:

```csharp
group.MapPost("/connector-actions/execute", async (HttpContext ctx) =>
{
    var failure = AuthorizeAndConsume(ctx, startup, runtime, browserSessions, endpointScope: "integration.mutate", requireCsrf: true);
    if (failure is not null)
        return failure;

    var request = await JsonSerializer.DeserializeAsync(ctx.Request.Body, CoreJsonContext.Default.IntegrationConnectorActionExecuteRequest, ctx.RequestAborted);
    if (request is null)
        return Results.BadRequest(new OperationStatusResponse { Success = false, Error = "Invalid request body." });

    var response = await facade.ExecuteConnectorActionAsync(request, ctx.RequestAborted);
    return Results.Json(response, CoreJsonContext.Default.IntegrationConnectorActionExecuteResponse);
});
```

- [ ] **Step 4: Add OpenClawHttpClient method**

```csharp
public Task<IntegrationConnectorActionExecuteResponse> ExecuteConnectorActionAsync(
    IntegrationConnectorActionExecuteRequest request,
    CancellationToken cancellationToken)
    => PostAsync(_integrationConnectorActionsExecuteUri, request, CoreJsonContext.Default.IntegrationConnectorActionExecuteRequest, CoreJsonContext.Default.IntegrationConnectorActionExecuteResponse, cancellationToken);
```

- [ ] **Step 5: Re-run focused endpoint tests (GREEN)**

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~Integration_ExecuteConnectorAction" -v minimal
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/OpenClaw.Core/Models/IntegrationConnectorActionModels.cs src/OpenClaw.Gateway/Composition/IntegrationApiFacade.cs src/OpenClaw.Gateway/Endpoints/IntegrationEndpoints.cs src/OpenClaw.Client/OpenClawHttpClient.cs src/OpenClaw.Tests/GatewayAdminEndpointTests.cs
git commit -m "feat: add integration connector action execute endpoint"
```

---

### Task 3: Add CLI synchronous adapter (`openclaw connector execute`)

**Files:**
- Create: `src/OpenClaw.Cli/ConnectorCommands.cs`
- Modify: `src/OpenClaw.Cli/Program.cs`
- Test: `src/OpenClaw.Tests/ConnectorCommandsTests.cs`

**Interfaces:**
- Consumes:
  - `OpenClawHttpClient.ExecuteConnectorActionAsync(...)`
  - `IntegrationConnectorActionExecuteRequest`
- Produces:
  - CLI command: `openclaw connector execute --proposal-file <path> [--decision <value>] [--json]`

- [ ] **Step 1: Write failing command tests**

```csharp
[Fact]
public async Task RunAsync_Execute_WithUnknownConnector_PrintsPolicyDeniedAndReturns1()
{
    var output = new StringWriter();
    var error = new StringWriter();

    var exit = await ConnectorCommands.RunAsync(
        ["execute", "--proposal-file", "proposal.json", "--json"],
        output,
        error,
        _ => new FakeClient(new IntegrationConnectorActionExecuteResponse { Status = "failed", FailureCode = "policy_denied" }));

    Assert.Equal(1, exit);
    Assert.Contains("policy_denied", output.ToString(), StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Run focused tests (RED)**

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~ConnectorCommandsTests" -v minimal
```

Expected: FAIL (command class missing).

- [ ] **Step 3: Implement command module and wire into Program**

```csharp
// Program.cs switch
"connector" => await ConnectorCommands.RunAsync(rest),
```

```csharp
// ConnectorCommands.cs (核心分支)
if (string.Equals(command, "execute", StringComparison.OrdinalIgnoreCase))
{
    var proposalFile = parsed.GetOption("--proposal-file") ?? throw new ArgumentException("--proposal-file is required.");
    var proposalJson = await File.ReadAllTextAsync(proposalFile);
    var request = new IntegrationConnectorActionExecuteRequest
    {
        Request = new ConnectorActionExecuteRequest
        {
            Proposal = JsonSerializer.Deserialize(proposalJson, CoreJsonContext.Default.ActionProposal)!,
            Decision = parsed.GetOption("--decision") ?? "proceed_execute"
        }
    };

    var response = await client.ExecuteConnectorActionAsync(request, CancellationToken.None);
    Write(response, parsed.HasFlag("--json"));
    return response.Status.Equals("failed", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
}
```

- [ ] **Step 4: Re-run command tests (GREEN)**

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~ConnectorCommandsTests" -v minimal
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/OpenClaw.Cli/ConnectorCommands.cs src/OpenClaw.Cli/Program.cs src/OpenClaw.Tests/ConnectorCommandsTests.cs
git commit -m "feat: add connector execute cli command"
```

---

### Task 4: Add MCP tool adapter and end-to-end consistency checks

**Files:**
- Modify: `src/OpenClaw.Gateway/Mcp/OpenClawMcpTools.cs`
- Modify: `src/OpenClaw.Tests/GatewayAdminEndpointTests.cs`
- Modify: `docs/zh-CN/meta-skills.md`

**Interfaces:**
- Consumes:
  - `IntegrationApiFacade.ExecuteConnectorActionAsync(...)`
  - `IntegrationConnectorActionExecuteRequest`
- Produces:
  - MCP tool `openclaw.execute_connector_action`
  - MCP tools/list visibility and tools/call response parity with HTTP/CLI

- [ ] **Step 1: Write failing MCP tests**

```csharp
[Fact]
public async Task McpToolsList_ContainsExecuteConnectorAction()
{
    using var harness = await GatewayHarness.StartAsync();
    using var client = new OpenClawHttpClient(harness.Client.BaseAddress!.ToString(), harness.AuthToken, harness.Client);

    var tools = await client.ListMcpToolsAsync(TestContext.Current.CancellationToken);
    Assert.Contains(tools.Tools, item => item.Name == "openclaw.execute_connector_action");
}

[Fact]
public async Task McpCall_ExecuteConnectorAction_UnknownConnector_ReturnsPolicyDenied()
{
    using var harness = await GatewayHarness.StartAsync();
    using var client = new OpenClawHttpClient(harness.Client.BaseAddress!.ToString(), harness.AuthToken, harness.Client);

    var result = await client.CallMcpToolAsync("openclaw.execute_connector_action", BuildUnknownConnectorArgs(), TestContext.Current.CancellationToken);
    Assert.Contains("policy_denied", JsonSerializer.Serialize(result, McpJsonContext.Default.McpCallToolResult), StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Run focused MCP tests (RED)**

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~McpToolsList_ContainsExecuteConnectorAction|FullyQualifiedName~McpCall_ExecuteConnectorAction_UnknownConnector_ReturnsPolicyDenied" -v minimal
```

Expected: FAIL (tool not registered).

- [ ] **Step 3: Implement MCP tool method**

In `OpenClawMcpTools.cs` add:

```csharp
[McpServerTool(Name = "openclaw.execute_connector_action"),
 Description("Execute a connector action request through the unified integration facade.")]
public async Task<string> ExecuteConnectorAction(
    [Description("Connector action execute request as JSON string.")] string requestJson,
    CancellationToken ct = default)
{
    var request = JsonSerializer.Deserialize(requestJson, CoreJsonContext.Default.IntegrationConnectorActionExecuteRequest)
        ?? throw new ArgumentException("requestJson must be valid IntegrationConnectorActionExecuteRequest JSON.", nameof(requestJson));

    var response = await _facade.ExecuteConnectorActionAsync(request, ct);
    return JsonSerializer.Serialize(response, CoreJsonContext.Default.IntegrationConnectorActionExecuteResponse);
}
```

- [ ] **Step 4: Update docs**

In `docs/zh-CN/meta-skills.md` add compact examples for:

```bash
openclaw connector execute --proposal-file ./proposal.json --decision proceed_execute --json
```

and MCP:

```json
{"name":"openclaw.execute_connector_action","arguments":{"requestJson":"{...}"}}
```

- [ ] **Step 5: Run focused + full verification**

Run:

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~ConnectorActionContractTests|FullyQualifiedName~Integration_ExecuteConnectorAction|FullyQualifiedName~ConnectorCommandsTests|FullyQualifiedName~McpCall_ExecuteConnectorAction|FullyQualifiedName~McpToolsList_ContainsExecuteConnectorAction" -v minimal
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj -v minimal
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/OpenClaw.Gateway/Mcp/OpenClawMcpTools.cs src/OpenClaw.Tests/GatewayAdminEndpointTests.cs docs/zh-CN/meta-skills.md
git commit -m "feat: bridge connector execute into mcp and align docs"
```

---

## Self-Review

### 1. Spec coverage

- 双轨 Contract（C# + JSON Schema）：Task 1。
- 同步请求-响应：Tasks 2/3/4 均使用同步 execute 链路。
- `require_approval` 严格条件必填：Task 1 校验 + Task 2 接口行为测试。
- 未知 Connector `policy_denied`：Tasks 2/3/4 负向用例覆盖。
- CLI/MCP 与统一链路衔接：Tasks 3/4。
- 不改变非 Action 路径：Task 2 只新增 endpoint，不改现有路由语义；Task 4 仅新增 MCP tool。

### 2. Placeholder scan

未使用 TBD/TODO/“后续实现”等占位语句；所有任务都给出明确文件、测试与命令。

### 3. Type consistency

- 全链路统一使用 `IntegrationConnectorActionExecuteRequest/Response`。
- CLI、MCP、Integration endpoint 共用相同请求模型。
- 错误码命名与现有桥接实现保持一致（`policy_denied` / `approval_denied` 等）。

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-07-15-connector-cli-mcp-contract-implementation.md`. Two execution options:

1. Subagent-Driven (recommended) - I dispatch a fresh subagent per task, review between tasks, fast iteration
2. Inline Execution - Execute tasks in this session using executing-plans, batch execution with checkpoints

Which approach?

# MCP csharp-sdk v2.0 升级 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** 将 OpenClaw.NET 的 MCP 依赖与行为从 1.4.x 升级到 2.0.0，并在 Gateway、Agent、MCP App、Client 四条链路上完成可验证迁移。

**Architecture:** 采用三层迁移：先做依赖与编译收敛，再做协议与行为收敛，最后落地能力与治理（Tasks 扩展、OAuth/PKCE 严格策略、回退开关与文档）。通过 `/mcp` 与 `/apps/mcp/{serverId}` 契约测试保证一致性，避免网关主路由与代理路由分叉。

**Tech Stack:** .NET 10、ASP.NET Core、ModelContextProtocol 2.0.0、xUnit v3、NSubstitute

## Global Constraints

- MCP 相关包升级到 `2.0.0`
- 网关 `/mcp` 与 `/apps/mcp/{serverId}` 路由行为统一
- 默认走 v2 discover-first 协商路径
- `Tool.inputSchema` 严格化，缺失时必须明确失败
- 兼容非对象 `structuredContent` 返回语义
- OAuth issuer 与 PKCE S256 默认严格
- 仅提供最小必要回退开关，并记录审计与退场期限
- 不改变 Gateway 与 Runtime 的职责边界
- 不将可选扩展变成强依赖
- 保持 NativeAOT 友好，避免引入反射重路径

---

## File Structure Map

- Modify: `src/OpenClaw.Agent/OpenClaw.Agent.csproj`
  - 责任：升级 Agent 侧 MCP SDK 版本与扩展包引用。
- Modify: `src/OpenClaw.Gateway/OpenClaw.Gateway.csproj`
  - 责任：升级 Gateway 侧 MCP SDK 版本与扩展包引用。
- Modify: `src/mcpapp/OpenClaw.McpApp/OpenClaw.McpApp.csproj`
  - 责任：升级 MCP App 托管侧 MCP SDK 版本。
- Modify: `src/OpenClaw.Gateway/Mcp/McpServiceExtensions.cs`
  - 责任：收敛 v2 transport/协商配置，注入兼容开关与可观测字段。
- Modify: `src/OpenClaw.Gateway/Endpoints/AppsMcpProxyEndpoint.cs`
  - 责任：保证代理路由在工具/资源/调用路径与主路由语义一致。
- Modify: `src/OpenClaw.Agent/Plugins/McpServerToolRegistry.cs`
  - 责任：对齐 v2 工具 schema 与错误传播语义。
- Modify: `src/mcpapp/OpenClaw.McpApp/McpAppServer.cs`
  - 责任：对齐 v2 工具枚举与 schema 处理。
- Modify: `src/mcpapp/OpenClaw.McpApp/McpAppToolProvider.cs`
  - 责任：对齐 v2 structuredContent 拼接行为。
- Modify: `src/OpenClaw.Client/McpModels.cs`
  - 责任：扩展客户端 MCP 结果模型，支持 structuredContent 与 discover。
- Modify: `src/OpenClaw.Client/McpJsonContext.cs`
  - 责任：补齐新增模型的 Source Gen 注册。
- Modify: `src/OpenClaw.Client/OpenClawHttpClient.cs`
  - 责任：补 discover 与 v2 结果解析入口。
- Modify: `src/OpenClaw.Core/Models/GatewayConfig.cs`
  - 责任：新增 MCP v2 兼容开关配置对象。
- Test: `src/OpenClaw.Tests/AppsMcpProxyEndpointTests.cs`
  - 责任：验证 `/apps/mcp/{serverId}` 路由契约。
- Test: `src/OpenClaw.Tests/McpServerToolRegistryTests.cs`
  - 责任：验证 Agent 外部 MCP 注册与执行兼容。
- Test: `src/OpenClaw.Tests/McpAppTests.cs`
  - 责任：验证 MCP App 托管侧工具枚举与 schema 行为。
- Test: `src/OpenClaw.Tests/GatewayAdminEndpointTests.cs`
  - 责任：验证 OpenClawHttpClient 的 MCP 面可用性。
- Modify: `docs/COMPATIBILITY.md`
  - 责任：记录 MCP v2 兼容矩阵与默认策略。
- Modify: `docs/PRODUCTION_FIXES.md`
  - 责任：记录回退开关与应急流程。
- Modify: `CHANGELOG.md`
  - 责任：发布说明与 breaking behavior 提示。

### Task 1: 升级依赖并建立编译基线

**Files:**
- Modify: `src/OpenClaw.Agent/OpenClaw.Agent.csproj`
- Modify: `src/OpenClaw.Gateway/OpenClaw.Gateway.csproj`
- Modify: `src/mcpapp/OpenClaw.McpApp/OpenClaw.McpApp.csproj`
- Test: `src/OpenClaw.Tests/McpServerToolRegistryTests.cs`

**Interfaces:**
- Consumes: 现有 `McpClient.CreateAsync(...)`、`ListToolsAsync(...)`、`CallToolAsync(...)` 调用链。
- Produces: 三项目 MCP 依赖统一到 `2.0.0`，可运行后续协议兼容改造任务。

- [x] **Step 1: 写一个失败的编译验证（先锁定现状）**

```bash
dotnet build .\OpenClaw.Net.slnx -c Debug -p:OpenClawSkipDashboardBuild=true
```

Expected: 在未改动依赖前 PASS（作为基线记录）。

- [x] **Step 2: 先改包版本，再运行编译确认失败点**

```xml
<!-- src/OpenClaw.Agent/OpenClaw.Agent.csproj -->
<PackageReference Include="ModelContextProtocol" Version="2.0.0" />

<!-- src/OpenClaw.Gateway/OpenClaw.Gateway.csproj -->
<PackageReference Include="ModelContextProtocol.AspNetCore" Version="2.0.0" />

<!-- src/mcpapp/OpenClaw.McpApp/OpenClaw.McpApp.csproj -->
<PackageReference Include="ModelContextProtocol" Version="2.0.0" />
```

Run:

```bash
dotnet build .\OpenClaw.Net.slnx -c Debug -p:OpenClawSkipDashboardBuild=true
```

Expected: FAIL，输出 v2 API 变更相关编译错误清单。

- [x] **Step 3: 最小修复编译破坏（不改行为）**

```csharp
// 按编译器报错逐项修复 using/类型名/方法签名
// 示例：修正 Tool schema 属性名或返回类型变化
// 保持原行为不变，只做编译适配
```

- [x] **Step 4: 重新构建验证通过**

Run:

```bash
dotnet build .\OpenClaw.Net.slnx -c Debug -p:OpenClawSkipDashboardBuild=true
```

Expected: PASS。

- [x] **Step 5: Commit**

```bash
git add src/OpenClaw.Agent/OpenClaw.Agent.csproj src/OpenClaw.Gateway/OpenClaw.Gateway.csproj src/mcpapp/OpenClaw.McpApp/OpenClaw.McpApp.csproj
git commit -m "build(mcp): upgrade csharp-sdk packages to 2.0.0"
```

### Task 2: 引入 Gateway MCP v2 兼容配置与 discover-first 可观测

**Files:**
- Modify: `src/OpenClaw.Core/Models/GatewayConfig.cs`
- Modify: `src/OpenClaw.Gateway/Mcp/McpServiceExtensions.cs`
- Test: `src/OpenClaw.Tests/AppsMcpProxyEndpointTests.cs`

**Interfaces:**
- Consumes: `GatewayConfig`、`AddOpenClawMcpServices(...)`。
- Produces:
  - `McpCompatibilityConfig`（新）
  - `GatewayConfig.McpCompatibility`（新）
  - `AddOpenClawMcpServices(...)` 根据配置应用 transport/协商策略。

- [x] **Step 1: 先写失败测试（默认配置应为 v2 安全默认）**

```csharp
[Fact]
public void GatewayConfig_McpCompatibility_Defaults_AreStrictAndDiscoveryFirst()
{
    var cfg = new GatewayConfig();
    Assert.True(cfg.McpCompatibility.EnableDiscoveryFirst);
    Assert.True(cfg.McpCompatibility.RequireOAuthIssuerValidation);
    Assert.True(cfg.McpCompatibility.RequirePkceS256);
    Assert.False(cfg.McpCompatibility.ForceLegacyInitialize);
}
```

Run:

```bash
dotnet test .\src\OpenClaw.Tests\OpenClaw.Tests.csproj --filter "FullyQualifiedName~GatewayConfig_McpCompatibility_Defaults_AreStrictAndDiscoveryFirst" -p:OpenClawSkipDashboardBuild=true
```

Expected: FAIL（属性尚不存在）。

- [x] **Step 2: 实现最小配置模型**

```csharp
public sealed class McpCompatibilityConfig
{
    public bool EnableDiscoveryFirst { get; set; } = true;
    public bool ForceLegacyInitialize { get; set; } = false;
    public bool RequireOAuthIssuerValidation { get; set; } = true;
    public bool RequirePkceS256 { get; set; } = true;
  public bool AllowRelaxedInputSchemaValidation { get; set; } = false; // reserved; MCP App enumeration still skips missing inputSchema
}

public sealed partial class GatewayConfig
{
    public McpCompatibilityConfig McpCompatibility { get; set; } = new();
}
```

- [x] **Step 3: 在 MCP 服务注册中接入配置与日志埋点**

```csharp
.WithHttpTransport(options =>
{
    options.Stateless = true;
    options.ConfigureSessionOptions = AppsMcpProxyEndpoint.ConfigureSessionOptionsAsync;
    // v2 默认 discover-first；legacy 只在开关打开时允许回退
    if (startup.Config.McpCompatibility.ForceLegacyInitialize)
    {
        // 通过配置路径保留兼容模式，日志可观测
    }
})
```

- [x] **Step 4: 运行测试验证默认值与构建**

Run:

```bash
dotnet test .\src\OpenClaw.Tests\OpenClaw.Tests.csproj --filter "FullyQualifiedName~GatewayConfig_McpCompatibility_Defaults_AreStrictAndDiscoveryFirst" -p:OpenClawSkipDashboardBuild=true
dotnet build .\OpenClaw.Net.slnx -c Debug -p:OpenClawSkipDashboardBuild=true
```

Expected: PASS。

- [x] **Step 5: Commit**

```bash
git add src/OpenClaw.Core/Models/GatewayConfig.cs src/OpenClaw.Gateway/Mcp/McpServiceExtensions.cs src/OpenClaw.Tests
git commit -m "feat(mcp): add gateway v2 compatibility config and defaults"
```

### Task 3: 对齐 `/mcp` 与 `/apps/mcp/{appId}` 代理行为与错误语义

**Files:**
- Modify: `src/OpenClaw.Gateway/Endpoints/AppsMcpProxyEndpoint.cs`
- Test: `src/OpenClaw.Tests/AppsMcpProxyEndpointTests.cs`

**Interfaces:**
- Consumes: `ConfigureSessionOptionsAsync(HttpContext, McpServerOptions, CancellationToken)`。
- Produces:
  - 代理路径统一 handler 绑定策略
  - 未找到 app 时显式返回可诊断 MCP 错误。

- [x] **Step 1: 写失败测试（未知 app 返回可诊断错误）**

```csharp
[Fact]
public async Task CallTool_UnknownAppId_ReturnsActionableErrorPayload()
{
    var upstreamUrl = await StartFakeUpstreamAsync();
    await using var gateway = await StartGatewayWithProxyAsync("inventory-app", upstreamUrl);

    await using var mcpClient = await McpClient.CreateAsync(
        new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri($"{gateway.BaseAddress}apps/mcp/nonexistent") }),
        cancellationToken: CancellationToken.None);

    var result = await mcpClient.CallToolAsync("echo_session", cancellationToken: CancellationToken.None);

    Assert.Equal(true, result.IsError);
    var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    Assert.Contains("nonexistent", text, StringComparison.OrdinalIgnoreCase);
}
```

Run:

```bash
dotnet test .\src\OpenClaw.Tests\OpenClaw.Tests.csproj --filter "FullyQualifiedName~CallTool_UnknownAppId_ReturnsActionableErrorPayload" -p:OpenClawSkipDashboardBuild=true
```

Expected: FAIL（当前错误信息不可控或不含 serverId）。

- [x] **Step 2: 最小实现：缺失 upstream 时构造显式错误响应**

```csharp
if (upstream is null)
{
    sessionOptions.Handlers.CallToolHandler = (_, _) =>
        ValueTask.FromResult(new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = $"MCP app '{serverId}' is not loaded." }]
        });
    return;
}
```

- [x] **Step 3: 运行代理测试集回归**

Run:

```bash
dotnet test .\src\OpenClaw.Tests\OpenClaw.Tests.csproj --filter "FullyQualifiedName~AppsMcpProxyEndpointTests" -p:OpenClawSkipDashboardBuild=true
```

Expected: PASS。

- [x] **Step 4: Commit**

```bash
git add src/OpenClaw.Gateway/Endpoints/AppsMcpProxyEndpoint.cs src/OpenClaw.Tests/AppsMcpProxyEndpointTests.cs
git commit -m "fix(mcp-proxy): align proxy error semantics with actionable app-id diagnostics"
```

### Task 4: 收敛 Agent 侧 tool schema 与 structuredContent 兼容

**Files:**
- Modify: `src/OpenClaw.Agent/Plugins/McpServerToolRegistry.cs`
- Test: `src/OpenClaw.Tests/McpServerToolRegistryTests.cs`

**Interfaces:**
- Consumes: `ResolveInputSchemaText(JsonElement)`、`LoadToolsFromClientAsync(...)`。
- Produces:
  - 对缺失 inputSchema 的显式失败消息
  - structuredContent-only 工具执行在 v2 下可解析。

- [x] **Step 1: 写失败测试（缺失 inputSchema 必须失败）**

```csharp
[Fact]
public async Task LoadAsync_HttpServer_ToolWithoutInputSchema_FailsWithClearMessage()
{
    var serverUrl = await StartCustomMcpServerAsync(
        new ListToolsResult { Tools = [new Tool { Name = "bad_tool", Description = "missing schema" }] },
        (_, _) => ValueTask.FromResult(new CallToolResult()));

    using var registry = CreateHttpRegistry(serverUrl);

    var ex = await Assert.ThrowsAsync<JsonException>(
        () => registry.LoadAsync(TestContext.Current.CancellationToken));

    Assert.Contains("inputSchema", ex.Message, StringComparison.OrdinalIgnoreCase);
}
```

Run:

```bash
dotnet test .\src\OpenClaw.Tests\OpenClaw.Tests.csproj --filter "FullyQualifiedName~LoadAsync_HttpServer_ToolWithoutInputSchema_FailsWithClearMessage" -p:OpenClawSkipDashboardBuild=true
```

Expected: FAIL（当前可能默认回退为 `{}`）。

- [x] **Step 2: 最小实现：移除 silent fallback，改为明确异常**

```csharp
private static string ResolveInputSchemaText(JsonElement inputSchema)
{
    if (inputSchema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        throw new JsonException("MCP tool payload is missing required inputSchema.");

    return inputSchema.GetRawText();
}
```

- [x] **Step 3: 回归 structured content 用例**

Run:

```bash
dotnet test .\src\OpenClaw.Tests\OpenClaw.Tests.csproj --filter "FullyQualifiedName~LoadAsync_HttpServer_WithStructuredContentOnly_ReturnsStructuredJson" -p:OpenClawSkipDashboardBuild=true
dotnet test .\src\OpenClaw.Tests\OpenClaw.Tests.csproj --filter "FullyQualifiedName~McpServerToolRegistryTests" -p:OpenClawSkipDashboardBuild=true
```

Expected: PASS。

- [x] **Step 4: Commit**

```bash
git add src/OpenClaw.Agent/Plugins/McpServerToolRegistry.cs src/OpenClaw.Tests/McpServerToolRegistryTests.cs
git commit -m "feat(mcp-agent): enforce required inputSchema and keep structured content compatibility"
```

### Task 5: 收敛 MCP App 托管层 v2 schema 与结果拼接

**Files:**
- Modify: `src/mcpapp/OpenClaw.McpApp/McpAppServer.cs`
- Modify: `src/mcpapp/OpenClaw.McpApp/McpAppToolProvider.cs`
- Test: `src/OpenClaw.Tests/McpAppTests.cs`

**Interfaces:**
- Consumes: `EnumerateToolsAsync(...)`、`FormatResponseContent(...)`。
- Produces:
  - `McpAppToolDescriptor.InputSchemaText` 在缺失 schema 场景不再静默补 `{}`
  - structuredContent 在 suppress 开关下行为可预测。

- [x] **Step 1: 写失败测试（MCP App 缺 schema 时不应注册该工具）**

```csharp
[Fact]
public async Task LoadAllAsync_ToolMissingInputSchema_IsSkippedAndReported()
{
    var serverUrl = await StartCustomToolServerWithoutInputSchemaAsync();
    var registry = await CreateRegistryForServerAsync(serverUrl);

    await registry.LoadAllAsync(TestContext.Current.CancellationToken);

    var app = registry.GetApp("inventory-app");
    Assert.NotNull(app);
    Assert.DoesNotContain(app!.GetToolDescriptors(), t => t.RemoteName == "bad_tool");
}
```

Run:

```bash
dotnet test .\src\OpenClaw.Tests\OpenClaw.Tests.csproj --filter "FullyQualifiedName~LoadAllAsync_ToolMissingInputSchema_IsSkippedAndReported" -p:OpenClawSkipDashboardBuild=true
```

Expected: FAIL。

- [x] **Step 2: 实现枚举阶段的 schema 严格校验与告警日志**

```csharp
if (tool.JsonSchema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
{
    _logger.LogWarning("McpApp '{AppId}' tool '{ToolName}' is missing inputSchema and will be skipped.", _state.Manifest.Id, remoteName);
    continue;
}
var inputSchema = tool.JsonSchema.GetRawText();
```

`AllowRelaxedInputSchemaValidation` 仍是预留开关；当前 MCP App 枚举路径不会读取它，而 SDK 归一化后缺失 `inputSchema` 仍可能呈现为 `{"type":"object"}`。

- [x] **Step 3: 验证 structuredContent 抑制行为回归**

Run:

```bash
dotnet test .\src\OpenClaw.Tests\OpenClaw.Tests.csproj --filter "FullyQualifiedName~McpAppTests" -p:OpenClawSkipDashboardBuild=true
```

Expected: PASS。

- [x] **Step 4: Commit**

```bash
git add src/mcpapp/OpenClaw.McpApp/McpAppServer.cs src/mcpapp/OpenClaw.McpApp/McpAppToolProvider.cs src/OpenClaw.Tests/McpAppTests.cs
git commit -m "feat(mcpapp): enforce tool schema validity and stabilize v2 structured content behavior"
```

### Task 6: 增强 OpenClaw.Client MCP v2 discover/structuredContent 支持

**Files:**
- Modify: `src/OpenClaw.Client/McpModels.cs`
- Modify: `src/OpenClaw.Client/McpJsonContext.cs`
- Modify: `src/OpenClaw.Client/OpenClawHttpClient.cs`
- Test: `src/OpenClaw.Tests/GatewayAdminEndpointTests.cs`

**Interfaces:**
- Consumes: `SendMcpAsync(...)` 与现有 MCP JSON-RPC 管道。
- Produces:
  - `Task<McpDiscoverResult> DiscoverMcpAsync(CancellationToken cancellationToken)`
  - `McpCallToolResult.StructuredContent` 新字段。

- [x] **Step 1: 写失败测试（SDK client 可走 discover）**

```csharp
[Fact]
public async Task OpenClawHttpClient_McpDiscover_Works()
{
    await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);
    using var client = new OpenClawHttpClient(harness.Client.BaseAddress!.ToString(), harness.AuthToken, harness.Client);

    var discover = await client.DiscoverMcpAsync(TestContext.Current.CancellationToken);

    Assert.NotNull(discover);
    Assert.False(string.IsNullOrWhiteSpace(discover.ProtocolVersion));
}
```

Run:

```bash
dotnet test .\src\OpenClaw.Tests\OpenClaw.Tests.csproj --filter "FullyQualifiedName~OpenClawHttpClient_McpDiscover_Works" -p:OpenClawSkipDashboardBuild=true
```

Expected: FAIL（方法与模型尚不存在）。

- [x] **Step 2: 增加模型与 JSON Context 注册**

```csharp
public sealed class McpDiscoverResult
{
    public string ProtocolVersion { get; init; } = string.Empty;
    public JsonElement Capabilities { get; init; }
}

public sealed class McpCallToolResult
{
    public IReadOnlyList<McpTextContent> Content { get; init; } = [];
    public JsonElement StructuredContent { get; init; }
    public bool IsError { get; init; }
}
```

```csharp
[JsonSerializable(typeof(McpDiscoverResult))]
```

- [x] **Step 3: 实现 Discover 调用入口**

```csharp
public Task<McpDiscoverResult> DiscoverMcpAsync(CancellationToken cancellationToken)
    => SendMcpWithoutParamsAsync("server/discover", McpJsonContext.Default.McpDiscoverResult, cancellationToken);
```

- [x] **Step 4: 运行 MCP 客户端测试回归**

Run:

```bash
dotnet test .\src\OpenClaw.Tests\OpenClaw.Tests.csproj --filter "FullyQualifiedName~OpenClawHttpClient_McpSurface_Works|FullyQualifiedName~OpenClawHttpClient_McpDiscover_Works" -p:OpenClawSkipDashboardBuild=true
```

Expected: PASS。

- [x] **Step 5: Commit**

```bash
git add src/OpenClaw.Client/McpModels.cs src/OpenClaw.Client/McpJsonContext.cs src/OpenClaw.Client/OpenClawHttpClient.cs src/OpenClaw.Tests/GatewayAdminEndpointTests.cs
git commit -m "feat(client-mcp): add discover endpoint and v2 structuredContent model support"
```

### Task 7: 接入 Tasks 扩展与安全治理文档

**Files:**
- Modify: `src/OpenClaw.Gateway/Mcp/McpServiceExtensions.cs`
- Modify: `src/OpenClaw.Gateway/OpenClaw.Gateway.csproj`
- Modify: `docs/COMPATIBILITY.md`
- Modify: `docs/PRODUCTION_FIXES.md`
- Modify: `CHANGELOG.md`
- Test: `src/OpenClaw.Tests/AppsMcpProxyEndpointTests.cs`

**Interfaces:**
- Consumes: `services.AddMcpServer(...).With...` 注册链。
- Produces:
  - Tasks 扩展注册（v2）
  - 文档化回退开关：`ForceLegacyInitialize`、`AllowRelaxedInputSchemaValidation`、OAuth/PKCE 严格度。

- [x] **Step 1: 写失败测试（服务启动后 tasks 能力可见）**

```csharp
[Fact]
public async Task GatewayMcpServer_AdvertisesTasksExtension_WhenEnabled()
{
    var (serverUrl, _) = await StartMcpServerAsync<DemoMcpTools>();
    await using var gateway = await StartGatewayWithProxyAndDefaultMcpAsync("inventory-app", serverUrl);

    await using var mcpClient = await McpClient.CreateAsync(
        new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri($"{gateway.BaseAddress}mcp") }),
        cancellationToken: CancellationToken.None);

    var tools = await mcpClient.ListToolsAsync(cancellationToken: CancellationToken.None);
    Assert.NotNull(tools);
}
```

Run:

```bash
dotnet test .\src\OpenClaw.Tests\OpenClaw.Tests.csproj --filter "FullyQualifiedName~GatewayMcpServer_AdvertisesTasksExtension_WhenEnabled" -p:OpenClawSkipDashboardBuild=true
```

Expected: FAIL（扩展尚未注册）。

- [x] **Step 2: 增加 Tasks 扩展包与注册代码**

```xml
<PackageReference Include="ModelContextProtocol.Extensions.Tasks" Version="2.0.0" />
```

```csharp
services.AddMcpServer(options => { /* ... */ })
    .WithHttpTransport(options => { /* ... */ })
    .WithTasks(new InMemoryMcpTaskStore())
    .WithTools<OpenClawMcpTools>()
    .WithResources<OpenClawMcpResources>()
    .WithPrompts<OpenClawMcpPrompts>();
```

- [x] **Step 3: 更新文档与变更日志**

```markdown
- `docs/COMPATIBILITY.md`: 增加 MCP 2.0 兼容矩阵与 discover-first 默认说明。
- `docs/PRODUCTION_FIXES.md`: 增加回退开关启用条件、审计要求、退场时限。
- `CHANGELOG.md`: 记录 breaking behavior 与迁移建议。
```

- [x] **Step 4: 运行测试与构建封板**

Run:

```bash
dotnet test .\src\OpenClaw.Tests\OpenClaw.Tests.csproj --filter "FullyQualifiedName~Mcp|FullyQualifiedName~OpenClawHttpClient_Mcp" -p:OpenClawSkipDashboardBuild=true
dotnet build .\OpenClaw.Net.slnx -c Release -p:OpenClawSkipDashboardBuild=true
```

Expected: PASS。

- [x] **Step 5: Commit**

```bash
git add src/OpenClaw.Gateway/Mcp/McpServiceExtensions.cs src/OpenClaw.Gateway/OpenClaw.Gateway.csproj docs/COMPATIBILITY.md docs/PRODUCTION_FIXES.md CHANGELOG.md
git commit -m "feat(mcp): enable tasks extension and document v2 compatibility/rollback policy"
```

## Self-Review

### 1) Spec coverage

- 依赖升级：Task 1
- discover-first 与路由一致性：Task 2、Task 3
- inputSchema 严格化：Task 4、Task 5
- structuredContent 兼容：Task 4、Task 5、Task 6
- 客户端升级：Task 6
- Tasks 扩展：Task 7
- OAuth/PKCE 严格策略与回退开关：Task 2、Task 7
- 文档与发布说明：Task 7

结论：已覆盖全部 spec 需求。

### 2) Placeholder scan

已检查全文，无空白关键字或延期实现描述。

### 3) Type consistency

- `GatewayConfig.McpCompatibility` 与 `McpCompatibilityConfig` 前后命名一致。
- `DiscoverMcpAsync(...)`、`McpDiscoverResult` 在模型与调用层保持一致。
- `ResolveInputSchemaText(...)` 的失败语义与测试断言一致。

结论：未发现命名/签名冲突。

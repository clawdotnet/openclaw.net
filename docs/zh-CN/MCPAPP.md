# MCP App 支持

MCP App 是 OpenClaw.NET 中将第三方 MCP 应用程序作为一等公民管理的模块。它提供基于清单（manifest）的自动发现、生命周期和进程管理以及工具桥接能力，让 MCP 应用像 VS Code 扩展一样被安装、发现和使用。

OpenClaw 以 MCP 优先方式集成 MCP App 生态，兼容 [Model Context Protocol](https://modelcontextprotocol.io/) 2025-03-26 协议版本和 `text/html;profile=mcp-app` 交互式 UI 规范。

## 与现有 MCP 集成的区别

OpenClaw 已有两层 MCP 支持，MCP App 模块是第三层：

| 层级 | 模块 | 方向 | 用途 |
|------|------|------|------|
| **MCP Server** | `OpenClaw.Gateway/Mcp` | 对外暴露 | 把 OpenClaw 自身作为 MCP Server，供 Claude Desktop 等客户端调用 |
| **MCP Client** | `OpenClaw.Agent/Plugins` | 对内消费 | 连接外部 MCP Server，将其工具注册为 OpenClaw Agent 的原生工具 |
| **MCP App** | `OpenClaw.McpApp` | 托管管理 | 发现、安装、隔离托管第三方 MCP 应用，管理完整生命周期 |

MCP App 专为**需要打包分发**的 MCP 应用设计——每个 App 有自己的 manifest、版本号、工具前缀和 UI 资源。

## 新增功能

- 基于 `openclaw.mcpapp.json` 清单文件的自动发现
- 多路径递归扫描，支持嵌套目录
- stdio / http 两种传输模式
- Allowlist / Denylist 过滤和 glob 模式匹配
- 每应用独立的启用/禁用和参数覆盖
- 工具名前缀自动应用，避免名称冲突
- MCP App UI 资源识别（`text/html;profile=mcp-app`）
- 六阶段生命周期跟踪（Discovered → Validated → Loaded → Running → Stopped → Failed）
- 与 `NativePluginRegistry` 无缝集成——发现工具自动变为 Agent 可用工具
- 浏览器 host 端点，让 MCP App UI 复用 Agent 当前使用的同一条上游 MCP 会话

## 浏览器 Host 端点

OpenClaw.NET 为浏览器侧 MCP App UI 暴露了一组面向 gateway 的 host 路由：

| 路由 | 用途 |
|------|------|
| `/apps/health` | 返回当前选中的 MCP App id，以及浏览器应连接的 gateway MCP 端点 |
| `/apps/chat` | 把浏览器 host 的聊天请求桥接到现有 `GatewayAppRuntime`，并输出 `session`、`text`、`tool`、`result`、`done` 形状的 SSE |
| `/apps/mcp/{appId}` | 把 MCP 请求代理到该 App 已经连接好的 `McpClient` |

关键点是：浏览器 UI 应连接 `/apps/mcp/{appId}`，而不是直接连接 MCP App 的原始上游 URL。这样浏览器触发的 MCP 调用与 Agent 触发的 MCP 调用才能落在同一条 OpenClaw 管理的会话上。

### 会话复用行为

- `/apps/health` 返回的 `mcp` 字段指向 gateway 自己的 `/apps/mcp/{appId}` 路由。
- `/apps/mcp/{appId}` 会把 `tools/list`、`resources/list`、`resources/read`、`tools/call` 转发给 `McpAppRegistry` 中已加载的 App。
- 如果浏览器在 MCP 端点 URL 上带了 `?sessionId=...`，OpenClaw 会在转发 `tools/call` 前把它注入到 `_meta.sessionId`。
- `/apps/chat` 会用同一个 session id 创建或恢复 gateway 会话，并把 host 友好的 SSE 事件流回浏览器。

这就是交互式 MCP App UI 和 Agent 能够围绕同一个 App 会话协作的桥梁，而不是各自单独建一条不相关的 MCP 连接。

### 与工作区 MCP 服务器热重载的区别

`/admin/workspace/mcp` 是面向普通 `Plugins:Mcp` 服务器定义的独立管理接口。它持久化工作区 MCP 服务器 JSON 并热重载标准 MCP 插件工具面。它**不**管理基于清单发现的 MCP Apps，也不替代本文档描述的 `/apps/*` 宿主/代理路由。

### 浏览器兼容性说明

- `/apps/mcp/{appId}` 上的 `tools/list` 故意保持原样透传，因此 app-only 工具依然会出现在浏览器侧列表里。
- 面向模型的可见性过滤发生在 OpenClaw 把工具注册进 `NativePluginRegistry` 时，而不是发生在浏览器 host 代理层。
- 带 `_meta.ui.resourceUri` 的 UI 工具在结果回流给模型时会抑制 `structuredContent`，但浏览器侧 MCP 代理仍然按正常 MCP 响应透传。
- 为支持跨源浏览器 MCP 客户端，OpenClaw 在 CORS 中放行了 `mcp-protocol-version` 和 `Mcp-Session-Id` 这两个 MCP Streamable HTTP 头。

## 架构

```
appsettings.json / GatewayConfig.McpApps
        │
        ▼
McpAppDiscovery
  扫描 DiscoveryPaths（默认 ./mcpapps/）
  查找 ** /openclaw.mcpapp.json
  反序列化 + 验证 + allow/deny 过滤
        │
        ▼
McpAppInstallState（每个 App 一个实例）
  生命周期: Discovered → Validated → [Disabled|Failed] → Loaded → Running → Stopped
        │
        ▼
McpAppRegistry
  管理全部 App 的集中注册表
  调用 McpAppServer 建立连接
        │
        ▼
McpAppServer
  通过 stdio 子进程 / HTTP 端点连接 MCP App
  枚举 tools、resources、prompts
  生成 IMcpAppInfoProvider
        │
        ▼
McpAppNativeTool（实现 ITool）
  注册进 NativePluginRegistry
  Agent 可像调用内置工具一样调用 MCP App 工具
```

### 项目结构

```
src/mcpapp/
├── OpenClaw.McpApp/
│   ├── OpenClaw.McpApp.csproj      # 依赖 OpenClaw.Core + ModelContextProtocol
│   ├── shared/
│   │   └── McpAppInfoProvider.cs   # IMcpAppInfoProvider + 描述符类型 + 默认实现
│   ├── Models/
│   │   ├── McpAppManifest.cs       # openclaw.mcpapp.json 清单模型 + AOT JSON 源生成
│   │   └── McpAppInstallState.cs   # 安装状态 + 六阶段生命周期枚举
│   ├── McpAppDiscovery.cs          # 清单扫描、加载、验证、allow/deny 过滤
│   ├── McpAppServer.cs             # 单 App 生命周期管理——连接、枚举、断开
│   ├── McpAppToolProvider.cs       # McpAppNativeTool——将 MCP App 工具桥接为 ITool
│   └── McpAppServiceExtensions.cs  # DI 注册 + McpAppRegistry 集中注册表
└── test-apps/
    └── GroceryInventory/
        └── openclaw.mcpapp.json    # 示例 MCP App 清单（GroceryInventory.Api）
```

## 清单格式（openclaw.mcpapp.json）

```json
{
  "id": "grocery-inventory",
  "name": "Grocery Inventory Dashboard",
  "description": "Multi-store grocery inventory management MCP App with interactive dashboard. Manage products, categories, suppliers, stores, and inventory levels across a retail chain. Includes stock adjustment, low-stock alerts, restock recommendations, and purchase order drafting.",
  "version": "1.0.0",
  "protocolVersion": "2025-03-26",
  "iconUrl": "https://img.icons8.com/color/48/grocery-store.png",
  "author": "OpenClaw MCP App Demo",
  "license": "MIT",
  "homepageUrl": "https://github.com/boclifton-MSFT/MCPAppsDemo",
  "tags": ["grocery", "inventory", "retail", "supply-chain", "dashboard"],
  "transport": "http",
  "url": "https://localhost:5001/mcp",
  "hasUi": true,
  "uiResourceUri": "ui://grocery/store-dashboard.html",
  "category": "data",
  "capabilities": ["tools", "resources", "prompts", "completions"],
  "startupTimeoutSeconds": 10,
  "requestTimeoutSeconds": 60,
  "toolNamePrefix": "grocery.",
  "headers": {}
}
```

### 清单字段说明

| 字段 | 类型 | 必需 | 说明 |
|------|------|------|------|
| `id` | string | **是** | 唯一应用标识符 |
| `name` | string | 否 | 人类可读的显示名称 |
| `description` | string | 否 | 简短功能描述 |
| `version` | string | 推荐 | 语义化版本号 |
| `protocolVersion` | string | 否 | MCP 协议版本，默认 `2025-03-26` |
| `transport` | string | 否 | `stdio` 或 `http`，默认 `stdio` |
| `command` | string | stdio 必需 | 启动命令 |
| `arguments` | string[] | 否 | 命令行参数 |
| `workingDirectory` | string | 否 | 工作目录 |
| `url` | string | http 必需 | 服务器端点 URL |
| `headers` | dict | 否 | HTTP 请求头 |
| `environment` | dict | 否 | 进程环境变量 |
| `hasUi` | bool | 否 | 是否包含 MCP App UI 包 |
| `uiResourceUri` | string | 否 | 主 UI 资源 URI |
| `toolNamePrefix` | string | 否 | 工具名前缀（如 `grocery.`） |
| `startupTimeoutSeconds` | int | 否 | 启动超时，默认 15 秒 |
| `requestTimeoutSeconds` | int | 否 | 请求超时，默认 60 秒 |
| `tags` | string[] | 否 | 分类标签 |
| `category` | string | 否 | 分组类别 |
| `capabilities` | string[] | 否 | 能力声明，默认 `["tools"]` |

## 配置

### GatewayConfig

在 `appsettings.json` 中配置：

```json
{
  "McpApps": {
    "Enabled": true,
    "DiscoveryPaths": ["./mcpapps"],
    "AllowlistSemantics": "legacy",
    "Allow": ["*"],
    "Deny": [],
    "Entries": {
      "grocery-inventory": {
        "Enabled": true,
        "Url": "https://localhost:5001/mcp",
        "ToolNamePrefix": "grocery.",
        "StartupTimeoutSeconds": 15
      }
    }
  }
}
```

### 配置字段

| 字段 | 默认值 | 说明 |
|------|--------|------|
| `Enabled` | `false` | 总开关 |
| `DiscoveryPaths` | `["./mcpapps"]` | 扫描 openclaw.mcpapp.json 的目录 |
| `AllowlistSemantics` | `"legacy"` | `legacy`（空=全部允许）或 `strict`（空=全部拒绝） |
| `Allow` | `[]` | 允许列表，支持 `*` 和 `prefix-*` 通配符 |
| `Deny` | `[]` | 拒绝列表，拒绝优先于允许 |
| `Entries` | `{}` | 按 App ID 的细粒度覆盖 |

### Entries 覆盖

`Entries` 中可覆盖清单中的以下字段：
- `Enabled`：单独禁用某个 App
- `Transport`、`Command`、`Url`：覆盖连接方式
- `ToolNamePrefix`：覆盖工具名前缀
- `StartupTimeoutSeconds`、`RequestTimeoutSeconds`：覆盖超时
- `Environment`：追加环境变量

## 生命周期

```
Discovered ──→ Validated ──→ Loaded ──→ Running
     │              │            │           │
     │              │            │           │
     └──────────────┴────────────┴─→ Failed  │
                                            │
                                     Stopped ←
```

| 状态 | 触发条件 | 说明 |
|------|----------|------|
| `Discovered` | 扫描到 manifest 文件 | 初始状态，尚未验证 |
| `Validated` | 通过字段校验 | 清单有效，可以加载 |
| `Disabled` | 被 allow/deny 或 entry 禁用 | 不会被加载 |
| `Loaded` | 开始连接 MCP Server | 正在建立连接 |
| `Running` | 成功枚举 tools/resources/prompts | 工具已可用 |
| `Stopped` | 主动断开或 Dispose | 连接已关闭 |
| `Failed` | 连接或枚举失败 | `LastError` 记录失败原因 |

## 工具桥接

每个 MCP App 发现的工具通过 `McpAppNativeTool`（实现 `ITool`）注册到 `NativePluginRegistry`：

- **工具注册 ID**：`mcpapp:{appId}`（如 `mcpapp:grocery-inventory`）
- **工具名前缀**：由 manifest 的 `toolNamePrefix` 自动应用
- **可见性处理**：带有 `"ui": { "visibility": ["app"] }` 这类 MCP App metadata 的工具仍会提供给浏览器 host，但不会作为模型可见工具注册给 Agent
- **错误处理**：无效 JSON 参数和远程执行错误均返回以 `Error:` 开头的结果文本
- **结构化输出**：支持 MCP 工具的文本内容和结构化内容；但带 UI 的工具在结果回流给模型时会抑制 `structuredContent`

```csharp
// 内部流程（Gateway 启动时自动执行）
await appRegistry.RegisterMcpAppToolsAsync(
    nativeRegistry,
    config.McpApps,
    cancellationToken);
```

注册后，MCP App 工具与内置工具、插件工具一起通过统一的偏好解析器（`NativePluginRegistry.ResolvePreference`）参与工具选择。

## 编写 MCP App

一个 MCP App 可以是任何实现了 MCP 协议的应用。最小示例（ASP.NET Core）：

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMcpServer()
    .WithToolsFromAssembly()
    .WithHttpTransport();

var app = builder.Build();
app.MapMcp("/mcp");
app.Run();
```

然后在项目根目录创建 `openclaw.mcpapp.json`，设置 `transport: "http"` 和 `url` 指向 MCP 端点。

对于包含交互式 UI 的 MCP App（如 GroceryInventory.Api 的仪表板），使用 `"text/html;profile=mcp-app"` MIME 类型提供 HTML 资源，并在工具调用的 `_meta.ui.resourceUri` 中引用该资源。

## 测试教程：GroceryInventory.Api

[GroceryInventory.Api](https://github.com/boclifton-MSFT/MCPAppsDemo) 是一个完整的多门店杂货库存管理 MCP App，作为 `OpenClaw.McpApp` 的测试夹具使用。其清单文件位于 `src/mcpapp/test-apps/GroceryInventory/openclaw.mcpapp.json`。

### 应用架构

```
GroceryInventory.Api (ASP.NET Core + MCP)
├── Program.cs                     # 启动入口：AddMcpServer() + MapMcp("/mcp")
├── Models/
│   ├── Category.cs                # 品类（Id, Name）
│   ├── Product.cs                 # 商品（Id, Sku, Name, UnitPrice, UnitOfMeasure）
│   ├── Supplier.cs                # 供应商（Id, Name, ContactEmail, Phone）
│   ├── Store.cs                   # 门店（Id, Name, City, State）
│   └── InventoryItem.cs           # 库存行（StoreId, ProductId, QuantityOnHand, ReorderThreshold）
├── Services/
│   ├── IInventoryService.cs       # 数据层抽象
│   └── InMemoryInventoryService.cs # 内存实现（含种子数据）
├── Endpoints/                     # REST API（/api/*）
│   ├── CategoryEndpoints.cs
│   ├── ProductEndpoints.cs
│   ├── SupplierEndpoints.cs
│   ├── StoreEndpoints.cs
│   └── InventoryEndpoints.cs
├── McpTools/
│   ├── Tools.cs                   # 核心 MCP 工具（get_categories, get_inventory, set_inventory…）
│   ├── DashboardTools.cs          # 仪表板工具（show_store_inventory_dashboard + 结构化输出）
│   ├── ResourceTemplates.cs       # 动态资源模板（inventory://stores/{storeId}/summary）
│   ├── Prompts.cs                 # MCP 提示模板（分析门店、草拟采购单…）
│   ├── Completions.cs             # MCP 补全处理器
│   └── ElicitationTools.cs        # 引导式交互工具
├── McpApp/
│   ├── GroceryDashboardResource.cs # MCP App UI 资源（text/html;profile=mcp-app）
│   ├── src/                       # TypeScript 前端源码（Vite 构建）
│   └── dist/index.html            # 构建产物：单文件交互式仪表板
└── Properties/
    └── launchSettings.json
```

### MCP 工具清单（共 12+ 个工具）

**数据查询工具**（`McpTools.cs`）：

| 工具名 | 说明 |
|--------|------|
| `grocery.get_categories` | 查询品类，可选 `id` 过滤 |
| `grocery.get_products` | 查询商品，支持 `id`/`categoryId`/`supplierId` 过滤 |
| `grocery.get_suppliers` | 查询供应商，可选 `id` |
| `grocery.get_stores` | 查询门店，可选 `id` |
| `grocery.get_inventory` | 查询库存，支持 `storeId`/`productId` 过滤 |
| `grocery.get_low_stock` | 列出低库存项（库存≤补货阈值） |
| `grocery.set_inventory` | 创建或替换库存行（绝对值） |
| `grocery.adjust_stock` | 按增量调整库存（正/负），操作后发送资源变更通知 |

#### 仪表板工具（`DashboardTools.cs`）

| 工具名 | 说明 |
|--------|------|
| `grocery.show_store_inventory_dashboard` | 渲染交互式多门店库存仪表板，`_meta.ui.resourceUri` 指向 `ui://grocery/store-dashboard.html` |

#### 引导式工具（`ElicitationTools.cs`）

支持分步式数据录入和决策引导。

### MCP 资源

| 资源 URI | 说明 |
|----------|------|
| `ui://grocery/store-dashboard.html` | MCP App UI：单文件 HTML 仪表板（MIME: `text/html;profile=mcp-app`） |
| `inventory://stores/{storeId}/summary` | 门店库存摘要 JSON |
| `inventory://products/{productId}/details` | 商品详情 + 各门店库存 |

### MCP 提示模板

| 提示名 | 说明 |
|--------|------|
| `analyze_store_performance` | 分析单个门店的库存健康度：覆盖度、低库存热点、风险价值、建议操作 |
| `draft_supplier_order` | 基于低库存数据草拟供应商采购单，支持 routine/urgent/emergency 紧急程度 |
| `explain_inventory_term` | 用通俗语言解释库存管理术语 |

### 在 OpenClaw 中集成 GroceryInventory.Api

#### 步骤 1：启动 GroceryInventory.Api

```bash
cd E:\GitHub\MCPAppsDemo\src\GroceryInventory.Api
dotnet run
# 输出：Now listening on: https://localhost:5001
```

> **提示**：首次运行或修改前端后，先执行 `npm run build`（在 `McpApp/` 目录下），确保 `McpApp/dist/index.html` 是最新构建产物。跳过此步骤将渲染占位页而非交互仪表板。

#### 步骤 2：部署清单文件

将清单文件复制到 OpenClaw 的 McpApp 发现路径：

```bash
# 创建发现目录
mkdir e:\GitHub\openclaw.net\.openclaw\mcpapps\grocery-inventory\

# 复制清单
copy src\mcpapp\test-apps\GroceryInventory\openclaw.mcpapp.json ^
     e:\GitHub\openclaw.net\.openclaw\mcpapps\grocery-inventory\
```

清单内容（`src/mcpapp/test-apps/GroceryInventory/openclaw.mcpapp.json`）：

```json
{
  "id": "grocery-inventory",
  "name": "Grocery Inventory Dashboard",
  "description": "Multi-store grocery inventory management MCP App…",
  "version": "1.0.0",
  "protocolVersion": "2025-03-26",
  "transport": "http",
  "url": "https://localhost:5001/mcp",
  "hasUi": true,
  "uiResourceUri": "ui://grocery/store-dashboard.html",
  "capabilities": ["tools", "resources", "prompts", "completions"],
  "toolNamePrefix": "grocery.",
  "startupTimeoutSeconds": 10,
  "requestTimeoutSeconds": 60
}
```

#### 步骤 3：配置 OpenClaw Gateway

在 `appsettings.json` 中启用 MCP App 支持：

```json
{
  "McpApps": {
    "Enabled": true,
    "DiscoveryPaths": [".openclaw/mcpapps"],
    "Allow": ["*"],
    "Entries": {
      "grocery-inventory": {
        "Enabled": true
      }
    }
  }
}
```

#### 步骤 4：启动 Gateway 并验证

```bash
cd e:\GitHub\openclaw.net
dotnet run --project src/OpenClaw.Gateway
```

启动日志中应看到：

```text
info: OpenClaw.McpApp.McpAppDiscovery[0]
      Scanning for MCP Apps in: .openclaw/mcpapps
info: OpenClaw.McpApp.McpAppDiscovery[0]
      Discovered McpApp 'grocery-inventory' (Grocery Inventory Dashboard) v1.0.0
info: OpenClaw.McpApp.McpAppServer[0]
      Connecting to McpApp 'grocery-inventory' via http
info: OpenClaw.McpApp.McpAppServer[0]
      McpApp 'grocery-inventory' connected: 12 tools, 3 resources, 3 prompts
```

验证工具已注册——通过 OpenClaw Admin 面板或 MCP 客户端查看，应出现带 `grocery.` 前缀的工具。

#### 步骤 5：调用 MCP App 工具

通过 Agent 对话或 MCP 客户端调用：

```text
> 用 show_store_inventory_dashboard 查看所有门店的库存
> get_low_stock，storeId=1
> 帮我分析 Downtown Market 门店的库存健康度（使用 analyze_store_performance 提示）
```

### 运行单元测试

GroceryInventory.Api 的 MCP 工具结构在 `McpAppTests.cs` 中有镜像测试工具 `GroceryMcpTools`，验证工具发现、参数传递、结构化输出等集成路径：

```bash
cd e:\GitHub\openclaw.net
dotnet test src/OpenClaw.Tests --filter "FullyQualifiedName~McpAppTests"
# 输出：通过! - 失败: 0，通过: 47
```

MCP App 模块包含完整的单元测试覆盖（47 个测试用例），涵盖：

| 测试类别 | 测试数 | 覆盖范围 |
|---------|--------|---------|
| 清单序列化 | 4 | JSON 往返、最小 JSON 默认值、stdio 传输、无效 JSON |
| 安装状态 | 3 | 默认值、生命周期转换时间戳、验证错误 |
| InfoProvider | 3 | 基本属性、名称回退、描述符管理 |
| 发现 | 16 | 禁用、无效路径、加载、嵌套扫描、字段验证、不支持传输、allow/deny、strict allowlist、无效 allowlist 语义、glob |
| NativeTool | 4 | HTTP 调用、带参调用、无效 JSON、数组 JSON |
| 服务器 | 8 | 连接、幂等、断开、失败、Dispose、前缀、传输覆盖规范化 |
| 注册表 | 4 | 失败优雅降级、未找到、幂等、双次 Dispose |
| 配置模型 | 2 | 默认值 |
| 描述符模型 | 3 | Schema 默认值、UI 资源、Prompt 参数 |

测试复用现有 `McpServerToolRegistryTests` 模式，使用 `WebApplication.CreateSlimBuilder()` 启动进程内 MCP HTTP Server 进行端到端验证。

## 排查

- **发现不到 App**：检查 `McpApps.Enabled=true`，`DiscoveryPaths` 路径存在且可读，子目录包含 `openclaw.mcpapp.json`
- **App 标记为 Invalid**：查看 `ValidationErrors`——常见原因是缺少 `id` 字段或 `stdio` 模式未指定 `command`
- **连接失败（Failed）**：查看 `LastError`——检查 `url` 可访问性、`command` 可执行性、`startupTimeoutSeconds` 是否足够
- **工具未注册**：确认 App 未被 `Deny` 或 `Entries[appId].Enabled=false` 过滤，确认 MCP Server 的 `tools/list` 返回结果非空
- **工具名称冲突**：通过 `toolNamePrefix` 为每个 App 设置唯一前缀
- **UI 资源未识别**：确认资源 MIME 类型为 `text/html;profile=mcp-app` 且在 manifest 中设置了 `hasUi: true`

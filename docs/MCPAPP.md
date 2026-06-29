# MCP App Support

MCP App is the module that treats third-party MCP applications as first-class citizens in OpenClaw.NET. It provides manifest-based automatic discovery, lifecycle management, sandbox isolation, and tool bridging — letting MCP applications be installed, discovered, and used much like VS Code extensions.

OpenClaw integrates with the MCP App ecosystem MCP-first, compatible with the [Model Context Protocol](https://modelcontextprotocol.io/) 2025-03-26 spec and the `text/html;profile=mcp-app` interactive UI specification.

## How It Differs from Existing MCP Integrations

OpenClaw already has two layers of MCP support. MCP App is the third:

| Layer | Module | Direction | Purpose |
|-------|--------|-----------|---------|
| **MCP Server** | `OpenClaw.Gateway/Mcp` | Outbound exposure | Expose OpenClaw as an MCP Server for clients like Claude Desktop |
| **MCP Client** | `OpenClaw.Agent/Plugins` | Inbound consumption | Connect to external MCP Servers and register their tools as OpenClaw Agent native tools |
| **MCP App** | `OpenClaw.McpApp` | Hosting & management | Discover, install, isolate, and manage third-party MCP applications with full lifecycle control |

MCP App is designed for MCP applications that need to be **packaged and distributed** — each App carries its own manifest, version number, tool prefix, and optional UI resources.

## What It Adds

- Automatic discovery based on `openclaw.mcpapp.json` manifest files
- Recursive multi-path scanning with nested directory support
- Three transport modes: stdio, http, and inprocess
- Allowlist / denylist filtering with glob pattern matching
- Per-app enable/disable toggle and parameter overrides
- Automatic tool name prefix application to avoid collisions
- MCP App UI resource detection (`text/html;profile=mcp-app`)
- Six-stage lifecycle tracking (Discovered → Validated → Loaded → Running → Stopped → Failed)
- Seamless integration with `NativePluginRegistry` — discovered tools become immediately available to the Agent

## Architecture

```
appsettings.json / GatewayConfig.McpApps
        │
        ▼
McpAppDiscovery
  Scans DiscoveryPaths (default ./mcpapps/)
  Looks for ** /openclaw.mcpapp.json
  Deserializes + validates + filters (allow/deny)
        │
        ▼
McpAppInstallState (one per App)
  Lifecycle: Discovered → Validated → [Disabled|Failed] → Loaded → Running → Stopped
        │
        ▼
McpAppRegistry
  Central registry for all Apps
  Calls McpAppServer to establish connections
        │
        ▼
McpAppServer
  Connects to the MCP App via stdio subprocess / HTTP endpoint
  Enumerates tools, resources, and prompts
  Produces an IMcpAppInfoProvider
        │
        ▼
McpAppNativeTool (implements ITool)
  Registered in NativePluginRegistry
  Agent can call MCP App tools the same as built-in tools
```

### Project Structure

```
src/mcpapp/
├── OpenClaw.McpApp/
│   ├── OpenClaw.McpApp.csproj      # Depends on OpenClaw.Core + ModelContextProtocol
│   ├── shared/
│   │   └── McpAppInfoProvider.cs   # IMcpAppInfoProvider + descriptor types + default impl
│   ├── Models/
│   │   ├── McpAppManifest.cs       # openclaw.mcpapp.json model + AOT JSON source gen
│   │   └── McpAppInstallState.cs   # Install state + six-stage lifecycle enum
│   ├── McpAppDiscovery.cs          # Manifest scanning, loading, validation, allow/deny filtering
│   ├── McpAppServer.cs             # Single-App lifecycle — connect, enumerate, disconnect
│   ├── McpAppToolProvider.cs       # McpAppNativeTool — bridges MCP App tools to ITool
│   └── McpAppServiceExtensions.cs  # DI registration + McpAppRegistry
└── test-apps/
    └── GroceryInventory/
        └── openclaw.mcpapp.json    # Example MCP App manifest (GroceryInventory.Api)
```

### Integration Points

| Integration Point | File | What It Does |
|-------------------|------|--------------|
| Config model | `GatewayConfig.cs` | Adds `McpApps` config section |
| Plugin configs | `PluginModels.cs` | `McpAppsConfig` + `McpAppEntryConfig` classes |
| DI registration | `ToolServicesExtensions.cs` | Calls `AddOpenClawMcpAppServices()` |
| Runtime init | `RuntimeInitializationExtensions.cs` | `RegisterMcpAppToolsAsync` on startup + cleanup on shutdown |
| Composition | `RuntimeInitializationExtensions.CompositionStages.cs` | Adds `McpAppRegistry` to `RuntimeServices` |
| Gateway bridge | `McpAppToolRegistrationExtensions.cs` | Gateway-layer tool registration into `NativePluginRegistry` |
| Project reference | `OpenClaw.Gateway.csproj` | References `OpenClaw.McpApp` |

## Manifest Format (openclaw.mcpapp.json)

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
  "homepageUrl": "https://github.com/MCPAppsDemo/GroceryInventory.Api",
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

### Manifest Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `id` | string | **Yes** | Unique application identifier |
| `name` | string | No | Human-readable display name |
| `description` | string | No | Short feature description |
| `version` | string | Recommended | Semantic version |
| `protocolVersion` | string | No | MCP protocol version, default `2025-03-26` |
| `transport` | string | No | `stdio`, `http`, or `inprocess`, default `stdio` |
| `command` | string | Required for stdio | Launch command |
| `arguments` | string[] | No | Command-line arguments |
| `workingDirectory` | string | No | Working directory for the process |
| `url` | string | Required for http | Server endpoint URL |
| `headers` | dict | No | HTTP request headers |
| `environment` | dict | No | Process environment variables |
| `hasUi` | bool | No | Whether the App includes an MCP App UI bundle |
| `uiResourceUri` | string | No | Primary UI resource URI |
| `toolNamePrefix` | string | No | Tool name prefix (e.g. `grocery.`) |
| `startupTimeoutSeconds` | int | No | Startup timeout, default 15 sec |
| `requestTimeoutSeconds` | int | No | Request timeout, default 60 sec |
| `tags` | string[] | No | Categorization tags |
| `category` | string | No | Grouping category |
| `capabilities` | string[] | No | Capability declarations, default `["tools"]` |

## Configuration

### GatewayConfig

Add to `appsettings.json`:

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

### Configuration Fields

| Field | Default | Description |
|-------|---------|-------------|
| `Enabled` | `false` | Master toggle |
| `DiscoveryPaths` | `["./mcpapps"]` | Directories to scan for `openclaw.mcpapp.json` |
| `AllowlistSemantics` | `"legacy"` | `legacy` (empty=allow all) or `strict` (empty=deny all) |
| `Allow` | `[]` | Allowlist, supports `*` and `prefix-*` wildcards |
| `Deny` | `[]` | Denylist, deny takes precedence over allow |
| `Entries` | `{}` | Per-App fine-grained overrides |

### Entries Overrides

`Entries` can override these manifest fields per App ID:

- `Enabled` — disable a specific App individually
- `Transport`, `Command`, `Url` — override connection method
- `ToolNamePrefix` — override tool name prefix
- `StartupTimeoutSeconds`, `RequestTimeoutSeconds` — override timeouts
- `Environment` — append environment variables

## Lifecycle

```
Discovered ──→ Validated ──→ Loaded ──→ Running
     │              │            │           │
     │              │            │           │
     └──────────────┴────────────┴─→ Failed  │
                                            │
                                     Stopped ←
```

| State | Trigger | Description |
|-------|---------|-------------|
| `Discovered` | Manifest file found by scanner | Initial state, not yet validated |
| `Validated` | Passed field validation | Manifest is valid, ready to load |
| `Disabled` | Filtered by allow/deny or entry config | Will not be loaded |
| `Loaded` | Connection to MCP Server started | Connection in progress |
| `Running` | Successfully enumerated tools/resources/prompts | Tools are available |
| `Stopped` | Explicit disconnect or Dispose | Connection closed |
| `Failed` | Connection or enumeration error | `LastError` records the cause |

## Tool Bridging

Every tool discovered from an MCP App is registered into the `NativePluginRegistry` via `McpAppNativeTool` (which implements `ITool`):

- **Registration ID**: `mcpapp:{appId}` (e.g. `mcpapp:grocery-inventory`)
- **Tool name prefix**: automatically applied from the manifest's `toolNamePrefix`
- **Error handling**: invalid JSON arguments and remote execution errors return result text prefixed with `Error:`
- **Structured output**: supports both text content and structured content from MCP tools

```csharp
// Internal flow — automatically executed during Gateway startup
await appRegistry.RegisterMcpAppToolsAsync(
    nativeRegistry,
    config.McpApps,
    cancellationToken);
```

After registration, MCP App tools participate in tool selection alongside built-in tools and plugin tools through the unified preference resolver (`NativePluginRegistry.ResolvePreference`).

## Authoring an MCP App

An MCP App can be any application implementing the MCP protocol. Minimal example (ASP.NET Core):

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMcpServer()
    .WithToolsFromAssembly()
    .WithHttpTransport();

var app = builder.Build();
app.MapMcp("/mcp");
app.Run();
```

Then create an `openclaw.mcpapp.json` in the project root, setting `transport: "http"` and `url` to point to the MCP endpoint.

For MCP Apps that include an interactive UI (such as GroceryInventory.Api's dashboard), serve the HTML resource with the `"text/html;profile=mcp-app"` MIME type, and reference it from tool call `_meta.ui.resourceUri`.

## Testing Tutorial: GroceryInventory.Api

[GroceryInventory.Api](https://github.com/MCPAppsDemo/GroceryInventory.Api) is a complete multi-store grocery inventory management MCP App, used as the test fixture for `OpenClaw.McpApp`. Its manifest lives at `src/mcpapp/test-apps/GroceryInventory/openclaw.mcpapp.json`.

### Application Architecture

```
GroceryInventory.Api (ASP.NET Core + MCP)
├── Program.cs                     # Entry point: AddMcpServer() + MapMcp("/mcp")
├── Models/
│   ├── Category.cs                # Category (Id, Name)
│   ├── Product.cs                 # Product (Id, Sku, Name, UnitPrice, UnitOfMeasure)
│   ├── Supplier.cs                # Supplier (Id, Name, ContactEmail, Phone)
│   ├── Store.cs                   # Store (Id, Name, City, State)
│   └── InventoryItem.cs           # Inventory row (StoreId, ProductId, QuantityOnHand, ReorderThreshold)
├── Services/
│   ├── IInventoryService.cs       # Data layer abstraction
│   └── InMemoryInventoryService.cs # In-memory implementation with seed data
├── Endpoints/                     # REST API (/api/*)
│   ├── CategoryEndpoints.cs
│   ├── ProductEndpoints.cs
│   ├── SupplierEndpoints.cs
│   ├── StoreEndpoints.cs
│   └── InventoryEndpoints.cs
├── McpTools/
│   ├── Tools.cs                   # Core MCP tools (get_categories, get_inventory, set_inventory…)
│   ├── DashboardTools.cs          # Dashboard tool (show_store_inventory_dashboard + structured output)
│   ├── ResourceTemplates.cs       # Dynamic resource templates (inventory://stores/{storeId}/summary)
│   ├── Prompts.cs                 # MCP prompt templates (analyze store, draft order…)
│   ├── Completions.cs             # MCP completion handler
│   └── ElicitationTools.cs        # Elicitation-style interactive tools
├── McpApp/
│   ├── GroceryDashboardResource.cs # MCP App UI resource (text/html;profile=mcp-app)
│   ├── src/                       # TypeScript frontend source (Vite build)
│   └── dist/index.html            # Build output: single-file interactive dashboard
└── Properties/
    └── launchSettings.json
```

### MCP Tools (12+ tools)

#### Data Query Tools (`McpTools.cs`)

| Tool Name | Description |
|-----------|-------------|
| `grocery.get_categories` | Query categories, optional `id` filter |
| `grocery.get_products` | Query products, supports `id`/`categoryId`/`supplierId` filters |
| `grocery.get_suppliers` | Query suppliers, optional `id` |
| `grocery.get_stores` | Query stores, optional `id` |
| `grocery.get_inventory` | Query inventory, supports `storeId`/`productId` filters |
| `grocery.get_low_stock` | List items at or below reorder threshold |
| `grocery.set_inventory` | Create or replace an inventory row (absolute values) |
| `grocery.adjust_stock` | Adjust on-hand quantity by delta (positive/negative), sends resource change notification |

#### Dashboard Tool (`DashboardTools.cs`)

| Tool Name | Description |
|-----------|-------------|
| `grocery.show_store_inventory_dashboard` | Render interactive multi-store inventory dashboard, with `_meta.ui.resourceUri` pointing to `ui://grocery/store-dashboard.html` |

#### Elicitation Tools (`ElicitationTools.cs`)

Supports step-by-step data entry and guided decision-making.

### MCP Resources

| Resource URI | Description |
|--------------|-------------|
| `ui://grocery/store-dashboard.html` | MCP App UI: single-file HTML dashboard (MIME: `text/html;profile=mcp-app`) |
| `inventory://stores/{storeId}/summary` | Per-store inventory summary JSON |
| `inventory://products/{productId}/details` | Product details with per-store stock levels |

### MCP Prompts

| Prompt Name | Description |
|-------------|-------------|
| `analyze_store_performance` | Analyze a store's inventory health: coverage, low-stock hotspots, value at risk, suggested actions |
| `draft_supplier_order` | Draft a supplier purchase order from low-stock data, with routine/urgent/emergency urgency levels |
| `explain_inventory_term` | Explain an inventory management term in plain language |

### Integrating GroceryInventory.Api with OpenClaw

#### Step 1: Start GroceryInventory.Api

```bash
cd E:\GitHub\MCPAppsDemo\src\GroceryInventory.Api
dotnet run
# Output: Now listening on: https://localhost:5001
```

> **Note**: After first checkout or after modifying the frontend, run `npm run build` (in the `McpApp/` directory) to ensure `McpApp/dist/index.html` is the latest build. Skipping this step renders a placeholder page instead of the interactive dashboard.

#### Step 2: Deploy the Manifest

Copy the manifest into OpenClaw's discovery path:

```bash
# Create the discovery directory
mkdir e:\GitHub\openclaw.net\.openclaw\mcpapps\grocery-inventory\

# Copy the manifest
copy src\mcpapp\test-apps\GroceryInventory\openclaw.mcpapp.json \
     e:\GitHub\openclaw.net\.openclaw\mcpapps\grocery-inventory\
```

#### Step 3: Configure OpenClaw Gateway

Enable MCP App support in `appsettings.json`:

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

#### Step 4: Start Gateway and Verify

```bash
cd e:\GitHub\openclaw.net
dotnet run --project src/OpenClaw.Gateway
```

Expected startup log output:

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

Verify tools are registered — check the OpenClaw Admin panel or MCP client; tools should appear with the `grocery.` prefix.

#### Step 5: Call MCP App Tools

Via Agent conversation or MCP client:

```text
> Show store inventory dashboard for all stores
> get_low_stock, storeId=1
> Analyze the Downtown Market store's inventory health (use analyze_store_performance prompt)
```

### Running the Unit Tests

GroceryInventory.Api's MCP tool structure has a mirror test class `GroceryMcpTools` in `McpAppTests.cs`, verifying tool discovery, argument passing, and structured output integration paths:

```bash
cd e:\GitHub\openclaw.net
dotnet test src/OpenClaw.Tests --filter "FullyQualifiedName~McpAppTests"
# Output: Passed! - Failed: 0, Passed: 43
```

## Testing

The MCP App module has comprehensive unit test coverage (43 test cases):

| Test Category | Count | Coverage |
|---------------|-------|----------|
| Manifest serialization | 5 | JSON round-trip, minimal JSON defaults, stdio transport, invalid JSON |
| Install state | 3 | Defaults, lifecycle transition timestamps, validation errors |
| InfoProvider | 4 | Basic properties, name fallback, descriptor management |
| Discovery | 10 | Disabled return, invalid paths, valid loading, nested scan, required field validation, unsupported transport, invalid JSON skip, allow/deny, glob matching, entry config disable |
| NativeTool | 4 | HTTP invocation, argument passing, invalid JSON error, array JSON error |
| Server | 7 | Connection enumeration, idempotent reconnect, disconnect state transition, invalid command failure, Dispose cleanup, manifest/entryConfig prefix override |
| Registry | 4 | Graceful degradation on failure, GetApp not found, idempotent loading, double Dispose |
| Config models | 2 | McpAppsConfig and McpAppEntryConfig defaults |
| Descriptor models | 3 | Default schema, UI resource flag, prompt arguments |

Tests reuse the existing `McpServerToolRegistryTests` pattern of spinning up in-process MCP HTTP Servers via `WebApplication.CreateSlimBuilder()` for end-to-end validation.

## Troubleshooting

- **No Apps discovered**: check `McpApps.Enabled=true`, `DiscoveryPaths` exists and is readable, subdirectories contain `openclaw.mcpapp.json`
- **App marked Invalid**: inspect `ValidationErrors` — common causes are a missing `id` field or `stdio` mode without a `command`
- **Connection failure (Failed)**: inspect `LastError` — check `url` reachability, `command` executability, whether `startupTimeoutSeconds` is sufficient
- **Tools not registered**: verify the App is not filtered by `Deny` or `Entries[appId].Enabled=false`, verify the MCP Server's `tools/list` response is non-empty
- **Tool name collisions**: assign a unique `toolNamePrefix` per App
- **UI resource not detected**: ensure the resource MIME type is `text/html;profile=mcp-app` and `hasUi: true` is set in the manifest

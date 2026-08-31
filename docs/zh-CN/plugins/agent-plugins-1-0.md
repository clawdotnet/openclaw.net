# Agent Plugins 1.0 技术文档与起步教程

Agent Plugins 1.0 是一种打包 MCP 服务器和 Agent Skills 的可移植插件格式。OpenClaw.NET 支持这种格式的本地发现、验证、技能加载、MCP 配置适配和运行时刷新，同时继续保留现有 `openclaw.plugin.json` 插件格式。

Agent Plugin 的完整发现面由三个标准构件组成：

- `plugin.json`：插件清单，声明名称、版本、描述、许可证、关键词和 schema 信息。
- `mcp.json`：MCP 配置，声明客户端如何连接服务器。
- `skills/`：Agent Skills，声明 Agent 何时以及如何使用这些工具。

这三层把服务器能做什么、如何访问它、何时使用它分开。OpenClaw.NET 不读取服务器源代码来推断插件能力，也不会解析 `extensions` 字段或 `com.*` 私有扩展目录来补全标准构件。

## 当前支持范围

当前实现覆盖本地插件包，不包含远程 GitHub 安装、更新、卸载 API 或管理 UI。

支持的能力：

- 发现 `plugin.json` 格式的 Agent Plugin 包。
- 发现 `skills/<skill>/SKILL.md` 直接子目录技能。
- 解析 `mcp.json` 的 `mcpServers` 配置。
- 将 stdio MCP 和 Streamable HTTP MCP 映射到现有 `McpServerConfig`。
- 展开 `${PLUGIN_ROOT}` 和 `${PLUGIN_DATA}`。
- 对不合规构件生成结构化诊断。
- 在网关运行时监听 `plugin.json` 和 `mcp.json` 变化，并刷新技能和 MCP 配置。
- 与现有 `openclaw.plugin.json` 插件格式共存。

未包含的能力：

- GitHub 稀疏克隆安装。
- 插件更新和卸载命令。
- 插件卡片和管理 UI。
- 读取或执行私有客户端扩展目录。
- legacy HTTP+SSE MCP 传输。

相关代码入口：

- [AgentPluginModels.cs](../../../src/OpenClaw.Core/Plugins/AgentPluginModels.cs)
- [PluginDiscovery.cs](../../../src/OpenClaw.Core/Plugins/PluginDiscovery.cs)
- [AgentPluginMcpAdapter.cs](../../../src/OpenClaw.Core/Plugins/AgentPluginMcpAdapter.cs)
- [AgentPluginSkillLoader.cs](../../../src/OpenClaw.Core/Plugins/AgentPluginSkillLoader.cs)
- [AgentPluginRuntimeManager.cs](../../../src/OpenClaw.Core/Plugins/AgentPluginRuntimeManager.cs)
- [AgentPluginWatcherService.cs](../../../src/OpenClaw.Gateway/AgentPluginWatcherService.cs)
- [AgentPluginDiscoveryTests.cs](../../../src/OpenClaw.Tests/AgentPluginDiscoveryTests.cs)

## 发现顺序

OpenClaw.NET 按以下顺序发现 Agent Plugins：

```text
Plugins:Load:Paths
  -> <workspace>/plugins
  -> ~/.openclaw/plugins
```

如果多个位置存在同名插件，先发现的高优先级来源生效，后发现的重复插件会被跳过并记录 `duplicate_plugin_id` 诊断。插件名也会用于 `${PLUGIN_DATA}` 目录和 MCP server id，因此必须是安全的单一路径段，不能是 `.`、`..`，也不能包含路径分隔符。

运行时加载受 `Plugins.Enabled` 主开关控制。Agent Plugin 的 MCP 服务器不受 `Plugins.Mcp.Enabled` 控制；它们走工作区 MCP reload 路径，与工作区 `mcp.json` 保持一致。

## 包结构

最小目录结构如下：

```text
plugins/notes-helper/
├── plugin.json
├── mcp.json
└── skills/
    └── note-search/
        └── SKILL.md
```

`plugin.json` 必须存在。`mcp.json` 和 `skills/` 是可选构件：没有 `mcp.json` 表示插件不提供 MCP 集成；没有 `skills/` 表示插件不提供技能。

### plugin.json

```json
{
  "$schema": "https://agent-plugins.org/schemas/1.0.0/plugin.schema.json",
  "name": "notes-helper",
  "version": "1.0.0",
  "description": "Search and summarize local notes through an MCP server.",
  "license": "MIT",
  "keywords": ["notes", "search", "mcp"],
  "author": "Example Author"
}
```

必需字段：

- `name`
- `version`
- `description`
- `license`

当前实现支持规范字段 `$schema`，也兼容测试夹具里的 `schema` 字段。未知 schema 会产生 `unknown_schema` warning，但不会阻止插件加载。未知顶层字段、`extensions` 字段和 `com.*` 目录会被忽略。

### mcp.json

stdio MCP 示例：

```json
{
  "mcpServers": {
    "notes": {
      "type": "stdio",
      "command": "node",
      "args": ["${PLUGIN_ROOT}/server.js"],
      "env": {
        "NOTES_DATA": "${PLUGIN_DATA}"
      },
      "cwd": "${PLUGIN_ROOT}"
    }
  }
}
```

Streamable HTTP 示例：

```json
{
  "mcpServers": {
    "notes-http": {
      "type": "streamable-http",
      "url": "http://localhost:4100/mcp",
      "headers": {
        "Authorization": "Bearer replace-with-runtime-token"
      }
    }
  }
}
```

适配规则：

- 出现 `command` 时映射为 stdio。
- 出现 `url` 时映射为 Streamable HTTP，内部归一为 `McpServerConfig.Transport = "http"`。
- `transport` 或 `type` 为 `sse` 时跳过该条，并记录 `unsupported_transport`。
- `args`、`env` 和 `cwd` 支持 `${PLUGIN_ROOT}` 与 `${PLUGIN_DATA}`。
- `cwd` 必须解析在插件根目录内。

变量含义：

- `${PLUGIN_ROOT}`：插件安装目录。
- `${PLUGIN_DATA}`：`~/.openclaw/plugin-data/<plugin-name>`，用于跨升级保留插件数据。

HTTP headers 会保留给初始请求使用。底层 HTTP transport 使用 manual redirect 策略，不会自动跟随 3xx，也不会把 headers 转发到重定向目标。当前 Agent Plugin 适配器只展开 `${PLUGIN_ROOT}` 和 `${PLUGIN_DATA}`；其他占位值必须由插件作者或运行环境自行提供成最终字符串。

### skills/

OpenClaw.NET 只发现 `skills/` 的直接子目录，每个技能目录必须包含一个名为 `SKILL.md` 的文件：

```text
skills/
└── note-search/
    └── SKILL.md
```

示例：

```markdown
---
name: note-search
description: Search local notes through the notes MCP tools.
---

# Note Search

Use this skill when the user asks to find, summarize, or cross-reference local notes.

Call the notes MCP tools to search the index before answering. Prefer concise summaries and include note titles when available.
```

不要把技能放在更深层级，例如 `skills/group/note-search/SKILL.md`，当前加载器不会递归发现它。

## 起步教程

下面用一个最小插件把本地 stdio MCP 服务器暴露给 OpenClaw.NET，并附带一个技能告诉 Agent 何时使用它。

### 1. 创建插件目录

在工作区根目录创建：

```text
plugins/notes-helper/
├── plugin.json
├── mcp.json
├── server.js
└── skills/
    └── note-search/
        └── SKILL.md
```

工作区 `plugins/` 是默认发现目录。也可以把插件放到 `~/.openclaw/plugins/`，或通过 `Plugins:Load:Paths` 指定额外根目录。

### 2. 写 plugin.json

```json
{
  "$schema": "https://agent-plugins.org/schemas/1.0.0/plugin.schema.json",
  "name": "notes-helper",
  "version": "1.0.0",
  "description": "Search and summarize local notes.",
  "license": "MIT",
  "keywords": ["notes", "mcp", "search"]
}
```

保持 `name` 为安全路径段。不要使用 `../notes`、`notes/helper` 或空字符串。

### 3. 写 mcp.json

```json
{
  "mcpServers": {
    "notes": {
      "command": "node",
      "args": ["${PLUGIN_ROOT}/server.js"],
      "env": {
        "NOTES_DATA": "${PLUGIN_DATA}"
      },
      "cwd": "${PLUGIN_ROOT}"
    }
  }
}
```

这里 `notes` 会与插件名组合成运行时 MCP server id。`server.js` 的工具清单由 MCP 协议提供；OpenClaw.NET 不通过读取 `server.js` 来猜测工具能力。

### 4. 写技能

```markdown
---
name: note-search
description: Search local notes when the user asks about saved notes or previous research.
---

# Note Search

Use this skill when the user asks to find, summarize, compare, or cite local notes.

Before answering, call the notes MCP search tool. If multiple notes match, summarize the strongest matches and mention uncertainty.
```

技能描述的是 Agent 的使用时机和调用规则，不实现工具逻辑。工具逻辑仍在 MCP 服务器中。

### 5. 启动或刷新网关

如果网关尚未运行，按常规方式启动 OpenClaw.NET。启动时会发现 Agent Plugin，并把有效技能目录加入技能加载链，把 MCP server 配置合并到工作区 MCP reload 路径。

如果网关已经运行，修改或新增 `plugin.json`、`mcp.json` 会触发 Agent Plugin watcher。已知技能目录内的 `SKILL.md` 内容变化由 Skill watcher 热加载。只有启动时已经存在的发现根会被 watcher 监听；如果后来才创建一个全新的发现根目录，需要重启网关。

### 6. 验证行为

最小验证路径：

```powershell
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter AgentPluginDiscoveryTests
```

这些测试覆盖发现、必需字段、未知 schema warning、技能直接子目录、变量展开、路径拒绝、SSE 跳过、HTTP headers 保留、双格式共存和提交的样例插件。

## 错误边界

Agent Plugin 的失败边界是局部化的：

| 问题 | 行为 |
| --- | --- |
| 缺少 `plugin.json` | 不发现该目录为 Agent Plugin |
| `plugin.json` 缺少必需字段 | 整个插件拒绝加载 |
| `plugin.json` schema 未知 | 记录 `unknown_schema` warning，当前实现继续加载 |
| `mcp.json` 不是 JSON object | 关闭该插件 MCP，技能仍可用 |
| 缺少 `mcpServers` | 视为没有 MCP server |
| 单条 MCP 缺少 `command` 和 `url` | 跳过该条 |
| `transport` 或 `type` 为 `sse` | 跳过该条 |
| `cwd` 逃逸插件根目录 | 跳过该条 |
| 技能目录缺少 `SKILL.md` | 跳过该技能 |
| `extensions` 字段或 `com.*` 目录 | 忽略 |

启动和刷新路径都会把诊断写入日志，错误使用 `LogError`，warning 使用 `LogWarning`。

## 与现有插件格式的关系

Agent Plugins 1.0 与 OpenClaw 现有插件格式并行存在：

- `plugin.json` 走 Agent Plugin 1.0 发现路径。
- `openclaw.plugin.json` 走现有 OpenClaw bridge 插件路径。
- Agent Plugin 不注册 channel、provider、service 或 command。
- 现有 bridge 插件能力和 AOT/JIT 约束不因 Agent Plugin 支持而改变。

如果一个目录需要运行 JS/TS bridge 插件能力，应继续使用现有 `openclaw.plugin.json` 格式。如果目标是发布跨客户端可移植的技能和 MCP 连接器，应使用 Agent Plugins 1.0 格式。

## 实现边界

核心实现保持 NativeAOT 友好：它只处理 JSON、路径、技能文件和现有 MCP 配置，不引入动态程序集加载，也不通过反射执行插件代码。MCP server 自身仍是外部进程或 HTTP 服务，由现有 MCP 运行时负责启动、连接、工具枚举和重载协调。
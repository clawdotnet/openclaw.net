# Agent Plugins 1.0 Technical Guide and Tutorial

Agent Plugins 1.0 is a portable package format for bundling MCP servers with Agent Skills. OpenClaw.NET supports local discovery, validation, skill loading, MCP configuration adaptation, and runtime refresh for this format while preserving the existing `openclaw.plugin.json` plugin format.

An Agent Plugin is described through three standard surfaces:

- `plugin.json`: the plugin manifest, including name, version, description, license, keywords, and schema metadata.
- `mcp.json`: the MCP configuration that tells the client how to connect to the server.
- `skills/`: Agent Skills that tell the agent when and how to use the tools.

These layers separate what the server can do, how to reach it, and when the agent should use it. OpenClaw.NET does not inspect server source code to infer plugin capabilities, and it does not parse `extensions` fields or `com.*` private extension directories to augment the standard surfaces.

## Current Support

The current implementation supports local plugin packages. It does not include remote GitHub installation, update and uninstall APIs, or a management UI.

Supported behavior:

- Discover Agent Plugin packages that contain `plugin.json`.
- Discover direct `skills/<skill>/SKILL.md` skill directories.
- Parse `mcp.json` `mcpServers` entries.
- Map stdio MCP and Streamable HTTP MCP servers into existing `McpServerConfig` instances.
- Expand `${PLUGIN_ROOT}` and `${PLUGIN_DATA}`.
- Emit structured diagnostics for invalid components.
- Watch `plugin.json` and `mcp.json` at gateway runtime and refresh skills and MCP configuration.
- Coexist with the existing `openclaw.plugin.json` plugin format.

Out of scope today:

- GitHub sparse-clone installation.
- Plugin update and uninstall commands.
- Plugin cards and management UI.
- Reading or executing private client extension directories.
- Legacy HTTP+SSE MCP transport.

Relevant code entry points:

- [AgentPluginModels.cs](../../src/OpenClaw.Core/Plugins/AgentPluginModels.cs)
- [PluginDiscovery.cs](../../src/OpenClaw.Core/Plugins/PluginDiscovery.cs)
- [AgentPluginMcpAdapter.cs](../../src/OpenClaw.Core/Plugins/AgentPluginMcpAdapter.cs)
- [AgentPluginSkillLoader.cs](../../src/OpenClaw.Core/Plugins/AgentPluginSkillLoader.cs)
- [AgentPluginRuntimeManager.cs](../../src/OpenClaw.Core/Plugins/AgentPluginRuntimeManager.cs)
- [AgentPluginWatcherService.cs](../../src/OpenClaw.Gateway/AgentPluginWatcherService.cs)
- [AgentPluginDiscoveryTests.cs](../../src/OpenClaw.Tests/AgentPluginDiscoveryTests.cs)

## Discovery Order

OpenClaw.NET discovers Agent Plugins in this order:

```text
Plugins:Load:Paths
  -> <workspace>/plugins
  -> ~/.openclaw/plugins
```

When multiple locations contain the same plugin name, the first, higher-priority source wins. Later duplicates are skipped with a `duplicate_plugin_id` diagnostic. The plugin name is also used for `${PLUGIN_DATA}` and MCP server ids, so it must be a safe single path segment. It cannot be `.`, `..`, or contain path separators.

Runtime loading is gated by the `Plugins.Enabled` master switch. Agent Plugin MCP servers are not gated by `Plugins.Mcp.Enabled`; they flow through the workspace MCP reload path, matching workspace `mcp.json` behavior.

## Package Structure

A minimal package looks like this:

```text
plugins/notes-helper/
├── plugin.json
├── mcp.json
└── skills/
    └── note-search/
        └── SKILL.md
```

`plugin.json` is required. `mcp.json` and `skills/` are optional: no `mcp.json` means the plugin has no MCP integration; no `skills/` means it contributes no skills.

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

Required fields:

- `name`
- `version`
- `description`
- `license`

The implementation supports the spec-standard `$schema` property and also accepts the `schema` property used by existing test fixtures. An unknown schema emits an `unknown_schema` warning but does not block loading. Unknown top-level fields, `extensions`, and `com.*` directories are ignored.

### mcp.json

stdio MCP example:

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

Streamable HTTP example:

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

Adapter rules:

- `command` maps to stdio.
- `url` maps to Streamable HTTP and is normalized internally as `McpServerConfig.Transport = "http"`.
- `transport` or `type` set to `sse` skips that server with `unsupported_transport`.
- `args`, `env`, and `cwd` support `${PLUGIN_ROOT}` and `${PLUGIN_DATA}`.
- `cwd` must resolve inside the plugin root.

Variable meanings:

- `${PLUGIN_ROOT}`: the plugin installation directory.
- `${PLUGIN_DATA}`: `~/.openclaw/plugin-data/<plugin-name>`, used to preserve data across upgrades.

HTTP headers are preserved for the initial request. The underlying HTTP transport uses manual redirect handling, so it does not automatically follow 3xx responses and does not forward headers to redirect targets. The Agent Plugin adapter only expands `${PLUGIN_ROOT}` and `${PLUGIN_DATA}`; any other placeholder-like values must already be final strings provided by the plugin author or runtime environment.

### skills/

OpenClaw.NET only discovers direct children of `skills/`. Each skill directory must contain a file named exactly `SKILL.md`:

```text
skills/
└── note-search/
    └── SKILL.md
```

Example:

```markdown
---
name: note-search
description: Search local notes through the notes MCP tools.
---

# Note Search

Use this skill when the user asks to find, summarize, or cross-reference local notes.

Call the notes MCP tools to search the index before answering. Prefer concise summaries and include note titles when available.
```

Do not place skills at deeper paths such as `skills/group/note-search/SKILL.md`; the current loader does not discover them recursively.

## Tutorial

This tutorial creates a minimal plugin that exposes a local stdio MCP server to OpenClaw.NET and bundles a skill that tells the agent when to use it.

### 1. Create the Plugin Directory

Create this structure at the workspace root:

```text
plugins/notes-helper/
├── plugin.json
├── mcp.json
├── server.js
└── skills/
    └── note-search/
        └── SKILL.md
```

The workspace `plugins/` directory is discovered by default. You can also place a plugin in `~/.openclaw/plugins/`, or add another root through `Plugins:Load:Paths`.

### 2. Write plugin.json

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

Keep `name` as a safe path segment. Do not use values like `../notes`, `notes/helper`, or an empty string.

### 3. Write mcp.json

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

The `notes` entry is combined with the plugin name to form the runtime MCP server id. The server tool list comes from the MCP protocol; OpenClaw.NET does not read `server.js` to guess capabilities.

### 4. Write the Skill

```markdown
---
name: note-search
description: Search local notes when the user asks about saved notes or previous research.
---

# Note Search

Use this skill when the user asks to find, summarize, compare, or cite local notes.

Before answering, call the notes MCP search tool. If multiple notes match, summarize the strongest matches and mention uncertainty.
```

The skill describes when the agent should use the tool and the calling rules. It does not implement tool logic. Tool logic stays in the MCP server.

### 5. Start or Refresh the Gateway

If the gateway is not running, start OpenClaw.NET normally. At startup, OpenClaw.NET discovers the Agent Plugin, adds valid skill directories to the skill loading chain, and merges MCP server configuration into the workspace MCP reload path.

If the gateway is already running, changes to `plugin.json` or `mcp.json` trigger the Agent Plugin watcher. Changes to `SKILL.md` files inside already-known plugin skill directories are handled by the skill watcher. Only discovery roots that existed at startup are watched; creating an entirely new discovery root later requires a gateway restart.

### 6. Verify Behavior

Run the focused test suite:

```powershell
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter AgentPluginDiscoveryTests
```

These tests cover discovery, required fields, unknown schema warnings, direct skill subdirectories, variable expansion, path rejection, SSE skipping, HTTP header preservation, coexistence with the legacy plugin format, and the committed sample plugin.

## Failure Boundaries

Agent Plugin failures are localized:

| Problem | Behavior |
| --- | --- |
| Missing `plugin.json` | Directory is not discovered as an Agent Plugin |
| `plugin.json` missing required fields | Entire plugin is rejected |
| Unknown `plugin.json` schema | Emits `unknown_schema` warning; current implementation still loads |
| `mcp.json` is not a JSON object | Plugin MCP surface is disabled; skills remain available |
| Missing `mcpServers` | Treated as no MCP servers |
| MCP server missing both `command` and `url` | That server is skipped |
| `transport` or `type` is `sse` | That server is skipped |
| `cwd` escapes the plugin root | That server is skipped |
| Skill directory has no `SKILL.md` | That skill is skipped |
| `extensions` field or `com.*` directory | Ignored |

Startup and refresh paths write diagnostics to logs. Errors use `LogError`; warnings use `LogWarning`.

## Relationship to Existing OpenClaw Plugins

Agent Plugins 1.0 and the existing OpenClaw plugin format run side by side:

- `plugin.json` uses the Agent Plugin 1.0 discovery path.
- `openclaw.plugin.json` uses the existing OpenClaw bridge plugin path.
- Agent Plugins do not register channels, providers, services, or commands.
- Existing bridge plugin capabilities and AOT/JIT constraints do not change because Agent Plugins are supported.

Use `openclaw.plugin.json` when a directory needs JS/TS bridge plugin capabilities. Use Agent Plugins 1.0 when the goal is to publish portable skills and MCP connectors across compatible clients.

## Implementation Boundary

The core implementation remains NativeAOT-friendly: it handles JSON, paths, skill files, and existing MCP configuration only. It does not introduce dynamic assembly loading, and it does not execute plugin code through reflection. The MCP server itself remains an external process or HTTP service managed by the existing MCP runtime, which handles startup, connection, tool enumeration, and reload reconciliation.
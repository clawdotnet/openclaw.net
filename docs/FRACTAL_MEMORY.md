# Fractal Memory

Fractal Memory integration is an optional structured project-memory provider for OpenClaw.NET. It is designed to reduce context overload, improve Runtime Pulse handoffs, and let operators inspect compact project state without replacing OpenClaw.NET's existing memory and session stores.

OpenClaw integrates with [agentqi/fractal-memory](https://github.com/agentqi/fractal-memory) MCP-first. OpenClaw does not reference `FractalMemory.Core`, does not require Fractal Memory for quickstart, and does not silently write durable Fractal Memory updates.

## What It Adds

- Read-only structured memory tools when enabled.
- A `ContextBudgetPlanner` that searches Fractal Memory and exports compact context.
- Optional automatic context injection for agent turns with `AutoContextMode=auto`.
- Optional Runtime Pulse context attachment with `AutoContextMode=pulse` or `auto`.
- Admin and CLI surfaces for status, search, open, export, recent nodes, validation, handoffs, and index refresh.

Fractal Memory remains a structured long-term project-memory source. OpenClaw memory still owns sessions, notes, branches, profiles, learning proposals, automations, and runtime state.

## Fractal Memory Shape

A Fractal Memory repository is file-backed. Nodes act like chapters:

- `index.md` or `index.html`
- `state.md` or `state.html`
- `timeline.md` or `timeline.html`
- `decisions.md` or `decisions.html`
- `children/`
- `artifacts/`

Files are the source of truth. Indexes are accelerators, not authorities.

## Setup

If the MCP server is published as a .NET tool:

```bash
dotnet tool install -g FractalMemory.McpServer
fractalmem-mcp
```

For a local checkout, create a wrapper script or shell alias named `fractalmem-mcp` that starts the Fractal Memory MCP server from that repo. OpenClaw launches the command over stdio and sets `FRACTALMEM_REPOSITORY_ROOT` when a repository root is configured.

## Configuration

Fractal Memory is disabled by default:

```yaml
OpenClaw:
  Memory:
    Fractal:
      Enabled: false
      Mode: "mcp"
      RepositoryRoot: ""
      McpCommand: "fractalmem-mcp"
      DefaultDepth: 1
      DefaultView: "index"
      DefaultExportMode: "compact"
      MaxContextChars: 24000
      MaxContextTokens: 6000
      AutoContextMode: "off"
      AllowWrites: false
      RequireApprovalForWrites: true
      AutoRefreshIndexes: false
      IncludeTimeline: false
      IncludeDecisions: true
      IncludeArtifacts: false
```

`RepositoryRoot=""` uses the gateway workspace path, then the current directory. Status reports a warning when `.fractal-memory/config.yaml` is not found there.

`AutoContextMode` controls prompt insertion:

- `off`: no automatic Fractal Memory context.
- `manual`: tools, admin, and CLI only.
- `pulse`: Runtime Pulse may attach compact Fractal Memory context.
- `auto`: normal agent turns and Runtime Pulse may attach compact context.

## Tools

OpenClaw registers Fractal tools only when `Memory.Fractal.Enabled=true`.

Read-only tools:

- `fractal_memory_search`
- `fractal_memory_open`
- `fractal_memory_recent`
- `fractal_memory_export`
- `fractal_memory_validate`

Write/update tools are registered only when `AllowWrites=true`:

- `fractal_memory_handoff_create`
- `fractal_memory_index_refresh`

Write/update tools are approval-required by default when `RequireApprovalForWrites=true`.

## CLI

The CLI calls the gateway admin API:

```bash
openclaw memory fractal status
openclaw memory fractal search "context bloat"
openclaw memory fractal open projects/openclaw-net --depth 1
openclaw memory fractal export projects/openclaw-net --mode compact
openclaw memory fractal recent
openclaw memory fractal handoff create projects/openclaw-net
openclaw memory fractal validate
openclaw memory fractal index refresh
```

Add `--json` for structured output.

## Admin API

Operator-authenticated endpoints:

- `GET /admin/memory/fractal/status`
- `GET /admin/memory/fractal/search`
- `GET /admin/memory/fractal/open`
- `GET /admin/memory/fractal/export`
- `GET /admin/memory/fractal/recent`
- `POST /admin/memory/fractal/validate`
- `POST /admin/memory/fractal/index/refresh`
- `POST /admin/memory/fractal/handoff`

Mutating endpoints require CSRF for browser sessions and require `AllowWrites=true`.

## Context Budget Planner

`ContextBudgetPlanner` searches Fractal Memory, selects a relevant node, exports compact context, preserves source labels, and enforces configured character/token budgets. The injected block is marked as untrusted reference data:

```text
<fractal_memory_context>
Source: projects/example
Mode: compact
Depth: 1
GeneratedAtUtc: ...
Trust: untrusted_reference_data
...
</fractal_memory_context>
```

The planner prefers compact export and source labels over full timeline history.

## Runtime Pulse

When Runtime Pulse is enabled and Fractal Memory is enabled with `AutoContextMode=pulse` or `auto`, Pulse asks the planner for compact context and includes it in the pulse prompt. It records a runtime event with action `fractal_memory_context_attached` when context is attached.

## Writes

V1 is read-first. OpenClaw does not implement `fractal_memory_update` learning proposals yet. Durable Fractal writes are limited to explicit handoff creation and index refresh, both disabled unless `AllowWrites=true` and still subject to tool/admin approval paths.

Planned review-first write support should propose updates, validate them, and apply only after explicit operator approval.

## Troubleshooting

- `status=disabled`: set `OpenClaw:Memory:Fractal:Enabled=true`.
- MCP command cannot start: install Fractal Memory MCP or update `McpCommand`.
- Repository warning: set `RepositoryRoot` to a folder containing `.fractal-memory/config.yaml`.
- No context attached: check `AutoContextMode`; `manual` does not inject context automatically.
- Write tools missing: set `AllowWrites=true`; keep `RequireApprovalForWrites=true` unless this is a trusted local-only environment.

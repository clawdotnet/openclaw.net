# Fractal Memory

Fractal Memory integration is an optional structured project-memory provider for OpenClaw.NET. It is designed to reduce context overload, improve Runtime Pulse handoffs, and let operators inspect compact project state without replacing OpenClaw.NET's existing memory and session stores.

OpenClaw integrates with [agentqi/fractal-memory](https://github.com/agentqi/fractal-memory) MCP-first. OpenClaw does not reference `FractalMemory.Core`, does not require Fractal Memory for quickstart, and does not silently write durable Fractal Memory updates.

## What It Adds

- Structured memory retrieval and preview tools when enabled, with opt-in capture and review tools.
- A `ContextBudgetPlanner` that searches Fractal Memory and builds bounded, source-linked context.
- Optional automatic context injection for agent turns with `AutoContextMode=auto`.
- Optional Runtime Pulse context attachment with `AutoContextMode=pulse` or `auto`.
- Admin and CLI surfaces for retrieval, document updates, decisions, reviews, imports, resume, diagnostics and handoffs.

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

For a local [FractalMem checkout](https://github.com/agentqi/fractal-memory), build the CLI and server:

```bash
dotnet build /path/to/fractal-memory/src/FractalMemory.Cli
dotnet build /path/to/fractal-memory/src/FractalMemory.McpServer
```

Initialize the **memory workspace**, then configure the gateway to launch the built server:

```bash
cd /path/to/memory-workspace
dotnet /path/to/fractal-memory/src/FractalMemory.Cli/bin/Debug/net10.0/fm.dll init
```

```json
{
  "OpenClaw": {
    "Memory": {
      "Fractal": {
        "Enabled": true,
        "RepositoryRoot": "/path/to/memory-workspace",
        "McpCommand": "dotnet",
        "McpArguments": ["/path/to/fractal-memory/src/FractalMemory.McpServer/bin/Debug/net10.0/fractalmem-mcp.dll"],
        "AutoContextMode": "manual"
      }
    }
  }
}
```

OpenClaw launches the executable directly over stdio, with each `McpArguments` entry passed as one argument. Shell aliases and combined command strings are not supported. Use absolute paths for the executable/DLL when the gateway runs as a service. The child working directory and `FRACTALMEM_REPOSITORY_ROOT` are both set to the resolved memory workspace. Restart the gateway after changing these settings.

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
      McpArguments: []
      DefaultDepth: 1
      DefaultView: "index"
      DefaultExportMode: "compact"
      MaxContextChars: 24000
      MaxContextTokens: 6000
      AutoContextMode: "off"
      AllowWrites: false
      RequireApprovalForWrites: true
```

`RepositoryRoot=""` uses the gateway workspace path, then the current directory. Relative roots resolve against the workspace. Status reports a warning when `.fractal-memory/config.yaml` is not found there; upstream can discover a repository in a parent directory. Status discovers server tools and runs repository validation, so a successful process launch alone is not reported as usable memory. `availableTools` lists upstream tool names; `validation` reports repository issues.

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
- `fractal_memory_read` — full document/section content, whole-document hash and source links
- `fractal_memory_list` — canonical node discovery
- `fractal_memory_attention` — stale nodes, overdue reviews and missing context
- `fractal_memory_decisions` — managed decision IDs and supersession history
- `fractal_memory_context` — bounded text with source hashes and omitted-section metadata
- `fractal_memory_resume` — current context, attention, latest handoff and changed files
- `fractal_memory_handoff_list`
- `fractal_memory_handoff_read`

`fractal_memory_doctor` and `fractal_memory_import` are also available with writes disabled. They default to diagnosis and import preview respectively. `repair=true` or `apply=true` requires write permission and follows the same approval policy as other mutations.

Write/update tools are registered only when `AllowWrites=true`:

- `fractal_memory_handoff_create`
- `fractal_memory_index_refresh`
- `fractal_memory_node_create`
- `fractal_memory_update`
- `fractal_memory_append`
- `fractal_memory_review`

Write/update tools are approval-required by default when `RequireApprovalForWrites=true`.

The workflow tools preserve upstream structured data under `data`, text under `text`, and resource links under `resources`. Read `data.hash` before an update/append, and `data.document.hash` after a successful write. Read the index document before setting review metadata. Follow source paths with `fractal_memory_read`, including node-relative `artifacts/` files. Server-side path containment and stale-hash checks remain authoritative.

New workflows require a FractalMem version exposing the corresponding `memory_*` tools. Older seven-tool servers retain the original operations and automatic export fallback; unsupported workflows return an actionable upgrade error. The integration is verified against upstream commit `893432e76805070d427f62a0362e5ef3e4847957` (MCP SDK 2.2.0).

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
openclaw memory fractal workflow list --arguments '{"scope":"projects"}' --json
openclaw memory fractal workflow read --arguments '{"path":"projects/openclaw-net","file":"state"}' --json
openclaw memory fractal workflow resume --arguments '{"path":"projects/openclaw-net","maxCharacters":6000}' --json
openclaw memory fractal workflow import --arguments-file ./import-preview.json --json
```

Add `--json` for structured output.

`workflow <operation>` accepts the tool suffix (`read`, `node_create`, `update`, `append`, `review`, `list`, `attention`, `decisions`, `context`, `resume`, `handoff_list`, `handoff_read`, `doctor`, `import`) and a JSON argument object. Use `--arguments-file` for multiline content. For example, after reading the current state, an update file can contain:

```json
{
  "path": "projects/openclaw-net",
  "file": "state",
  "section": "Current Objective",
  "content": "Verify the memory integration.",
  "expectedHash": "<hash returned by workflow read>"
}
```

Apply with `openclaw memory fractal workflow update --arguments-file ./update.json --json`. Stale hashes fail without overwriting the newer document. The CLI/admin call is an explicit operator action; agent approval prompts apply to agent tool execution.

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
- `POST /admin/memory/fractal/workflows/{operation}` — JSON body uses the upstream workflow argument names

Mutating endpoints require CSRF for browser sessions and require `AllowWrites=true`.

The workflow endpoint validates its explicit operation allowlist and argument schema. Reads/previews require `admin.memory`; writes additionally require `admin.memory.mutate` and browser-session CSRF. Write audits record the operation and outcome without copying document bodies into the audit log.

## Context Budget Planner

`ContextBudgetPlanner` searches Fractal Memory, selects a relevant node, and uses `memory_context` when the server provides it. Older servers use `memory_export` with `DefaultExportMode`. It preserves source labels and enforces configured character/token-estimate budgets on the **entire injected block**, including labels and wrapper text. The injected block is marked as untrusted reference data:

```text
<fractal_memory_context>
Source: projects/example
Mode: context
Depth: 1
GeneratedAtUtc: ...
Trust: untrusted_reference_data
...
</fractal_memory_context>
```

Upstream's `maxCharacters` bounds only its `text` field, not JSON metadata or resource links. Manual workflow results preserve that metadata; automatic injection reserves room for the wrapper and includes source labels only when they fit alongside the bounded text. The structured result retains every source even when a label does not fit. The final OpenClaw budget still applies to the entire block. `MaxContextTokens` uses the existing four-characters-per-token estimate, not model-specific tokenization.

## Runtime Pulse

When Runtime Pulse is enabled and Fractal Memory is enabled with `AutoContextMode=pulse` or `auto`, Pulse asks the planner for compact context and includes it in the pulse prompt. It records a runtime event with action `fractal_memory_context_attached` when context is attached.

## Writes

Durable writes are opt-in. Agents must read a document before changing it and use its hash in `expectedHash`; approval fingerprints include the full request, including content, hash and mutation flags. New nodes never overwrite existing ones. Import is preview-first; only `apply=true` persists a supplied note. Doctor repairs derived indexes only. OpenClaw does not automatically capture conversations or apply learning proposals.

## Troubleshooting

- `status=disabled`: set `OpenClaw:Memory:Fractal:Enabled=true`.
- MCP command cannot start: install Fractal Memory MCP or update `McpCommand`.
- Repository warning: set `RepositoryRoot` to a folder containing `.fractal-memory/config.yaml`.
- No context attached: check `AutoContextMode`; `manual` does not inject context automatically.
- Write tools missing: set `AllowWrites=true`; keep `RequireApprovalForWrites=true` unless this is a trusted local-only environment.
- Workflow unavailable: update the Fractal Memory server; inspect `availableTools` in status.

## Verification

Hermetic stdio and gateway tests run with the normal test suite (Node.js is required for the test MCP peer). To test the real upstream CLI and MCP server against an isolated temporary memory workspace:

```bash
dotnet build /path/to/fractal-memory/src/FractalMemory.Cli
dotnet build /path/to/fractal-memory/src/FractalMemory.McpServer
OPENCLAW_FRACTAL_SOURCE=/path/to/fractal-memory \
  dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter FullyQualifiedName~FractalMemory
```

The live test covers all fourteen workflows, the original seven tools, stale-write rejection, preview versus apply, source links, and automatic context budgeting. It creates and removes its own temporary repository.

# Capability Matrix

This matrix summarizes the current OpenClaw.NET capability lanes. It complements [COMPATIBILITY.md](COMPATIBILITY.md), which remains the canonical detailed compatibility guide.

## Status Legend

- `Core`: available in the default runtime path.
- `Optional`: available when the related config, package, provider, or host is enabled.
- `Experimental`: usable for evaluation, but not a committed stable surface.
- `JIT-only`: requires the `jit` runtime lane and fails fast in `aot`.

## Runtime And Host Capabilities

| Capability | Lane | Notes |
| --- | --- | --- |
| Agent runtime loop | Core | Tool calls, streaming, cancellation, retries, sessions, memory, and hooks. |
| Gateway HTTP host | Core | Local/self-hosted host for chat, admin, health, diagnostics, OpenAI-compatible APIs, MCP, and websockets. |
| Background session execution | Core | Sessions with active Goals auto-continue through the `MessagePipeline` after all Channel disconnects; bounded batches, native/MAF parity, startup recovery. |
| CLI setup and launch | Core | Source checkout, managed local config, diagnostics, model/profile tools, plugin/skill commands. |
| NativeAOT publish | Core | Runtime and gateway are designed for the strict AOT lane. |
| Desktop Companion | Optional | Included in desktop bundles and solution builds; release managers should still run manual smoke checks. |
| TUI | Optional | Terminal UI surface for operator workflows. |
| OpenAI-compatible endpoints | Core | Hosted by the gateway. |
| MCP endpoint | Core | Hosted by the gateway. |
| Workspace MCP hot reload | Optional | Persist and hot-reload `Plugins:Mcp` server config via `/admin/workspace/mcp` without gateway restart. Separate from MCP Apps. |
| Workspace admin APIs | Optional | Workspace file browse, tree, upload, download, and MCP config management via `/admin/workspace/*`. |
| Admin channel config endpoints | Optional | Per-channel overrides persisted via `ChannelConfigStore` across restarts. |
| Public-bind hardening | Core | Gateway refuses unsafe public-bind configurations until required settings are hardened. |

## Tools And Extensions

| Capability | Lane | Notes |
| --- | --- | --- |
| First-party and optional tool surfaces (80+) | Core / Optional | File, shell, memory, session, web, database, email, calendar, browser, MQTT, Canvas/A2UI, FractalMemory, MetaSkill, Goal, Loop, MCP App, Semantic Kernel, and related tools. The active set is configuration-dependent. |
| Browser tool | Optional | Implemented in `OpenClaw.Protocols.Browser`; conservative defaults; local execution depends on runtime capability and sandbox/backend settings. |
| MQTT tools and event bridge | Optional | Implemented in `OpenClaw.Protocols.Mqtt` and composed by the gateway when native MQTT config is enabled. |
| Home Assistant tools and event bridge | Optional | Native home-automation surface under native plugin config. |
| Plugin bridge tools/services | Optional | Mainstream JS/TS plugin tools and services are supported with explicit diagnostics. |
| Dynamic plugin channels | JIT-only | Fails fast outside the JIT lane. |
| Dynamic plugin commands | JIT-only | Registered as dynamic chat commands in JIT mode. |
| Dynamic plugin hooks | JIT-only | `tool:before` / `tool:after` hooks with timeout protections. |
| Dynamic plugin providers | JIT-only | Plugin-provided model providers through the dynamic provider seam. |
| Native dynamic .NET plugins | JIT-only | Loaded through `OpenClaw:Plugins:DynamicNative`. |
| Payment tools | Optional | Native payment runtime owns live secrets; bridge payment plugins are diagnostic/test-only unless explicitly sandboxed. |

## Providers, Models, And Workflows

| Capability | Lane | Notes |
| --- | --- | --- |
| OpenAI provider | Optional | Enabled through provider config and secret refs. |
| Claude provider | Optional | Enabled through provider config and secret refs. |
| Gemini provider | Optional | Enabled through provider config and secret refs. |
| Azure OpenAI provider | Optional | Enabled through provider config and secret refs. |
| Ollama provider | Optional | Local provider path with model profile diagnostics. |
| OpenAI-compatible provider | Optional | Provider-neutral path for compatible endpoints. |
| Embedded local model sidecar | Experimental | Supervised local model packages, including multimodal frame-based video handling. |
| Microsoft Agent Framework adapter | Optional | Select with `Runtime.Orchestrator=maf`; native runtime remains the default. |
| Dynamic turn routing | Experimental | Stable runtime wiring with conservative thresholds; classifier quality is not claimed to match OpenSquilla-equivalent accuracy. |
| Durable workflow backend | Optional | Delegates long-running runs to supported workflow hosts without making normal turns durable. |
| Fractal Memory MCP integration | Optional | MCP-first structured memory integration. |
| Mempalace memory provider | Optional | Optional temporal knowledge graph memory provider. |

## Channels And Clients

| Capability | Lane | Notes |
| --- | --- | --- |
| Web chat | Core | Gateway-hosted local UI. |
| Admin UI | Core | Gateway-hosted diagnostics and operator surface. |
| Typed .NET client | Optional | Integration API and MCP facade client. |
| Telegram channel | Optional | Operator DM policy, allowlists, diagnostics, and recent sender support. |
| SMS channel | Optional | Twilio-backed channel surface. |
| WhatsApp channel | Optional | Worker-backed integration paths. |
| Teams channel | Optional | Microsoft Teams setup path. |
| Slack channel | Optional | Channel adapter with shared operator model. |
| Discord channel | Optional | Includes slash-command ingress parity. |
| Feishu channel | Optional | Lark/Feishu IM adapter with hot-reloadable config, message dedup, and media handling. |
| DingTalk channel | Optional | DingTalk IM adapter with hot-reloadable config. |
| WeCom channel | Optional | WeChat Work IM adapter with hot-reloadable config. |
| Signal channel | Optional | Channel adapter with shared operator model. |
| Email channel | Optional | Separate operational surface from chat-channel allowlists. |
| Generic webhooks | Optional | Authenticated inbound triggers. |

## Contributor Rule

When a capability adds provider SDK weight, dynamic loading, vendor-specific defaults, or protocol-specific behavior, prefer an optional project or extension boundary over adding it to `OpenClaw.Core` or the default agent path.

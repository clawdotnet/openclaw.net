# Compatibility Guide

This is the canonical compatibility matrix for OpenClaw.NET. It covers runtime modes, upstream `SKILL.md` reuse, plugin compatibility, channel operator parity, and the current limitations you should plan around.

## Status Legend

- `Supported`: intended production path, backed by automated tests
- `Supported with caveats`: works today, but there are important scope limits or mode requirements
- `Not supported`: fails fast with explicit diagnostics instead of loading partially

## Runtime Modes

- `OpenClaw:Runtime:Mode=auto` resolves to `jit` when dynamic code is available and `aot` when it is not.
- `OpenClaw:Runtime:Mode=aot` forces the strict trim-safe lane even on a JIT-capable build.
- `OpenClaw:Runtime:Mode=jit` enables the expanded plugin and dynamic-native lane.

## MCP v2 Compatibility (C# SDK 2.0.0)

OpenClaw.NET now targets `ModelContextProtocol` / `ModelContextProtocol.AspNetCore` `2.0.0` and enables the Tasks extension on the gateway MCP server.

| Surface | Status | Notes |
| --- | --- | --- |
| Gateway `/mcp` on MCP v2 SDK | Supported | Gateway MCP services run on `ModelContextProtocol.AspNetCore` `2.0.0` with stateless transport by default. |
| MCP Tasks extension | Supported | Registered via `ModelContextProtocol.Extensions.Tasks` with in-memory task store for async task operations. |
| Discover-first negotiation | Supported with caveats | Default path is discover-first; legacy clients can still initialize through compatibility handling. |
| Required `inputSchema` handling | Supported with caveats | Tool schema validation is strict by default; missing/defaulted schema is skipped on MCP App enumeration. |

Compatibility switches (Gateway config):

- `OpenClaw:McpCompatibility:ForceLegacyInitialize=true`
  - Forces legacy initialize-first behavior (disables stateless discover-first mode).
- `OpenClaw:McpCompatibility:AllowRelaxedInputSchemaValidation=true`
  - Reserved flag only. It is not currently wired into MCP App enumeration; with the current SDK, omitted `inputSchema` may still surface as synthesized `{"type":"object"}` on that path.
- `OpenClaw:McpCompatibility:EnableDiscoveryFirst=true`
  - Keeps discover-first path enabled (default).

## Upstream Skill Compatibility

| Surface | Status | Notes |
| --- | --- | --- |
| Standalone `SKILL.md` packages | Supported | This is the cleanest upstream reuse path. No bridge is required. |
| ClawHub skill install flow | Supported | Use `openclaw clawhub install <slug>` with workspace or managed skill locations. |
| Plugin-packaged skills (`manifest.skills[]`) | Supported | Skills load into the normal precedence chain: `extra < bundled < managed < plugin < workspace`. |
| Workspace / managed / bundled skill precedence | Supported | Workspace overrides remain the highest-priority operator-controlled layer. |
| Runtime reload expectations | Supported with caveats | Newly installed skills are picked up on restart; do not assume hot-reload for every deployment shape. |

## Plugin Package Compatibility

OpenClaw.NET keeps plugin compatibility explicit by runtime mode. The goal is to support the mainstream tool-and-skill path in `aot`, then offer a broader compatibility lane in `jit` without pretending the two modes are equivalent.

| Surface | Status | Notes |
| --- | --- | --- |
| `api.registerTool()` | Supported | Available in both `aot` and `jit`. Covered by hermetic bridge tests. |
| `api.registerService()` | Supported | Available in both `aot` and `jit`, including `start` / `stop` lifecycle coverage. |
| `api.registerChannel()` | Supported with caveats | `jit` only. `aot` fails fast with `jit_mode_required`. |
| `api.registerCommand()` | Supported with caveats | `jit` only. Registered as dynamic chat commands. |
| `api.on(...)` | Supported with caveats | `jit` only. `tool:before` / `tool:after` hooks are bridged with timeout protections. |
| `api.registerProvider()` | Supported with caveats | `jit` only. Plugin-provided LLMs are wired through the dynamic provider seam. |
| `OpenClaw.Providers.MicrosoftExtensionsAI` | Supported with caveats | `jit` only through native dynamic plugins. Use this to bring an arbitrary `IChatClient`; AOT users should use built-in providers or OpenAI-compatible endpoints. |
| Standalone `.js`, `.mjs`, `.ts` in `.openclaw/extensions` | Supported with caveats | `.ts` requires local `jiti`. |
| Manifest/package discovery via `Plugins:Load:Paths` | Supported | Includes `openclaw.plugin.json` and `package.json` `openclaw.extensions`. |
| `openclaw plugins install --dry-run` trust inspection | Supported | Prints trust level, declared surface, diagnostics, and blocks install when compatibility errors are present. |
| Plugin config validation | Supported with caveats | Validated against the documented JSON Schema subset below before startup. |
| Plugin diagnostics in `/doctor` | Supported | Discovery, load, config, and compatibility failures are reported explicitly. |
| Plugin bridge runtime budgets | Supported | `OpenClaw:Plugins:RuntimeBudget` can auto-quarantine bridge plugins by restart count, working set, and compatibility error thresholds. |
| `Plugins:Transport:Mode=stdio` | Supported | JSON-RPC over child process stdin/stdout. |
| `Plugins:Transport:Mode=socket` | Supported | Local IPC with authenticated handshake and private runtime socket directories. |
| `Plugins:Transport:Mode=hybrid` | Supported | `init` over stdio, then runtime RPC/notifications over the local IPC socket transport. |
| Native dynamic .NET plugins | Supported with caveats | `jit` only through `OpenClaw:Plugins:DynamicNative`. AOT fails fast before load. |
| Upstream/TypeScript `payment` plugin live execution | Not supported | Native OpenClaw.NET payment runtime owns live payment secrets. Bridge plugins named/providing `payment` are diagnostic/test-only unless explicitly sandboxed; live execution routes through the native `payment` tool. |

## Unsupported Today

These APIs are not bridged. If a plugin uses them, initialization fails fast with structured diagnostics instead of loading partially:

| Surface | Status | Failure code |
| --- | --- | --- |
| `api.registerGatewayMethod()` | Not supported | `unsupported_gateway_method` |
| `api.registerCli()` | Not supported | `unsupported_cli_registration` |

## Canvas and A2UI Compatibility

OpenClaw.NET ships a Canvas/A2UI workspace for websocket clients. The supported target is local Canvas content, A2UI v0.8 JSONL compatibility, and A2UI v0.9 structured surfaces with catalog negotiation; remote webpage Canvas control remains intentionally unsupported. See [CANVAS_A2UI.md](CANVAS_A2UI.md).

| Surface | Status | Notes |
| --- | --- | --- |
| Canvas present/hide/snapshot commands | Supported | Typed websocket envelopes routed through the session-scoped broker. Snapshots can target a specific `surfaceId`. |
| Local Canvas navigation | Supported with caveats | `about:blank` is supported. Inline local HTML is supported in webchat via sandboxed `srcdoc`; Companion reports an unsupported diagnostic without a native WebView. |
| Remote webpage Canvas navigation/eval | Not supported | `http:` and `https:` URLs are rejected; use the browser tool for remote pages. |
| A2UI v0.8 JSONL rendering | Supported | Webchat and Companion render text, markdown, card, button, input, select, checklist, table, image, progress, and simple chart frames through `a2ui_push`. |
| A2UI v0.9 structured surfaces | Supported | `a2ui_create_surface`, `a2ui_update_components`, `a2ui_update_data_model`, `a2ui_delete_surface`, and `a2ui_sync_ui_to_data` are capability-gated on `a2ui.v0_9`. |
| A2UI catalog negotiation | Supported | Clients advertise `supportedCatalogIds`; the broker chooses or validates the requested catalog and locks it per `senderId + sessionId + surfaceId`. |
| A2UI interaction feedback | Supported | v0.8 client events return as `a2ui_event`; v0.9 surface actions return as `a2ui_action` session turns. |
| A2UI eval | Supported with caveats | The gateway tool is capability-gated, but no first-party client advertises `a2ui.eval`; webchat and Companion return unsupported diagnostics. |
| Advanced AGenUI components | Supported with caveats | Webchat and Companion render native subsets and use conservative placeholders/diagnostics for media, carousel, web, or unsupported advanced components. |

## Channel Compatibility

The messaging channels below now share the same operator model for DM policy, recent senders, diagnostics, and dynamic allowlist administration.

| Channel | Status | Operator surface |
| --- | --- | --- |
| Telegram | Supported | DM policy, dynamic allowlists, recent senders, readiness and diagnostics |
| Twilio SMS | Supported | DM policy, dynamic allowlists, recent senders, readiness and diagnostics |
| WhatsApp | Supported | DM policy, dynamic allowlists, recent senders, readiness and diagnostics |
| Teams | Supported | DM policy, dynamic allowlists, recent senders, readiness and diagnostics |
| Slack | Supported | DM policy, dynamic allowlists, recent senders, readiness and diagnostics |
| Discord | Supported | DM policy, dynamic allowlists, recent senders, readiness and diagnostics, including slash-command ingress parity |
| Signal | Supported | DM policy, dynamic allowlists, recent senders, readiness and diagnostics |
| Email | Supported with caveats | Email is an inbound transport, but it does not use the same sender-allowlist model as the chat channels above. Treat it as a separate operational surface. |
| Generic webhooks | Supported with caveats | Webhooks support authenticated inbound triggers, but they are not a DM-policy / allowlist channel. |

## TypeScript Requirements

TypeScript plugins are supported when `jiti` is available in the plugin dependency tree.

Install it in the plugin directory or its parent workspace:

```bash
npm install jiti
```

If `jiti` is missing, plugin load fails with an actionable error instead of falling back silently.

## Supported Config Schema Subset

`openclaw.plugin.json` `configSchema` is validated before the bridge starts. Supported keywords:

- `type`
- `properties`
- `required`
- `additionalProperties`
- `items`
- `enum`
- `const`
- `minLength`
- `maxLength`
- `minimum`
- `maximum`
- `minItems`
- `maxItems`
- `pattern`
- `oneOf`
- `anyOf`
- documentation-only fields such as `title`, `description`, and `default`

Unsupported schema keywords are rejected with `unsupported_schema_keyword`.

## Operator Trust Workflow

- Plugin install candidates are classified as `first-party`, `upstream-compatible`, or `untrusted` at install time.
- Operators can promote a loaded plugin to `third-party-reviewed` from the admin UI or `POST /admin/plugins/{id}/review`.
- `GET /admin/plugins` exposes trust level, compatibility status, declared surface, diagnostics counts, runtime restart/memory data, budget violations, review notes, and source paths.
- `GET /admin/skills` exposes the loaded skill inventory with trust level, host requirements, dispatch metadata, and source location.
- Local `SKILL.md` folders or `.tgz` bundles can be inspected and installed with `openclaw skills inspect` and `openclaw skills install`.

## Public-bind and CI surfaces

- `OpenClaw:Security:StrictPublicBindProfile=true` applies the hardened Internet-facing preset over approvals, raw secret refs, plugin bridge exposure, and unsafe local tool execution.
- `GET /api/integration/compatibility/export` and `GET /admin/compatibility/export` emit the machine-readable compatibility snapshot used by CI and deployment validation.

## Tested Catalog

OpenClaw.NET now ships the pinned public compatibility catalog used by the smoke lane itself.

- `openclaw compatibility catalog`
- `openclaw compatibility catalog --status compatible --kind npm-plugin`
- `GET /admin/compatibility/catalog`
- `GET /api/integration/compatibility/catalog`

The catalog is scenario-based rather than marketing-based:

- positive scenarios show pinned packages expected to load successfully
- negative scenarios show pinned configs or packages expected to fail with explicit diagnostics
- each entry includes install guidance, required config examples where relevant, and expected tools, skills, or diagnostics

## Known Limitations

- Public-bind setup defaults intentionally disable bridge plugins and shell until you opt into the relevant trust settings.
- JIT-only capabilities remain JIT-only; `aot` does not attempt partial dynamic fallback.
- TypeScript plugin loading depends on `jiti`; OpenClaw.NET does not bundle a TypeScript runtime automatically.
- Out-of-root plugin entry files, manifests, and native dynamic assemblies fail explicitly instead of being resolved elsewhere on disk.
- Tool-name collisions are deterministic: the first tool wins, later duplicates are skipped and reported.

## Automated Proof

The compatibility claim is backed by automated validation in `src/OpenClaw.Tests`:

- `PluginBridgeIntegrationTests.cs`
  - `.js`, `.mjs`, `.ts` loading
  - `jiti` success and failure paths
  - `registerService()`
  - `aot` vs `jit` capability gating
  - `registerChannel()` / `registerCommand()` / `registerProvider()` / `api.on(...)`
  - plugin-packaged skills
  - config validation, including `oneOf`
  - unsupported-surface failure modes
- `NativeDynamicPluginHostTests.cs`
  - JIT-mode in-process plugin loading
  - command and service lifecycle
  - plugin-packaged skills
  - AOT rejection before load
- `PublicCompatibilitySmokeTests.cs`
  - pinned ClawHub skill package
  - pinned JS plugin package
  - pinned TS + `jiti` plugin package
  - pinned config-schema rejection case
  - pinned unsupported-surface plugin case

The nightly/manual CI smoke lane runs those public packages with `OPENCLAW_PUBLIC_SMOKE=1`.

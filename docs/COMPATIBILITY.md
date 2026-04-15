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
| Standalone `.js`, `.mjs`, `.ts` in `.openclaw/extensions` | Supported with caveats | `.ts` requires local `jiti`. |
| Manifest/package discovery via `Plugins:Load:Paths` | Supported | Includes `openclaw.plugin.json` and `package.json` `openclaw.extensions`. |
| `openclaw plugins install --dry-run` trust inspection | Supported | Prints trust level, declared surface, diagnostics, and blocks install when compatibility errors are present. |
| Plugin config validation | Supported with caveats | Validated against the documented JSON Schema subset below before startup. |
| Plugin diagnostics in `/doctor` | Supported | Discovery, load, config, and compatibility failures are reported explicitly. |
| `Plugins:Transport:Mode=stdio` | Supported | JSON-RPC over child process stdin/stdout. |
| `Plugins:Transport:Mode=socket` | Supported | Local IPC with authenticated handshake and private runtime socket directories. |
| `Plugins:Transport:Mode=hybrid` | Supported | `init` over stdio, then runtime RPC/notifications over the local IPC socket transport. |
| Native dynamic .NET plugins | Supported with caveats | `jit` only through `OpenClaw:Plugins:DynamicNative`. AOT fails fast before load. |

## Unsupported Today

These APIs are not bridged. If a plugin uses them, initialization fails fast with structured diagnostics instead of loading partially:

| Surface | Status | Failure code |
| --- | --- | --- |
| `api.registerGatewayMethod()` | Not supported | `unsupported_gateway_method` |
| `api.registerCli()` | Not supported | `unsupported_cli_registration` |

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
- `GET /admin/plugins` exposes trust level, compatibility status, declared surface, diagnostics counts, review notes, and source paths.
- `GET /admin/skills` exposes the loaded skill inventory with trust level, host requirements, dispatch metadata, and source location.
- Local `SKILL.md` folders or `.tgz` bundles can be inspected and installed with `openclaw skills inspect` and `openclaw skills install`.

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

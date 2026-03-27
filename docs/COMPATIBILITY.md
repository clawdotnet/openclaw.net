# Plugin Compatibility Guide

OpenClaw.NET keeps plugin compatibility explicit by runtime mode. The goal is to support the mainstream tool/skill path in AOT mode, then offer a broader compatibility lane in JIT mode without pretending the two modes are equivalent.

## Runtime Modes

- `OpenClaw:Runtime:Mode=auto` resolves to `jit` when dynamic code is available and `aot` when it is not.
- `OpenClaw:Runtime:Mode=aot` forces the strict low-memory compatibility lane even on a JIT-capable build.
- `OpenClaw:Runtime:Mode=jit` requires dynamic code support and enables the expanded plugin lane.

## Bridge Matrix

| Surface | Status | Notes |
| --- | --- | --- |
| `api.registerTool()` | Supported in `aot` and `jit` | Tool registration and execution are covered by hermetic bridge tests. |
| `api.registerService()` | Supported in `aot` and `jit` | `start` / `stop` lifecycle is covered by integration tests. |
| `api.registerChannel()` | Supported in `jit` only | AOT mode blocks the plugin with `jit_mode_required`. |
| `api.registerCommand()` | Supported in `jit` only | Registered as dynamic chat commands (e.g. `/mycommand`). |
| `api.on(...)` | Supported in `jit` only | Event hooks for `tool:before` / `tool:after`. 5-second timeout on `before` with fallback to allow. |
| `api.registerProvider()` | Supported in `jit` only | Plugin-provided LLM backends, registered as dynamic providers in `LlmClientFactory`. |
| Plugin-packaged skills (`manifest.skills[]`) | Supported | Loaded into the skill pipeline with precedence `extra < bundled < managed < plugin < workspace`. |
| Standalone ClawHub `SKILL.md` packages | Supported | No bridge required; this is the most plug-and-play compatibility path. |
| Standalone `.js`, `.mjs`, `.ts` in `.openclaw/extensions` | Supported | `.ts` requires local `jiti`. |
| Manifest/package discovery via `Plugins:Load:Paths` | Supported | Includes `openclaw.plugin.json` and `package.json` `openclaw.extensions`. |
| Plugin config validation | Supported subset | Validated before bridge startup against the supported JSON Schema subset below. |
| Plugin diagnostics in `/doctor` | Supported | Discovery, load, config, and compatibility failures are reported explicitly. |
| `Plugins:Transport:Mode=stdio` | Supported | JSON-RPC over child process stdin/stdout. |
| `Plugins:Transport:Mode=socket` | Supported | JSON-RPC over local IPC: Unix domain sockets on Unix, named pipes on Windows. Local IPC now uses an authenticated handshake and private runtime socket directories by default. |
| `Plugins:Transport:Mode=hybrid` | Supported | `init` over stdio, then runtime RPC/notifications over the local IPC socket transport. Socket handoff uses the same authenticated local IPC path. |

## Unsupported Today

These APIs are not bridged. If a plugin uses them, plugin initialization fails fast with structured diagnostics instead of loading partially:

| Surface | Failure code |
| --- | --- |
| `api.registerGatewayMethod()` | `unsupported_gateway_method` |
| `api.registerCli()` | `unsupported_cli_registration` |

## Native Dynamic Plugins

JIT mode also supports in-process native dynamic plugins through `OpenClaw:Plugins:DynamicNative`.

- Discovery manifest: `openclaw.native-plugin.json`
- Standard locations: configured `Plugins:DynamicNative:Load:Paths`, workspace `.openclaw/native-plugins`, global `~/.openclaw/native-plugins`
- Capability model: `native_dynamic` plus the declared/registered surfaces (`tools`, `services`, `commands`, `channels`, `providers`, `hooks`, `skills`)
- AOT behavior: fail fast before load with `jit_mode_required`
- Assembly paths must resolve under the discovered plugin root; out-of-root paths fail with structured diagnostics.

## Plugin Root Containment

Discovered plugin manifests do not get to escape their plugin root.

- package entry files must resolve under the discovered plugin directory
- manifest entry files must resolve under the discovered plugin directory
- native dynamic plugin assembly paths must resolve under the discovered plugin directory

Out-of-root targets fail explicitly during discovery or load instead of being executed.

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

## What Failure Looks Like

- Discovery problems such as invalid manifests, duplicate plugin ids, or missing entry files produce structured plugin reports.
- Out-of-root manifest or assembly paths fail with explicit diagnostics instead of silently resolving elsewhere on disk.
- Config problems fail before Node startup with field-specific diagnostics.
- Unsupported bridge APIs fail plugin initialization with explicit compatibility codes.
- JIT-only capabilities in AOT mode fail before wiring with explicit runtime-mode diagnostics.
- Tool-name collisions are deterministic: the first tool wins, later duplicates are skipped and reported.

## Automated Proof

The compatibility claim is backed by two layers of automated validation:

- Hermetic bridge tests in `src/OpenClaw.Tests/PluginBridgeIntegrationTests.cs`
  - `.js`, `.mjs`, `.ts` loading
  - `jiti` success/failure
  - `registerService()`
  - `aot` vs `jit` capability gating
  - `registerChannel()` / `registerCommand()` / `registerProvider()` / `api.on(...)`
  - plugin-packaged skills
  - config validation, including `oneOf`
  - unsupported-surface failure modes
- Hermetic native dynamic tests in `src/OpenClaw.Tests/NativeDynamicPluginHostTests.cs`
  - JIT-mode in-process plugin loading
  - command + service lifecycle
  - plugin-packaged skills
  - AOT rejection before load
- Public smoke manifest in `compat/public-smoke.json`
  - pinned ClawHub skill package
  - pinned JS plugin package
  - pinned TS + `jiti` plugin package
  - pinned config-schema rejection case
  - pinned unsupported-surface plugin case

The nightly/manual CI smoke lane runs those public packages with `OPENCLAW_PUBLIC_SMOKE=1`.

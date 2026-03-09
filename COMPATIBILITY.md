# Plugin Compatibility Guide

OpenClaw.NET keeps plugin compatibility intentionally narrow and explicit. The goal is to support mainstream tool plugins through the Node.js bridge, not to emulate the full upstream extension host.

## Supported Today

| Surface | Status | Notes |
| --- | --- | --- |
| `api.registerTool()` | Supported | Tool registration and execution are covered by hermetic bridge tests. |
| `api.registerService()` | Supported | `start` / `stop` lifecycle is covered by integration tests. |
| Plugin-packaged skills (`manifest.skills[]`) | Supported | Loaded into the skill pipeline with precedence `extra < bundled < managed < plugin < workspace`. |
| Standalone `.js`, `.mjs`, `.ts` in `.openclaw/extensions` | Supported | `.ts` requires local `jiti`. |
| Manifest/package discovery via `Plugins:Load:Paths` | Supported | Includes `openclaw.plugin.json` and `package.json` `openclaw.extensions`. |
| Plugin config validation | Supported subset | Validated before bridge startup against the supported JSON Schema subset below. |
| Plugin diagnostics in `/doctor` | Supported | Discovery, load, config, and compatibility failures are reported explicitly. |

## Unsupported Today

These APIs are not bridged. If a plugin uses them, plugin initialization fails fast with structured diagnostics instead of loading partially:

| Surface | Failure code |
| --- | --- |
| `api.registerChannel()` | `unsupported_channel_registration` |
| `api.registerGatewayMethod()` | `unsupported_gateway_method` |
| `api.registerCli()` | `unsupported_cli_registration` |
| `api.registerCommand()` | `unsupported_command_registration` |
| `api.registerProvider()` | `unsupported_provider_registration` |
| `api.on(...)` | `unsupported_event_hook` |

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
- Config problems fail before Node startup with field-specific diagnostics.
- Unsupported bridge APIs fail plugin initialization with explicit compatibility codes.
- Tool-name collisions are deterministic: the first tool wins, later duplicates are skipped and reported.

## Automated Proof

The compatibility claim is backed by two layers of automated validation:

- Hermetic bridge tests in `src/OpenClaw.Tests/PluginBridgeIntegrationTests.cs`
  - `.js`, `.mjs`, `.ts` loading
  - `jiti` success/failure
  - `registerService()`
  - plugin-packaged skills
  - config validation, including `oneOf`
  - unsupported-surface failure modes
- Public smoke manifest in `compat/public-smoke.json`
  - pinned ClawHub skill package
  - pinned JS plugin package
  - pinned TS + `jiti` plugin package
  - pinned config-schema rejection case
  - pinned unsupported-surface plugin case

The nightly/manual CI smoke lane runs those public packages with `OPENCLAW_PUBLIC_SMOKE=1`.

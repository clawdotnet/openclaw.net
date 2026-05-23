# Harness Regression Suite

The Harness Regression Suite checks whether important OpenClaw.NET runtime guarantees still hold.

It is designed for:

- maintainers
- contributors
- CI
- release preparation
- review-first harness changes
- future Harness Evolution Proposals

It helps answer:

- Does quickstart still work?
- Are security defaults still safe?
- Are approvals still enforced?
- Does memory still round-trip?
- Do harness models still serialize?
- Are provider configs valid?
- Are MCP/OpenAI-compatible surfaces structurally intact?

## Usage

Run the suite from a checkout or an installed CLI:

```bash
openclaw harness test
openclaw harness test --offline
openclaw harness test --category security
openclaw harness test --json
openclaw harness test --strict
```

From source, use:

```bash
dotnet run --project src/OpenClaw.Cli -c Release -- harness test
```

The command is offline-first and does not require cloud provider keys. It does not start the gateway, call model providers, or contact external MCP servers.

## Output

Default text output is intended for humans:

```text
OpenClaw Harness Regression

PASS onboarding.quickstart_config - Config loaded successfully.
PASS security.url_safety_defaults - Default URL safety blocks loopback, private, and metadata targets.
PASS providers.config_shape - Provider/model shape is valid without external network calls.
PASS mcp.initialize_shape - MCP initialize request shape serializes without running a gateway.
FAIL security.public_bind_hardening - Public/non-loopback bind is missing required hardening.

Summary:
14 passed, 1 failed, 1 skipped, 0 warning, 0 not applicable
```

Use `--json` to emit the `HarnessRegressionReport` model. Use `--output <path>` to also write the selected output format to a file.

## Categories

Use `--category <name>` to run a focused subset:

- `onboarding`
- `security`
- `approvals`
- `memory`
- `providers`
- `tools`
- `mcp`
- `openai_compat`
- `sessions`
- `harness`
- `deployment`
- `docs`

## Exit Codes

- `0`: all required checks passed or were skipped appropriately
- non-zero: at least one required check failed
- `--strict`: treats required warnings and skips as failures

## What This Does Not Do Yet

- It is not a replacement for unit tests.
- It is not a full model/provider integration test.
- It is not a guarantee that every agent outcome is correct.
- It does not require provider keys by default.
- It does not run automatically during normal runtime.
- It does not create Evidence Bundles by default.

The suite is a CLI/checking surface. Normal chat, providers, tool execution, approvals, memory, Companion setup, MCP, and OpenAI-compatible routes are unchanged unless the command is explicitly invoked.

# Quickstart Guide

This guide gets OpenClaw.NET to a first working agent with the supported setup path.

## Prerequisites

- .NET 10 SDK
- Optional: Node.js 20+ if you want upstream-style TS/JS plugin support

Examples below use `openclaw ...`. From a source checkout, replace that with `dotnet run --project src/OpenClaw.Cli -c Release -- ...`.

## Choose The Right Entrypoint

| Command | Use when |
| --- | --- |
| `openclaw setup` | You want the guided onboarding flow that writes config, prints launch commands, and gives you `--doctor` plus `admin posture` follow-ups. |
| `openclaw init` | You want raw bootstrap files to edit manually before running the gateway. |
| Direct config editing | You already know the runtime shape you want and do not need the guided path. |

## Fastest Local Start

1. Run the guided setup flow:

```bash
openclaw setup
```

2. Accept the local defaults or supply your preferred provider, model, API key reference, workspace path, and optional execution backend.

3. Start the gateway with the printed command, for example:

```bash
dotnet run --project src/OpenClaw.Gateway -c Release -- --config ~/.openclaw/config/openclaw.settings.json
```

4. Run the printed verification commands after the gateway is up:

```bash
dotnet run --project src/OpenClaw.Gateway -c Release -- --config ~/.openclaw/config/openclaw.settings.json --doctor
```

```bash
OPENCLAW_BASE_URL=http://127.0.0.1:18789 OPENCLAW_AUTH_TOKEN=... openclaw admin posture
```

Default local endpoints:

- Web UI: `http://127.0.0.1:18789/chat`
- WebSocket: `ws://127.0.0.1:18789/ws`
- Integration API: `http://127.0.0.1:18789/api/integration/status`
- MCP endpoint: `http://127.0.0.1:18789/mcp`
- Health: `http://127.0.0.1:18789/health`

## Public / Reverse Proxy Start

Use the public profile when the gateway will sit behind a real reverse proxy and TLS terminator:

```bash
openclaw setup --profile public
```

The public profile changes the defaults in ways that matter:

- bind address defaults to `0.0.0.0`
- `TrustForwardedHeaders=true`
- requester-matched HTTP approvals are enabled
- shell is disabled by default
- bridge plugins are disabled by default until you opt into public-bind trust settings

Run the exact `--doctor` and `admin posture` commands printed by `setup` before exposing the service.

## Advanced Manual Bootstrap

If you want editable starter files instead of the guided wizard:

```bash
openclaw init --preset both --output ./.openclaw-init
```

That writes:

- `.env.example`
- `config.local.json`
- `config.public.json`
- `deploy/Caddyfile.sample`
- `deploy/docker-compose.override.sample.yml`

Use `init` when you want to hand-edit the generated files before your first launch. Use `setup` when you want the supported guided path.

## Channel Setup

After the base config exists, configure common channels with the channel wizard:

```bash
openclaw setup channel telegram --config ~/.openclaw/config/openclaw.settings.json
openclaw setup channel slack --config ~/.openclaw/config/openclaw.settings.json
openclaw setup channel discord --config ~/.openclaw/config/openclaw.settings.json
openclaw setup channel teams --config ~/.openclaw/config/openclaw.settings.json
openclaw setup channel whatsapp --config ~/.openclaw/config/openclaw.settings.json
```

The wizard updates the existing external config, enables the selected channel, applies safe defaults such as signature or token validation where supported, and prints the endpoint hints you need to register with the provider.

## First Ways To Use It

### Browser UI

Open:

```text
http://127.0.0.1:18789/chat
```

### CLI Chat

```bash
dotnet run --project src/OpenClaw.Cli -c Release -- chat
```

### One-shot CLI Run

```bash
dotnet run --project src/OpenClaw.Cli -c Release -- run "summarize this repository" --file ./README.md
```

### Desktop Companion

```bash
dotnet run --project src/OpenClaw.Companion -c Release
```

### Typed integration API and MCP

Quick probes:

```bash
curl http://127.0.0.1:18789/api/integration/status
```

```bash
curl -X POST http://127.0.0.1:18789/mcp \
  -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":"1","method":"initialize","params":{"protocolVersion":"2025-03-26"}}'
```

For .NET automation, use `OpenClaw.Client` for typed access to both the integration API and the MCP facade.

## Runtime Modes

### `aot`

Use this when you want the safer, trim-friendly lane.

Supported mainstream plugin capabilities here:

- `registerTool()`
- `registerService()`
- plugin-packaged skills
- supported manifest/config subset

### `jit`

Use this when you need the expanded compatibility lane.

Additional support here includes:

- `registerChannel()`
- `registerCommand()`
- `registerProvider()`
- `api.on(...)`
- native dynamic in-process .NET plugins

## Recommended Local Workflow

1. Run `--doctor`
2. Start the gateway
3. Use the browser UI or Companion for interactive work
4. Use the CLI for scripted or repeatable tasks
5. Use `OpenClaw.Client` when you want stable typed access to `/api/integration/*` or `/mcp`
6. Switch to `jit` only when you actually need expanded plugin compatibility
7. On any non-loopback deployment, also check `openclaw admin posture` after the real proxy and TLS settings are in place

## Next Docs

- [README.md](../README.md) for the high-level overview
- [COMPATIBILITY.md](COMPATIBILITY.md) for the supported upstream skill/plugin/channel surface
- [USER_GUIDE.md](USER_GUIDE.md) for provider, tools, skills, and channels
- [SECURITY.md](../SECURITY.md) before any public deployment
- [architecture-startup-refactor.md](architecture-startup-refactor.md) for the current startup layout

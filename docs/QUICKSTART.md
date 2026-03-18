# Quickstart Guide

This guide gets OpenClaw.NET running locally with the fewest moving parts.

## Prerequisites

- .NET 10 SDK
- Optional: Node.js 20+ if you want upstream-style TS/JS plugin support

## Fastest Local Start

1. Set your model key:

```bash
export MODEL_PROVIDER_KEY="sk-..."
```

2. Optional but recommended: set a workspace directory for file tools and workspace skills:

```bash
export OPENCLAW_WORKSPACE="$PWD/workspace"
mkdir -p "$OPENCLAW_WORKSPACE"
```

3. Choose a runtime lane:

- Trim-safe lane:

```bash
export OpenClaw__Runtime__Mode="aot"
```

- Expanded compatibility lane:

```bash
export OpenClaw__Runtime__Mode="jit"
```

- Or leave it unset and let `auto` decide.

4. Validate startup config before running:

```bash
dotnet run --project src/OpenClaw.Gateway -c Release -- --doctor
```

5. Start the gateway:

```bash
dotnet run --project src/OpenClaw.Gateway -c Release
```

Default local endpoints:

- Web UI: `http://127.0.0.1:18789/chat`
- WebSocket: `ws://127.0.0.1:18789/ws`
- Integration API: `http://127.0.0.1:18789/api/integration/status`
- MCP endpoint: `http://127.0.0.1:18789/mcp`
- Health: `http://127.0.0.1:18789/health`

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

## Using A Config File

If you want to keep config outside the repo:

```bash
dotnet run --project src/OpenClaw.Gateway -c Release -- --config ~/.openclaw/config.json
```

You can also use:

```bash
export OPENCLAW_CONFIG_PATH="$HOME/.openclaw/config.json"
```

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

## Next Docs

- [README.md](../README.md) for the high-level overview
- [USER_GUIDE.md](USER_GUIDE.md) for provider, tools, skills, and channels
- [SECURITY.md](../SECURITY.md) before any public deployment
- [architecture-startup-refactor.md](architecture-startup-refactor.md) for the current startup layout
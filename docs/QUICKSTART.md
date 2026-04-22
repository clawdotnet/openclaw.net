# Quickstart Guide

This guide gets OpenClaw.NET to a first working agent with the supported setup path.

If you want the broader overview first, start with [GETTING_STARTED.md](GETTING_STARTED.md). That guide explains what each project is, how the runtime layers fit together, and which parts most contributors actually need.

## First 10 Minutes

Follow this path end-to-end before branching into anything else. Ignore every "optional" section, every channel, and anything involving Docker or sandboxing until this works.

**1. Install prerequisites.** .NET 10 SDK and Git. Nothing else is required for the first run.

**2. Set a provider key and run setup.**

```bash
export MODEL_PROVIDER_KEY="sk-..."
dotnet run --project src/OpenClaw.Cli -c Release -- setup
```

Accept the defaults. This writes `~/.openclaw/config/openclaw.settings.json`.

**3. Launch the gateway.**

```bash
dotnet run --project src/OpenClaw.Cli -c Release -- setup launch --config ~/.openclaw/config/openclaw.settings.json
```

**Expected:** startup phase lines (`Loading configuration`, `Building services`, `Initializing runtime`, `Starting listener`) followed by an `OpenClaw gateway ready.` block listing the working URLs. If you see `Started with notices:`, the gateway is still up; those are non-fatal startup advisories. If you do not see the ready block, the gateway is not ready yet.

**4. Open the browser UI.**

Go to `http://127.0.0.1:18789/chat` (not `/`, not the root URL). You should see the chat interface. Send a message; you should get a reply.

**5. If anything is wrong, run the doctor.**

```bash
dotnet run --project src/OpenClaw.Gateway -c Release -- --config ~/.openclaw/config/openclaw.settings.json --doctor
```

That's the whole first run. Skip everything below until this works.

If you intentionally skip the CLI flow and start the gateway process directly, use:

```bash
dotnet run --project src/OpenClaw.Gateway -c Release -- --quickstart
```

`--quickstart` is the direct terminal fallback. It keeps the gateway on `127.0.0.1`, prompts for missing provider values, retries in-process on the common local startup failures, and after a successful start can save the resulting config to the standard `~/.openclaw/config/openclaw.settings.json`.

You explicitly do **not** need any of these to get started:

- Docker
- OpenSandbox (see [sandboxing.md](sandboxing.md) only if you are certain you need it)
- Channel setup (Telegram, Slack, Discord, Teams, WhatsApp)
- A public / reverse-proxy deployment
- Runtime mode tuning (`aot` / `jit`)

---

## Prerequisites

- .NET 10 SDK
- Optional: Node.js 20+ if you want upstream-style TS/JS plugin support

Examples below use `openclaw ...`. From a source checkout, replace that with `dotnet run --project src/OpenClaw.Cli -c Release -- ...`.

For a first run from source, prefer the generated external config from `openclaw setup`. Do not start by relying on the checked-in `src/OpenClaw.Gateway/appsettings.json` unless you intentionally want to debug raw repo defaults.

## Choose The Right Entrypoint

| Command | Use when |
| --- | --- |
| `openclaw setup` | You want the guided onboarding flow that writes config, prints launch commands, and gives you `--doctor` plus `admin posture` follow-ups. |
| `dotnet run --project src/OpenClaw.Gateway -c Release -- --quickstart` | You want to start the gateway directly from a repo checkout and let the gateway recover into a safe local profile instead of preparing config first. |
| `openclaw init` | You want raw bootstrap files to edit manually before running the gateway. |
| Direct config editing | You already know the runtime shape you want and do not need the guided path. |

> **Breaking change**: `OPENCLAW_AUTH_TOKEN` is now the bootstrap and breakglass credential for non-loopback deployments. Browser admin usage is account/session-first, and Companion, CLI, API, and websocket clients should use operator account tokens.

## Fastest Local Start

1. Run the guided setup flow:

```bash
openclaw setup
```

2. Accept the local defaults or supply your preferred provider, model, API key reference, workspace path, and optional execution backend.

3. Start the supported local launch runner for that config:

```bash
openclaw setup launch --config ~/.openclaw/config/openclaw.settings.json
```

If you prefer to run the gateway process directly, use the printed command, for example:

```bash
dotnet run --project src/OpenClaw.Gateway -c Release -- --config ~/.openclaw/config/openclaw.settings.json
```

If that direct launch fails before the listener comes up and you are in an interactive terminal, the gateway now prints actionable guidance and offers a minimal local recovery flow instead of dropping straight to an unhandled exception. For the shortest direct path, prefer `--quickstart`.

4. Run the printed verification commands after the gateway is up:

```bash
dotnet run --project src/OpenClaw.Gateway -c Release -- --config ~/.openclaw/config/openclaw.settings.json --doctor
```

```bash
OPENCLAW_BASE_URL=http://127.0.0.1:18789 OPENCLAW_AUTH_TOKEN=... openclaw admin posture
```

Default local endpoints:

- Web UI: `http://127.0.0.1:18789/chat`
- Root redirect: `http://127.0.0.1:18789/` -> `/chat`
- WebSocket: `ws://127.0.0.1:18789/ws`
- Integration API: `http://127.0.0.1:18789/api/integration/status`
- MCP endpoint: `http://127.0.0.1:18789/mcp`
- Health: `http://127.0.0.1:18789/health`

Important:

- the browser chat UI is `/chat`, not the root URL
- the admin UI is `http://127.0.0.1:18789/admin`
- the ready banner also prints `Ctrl-C to stop`, startup notices, and follow-up commands

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

Generate deploy artifacts and inspect the resulting posture before you put a proxy in front of it:

```bash
openclaw setup service --config ~/.openclaw/config/openclaw.settings.json --platform all
openclaw setup status --config ~/.openclaw/config/openclaw.settings.json
```

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

For the simplest local source run, `openclaw init --preset local` gives you a starter config without forcing you through the optional sandbox path.

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

For operator workflows, use the admin UI at `http://127.0.0.1:18789/admin`.

If you browse to `http://127.0.0.1:18789/`, you are at the wrong URL for chat. Use `/chat`.

Recommended auth flow:

1. Use the bootstrap token once to create your first operator account on a non-loopback deployment.
2. Sign into `/admin` with the operator account username and password.
3. Exchange credentials for an operator account token when setting up Companion, CLI automation, API clients, or websocket integrations.

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

Use the **Admin** tab to exchange account credentials for an operator token and persist it into the OS-backed secret store.

## Upstream Migration

Translate an upstream-style OpenClaw checkout into an external OpenClaw.NET config with:

```bash
openclaw migrate upstream \
  --source ./upstream-agent \
  --target-config ~/.openclaw/config/openclaw.settings.json \
  --report ./migration-report.json
```

Add `--apply` when you are ready to write the translated config, import managed skills, and create the plugin review plan.

> **Breaking change**: bare `openclaw migrate` remains the legacy automation migration alias in this release. Use `openclaw migrate upstream` for upstream config and skill translation.

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

## Debugging First-Run Problems

If your first launch feels unclear, use this sequence:

1. Watch the logs through the supported launch helper:

```bash
openclaw setup launch --config ~/.openclaw/config/openclaw.settings.json
```

2. Run the gateway doctor command for the generated config:

```bash
dotnet run --project src/OpenClaw.Gateway -c Release -- --config ~/.openclaw/config/openclaw.settings.json --doctor
```

3. Check the summarized setup posture:

```bash
openclaw setup status --config ~/.openclaw/config/openclaw.settings.json
```

4. Validate the security posture that the running gateway sees:

```bash
OPENCLAW_BASE_URL=http://127.0.0.1:18789 OPENCLAW_AUTH_TOKEN=... openclaw admin posture
```

5. Use the browser UI Doctor view or fetch `/doctor/text` after the gateway is up for a readable report.

When in doubt, do not skip back and forth between several manual entrypoints. `setup`, `setup launch`, `--doctor`, and `admin posture` are the intended onboarding loop.

### Sandbox confusion on local/source builds

If you are running from Visual Studio or directly starting `OpenClaw.Gateway`, sandboxing is the most common source of documentation confusion.

What is true in the current codebase:

- OpenSandbox support is optional
- the default gateway configs start with `OpenClaw:Sandbox:Provider=None`
- the default gateway build does not include the OpenSandbox integration unless you compile with `-p:OpenClawEnableOpenSandbox=true`
- `shell`, `code_exec`, and `browser` are the sandbox-capable native tools
- the easiest local path is to ignore sandboxing entirely

If you do not want sandboxing on a local run, use:

```json
{
  "OpenClaw": {
    "Sandbox": {
      "Provider": "None"
    }
  }
}
```

Use [sandboxing.md](sandboxing.md) only when you intentionally want isolated execution.

## Next Docs

- [GETTING_STARTED.md](GETTING_STARTED.md) for the mental model, repository map, and debugging flow
- [README.md](../README.md) for the high-level overview
- [COMPATIBILITY.md](COMPATIBILITY.md) for the supported upstream skill/plugin/channel surface
- [USER_GUIDE.md](USER_GUIDE.md) for provider, tools, skills, and channels
- [SECURITY.md](../SECURITY.md) before any public deployment
- [architecture-startup-refactor.md](architecture-startup-refactor.md) for the current startup layout

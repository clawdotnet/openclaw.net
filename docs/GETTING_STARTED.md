# Getting Started

This guide is for the "I cloned the repo, but I still do not know what the main pieces are" problem.

If you want the shortest path to a running instance, use [QUICKSTART.md](QUICKSTART.md). If you want the broader mental model first, read this page once, then run the quickstart.

## What OpenClaw.NET Is

OpenClaw.NET is a self-hosted .NET agent platform made of a few distinct layers:

1. A gateway process that exposes HTTP, WebSocket, web UI, admin UI, and webhook endpoints.
2. An agent runtime that runs the model loop, selects tools, and coordinates sessions, memory, approvals, and routing.
3. Tool backends and integrations that let the agent read files, run shell commands, search the web, talk to channels, and call external systems.
4. Optional client surfaces such as the CLI, desktop Companion, TUI, and typed .NET client.

The fastest way to stay oriented is to think of it like this:

`user/channel -> gateway -> session/runtime -> model -> tools/integrations -> response`

```mermaid
flowchart LR
    User["User or External System"]
    Surface["UI / CLI / SDK / Channel"]
    Gateway["Gateway Host"]
    Runtime["Agent Runtime"]
    Model["LLM Provider"]
    Tools["Tools and Integrations"]
    Response["Response / Side Effects"]

    User --> Surface
    Surface --> Gateway
    Gateway --> Runtime
    Runtime --> Model
    Runtime --> Tools
    Model --> Runtime
    Tools --> Runtime
    Runtime --> Gateway
    Gateway --> Response
```

## Main Parts Of The Repository

These are the directories most people need first:

| Path | What it is |
| --- | --- |
| `src/OpenClaw.Gateway` | Main ASP.NET host. Starts the server, maps endpoints, serves `/chat`, `/admin`, `/mcp`, webhooks, diagnostics, and auth surfaces. |
| `src/OpenClaw.Core` | Shared models and infrastructure: config, memory, sessions, security, observability, plugin metadata, validation. |
| `src/OpenClaw.Agent` | Agent runtime, tool execution, plugin bridge, delegation, and the reasoning/tool loop. |
| `src/OpenClaw.Channels` | Channel adapters and channel-facing transport logic. |
| `src/OpenClaw.Cli` | `openclaw` command-line entrypoint: setup, launch, status, admin, models, plugins, skills, and one-shot/chat flows. |
| `src/OpenClaw.Companion` | Desktop companion app. Useful for local operator workflows. |
| `src/OpenClaw.Tui` | Terminal UI. |
| `src/OpenClaw.Client` | Typed .NET client for the integration API and MCP facade. |
| `src/OpenClaw.SemanticKernelAdapter` | Semantic Kernel integration layer. |
| `src/OpenClaw.MicrosoftAgentFrameworkAdapter` | Optional Microsoft Agent Framework adapter. |
| `src/OpenClaw.PluginKit` | Support code for plugin authoring and plugin integration. |
| `src/OpenClaw.Tests` | Unit and integration-style tests for the runtime and services. |
| `src/OpenClaw.WhatsApp.BaileysWorker` | .NET-facing WhatsApp worker integration project. |
| `src/whatsapp-baileys-worker` | Node.js worker used by the WhatsApp Baileys bridge. |
| `src/whatsapp-whatsmeow-worker` | Go worker for WhatsApp-related integration work. |

## How The Pieces Fit Together

### Repository mental model

This is the simplest way to understand the codebase boundaries:

```mermaid
flowchart TB
    subgraph Surfaces["User-facing surfaces"]
        CLI["OpenClaw.Cli"]
        Companion["OpenClaw.Companion"]
        TUI["OpenClaw.Tui"]
        Client["OpenClaw.Client"]
        Channels["OpenClaw.Channels"]
    end

    subgraph Host["Server host"]
        Gateway["OpenClaw.Gateway"]
    end

    subgraph Runtime["Runtime and shared infrastructure"]
        Agent["OpenClaw.Agent"]
        Core["OpenClaw.Core"]
        PluginKit["OpenClaw.PluginKit"]
    end

    subgraph Optional["Optional adapters and workers"]
        SK["SemanticKernelAdapter"]
        MAF["MicrosoftAgentFrameworkAdapter"]
        WA1["WhatsApp Baileys Worker (.NET)"]
        WA2["whatsapp-baileys-worker (Node.js)"]
        WA3["whatsapp-whatsmeow-worker (Go)"]
    end

    CLI --> Gateway
    Companion --> Gateway
    TUI --> Gateway
    Client --> Gateway
    Channels --> Gateway

    Gateway --> Agent
    Gateway --> Core
    Agent --> Core
    Agent --> PluginKit

    Gateway --> SK
    Gateway --> MAF
    Gateway --> WA1
    WA1 --> WA2
    WA1 --> WA3
```

### Runtime path

When a request comes in from the browser UI, CLI, WebSocket, or a channel:

1. `OpenClaw.Gateway` receives it and applies auth, policy, and routing.
2. Session and memory services from `OpenClaw.Core` load or create the session state.
3. `OpenClaw.Agent` runs the turn: prompt assembly, model call, tool calling, retries, delegation, approvals, and final response.
4. Tools execute against native implementations, plugin bridges, or external systems.
5. The gateway sends the response back to the caller and records telemetry.

```mermaid
sequenceDiagram
    participant Caller as Caller
    participant Gateway as OpenClaw.Gateway
    participant Session as Sessions and Memory
    participant Agent as OpenClaw.Agent
    participant Model as LLM Provider
    participant Tools as Tools / Plugins / External APIs

    Caller->>Gateway: HTTP, WebSocket, UI, CLI, or channel message
    Gateway->>Session: Resolve actor, route, and session state
    Session-->>Gateway: Session context
    Gateway->>Agent: Run turn with config, tools, and context
    Agent->>Model: Prompt + tool schema + history
    Model-->>Agent: Response or tool call request
    Agent->>Tools: Execute approved tool calls
    Tools-->>Agent: Tool results
    Agent->>Model: Continue turn with tool outputs
    Model-->>Agent: Final response
    Agent-->>Gateway: Assistant output + telemetry
    Gateway-->>Caller: Final message / streamed events
```

### Startup mental model

At startup, the gateway is doing more than "run ASP.NET". It has a staged boot path:

```mermaid
flowchart LR
    Args["CLI args + config path"] --> Bootstrap["Bootstrap"]
    Bootstrap --> Config["Bind GatewayConfig + resolve secrets"]
    Config --> Checks["Early checks: health-check, doctor, hardening"]
    Checks --> Services["Composition: register services"]
    Services --> Build["builder.Build()"]
    Build --> Runtime["Initialize runtime"]
    Runtime --> Plugins["Load providers, skills, plugins, workers"]
    Plugins --> Pipeline["Apply middleware and pipeline"]
    Pipeline --> Endpoints["Map /chat, /admin, /mcp, webhooks, diagnostics"]
    Endpoints --> Run["Run gateway"]
```

### Why there are several executables

- Use `OpenClaw.Gateway` when you want the server itself.
- Use `OpenClaw.Cli` when you want setup, diagnostics, one-shot runs, chat, or local launch helpers.
- Use `OpenClaw.Companion` when you want the desktop operator experience.
- Use `OpenClaw.Tui` when you want a terminal interface instead of the browser or desktop app.

The CLI is the normal first entrypoint even if the gateway is your real target, because `openclaw setup` creates config and `openclaw setup launch` gives the easiest first run from source.

### Runtime modes mental model

```mermaid
flowchart LR
    Start["Runtime mode"] --> Auto["auto"]
    Start --> AOT["aot"]
    Start --> JIT["jit"]

    Auto --> Detect{"Dynamic code available?"}
    Detect -- yes --> JIT
    Detect -- no --> AOT

    AOT --> AOTSurface["Trim-safe lane<br/>native tools + mainstream bridge support"]
    JIT --> JITSurface["Expanded compatibility lane<br/>dynamic plugins + wider extension surface"]
```

## Prerequisites

- .NET 10 SDK
- Git
- Optional: Node.js 20+ if you want upstream-style TS/JS plugin support
- Optional: Docker if you want container deployment or isolated tool execution backends
- Optional: Go if you plan to work on the Go-based WhatsApp worker

## Recommended First Local Run

From a source checkout:

```bash
git clone https://github.com/clawdotnet/openclaw.net
cd openclaw.net

export MODEL_PROVIDER_KEY="sk-..."

dotnet restore
dotnet build
dotnet run --project src/OpenClaw.Cli -c Release -- setup
```

What `setup` gives you:

- an external config file, usually at `~/.openclaw/config/openclaw.settings.json`
- an adjacent env example
- the exact commands to launch the gateway
- follow-up diagnostics commands such as `--doctor` and `openclaw admin posture`
- a launch path that avoids the common local startup failures caused by raw production defaults

Why this matters:

- it avoids relying on the checked-in `src/OpenClaw.Gateway/appsettings.json` defaults
- it gives you a config that matches the supported onboarding path
- it avoids first-run confusion around optional features such as sandbox backends

Then launch the supported local dev flow:

```bash
dotnet run --project src/OpenClaw.Cli -c Release -- setup launch --config ~/.openclaw/config/openclaw.settings.json
```

`setup launch` is the easiest place to start because it boots the gateway, starts Companion, waits for readiness, and streams logs until you stop it.

If you are starting `OpenClaw.Gateway` directly from a repo checkout and do not want to run `setup` first, the direct local fallback is:

```bash
dotnet run --project src/OpenClaw.Gateway -c Release -- --quickstart
```

That mode is interactive-only. It applies a loopback-local profile for the current process, prompts for missing provider inputs, retries on the common local startup failures, and can save the resulting config to `~/.openclaw/config/openclaw.settings.json` after the gateway is ready.

## What To Open After Startup

For a default local setup:

- Browser chat UI: `http://127.0.0.1:18789/chat`
- Admin UI: `http://127.0.0.1:18789/admin`
- MCP endpoint: `http://127.0.0.1:18789/mcp`
- Integration API status: `http://127.0.0.1:18789/api/integration/status`

Important:

- the browser chat entrypoint is `/chat`
- the operator/admin entrypoint is `/admin`
- the root URL currently redirects to `/chat`, but `/chat` is the explicit browser UI entrypoint

### If You Start The Gateway Directly From Visual Studio

This is where many first-run surprises come from.

If you launch `OpenClaw.Gateway` directly without an external config:

- the gateway still serves the browser chat UI at `/chat`
- `wwwroot/webchat.html` and `wwwroot/admin.html` are bundled in normal source builds
- you are now relying on the checked-in `appsettings.json`, which is a repo default rather than the generated onboarding config

For the least confusing Visual Studio/source setup, prefer one of these:

1. Use the config generated by `openclaw setup` and pass it with `--config`
2. Use `openclaw init --preset local` and point the gateway at the generated `config.local.json`
3. If you insist on using the checked-in defaults, override sandboxing to off:

```json
{
  "OpenClaw": {
    "Sandbox": {
      "Provider": "None"
    }
  }
}
```

If you prefer direct server startup instead of the launch helper, use the command printed by `setup`, typically:

```bash
dotnet run --project src/OpenClaw.Gateway -c Release -- --config ~/.openclaw/config/openclaw.settings.json
```

The direct gateway path now prints startup phases and a clear ready block:

- `Loading configuration`
- `Building services`
- `Initializing runtime`
- `Starting listener`
- `Ready`

Then it prints `OpenClaw gateway ready.`, the working `/chat`, `/admin`, `/doctor/text`, `/health`, `/mcp`, and `/ws` URLs, `Ctrl-C to stop`, and a `Started with notices:` section for non-fatal startup advisories.

## Typical Setup Questions Answered

### Where does configuration live?

The supported path is an external JSON config generated by `openclaw setup`. You do not need to start by editing `src/OpenClaw.Gateway/appsettings.json`.

If you are running from Visual Studio, that distinction matters. The checked-in `appsettings.json` is a repo default, not the recommended first-run config.

### What is the correct browser URL after the gateway starts?

Use:

- `http://127.0.0.1:18789/chat` for browser chat
- `http://127.0.0.1:18789/admin` for the admin UI

The root URL (`/`) currently redirects to `/chat`, but `/chat` is still the canonical browser UI route.

### What if the gateway fails before it starts listening?

In an interactive local terminal, the gateway now classifies the common startup failures and offers a guided recovery path instead of exiting with a raw unhandled exception. This currently covers:

- missing `OPENCLAW_AUTH_TOKEN` on a non-loopback bind
- missing `MODEL_PROVIDER_KEY` or `MODEL_PROVIDER_ENDPOINT`
- unwritable memory storage
- port already in use

For the shortest direct recovery path, rerun with:

```bash
dotnet run --project src/OpenClaw.Gateway -c Release -- --quickstart
```

Headless, redirected, `--doctor`, and `--health-check` runs still stay non-interactive and fail fast with actionable text.

### Do I need sandboxing to get started locally?

No.

For a normal local/source setup, treat sandboxing as optional. The supported beginner path is:

- use `openclaw setup`, which does not require a sandbox backend by default
- or set `OpenClaw:Sandbox:Provider=None` if you are starting from a raw config

OpenSandbox only matters when you explicitly want isolated execution for high-risk native tools such as:

- `shell`
- `code_exec`
- `browser`

### Why do I see OpenSandbox in the repo if it is optional?

Because the codebase still contains an optional OpenSandbox integration, but the default gateway configs start with sandboxing disabled and the default gateway build does not include that integration unless you compile with the sandbox flag enabled.

That means these two things are both true:

- the repo contains sandbox code and sandbox docs
- a standard source build can still be used locally without OpenSandbox

See [sandboxing.md](sandboxing.md) for the advanced path. For onboarding, it is fine to ignore it or disable it explicitly.

### When do I need `aot` vs `jit`?

- Use `aot` when you want the trim-safe, lower-complexity runtime lane.
- Use `jit` when you need the wider plugin compatibility surface, including dynamic plugin features.
- Use `auto` if you want the runtime to choose based on environment support.

If you are just trying to get the project running locally, do not optimize this early. Start with the generated defaults and switch only if you hit a plugin/runtime requirement.

### Do I need every subproject to work on the repo?

No. Most contributors only need:

- `OpenClaw.Gateway`
- `OpenClaw.Cli`
- `OpenClaw.Core`
- `OpenClaw.Agent`
- `OpenClaw.Tests`

The channel workers and optional adapters matter only when you are working in those areas.

## How To Debug A First-Run Problem

Start with the tools that already exist for onboarding diagnostics:

1. Run the generated doctor command:

```bash
dotnet run --project src/OpenClaw.Gateway -c Release -- --config ~/.openclaw/config/openclaw.settings.json --doctor
```

2. Check the security and deployment posture:

```bash
OPENCLAW_BASE_URL=http://127.0.0.1:18789 OPENCLAW_AUTH_TOKEN=... dotnet run --project src/OpenClaw.Cli -c Release -- admin posture
```

3. Summarize the config and artifact state:

```bash
dotnet run --project src/OpenClaw.Cli -c Release -- setup status --config ~/.openclaw/config/openclaw.settings.json
```

4. Open the browser UI and use the Doctor view or fetch `/doctor/text` when the gateway is already up.

When debugging, prefer `setup launch` over manually starting several processes. It gives one place to watch logs and confirm whether the gateway actually became ready.

If the browser UI behaves differently from what the docs describe, check which config file the gateway is actually using before debugging anything deeper.

### Common causes

| Symptom | Usually means |
| --- | --- |
| Gateway fails before first prompt | Config or secret problem, often missing API key or invalid provider/model settings. |
| Gateway starts but tools are missing | Tooling restrictions, plugin trust/runtime mode mismatch, or optional integrations not configured. |
| Public bind fails hardening checks | Missing `OPENCLAW_AUTH_TOKEN`, missing proxy/TLS-related settings, or an unsafe tool/plugin posture for a non-loopback bind. |
| Workspace/file tools behave unexpectedly | Workspace root or allowed read/write roots do not match the directory you expected. |
| Plugin works in one mode but not another | You likely need `jit`, or the plugin uses a surface that is intentionally unavailable in `aot`. |

## Recommended Reading Order

1. This guide
2. [QUICKSTART.md](QUICKSTART.md)
3. [USER_GUIDE.md](USER_GUIDE.md)
4. [TOOLS_GUIDE.md](TOOLS_GUIDE.md)
5. [COMPATIBILITY.md](COMPATIBILITY.md)
6. [SECURITY.md](../SECURITY.md) before any public deployment

Keep [GLOSSARY.md](GLOSSARY.md) and the [docs index](README.md) open alongside these. The glossary covers terms that show up across docs (*gateway*, *runtime*, *bridge*, *skill*, *plugin*, *posture*, `aot` / `jit` / `auto`); the index groups every doc by purpose.

## If You Are Contributing Code

Use [CONTRIBUTING.md](../CONTRIBUTING.md) for build, test, and PR expectations. The contributor guide now includes a current project map, but this page is still the better first stop when you need to understand the runtime shape before changing code.

## Getting Help

If you have run the diagnostics above and are still stuck, use these in order:

1. Check [GLOSSARY.md](GLOSSARY.md) if a term in another doc is the blocker. Terms like *posture*, *bridge*, *bootstrap token*, or `aot` / `jit` are defined there in one place.
2. Skim the [docs index](README.md) — the doc you need may exist under a heading you did not expect (channel setup, sandboxing, model profiles, prompt caching, etc.).
3. Search existing GitHub issues, including closed ones. Onboarding problems often repeat.
4. Open a new issue with:
   - the commands you ran (especially the exact `openclaw setup` invocation),
   - the relevant output of `--doctor`, `openclaw admin posture`, and `openclaw setup status`,
   - which doc you were following and the step it stopped working at,
   - your OS, .NET SDK version, and whether you are running from source or a container.

"I followed the Getting Started guide and got stuck at step X" is useful. "I still have tons of questions" is not — it leaves us guessing. The more specific the question, the faster it turns into a doc fix.

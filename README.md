<div align="center">
  <img src="src/OpenClaw.Gateway/wwwroot/image.png" alt="OpenClaw.NET Logo" width="180" />
</div>

# OpenClaw.NET

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)
![NativeAOT-friendly](https://img.shields.io/badge/NativeAOT-friendly-blue)
![Plugin compatibility](https://img.shields.io/badge/plugin%20compatibility-evolving-green)
![Tools](https://img.shields.io/badge/native%20tools-48-green)
![Channels](https://img.shields.io/badge/channels-9-green)
[![Ask DeepWiki](https://deepwiki.com/badge.svg)](https://deepwiki.com/clawdotnet/openclaw.net)

> **Disclaimer**: This project is not affiliated with, endorsed by, or associated with [OpenClaw](https://github.com/openclaw/openclaw). It is an independent .NET implementation inspired by their work.

OpenClaw.NET is a NativeAOT-friendly AI agent runtime and gateway for .NET with practical OpenClaw ecosystem compatibility.

It is for .NET developers and operators who want a local or self-hosted agent gateway with explicit diagnostics, first-party .NET tools, OpenAI-compatible HTTP surfaces, and a path from source checkout to NativeAOT release artifacts.

> **Docs:** [AgentQi.dev](https://agentqi.dev) is the documentation and ecosystem home for OpenClaw.NET. OpenClaw.NET remains the current runtime and repository identity.

## AgentQi Documentation

AgentQi is the broader developer-infrastructure direction behind OpenClaw.NET: practical, observable, self-hosted AI agent systems for .NET developers.

OpenClaw.NET is the runtime and repository you can use today. AgentQiX is the likely future runtime identity.

Start here:

- [Quickstart](https://agentqi.dev/docs/quickstart)
- [Getting Started](https://agentqi.dev/docs/getting-started)
- [Architecture](https://agentqi.dev/docs/start-here)
- [Security](https://agentqi.dev/docs/security)
- [Roadmap](https://agentqi.dev/docs/roadmap)

## What Works Now

- **NativeAOT-friendly** runtime and gateway for .NET agent workloads
- **Agent runtime** with tool execution, streaming, cancellation, retry, memory, and session support
- **Gateway** with chat UI, admin UI, OpenAI-compatible endpoints, MCP, websocket, health, and diagnostics
- **Passive Harness Contracts** for inspectable agent-work plans without changing default chat or approval behavior
- **Passive Evidence Bundles** for inspectable run evidence, checks, risks, and human review without default runtime interception
- **Optional Plan-Execute-Verify Mode** for governed high-risk tool execution with contracts, evidence, and verification
- **Passive Governance Ledger** for durable approval and oversight decisions without auto-approving future actions
- **Harness Regression Suite** via `openclaw harness test` for offline checks before trusting harness/runtime changes
- **Harness Evolution Proposals** for review-first suggestions to improve harness policies, routing, memory retrieval, verification, pulse behavior, and tool governance
- **Optional Fractal Memory MCP integration** for compact structured project memory and Runtime Pulse context without replacing OpenClaw memory/session stores
- **Shared Harness State** for passive delegated-work coordination across participants, actions, read/write sets, assumptions, verifier obligations, evidence links, and conflicts
- **First-class optional Microsoft Agent Framework adapter** for `Runtime.Orchestrator=maf` without a special build
- **Durable workflow delegation** through supported workflow backends such as `maf-durable-http`
- **CLI and Companion** setup flows for source checkouts and desktop bundles
- **48 native tools** covering file ops, sessions, memory, web, messaging, home automation, databases, email, and more
- **9 channel adapters** (Telegram, SMS, WhatsApp, Teams, Slack, Discord, Signal, email, webhooks) with DM policy, allowlists, and signature validation
- **Native LLM providers** for OpenAI, Claude, Gemini, Azure OpenAI, Ollama, and OpenAI-compatible endpoints
- **Optional embedded local models** with Gemma 4 GGUF packages, package install/verify CLI commands, supervised sidecar inference, and frame-based video understanding
- **Practical reuse** of existing OpenClaw TS/JS plugins and `SKILL.md` packages

Start with [docs/START_HERE.md](docs/START_HERE.md) for the evaluator overview, [docs/QUICKSTART.md](docs/QUICKSTART.md) for the supported local setup path, or [docs/RELEASES.md](docs/RELEASES.md) for desktop downloads.

For Microsoft Agent Framework, A2A, and durable workflow setup, see [docs/integrations/microsoft-agent-framework.md](docs/integrations/microsoft-agent-framework.md), [docs/a2a.md](docs/a2a.md), and [docs/workflow-backends.md](docs/workflow-backends.md).

## Download And Run Desktop

For the lowest-friction desktop start, download the latest desktop bundle for your platform:

| Platform | Download |
|----------|----------|
| Windows x64 | [openclaw-desktop-win-x64.zip](https://github.com/clawdotnet/openclaw.net/releases/latest/download/openclaw-desktop-win-x64.zip) |
| Apple Silicon macOS | [openclaw-desktop-osx-arm64.zip](https://github.com/clawdotnet/openclaw.net/releases/latest/download/openclaw-desktop-osx-arm64.zip) |
| Linux x64 | [openclaw-desktop-linux-x64.zip](https://github.com/clawdotnet/openclaw.net/releases/latest/download/openclaw-desktop-linux-x64.zip) |

Each desktop bundle includes Companion, the NativeAOT gateway, and the NativeAOT CLI.

1. Extract the archive.
2. Launch Companion from the `companion` folder.
3. Open the **Setup** tab.
4. Choose a provider/model and enter the provider key, choose Ollama for a local model server, or choose Embedded for an OpenClaw-managed local model such as Gemma 4.
5. Click **Set Up and Start**.

Companion writes a local config, starts the bundled gateway on `127.0.0.1`, and connects to it. The current Windows and macOS release archives are unsigned, so first-run OS warnings are expected. See [docs/RELEASES.md](docs/RELEASES.md) for checksums, standalone CLI/gateway archives, signing status, and maintainer release flow.

## Quickstart

For a real local gateway from source:

```bash
export MODEL_PROVIDER_KEY="sk-..."
dotnet run --project src/OpenClaw.Cli -c Release -- start
```

When the gateway finishes startup it now prints explicit phase markers, a final `OpenClaw gateway ready.` block, the localhost URLs, `Ctrl-C to stop`, and any non-fatal startup notices under `Started with notices:`. Then open:

| Surface | URL |
|---------|-----|
| Web UI / Live Chat | `http://127.0.0.1:18789/chat` |
| Admin UI | `http://127.0.0.1:18789/admin` |
| Integration API | `http://127.0.0.1:18789/api/integration/status` |
| MCP endpoint | `http://127.0.0.1:18789/mcp` |

The root URL redirects to `/chat`. For the full first-run walkthrough (including the "First 10 Minutes" runbook and debugging flow), see [docs/QUICKSTART.md](docs/QUICKSTART.md). For the project shape and repository map before changing code, see [docs/GETTING_STARTED.md](docs/GETTING_STARTED.md).

If you want a direct gateway fallback instead of the full CLI onboarding flow, run:

```bash
dotnet run --project src/OpenClaw.Gateway -c Release -- --quickstart
```

`--quickstart` is interactive-only. It applies a minimal loopback-local profile for the current process, prompts for missing provider inputs, retries on the common first-run failures, and after a successful start can save the working setup to `~/.openclaw/config/openclaw.settings.json`.

If the CLI is already on your `PATH`, the same guided entrypoints are:

```bash
openclaw start
openclaw setup
openclaw setup launch --config ~/.openclaw/config/openclaw.settings.json
openclaw setup service --config ~/.openclaw/config/openclaw.settings.json --platform all
openclaw setup status --config ~/.openclaw/config/openclaw.settings.json
openclaw upgrade check --config ~/.openclaw/config/openclaw.settings.json
openclaw upgrade rollback --config ~/.openclaw/config/openclaw.settings.json --offline
```

Useful follow-up commands and surfaces:

```bash
openclaw models presets
openclaw models packages
openclaw models install gemma-4-e4b --accept-license --path ~/Downloads/gemma-4-E4B-it-Q4_K_M.gguf --mmproj-path ~/Downloads/mmproj-gemma-4-E4B-it-Q8_0.gguf
openclaw models doctor
openclaw maintenance scan --config ~/.openclaw/config/openclaw.settings.json
openclaw maintenance fix --config ~/.openclaw/config/openclaw.settings.json --dry-run
openclaw skills inspect ./skills/my-skill
openclaw compatibility catalog
openclaw insights
openclaw admin trajectory export --anonymize --output ./trajectory.jsonl
openclaw upgrade check --config ~/.openclaw/config/openclaw.settings.json --offline
openclaw upgrade rollback --config ~/.openclaw/config/openclaw.settings.json --offline
openclaw migrate upstream --source ./upstream-agent --target-config ~/.openclaw/config/openclaw.settings.json
```

- Skill inventory: `/admin/skills`
- Maintenance report: `/admin/maintenance`
- Observability summary: `/admin/observability/summary`
- Operator insights: `/admin/insights`
- Audit export: `/admin/audit/export`
- Trajectory export: `/admin/trajectory/export`
- Compatibility matrix: [docs/COMPATIBILITY.md](docs/COMPATIBILITY.md)

For local Ollama setups, prefer the native root endpoint and an explicit preset:

```bash
openclaw setup --non-interactive --profile local --workspace ./workspace --provider ollama --model llama3.2 --model-preset ollama-general
```

OpenClaw.NET now treats Ollama as a first-class native provider at `http://127.0.0.1:11434`. Older `/v1` endpoints still work for one compatibility cycle, but `openclaw models doctor` will flag them so you can migrate cleanly.

For OpenClaw-managed local inference, use provider `embedded` with an installable package. Gemma 4 is now the main embedded local model path:

```bash
openclaw models packages
openclaw models install gemma-4-e4b \
  --accept-license \
  --path ~/Downloads/gemma-4-E4B-it-Q4_K_M.gguf \
  --mmproj-path ~/Downloads/mmproj-gemma-4-E4B-it-Q8_0.gguf
openclaw setup --provider embedded --model-preset embedded-gemma-4-e4b --model gemma-4-e4b
openclaw models status gemma-4-e4b
```

The package catalog includes Gemma 4 E2B, E4B, 31B, and 26B-A4B GGUF entries, plus the experimental Gemma 4 E2B LiteRT-LM package for adapter work. The older `gemma-local-small-q4` Gemma 3 package remains available for smaller machines.

Embedded video support is frame-based: OpenClaw samples local `video/*` inputs into ordered image frames before calling the local sidecar, and Gemma 4 GGUF packages include the multimodal projector file needed for image-frame inputs. LiteRT-LM packages are experimental and require an OpenClaw-compatible adapter binary; see [docs/LOCAL_MODELS.md](docs/LOCAL_MODELS.md).

> **Breaking change**: browser admin usage is account/session-first. Use named operator accounts for `/admin`, and use operator account tokens for Companion, CLI, API, and websocket clients.

## Private Access With Tailscale Serve

OpenClaw.NET can be exposed privately inside a tailnet using Tailscale Serve while keeping the gateway bound to `127.0.0.1`.

This is useful for private access to `/chat`, `/admin`, `/mcp`, `/api/integration/*`, and `/ws` without binding the gateway publicly.

Use the guided helper for instructions:

```bash
openclaw setup tailscale serve
```

See [docs/deployment/TAILSCALE.md](docs/deployment/TAILSCALE.md).

## Security

When binding to a non-loopback address, the gateway **refuses to start** unless dangerous settings are explicitly hardened (auth token required, tooling roots restricted, signature validation enforced, `raw:` secret refs rejected). See [SECURITY.md](SECURITY.md) before exposing the gateway publicly.

Outbound web fetches and browser navigations run through `OpenClaw:Tooling:UrlSafety` by default. The safe default blocks loopback, private/link-local, multicast, and metadata hosts; operators can disable the policy intentionally or add `BlockedHostGlobs` and `BlockedCidrs` for environment-specific deny lists.

## Docs

The public documentation site is **[AgentQi.dev](https://agentqi.dev)**. The source documentation map lives at **[docs/README.md](docs/README.md)**. Starting points:

| Doc | When to read |
|-----|--------------|
| [docs/GETTING_STARTED.md](docs/GETTING_STARTED.md) | Project shape, repository map, and first-run debugging flow |
| [docs/QUICKSTART.md](docs/QUICKSTART.md) | Shortest supported path to a running local instance |
| [docs/USER_GUIDE.md](docs/USER_GUIDE.md) | Providers, tools, skills, memory, channels, and day-to-day operation |
| [docs/RELEASES.md](docs/RELEASES.md) | Desktop downloads, release assets, and signing status |
| [docs/TOOLS_GUIDE.md](docs/TOOLS_GUIDE.md) | Native tool catalog and configuration |
| [docs/LOCAL_MODELS.md](docs/LOCAL_MODELS.md) | Embedded local models, frame-based video, and experimental LiteRT-LM adapter notes |
| [docs/mempalace-memory.md](docs/mempalace-memory.md) | Optional MemPalace.NET memory provider and temporal knowledge graph |
| [docs/CANVAS_A2UI.md](docs/CANVAS_A2UI.md) | Supported Canvas and A2UI visual workspace behavior |
| [docs/MODEL_PROFILES.md](docs/MODEL_PROFILES.md) | Provider-agnostic named model profiles (including Gemma) |
| [docs/deployment/TAILSCALE.md](docs/deployment/TAILSCALE.md) | Optional Tailscale Serve private access |
| [docs/COMPATIBILITY.md](docs/COMPATIBILITY.md) | Supported upstream skill, plugin, and channel surface |
| [SECURITY.md](SECURITY.md) | Hardening guidance for public deployments |

## Contributing

Contributions welcome — especially security review, NativeAOT trimming improvements, sandboxing ideas, new channel adapters, and performance benchmarks. See [CONTRIBUTING.md](CONTRIBUTING.md).

If this project helps your .NET AI work, please star it.

## License

[MIT](LICENSE)

## Fastest Source Proof

This deterministic sample proves the runtime loop and tool path without provider keys, Ollama, Docker, or a browser:

```bash
git clone https://github.com/clawdotnet/openclaw.net
cd openclaw.net

dotnet restore OpenClaw.Net.slnx
dotnet build OpenClaw.Net.slnx --configuration Release --no-restore
dotnet run --project samples/OpenClaw.HelloAgent -c Release --no-build
```

Expected output:

```text
OpenClaw.HelloAgent
User: hello
Agent: hello from OpenClaw.NET
Tool: echo(hello): ok
```

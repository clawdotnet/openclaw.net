<div align="center">
  <img src="src/OpenClaw.Gateway/wwwroot/image.png" alt="OpenClaw.NET Logo" width="180" />
</div>

# OpenClaw.NET

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)
![NativeAOT-friendly](https://img.shields.io/badge/NativeAOT-friendly-blue)
![Plugin compatibility](https://img.shields.io/badge/plugin%20compatibility-practical-green)
![Tools](https://img.shields.io/badge/native%20tools-48-green)
![Channels](https://img.shields.io/badge/channels-9-green)

> **Disclaimer**: This project is not affiliated with, endorsed by, or associated with [OpenClaw](https://github.com/openclaw/openclaw). It is an independent .NET implementation inspired by their work.

Self-hosted **AI agent runtime and gateway for .NET** with 48 native tools, 9 channel adapters, multi-agent routing, review-first self-evolving features, built-in OpenAI/Claude/Gemini provider support, NativeAOT support, and practical OpenClaw ecosystem compatibility.

## Highlights

- **NativeAOT-friendly** runtime and gateway for .NET agent workloads
- **48 native tools** covering file ops, sessions, memory, web, messaging, home automation, databases, email, and more
- **9 channel adapters** (Telegram, SMS, WhatsApp, Teams, Slack, Discord, Signal, email, webhooks) with DM policy, allowlists, and signature validation
- **Native LLM providers** for OpenAI, Claude, Gemini, Azure OpenAI, Ollama, and OpenAI-compatible endpoints
- **Practical reuse** of existing OpenClaw TS/JS plugins and `SKILL.md` packages
- **Review-first self-evolving** workflows — the runtime proposes profile updates, automation drafts, and skill drafts from observed sessions; operators approve or reject

## Quickstart

```bash
git clone https://github.com/clawdotnet/openclaw.net
cd openclaw.net

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
openclaw models doctor
openclaw maintenance scan --config ~/.openclaw/config/openclaw.settings.json
openclaw maintenance fix --config ~/.openclaw/config/openclaw.settings.json --dry-run
openclaw skills inspect ./skills/my-skill
openclaw compatibility catalog
openclaw upgrade check --config ~/.openclaw/config/openclaw.settings.json --offline
openclaw upgrade rollback --config ~/.openclaw/config/openclaw.settings.json --offline
openclaw migrate upstream --source ./upstream-agent --target-config ~/.openclaw/config/openclaw.settings.json
```

- Skill inventory: `/admin/skills`
- Maintenance report: `/admin/maintenance`
- Observability summary: `/admin/observability/summary`
- Audit export: `/admin/audit/export`
- Compatibility matrix: [docs/COMPATIBILITY.md](docs/COMPATIBILITY.md)

For local Ollama setups, prefer the native root endpoint and an explicit preset:

```bash
openclaw setup --non-interactive --profile local --workspace ./workspace --provider ollama --model llama3.2 --model-preset ollama-general
```

OpenClaw.NET now treats Ollama as a first-class native provider at `http://127.0.0.1:11434`. Older `/v1` endpoints still work for one compatibility cycle, but `openclaw models doctor` will flag them so you can migrate cleanly.

> **Breaking change**: browser admin usage is account/session-first. Use named operator accounts for `/admin`, and use operator account tokens for Companion, CLI, API, and websocket clients.

## Security

When binding to a non-loopback address, the gateway **refuses to start** unless dangerous settings are explicitly hardened (auth token required, tooling roots restricted, signature validation enforced, `raw:` secret refs rejected). See [SECURITY.md](SECURITY.md) before exposing the gateway publicly.

## Docs

The full documentation map lives at **[docs/README.md](docs/README.md)**. Starting points:

| Doc | When to read |
|-----|--------------|
| [docs/GETTING_STARTED.md](docs/GETTING_STARTED.md) | Project shape, repository map, and first-run debugging flow |
| [docs/QUICKSTART.md](docs/QUICKSTART.md) | Shortest supported path to a running local instance |
| [docs/USER_GUIDE.md](docs/USER_GUIDE.md) | Providers, tools, skills, memory, channels, and day-to-day operation |
| [docs/TOOLS_GUIDE.md](docs/TOOLS_GUIDE.md) | Native tool catalog and configuration |
| [docs/MODEL_PROFILES.md](docs/MODEL_PROFILES.md) | Provider-agnostic named model profiles (including Gemma) |
| [docs/COMPATIBILITY.md](docs/COMPATIBILITY.md) | Supported upstream skill, plugin, and channel surface |
| [SECURITY.md](SECURITY.md) | Hardening guidance for public deployments |

## Contributing

Contributions welcome — especially security review, NativeAOT trimming improvements, sandboxing ideas, new channel adapters, and performance benchmarks. See [CONTRIBUTING.md](CONTRIBUTING.md).

If this project helps your .NET AI work, please star it.

## License

[MIT](LICENSE)

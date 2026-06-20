# OpenClaw.NET Documentation Index

Use this page as the map. If you are unsure where to go next, the groups below are roughly the order most people need them.

## Start Here

| Doc | When to read |
| --- | --- |
| [START_HERE.md](START_HERE.md) | You are evaluating the repo and want the shortest orientation: what works, what is experimental, how to prove the runtime, and where the compatibility boundaries are. |
| [SITE_MAP.md](SITE_MAP.md) | You are turning these Markdown files into a documentation website and need a stable navigation order. |
| [GETTING_STARTED.md](GETTING_STARTED.md) | You cloned the repo and want the project shape, repository map, and first-run debugging flow before running commands. |
| [QUICKSTART.md](QUICKSTART.md) | You want the shortest supported path to a running local instance. |
| [GLOSSARY.md](GLOSSARY.md) | A term in another doc is unfamiliar — *gateway*, *runtime*, *skill*, *plugin*, *profile*, *posture*, `aot` / `jit` / `auto`, etc. |
| [../README.md](../README.md) | High-level overview, feature list, and headline capabilities. |
| [../README-cn.md](../README-cn.md) | 项目概述、功能列表和核心能力（简体中文）。 |
| [ARCHITECTURE_BOUNDARIES.md](ARCHITECTURE_BOUNDARIES.md) | Core, gateway, extension, AOT/JIT, and Industrial Pack boundaries. |
| [CAPABILITY_MATRIX.md](CAPABILITY_MATRIX.md) | Core, optional, experimental, and JIT-only capability lanes. |
| [zh-CN/CAPABILITY_MATRIX.md](zh-CN/CAPABILITY_MATRIX.md) | 能力矩阵（简体中文）。 |
| [zh-CN/START_HERE.md](zh-CN/START_HERE.md) | Simplified Chinese first-run orientation. |

## Using It

| Doc | What it covers |
| --- | --- |
| [USER_GUIDE.md](USER_GUIDE.md) | Providers, tools, skills, channels, and the day-to-day operator surface. |
| [RELEASES.md](RELEASES.md) | Desktop download bundles, release assets, and signing/notarization status. |
| [TOOLS_GUIDE.md](TOOLS_GUIDE.md) | Native tool catalog, behavior, and configuration (75 tools). |
| [zh-CN/TOOLS_GUIDE.md](zh-CN/TOOLS_GUIDE.md) | 原生工具目录与配置（简体中文，75 个工具）。 |
| [zh-CN/LOOP_TECHNICAL_ARCHITECTURE.md](zh-CN/LOOP_TECHNICAL_ARCHITECTURE.md) | /loop 定时循环命令技术架构（简体中文）。 |
| [zh-CN/GOAL_TECHNICAL_ARCHITECTURE.md](zh-CN/GOAL_TECHNICAL_ARCHITECTURE.md) | Goal 目标机制技术架构（简体中文）。 |
| [SKILLKIT.md](SKILLKIT.md) | Local-first `openclaw skill` workflows for defining, validating, critiquing, packaging, and dry-running reusable OpenClaw skills. |
| [LOCAL_MODELS.md](LOCAL_MODELS.md) | Embedded local model packages, supervised sidecars, frame-based video support, and experimental LiteRT-LM adapter guidance. |
| [opensquilla-dynamic-turn-routing.md](opensquilla-dynamic-turn-routing.md) | Independent guide to the OpenSquilla-style dynamic turn-routing surface, current implementation status, runtime wiring, and operator expectations. |
| [opensquilla-meta-skill-migration.md](opensquilla-meta-skill-migration.md) | OpenClaw.NET meta-skill migration notes for OpenSquilla-style DAG orchestration, current parity, and remaining migration gaps. |
| [dynamic-turn-routing-model-profiles.md](dynamic-turn-routing-model-profiles.md) | Collaboration playbook for how Dynamic Turn Routing and Model Profiles work together (precedence, field projection, fallback, and restore semantics). |
| [EXTERNAL_CLI_CONNECTORS.md](EXTERNAL_CLI_CONNECTORS.md) | Governed external CLI connectors, optional presets, named command allowlists, approvals, redaction, and audit behavior. |
| [plugins/payment.md](plugins/payment.md) | Native payment tool, virtual cards, machine payments, providers, and safe agent-facing actions. |
| [cli/payment.md](cli/payment.md) | `openclaw payment ...` gateway-backed CLI commands and safe output contract. |
| [cli/routing.md](cli/routing.md) | `openclaw routing ...` commands for onboarding, router modes, tier/provider mapping, and diagnostics. |
| [mempalace-memory.md](mempalace-memory.md) | Optional ElBruno.MempalaceNet memory provider and temporal KG tool. |
| [FRACTAL_MEMORY.md](FRACTAL_MEMORY.md) | Optional MCP-first Fractal Memory integration for compact structured project memory and Runtime Pulse context. |
| [providers/microsoft-extensions-ai.md](providers/microsoft-extensions-ai.md) | Optional JIT bridge for arbitrary `Microsoft.Extensions.AI.IChatClient` providers. |
| [SESSIONS.md](SESSIONS.md) | Session lifecycle, the `SessionManager`, `sessions_spawn` / `sessions_yield` / `sessions` tools, and per-turn token accounting semantics (turn/session/runtime/provider views). |
| [CANVAS_A2UI.md](CANVAS_A2UI.md) | Supported Canvas and A2UI behavior for agent-rendered visual workspaces. |
| [integrations/microsoft-agent-framework.md](integrations/microsoft-agent-framework.md) | Supported optional Microsoft Agent Framework runtime adapter, runtime selection, A2A setup, and migration from old experimental config. |
| [workflow-backends.md](workflow-backends.md) | Durable workflow delegation, `maf-durable-http`, integration API and MCP tools, status model, and sample host. |
| [a2a.md](a2a.md) | A2A v1 discovery, endpoint, authentication, and deployment contract through the Microsoft Agent Framework adapter. |
| [MODEL_PROFILES.md](MODEL_PROFILES.md) | Provider-agnostic named model profiles, including Gemma-family setups. |
| [PROMPT_CACHING.md](PROMPT_CACHING.md) | Provider-aware prompt caching hints, dialects, diagnostics. |
| [PULSE.md](PULSE.md) | Runtime Pulse scheduled heartbeat turns, `HEARTBEAT.md`, alert suppression, and operator controls. |
| [LEARNING.md](LEARNING.md) | Review-first learning proposals, automation suggestion quality gates, feedback events, and harness regression relationships. |
| [HARNESS_CONTRACTS.md](HARNESS_CONTRACTS.md) | Passive, inspectable Harness Contract records for future plan-execute-verify and evidence workflows. |
| [EVIDENCE_BUNDLES.md](EVIDENCE_BUNDLES.md) | Passive evidence records for what happened, what was checked, remaining uncertainty, and operator trust. |
| [PLAN_EXECUTE_VERIFY.md](PLAN_EXECUTE_VERIFY.md) | Optional governed Plan-Execute-Verify mode for high-risk tool execution. |
| [GOVERNANCE_LEDGER.md](GOVERNANCE_LEDGER.md) | Passive approval and oversight decision history as durable harness state. |
| [HARNESS_REGRESSION.md](HARNESS_REGRESSION.md) | Offline-first CLI regression checks for harness safety, onboarding, memory, provider-shape, MCP, OpenAI-compatible, and serialization guarantees. |
| [HARNESS_EVOLUTION.md](HARNESS_EVOLUTION.md) | Review-first proposals for harness changes to policies, routing, memory retrieval, verification, pulse behavior, and tool governance. |
| [SHARED_HARNESS_STATE.md](SHARED_HARNESS_STATE.md) | Passive shared state for delegated and future multi-agent workflows, including participants, actions, read/write sets, assumptions, verifier obligations, and conflicts. |
| [CODEBASE_HARNESS_MAP.md](CODEBASE_HARNESS_MAP.md) | Passive static repository maps for agent harness environments, including projects, modules, endpoints, tools, providers, channels, config surfaces, tests, and diagnostics. |
| [governance/sidecar-pattern.md](governance/sidecar-pattern.md) | Optional central tool-governance middleware, sidecar flow, decisions, and audit fields. |
| [governance/microsoft-agent-governance.md](governance/microsoft-agent-governance.md) | Microsoft Agent Governance sidecar integration notes and deployment cautions. |
| [deployment/TAILSCALE.md](deployment/TAILSCALE.md) | Optional Tailscale Serve private runtime access guidance. |

## Testing and Evaluation

| Doc | What it covers |
| --- | --- |
| [testing/agent-testing-harness.md](testing/agent-testing-harness.md) | Scenario-based agent tests, trace artifacts, explicit oracles, CLI usage, xUnit usage, and future runtime/gateway adapter seams. |
| [testing/ai-assisted-testing-playbook.md](testing/ai-assisted-testing-playbook.md) | Disciplined AI-assisted testing workflow: scenario matrices, oracle requirements, boundary cases, human review, and trace-to-regression loops. |
| [testing/routing-evaluation.md](testing/routing-evaluation.md) | Offline dynamic routing baseline comparison, sample schema, and regression gate for `alwaysT2`, `ruleOnly`, and `classifierPlusRules`. |
| [HARNESS_REGRESSION.md](HARNESS_REGRESSION.md) | `openclaw harness test` for offline regression checks before trusting harness/runtime changes. |
| [MODEL_PROFILES.md#evaluation-harness](MODEL_PROFILES.md#evaluation-harness) | Existing gateway-backed model/profile evaluation surface exposed by `openclaw eval`. |

## Channels and Integrations

| Doc | What it covers |
| --- | --- |
| [TEAMS_SETUP.md](TEAMS_SETUP.md) | Microsoft Teams channel setup. |
| [WHATSAPP_SETUP.md](WHATSAPP_SETUP.md) | First-party WhatsApp integration (Baileys and whatsmeow workers). |
| [SEMANTIC_KERNEL.md](SEMANTIC_KERNEL.md) | Semantic Kernel interop surface. |
| [external-coding-backends.md](external-coding-backends.md) | External coding backend integration. |
| [external-coding-backends-summary.md](external-coding-backends-summary.md) | Summary of supported OSS external coding backends. |

## Operating It

| Doc | What it covers |
| --- | --- |
| [../SECURITY.md](../SECURITY.md) | Security posture, required settings before a public deployment, and breaking-change credential rules. |
| [security/payments.md](security/payments.md) | Payment approvals, vaulting, redaction, sentinel substitution, and provider security invariants. |
| [COMPATIBILITY.md](COMPATIBILITY.md) | Supported upstream skill, plugin, and channel surface. |
| [sandboxing.md](sandboxing.md) | Optional sandbox execution backends. |
| [DOCKERHUB.md](DOCKERHUB.md) | Official container image reference. |
| [build/dashboard-assets.md](build/dashboard-assets.md) | Gateway Dashboard asset build and publish behavior. |
| [deployment/TAILSCALE.md](deployment/TAILSCALE.md) | Tailscale Serve/Funnel patterns and private runtime access security notes. |
| [PRODUCTION_FIXES.md](PRODUCTION_FIXES.md) | Known production-readiness fixes and their verification. |

## Extending It

| Doc | What it covers |
| --- | --- |
| [../CONTRIBUTING.md](../CONTRIBUTING.md) | Build, test, and PR expectations for contributors. |
| [project/governance.md](project/governance.md) | Project governance principles, role boundaries, commercial-use disclosure, vendor neutrality, and Industrial Pack scope. |
| [project/maintainers.md](project/maintainers.md) | Maintainer role definitions, area maintainer scope, review expectations, and approval boundaries. |
| [project/sponsors.md](project/sponsors.md) | Sponsorship support, limits, commercial-use guidance, and sponsor listing policy. |
| [project/branch-protection.md](project/branch-protection.md) | Recommended `main` branch protection, required checks, and CODEOWNERS relationship. |
| [maintainers/review-checklist.md](maintainers/review-checklist.md) | Maintainer checklist for runtime, gateway, extension, industrial, documentation, security, AOT, and commercial review. |
| [ai-contributor-guide.md](ai-contributor-guide.md) | Guidance for AI-assisted contribution workflows. |
| [architecture-startup-refactor.md](architecture-startup-refactor.md) | Current gateway startup layout and composition seams. |
| [architecture/optional-dependency-split.md](architecture/optional-dependency-split.md) | Optional dependency split guidance and follow-up seams. |
| [proposals/industrial-pack-preview.md](proposals/industrial-pack-preview.md) | Proposal for a reusable, vendor-neutral Industrial Pack preview. |
| [ROADMAP.md](ROADMAP.md) | Planned direction and priorities. |

## Research and Experiments

The [experiments/](experiments/) directory holds time-stamped findings from specific investigations. These are frozen snapshots, not living documentation — read them when you are tracing the history of a decision (most are about the Microsoft Agent Framework AOT/JIT work).

## Doc Conventions

- Commands assume a source checkout. Replace `openclaw ...` with `dotnet run --project src/OpenClaw.Cli -c Release -- ...` when running from source.
- The supported config path is an external JSON file generated by `openclaw setup`, typically at `~/.openclaw/config/openclaw.settings.json`.
- "Posture" refers to the combined security/deployment checks surfaced by `--doctor`, `openclaw admin posture`, and `openclaw setup status`. When a doc says "check your posture", run those three.

## Missing Something?

If a doc does not answer the question you came with, that is a bug. Open a documentation issue with the exact question and the doc you read, or link to this page in a PR that adds the missing piece. See the "Getting help" section at the end of [GETTING_STARTED.md](GETTING_STARTED.md#getting-help) for the full loop.

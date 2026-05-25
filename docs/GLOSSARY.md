# Glossary

A single place for the terms that show up across OpenClaw.NET docs. If a term in another doc is unfamiliar, check here first.

## Runtime Shape

**Gateway** — the ASP.NET host in `src/OpenClaw.Gateway`. Terminates HTTP and WebSocket traffic, serves `/chat`, `/admin`, `/mcp`, webhooks, and diagnostics, applies auth and policy, and hands requests to the runtime. This is the long-running server process.

**Runtime** — the agent runtime in `src/OpenClaw.Agent`. Runs a turn: prompt assembly, model call, tool selection, tool execution, retries, delegation, approvals, and final response. The gateway is the host; the runtime is the agent loop inside it.

**Core** — shared infrastructure in `src/OpenClaw.Core`: configuration binding, sessions, memory, security, observability, plugin metadata, validation. Consumed by both the gateway and the runtime.

**Companion** — the desktop operator app in `src/OpenClaw.Companion`. A local client against the gateway, useful for interactive operator workflows.

**TUI** — the terminal UI in `src/OpenClaw.Tui`. Same idea as Companion, different surface.

**CLI** — the `openclaw` command in `src/OpenClaw.Cli`. Entrypoint for `setup`, `setup launch`, `setup status`, `admin posture`, `chat`, `run`, `migrate`, `plugins`, `skills`, and related helpers.

## Execution Lanes

**`aot`** — Ahead-of-time, trim-safe runtime lane. Narrower plugin surface (native tools and mainstream bridge capabilities only). Use when you want low memory, small binaries, and no dynamic code.

**`jit`** — Just-in-time runtime lane. Full plugin surface including `registerChannel()`, `registerCommand()`, `registerProvider()`, `api.on(...)`, and dynamic in-process .NET plugins. Use when you need plugin features that `aot` intentionally excludes.

**`auto`** — Picks `jit` when dynamic code is available at runtime, `aot` otherwise. Reasonable default unless you know you need one lane.

## Tools, Plugins, Skills

**Tool** — a function the runtime can call during a turn (file ops, shell, web search, memory, channels, and so on). Native tools live in `src/OpenClaw.Agent`. Approvals, timeouts, and usage tracking apply uniformly.

**Plugin** — an extension loaded into the runtime. Two kinds: native dynamic .NET plugins (jit only) and TS/JS bridge plugins from the upstream OpenClaw ecosystem. Install and inspect with `openclaw plugins install`.

**Skill** — a packaged `SKILL.md` capability. Installable with `openclaw skills install`. Skills can be authored by a human or proposed by the review-first learning loop as a `skill_draft`.

**Bridge** — the in-process adapter layer that lets TS/JS upstream plugins run against the .NET runtime without the runtime depending on a specific plugin host.

## Identity and Access

**Bootstrap token** — the value of `OPENCLAW_AUTH_TOKEN`. Used once on a non-loopback deployment to create the first operator account, and retained as the breakglass credential if the operator account store is unreachable. It is not the recommended day-to-day credential.

**Breakglass credential** — same token, different role: the fallback admission path when account-based auth cannot be used (for example, during recovery). Treat it as privileged.

**Operator account** — the normal named login for the admin UI. Preferred for browser sessions and for issuing operator account tokens consumed by Companion, CLI, API, and WebSocket clients.

**Operator account token** — a token minted from an operator account, used by Companion, CLI automation, API clients, and WebSocket integrations. Replaces day-to-day use of the bootstrap token.

## Deployment Posture

**Posture** — the combined security and deployment state as seen by the running gateway: bind address class, forwarded-header trust, approval policy, tool restrictions, plugin trust, channel validation, and related checks. When a doc says "check your posture", run the three below.

**`--doctor`** — on the gateway process. Runs the onboarding diagnostic against a config file without staying up.

**`openclaw admin posture`** — against a running gateway. Validates the live security posture as the gateway sees it.

**`openclaw setup status`** — against a config file. Summarizes the generated artifacts and what the next steps are.

**Profile (`local` / `public`)** — a setup preset. `local` defaults to loopback bind and permissive tool defaults. `public` defaults to `0.0.0.0`, trusts forwarded headers, enables requester-matched HTTP approvals, disables shell, and disables bridge plugins until explicitly opted in.

## Sessions, Memory, Profiles, Automations

**Session** — one persistent conversation with its own history, todo state, and memory. Resolved per actor and route.

**Memory** — project-scoped notes and facts the agent can read and write. Separate from session history. Inspectable and editable from the admin memory console.

**User profile** — stable facts, preferences, projects, tone, and recent intent about a specific user. Read and written through `profile_read` / `profile_write` tools. Exportable between deployments.

**Model profile** — a provider-agnostic named model configuration (provider, model id, capabilities, tags). Routes requests to the right model without hard-coding provider-specific paths. Gemma-family setups are typically defined as model profiles.

**Automation** — a saved task that runs on demand or on a cron schedule. Supports list, get, preview, create, update, pause, resume, run.

**Learning proposal** — a pending change the runtime suggests after observing successful sessions. Kinds include `profile_update`, `automation_suggestion`, `skill_draft`, and review-first `harness_change` proposals. Operators approve, reject, or roll back supported proposal types. Nothing mutates behavior until approved, and `harness_change` approvals are manual-only in the current implementation. See [LEARNING.md](LEARNING.md).

**Harness Evolution Proposal** — a `harness_change` learning proposal for review-first improvements to harness behavior such as memory retrieval, routing, verification, tool governance, approvals, pulse behavior, context budgets, or security policy.

## Channels and Surfaces

**Channel** — an inbound adapter that turns an external messaging surface (Telegram, Slack, Discord, Teams, SMS, email, WhatsApp, Signal, webhooks) into a request the gateway can route. Channels carry their own signature validation, DM policy, and allowlists.

**Route** — the mapping from an actor and channel to a specific model profile, prompt, tool preset, and tool allowlist. The primary way multi-agent behavior is configured without branching the runtime.

**MCP** — the Model Context Protocol endpoint exposed at `/mcp`. Lets external MCP clients call the gateway's tools.

**Integration API** — the typed HTTP surface under `/api/integration/*`. Use `OpenClaw.Client` for typed .NET access.

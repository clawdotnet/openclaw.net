# Roadmap

## Recently Completed

- **Channel expansion**: Discord (Gateway WebSocket + interaction webhook), Slack (Events API + slash commands), Signal (signald/signal-cli bridge) channel adapters with DM policy, allowlists, thread-to-session mapping, and signature validation.
- **Tool expansion** (34 → 48 native tools): edit_file, apply_patch, message, x_search, memory_get, sessions_history, sessions_send, sessions_spawn, session_status, sessions_yield, agents_list, cron, gateway, profile_write.
- **Tool presets and groups**: 4 new built-in presets (full, coding, messaging, minimal) and 7 built-in tool groups (group:runtime, group:fs, group:sessions, group:memory, group:web, group:automation, group:messaging).
- **Chat commands**: /think (reasoning effort), /compact (history compaction), /verbose (tool call/token output).
- **Multi-agent routing**: per-channel/sender routing with model override, route-scoped prompt instructions, tool presets, and tool allowlist restrictions.
- **Integrations**: Tailscale Serve/Funnel, Gmail Pub/Sub event bridge, mDNS/Bonjour service discovery.
- **Plugin installer**: built-in `openclaw plugins install/remove/list/search` for npm/ClawHub packages.
- Security audit closure for plugin IPC hardening, plugin-root containment, browser cancellation recovery, strict session-cap admission, and session-lock disposal.
- Admin/operator tooling:
  - posture diagnostics
  - approval policy simulation
  - redacted incident export
- Expanded observability for approval decisions, session evictions/cap rejects, browser cancellation resets, plugin bridge auth/restart behavior, and sandbox lease lifecycle.
- Optional estimated token admission control.
- Startup/runtime composition split into explicit service, channel, plugin, and runtime assembly stages.
- Optional native Notion scratchpad integration with scoped read/write tools (`notion`, `notion_write`), allowlists, and write approvals by default.
- Canvas and A2UI v1 visual workspace with session-scoped websocket command broker, webchat Canvas host, Companion native Canvas tab, A2UI v0.8 JSONL renderer, event feedback, snapshots, and public-bind hardening.

## Runtime and Platform Expansion

These are strong candidates for the next roadmap phases because they extend the current runtime, channel, and operator model without fighting the existing architecture.

### Multimodal and Input Expansion

4. **Voice memo transcription**
   - Detect inbound audio across supported channels and route it through a transcription provider.
   - Inject transcript text into the runtime before the normal agent turn starts.
   - Provide clear degraded behavior when transcription is disabled or unavailable.

5. **Checkpoint and resume for long-running tasks**
   - Persist structured save points during multi-step execution.
   - Allow interrupted or restarted sessions to resume from the last completed checkpoint.
   - Start with checkpointing after successful tool batches instead of trying to snapshot every internal runtime state transition.

6. **Mixture-of-agents execution**
   - Fan out a prompt to multiple providers and synthesize a final answer from their outputs.
   - Expose this as an optional high-cost/high-confidence runtime mode or explicit tool.
   - Keep it profile-driven so it can be limited to selected models and use cases.

### Execution and Deployment Options

7. **Daytona execution backend**
   - Add a remote workspace backend with hibernation and resume support.
   - Fit it into the existing `IExecutionBackend` and process execution model rather than adding a separate tool path.
   - Useful for persistent remote development-style sandboxes.

8. **Modal execution backend**
   - Add a serverless execution backend for short-lived compute-heavy tasks.
   - Focus on one-shot and bounded process execution first.
   - Treat GPU-enabled workloads as an optional extension once the base backend is stable.

### Operator Visibility and Safety

9. **CLI/TUI insights**
   - Add an `openclaw insights` command and matching TUI panel.
   - Summarize provider usage, token spend, tool frequency, and session counts from existing telemetry.
   - Prefer operator-readable summaries over introducing a new analytics subsystem.

10. **URL safety validation**
   - Add SSRF-oriented URL validation in web fetch and browser tooling.
   - Block loopback/private targets by default and allow optional blocklists.
   - Keep this configurable, but make the safe path easy to enable globally.

11. **Trajectory export**
   - Export prompts, tool calls, results, and responses as JSONL for analysis or training pipelines.
   - Support date-range or session-scoped export plus optional anonymization.
   - Expose it through admin and CLI surfaces instead of burying it in storage internals.

## Security Hardening (Likely Breaking)

These are worthwhile changes, but they can break existing deployments or require new configuration.
Recommend implementing behind flags first, then enabling by default in a major release.

1. **Require auth on loopback for control/admin surfaces**
   - Scope: `/ws`, `/v1/*`, `/allowlists/*`, `/tools/approve`, `/webhooks/*`
   - Goal: reduce “local process / local browser” attack surface.

2. **Default allowlist semantics to `strict`**
   - Current: `legacy` makes empty allowlist behave as allow-all for some channels.
   - Target: `strict` should be the default for safer out-of-the-box behavior.

3. **Encrypt Companion token storage**
   - Store the auth token using OS-provided secure storage (Keychain/DPAPI/etc).
   - Include migration from existing plaintext settings.

4. **Default Telegram webhook signature validation to `true`**
   - Requires `WebhookSecretToken`/`WebhookSecretTokenRef` to be configured.
   - Improves default webhook authenticity guarantees.

## Semantic Kernel Interop (Non-Breaking, Optional)

Goal: make it straightforward to run `Microsoft.SemanticKernel` code behind the OpenClaw gateway/runtime while keeping SK integration **optional** (so the core stays NativeAOT-friendly).

Principles:
1. Ship SK support as a separate package (no SK dependency in the core runtime).
2. Treat SK execution as "just another tool" so OpenClaw policies (auth, rate limits, tool approval, tracing) still govern it.
3. Prefer stable SK surfaces (Kernel + Functions/Plugins) and avoid betting on planners in the first iterations.

### Phase 0 (Done): Documentation
- README section describing supported integration patterns (wrap SK as a tool; host SK behind the gateway).

### Phase 1 (Done): Minimal Adapter Package + Sample (High ROI)
- Add a new optional NuGet package (tentative): `OpenClaw.SemanticKernelAdapter`.
- Provide `IServiceCollection` extensions to register an SK-backed tool.
- Define a small, explicit request/response contract:
  - Identify SK function by `(plugin, function)` or a single "entrypoint" function name.
  - Pass args as JSON object; return JSON result + optional text.
- Add a working sample (recommended location): `samples/SemanticKernelInterop/`
  - Demonstrate: OpenClaw tool call -> SK function -> result -> returned via `/v1/responses`
  - Include OpenTelemetry correlation (same trace/span across gateway -> tool -> SK call).

### Phase 2 (Done): "Load SK Plugins as Tools" (Selective Mapping)
- Optional startup mapping:
  - Load a configured set of SK plugins/functions and expose each as an OpenClaw tool.
  - Preserve OpenClaw tool naming rules and add predictable name mapping (e.g. `sk.<plugin>.<function>`).
- Enforce governance:
  - Per-tool allow/deny lists and per-tool rate limits (in OpenClaw config, not inside SK).
  - Explicit secrets boundary: SK connectors should use the same secret ref system (`env:`, etc).

### Phase 3 (Done): Streaming + Observability Polish
- If SK invocation supports streaming in your chosen integration surface:
  - Surface streaming responses through OpenClaw without bypassing message/token accounting.
- Bridge OTEL activities:
  - Tag tool spans with `sk.plugin`, `sk.function`, duration, and error metadata.
  - Ensure errors propagate as structured tool failures (not raw exceptions).

### Phase 4 (Done): NativeAOT/Trimming Guidance (Documentation + Constraints)
- Document a supported/known-good configuration:
  - Which SK features are compatible with trimming/AOT and which are not.
- Add sample trimming config / annotations if required (only in the adapter/sample).

Non-goals (initially):
- Re-implement Semantic Kernel planners inside OpenClaw.
- Promise "drop-in" compatibility for every SK connector/plugin without validation.

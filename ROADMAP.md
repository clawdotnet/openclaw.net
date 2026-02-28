# Roadmap

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

### Phase 1: Minimal Adapter Package + Sample (High ROI)
- Add a new optional NuGet package (tentative): `OpenClaw.SemanticKernelAdapter`.
- Provide `IServiceCollection` extensions to register an SK-backed tool.
- Define a small, explicit request/response contract:
  - Identify SK function by `(plugin, function)` or a single "entrypoint" function name.
  - Pass args as JSON object; return JSON result + optional text.
- Add a working sample (recommended location): `samples/SemanticKernelInterop/`
  - Demonstrate: OpenClaw tool call -> SK function -> result -> returned via `/v1/responses`
  - Include OpenTelemetry correlation (same trace/span across gateway -> tool -> SK call).

### Phase 2: "Load SK Plugins as Tools" (Selective Mapping)
- Optional startup mapping:
  - Load a configured set of SK plugins/functions and expose each as an OpenClaw tool.
  - Preserve OpenClaw tool naming rules and add predictable name mapping (e.g. `sk.<plugin>.<function>`).
- Enforce governance:
  - Per-tool allow/deny lists and per-tool rate limits (in OpenClaw config, not inside SK).
  - Explicit secrets boundary: SK connectors should use the same secret ref system (`env:`, etc).

### Phase 3: Streaming + Observability Polish
- If SK invocation supports streaming in your chosen integration surface:
  - Surface streaming responses through OpenClaw without bypassing message/token accounting.
- Bridge OTEL activities:
  - Tag tool spans with `sk.plugin`, `sk.function`, duration, and error metadata.
  - Ensure errors propagate as structured tool failures (not raw exceptions).

### Phase 4: NativeAOT/Trimming Guidance (Documentation + Constraints)
- Document a supported/known-good configuration:
  - Which SK features are compatible with trimming/AOT and which are not.
- Add sample trimming config / annotations if required (only in the adapter/sample).

Non-goals (initially):
- Re-implement Semantic Kernel planners inside OpenClaw.
- Promise "drop-in" compatibility for every SK connector/plugin without validation.

# Gateway Startup Refactor

## Summary

The gateway startup path is now split into three layers:

1. `Bootstrap/`
   - Loads config overrides.
   - Binds the canonical `GatewayConfig`.
   - Applies environment overrides and explicit secret resolution.
   - Resolves `GatewayRuntimeState`.
   - Handles early exits for `--health-check` and `--doctor`.
   - Enforces non-loopback auth-token requirements and public-bind hardening.

2. `Composition/` and `Profiles/`
   - Registers pre-build services in grouped extension methods.
   - Preserves the existing Core/Agent runtime model as the single source of truth for AOT vs JIT.
   - Builds runtime-only objects after `builder.Build()` in `InitializeOpenClawRuntimeAsync(...)`.
   - Runtime assembly is now staged as:
     - service resolution
     - channel/webhook composition
     - plugin/runtime extension loading
     - agent runtime assembly
     - final `GatewayAppRuntime` construction
   - Keeps plugin loading, provider registration, skill loading, hooks, and worker startup order intact.

3. `Pipeline/` and `Endpoints/`
   - Applies forwarded headers, CORS, WebSockets, worker startup, channel startup, and shutdown handling.
   - Maps all HTTP/WebSocket routes outside `Program.cs`.

## AOT vs JIT

Runtime mode selection still comes from `GatewayConfig.Runtime` and `RuntimeModeResolver`.

- `auto` resolves to `jit` when dynamic code is supported and `aot` otherwise.
- `aot` keeps the trim-safe, mainstream bridge lane.
- `jit` keeps the expanded compatibility lane, including native dynamic plugins.

The gateway profile layer does not replace plugin/runtime capability enforcement. Existing fail-fast blocking and `/doctor` diagnostics still come from the core runtime/plugin subsystems.

## Where Things Moved

- `Program.cs`
  - now only orchestrates bootstrap, service registration, runtime initialization, pipeline mapping, endpoint mapping, and `Run(...)`
- `Bootstrap/GatewayBootstrapExtensions.cs`
  - config loading, validation, runtime-state resolution, and early exits
- `Composition/*`
  - grouped DI registration and post-build runtime assembly
- `Profiles/*`
  - thin gateway composition hooks keyed off the already-resolved effective runtime mode
- `Pipeline/PipelineExtensions.cs`
  - middleware, channel startup, workers, and shutdown
- `Endpoints/*`
  - grouped route mapping by diagnostics, OpenAI surface, UI/control, WebSocket, and webhook boundaries

## Adding New Startup Behavior

- Add config/bootstrap rules in `Bootstrap/` when behavior must happen before `builder.Build()`.
- Add DI-friendly services in the appropriate `Composition/*ServicesExtensions.cs` file.
- Add runtime-only initialization in the relevant `InitializeOpenClawRuntimeAsync(...)` stage helper if it depends on built services, app lifetime, plugin loading, or runtime ordering.
- Add new route handlers in the relevant `Endpoints/*` module and register them through `MapOpenClawEndpoints(...)`.
- Add profile-specific gateway composition only in `Profiles/*`; do not duplicate runtime-mode or plugin capability policy there.

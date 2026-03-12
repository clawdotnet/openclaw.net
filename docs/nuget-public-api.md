# NuGet Public API Guidance

This repo currently exposes a broad set of `public` types, but the first NuGet release should keep its *supported* API surface narrower than the implementation surface.

The goal for `0.1.x` should be:

- keep the package contract small,
- treat gateway/runtime internals as implementation details,
- and avoid promising long-term compatibility for transport- or storage-specific types too early.

## Planned NuGet Packages

- `OpenClaw.Core`
- `OpenClaw.PluginKit`
- `OpenClaw.SemanticKernelAdapter`

## Recommended Supported API For `0.1.x`

### `OpenClaw.Core`

These are the most defensible public contracts to support in the initial package line:

- `OpenClaw.Core.Abstractions`
  - `ITool`
  - `IStreamingTool`
  - `IToolHook`
  - `IToolHookWithContext`
  - `ToolHookContext`
  - `IMemoryStore`
  - `IMemoryNoteSearch`
  - `IMemoryRetentionStore`
  - `ISessionAdminStore`
  - `IChannelAdapter`
- Configuration and validation used by hosts/integrators
  - `GatewayConfig`
  - nested config types that are directly part of host configuration
  - `ConfigValidator`
  - `DoctorCheck`
  - `RuntimeConfig`
  - `GatewayRuntimeState`
  - `RuntimeModeResolver`
- Core domain models likely to be shared across integrations
  - `Session`
  - `ChatTurn`
  - `ToolInvocation`
  - `InboundMessage`
  - `OutboundMessage`
  - `AgentProfile`
  - `DelegationConfig`
- Middleware/pipeline contracts
  - `MessageContext`
  - `IMessageMiddleware`
  - `MiddlewarePipeline`

### `OpenClaw.PluginKit`

Treat the whole package as supported:

- `INativeDynamicPlugin`
- `INativeDynamicPluginContext`
- `INativeDynamicPluginService`

This package is small and already reads like a deliberate plugin contract.

### `OpenClaw.SemanticKernelAdapter`

These are reasonable to support from the start:

- `SemanticKernelAdapterExtensions`
- `SemanticKernelToolFactory`
- `SemanticKernelInteropOptions`
- `SemanticKernelToolMappingOptions`
- `SemanticKernelPolicyOptions`

These concrete tool/hook classes can remain public, but should be documented as advanced usage:

- `SemanticKernelFunctionTool`
- `SemanticKernelEntrypointTool`
- `SemanticKernelPolicyHook`

## Public But Should Be Treated As Internal For Now

These types are currently public, but should not be documented as stable NuGet contract until they are intentionally curated:

- `OpenClaw.Core.Plugins.*`
  - bridge transport shapes
  - plugin manifest/load/discovery internals
  - capability negotiation details
- `OpenClaw.Core.Models.OpenAi*`
  - provider/wire-format DTOs are likely to evolve
- `OpenClaw.Core.Models.Ws*`
  - websocket envelope contracts may change with protocol revisions
- `OpenClaw.Core.Security.*`
  - operational/runtime helpers rather than reusable SDK contract
- `OpenClaw.Core.Observability.*`
  - metrics/telemetry helpers are likely to move as observability evolves
- `OpenClaw.Core.Memory.FileMemoryStore`
- `OpenClaw.Core.Memory.SqliteMemoryStore`
- `OpenClaw.Core.Sessions.SessionManager`
- `OpenClaw.Core.Pipeline.*`
  - scheduler, sender cache, command processor, approval service
- `OpenClaw.Core.Contacts.*`
  - persistence helpers, not yet obviously stable SDK surface

## Versioning Guidance

For `0.1.x`, compatibility should be interpreted as:

- keep the supported types and member names stable,
- avoid breaking constructor signatures and option property names in documented APIs,
- allow refactoring of undocumented public types until the package surface is tightened.

If a future release wants stronger compatibility guarantees, the next step should be to:

1. mark unsupported public types as internal where possible,
2. split transport/runtime internals into non-packaged assemblies or internal namespaces,
3. add API approval or public API snapshot checks in CI.

## Practical Release Rule

When documenting NuGet usage, only show examples built on:

- `OpenClaw.Core.Abstractions.*`
- `OpenClaw.PluginKit.*`
- `OpenClaw.SemanticKernelAdapter.*`
- selected configuration/domain types from `OpenClaw.Core.Models`

If an example needs `Plugins.*`, `OpenAi*`, storage implementations, or runtime managers, that is a sign the API boundary is still too leaky for a stable package contract.

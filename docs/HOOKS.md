# Tool Hooks

Tool hooks intercept tool execution in the agent runtime loop — before and after
every tool call — enabling auditing, policy enforcement, autonomy gating,
contract scope limiting, and external plugin integration.

---

## Architecture Overview

```text
┌──────────────────────────────────────────────────────────────┐
│                    Gateway Startup                            │
│  CreateHooks()                                               │
│  ┌──────────────────────────────────────────────────────┐    │
│  │ Always: AuditLogHook, AutonomyHook, ContractScopeHook │    │
│  │ + pluginHost.ToolHooks (BridgedToolHook instances)    │    │
│  │ + nativeDynamicPluginHost.ToolHooks (custom IToolHook)│    │
│  └──────────────────────┬───────────────────────────────┘    │
└────────────────────────┬──────────────────────────────────────┘
                         │ IReadOnlyList<IToolHook>
                         ▼
┌──────────────────────────────────────────────────────────────┐
│                   AgentRuntime                                │
│  _hooks → OpenClawToolExecutor(_hooks, ...)                  │
└────────────────────────┬──────────────────────────────────────┘
                         │
                         ▼
┌──────────────────────────────────────────────────────────────┐
│              OpenClawToolExecutor.ExecuteAsync()              │
│                                                              │
│  1. Build ToolHookContext (sessionId, channelId, ...)        │
│  2. BEFORE: foreach hook in _hooks:                          │
│     - IToolHookWithContext? → BeforeExecuteAsync(ctx)        │
│     - else → BeforeExecuteAsync(name, args)                  │
│     - Any hook returns false → hard-deny                     │
│  3. Execute actual tool                                      │
│  4. AFTER: foreach hook in _hooks:                           │
│     - AfterExecuteAsync(...), exception-safe                 │
└──────────────────────────────────────────────────────────────┘
```

**Key design principles:**

- **No centralized registry** — hooks are a flat `IReadOnlyList<IToolHook>`.
- **Breadth-first execution** — every hook runs on every tool call in both before and after phases.
- **Hard-deny** — any single hook returning `false` in `BeforeExecuteAsync` skips the tool and returns the denial message to the LLM.
- **After-hook fail-safe** — exceptions in after-hooks are caught and logged; they never propagate to the caller.
- **Bridge timeout** — plugin bridge hooks enforce a 5-second timeout on `BeforeExecuteAsync` to prevent stalled plugins blocking the agent loop.

---

## Core Interfaces

### `IToolHook`

Defined in `src/OpenClaw.Core/Abstractions/IToolHook.cs`:

```csharp
public interface IToolHook
{
    string Name { get; }

    ValueTask<bool> BeforeExecuteAsync(
        string toolName,
        string arguments,
        CancellationToken ct);

    ValueTask AfterExecuteAsync(
        string toolName,
        string arguments,
        string result,
        TimeSpan duration,
        bool failed,
        CancellationToken ct);
}
```

| Member | Phase | Return | Behavior |
|--------|-------|--------|----------|
| `Name` | — | `string` | Human-readable identifier for logging. |
| `BeforeExecuteAsync` | Before | `ValueTask<bool>` | Return `false` to deny execution. LLM receives: `"Tool execution denied by hook: {hookName}"`. |
| `AfterExecuteAsync` | After | `ValueTask` | Called after every tool execution, even if the tool failed. Fire-and-forget; exceptions are caught. |

### `IToolHookWithContext`

Defined in `src/OpenClaw.Core/Abstractions/IToolHookWithContext.cs`:

Extends `IToolHook` with overloads that receive a `ToolHookContext` for hooks that need session, channel, or sender metadata:

```csharp
public interface IToolHookWithContext : IToolHook
{
    ValueTask<bool> BeforeExecuteAsync(
        ToolHookContext context,
        CancellationToken ct);

    ValueTask AfterExecuteAsync(
        ToolHookContext context,
        string result,
        TimeSpan duration,
        bool failed,
        CancellationToken ct);
}
```

### `ToolHookContext`

Defined in `src/OpenClaw.Core/Abstractions/ToolHookContext.cs`:

```csharp
public readonly record struct ToolHookContext(
    string SessionId,
    string ChannelId,
    string SenderId,
    string CorrelationId,
    string ToolName,
    string ArgumentsJson,
    bool IsStreaming);
```

`OpenClawToolExecutor` builds this context before each hook phase. Hooks implementing `IToolHookWithContext` receive it directly; plain `IToolHook` hooks receive just `toolName` and `arguments`.

---

## Built-in Hooks

Three hooks are always registered at gateway startup. They run on every tool call, in order.

### `AuditLogHook`

Source: `src/OpenClaw.Agent/AuditLogHook.cs`

Records every tool execution for audit purposes. Implements `IToolHookWithContext`.

- `BeforeExecute` — always returns `true` (never denies).
- `AfterExecute` — logs success/failure, duration, result length, and session/channel/sender metadata.

**Purpose:** Observability and audit compliance. Every tool call leaves a trace.

### `AutonomyHook`

Source: `src/OpenClaw.Agent/AutonomyHook.cs`

Enforces autonomy modes as a hard-deny layer. Implements `IToolHook`.

| Policy | Config Key | Behavior |
|--------|-----------|----------|
| `AutonomyMode = "readonly"` | `ToolingConfig.AutonomyMode` | Denies write tools (`shell`, `write_file`, etc.). Read-only tools pass unchanged. |
| `WorkspaceOnly` | `ToolingConfig.WorkspaceOnly` | Ensures file system tool paths stay within the workspace root. |
| `ForbiddenPathGlobs` | `ToolingConfig.ForbiddenPathGlobs` | Denies file paths or shell commands matching any glob pattern. |
| `AllowShell` | `ToolingConfig.AllowShell` | When `false`, denies all shell tool calls. |
| `AllowedShellCommandGlobs` | `ToolingConfig.AllowedShellCommandGlobs` | When set, only shell commands matching a glob are permitted. |

**Purpose:** Safety gating. Prevents agents from running arbitrary commands or writing outside allowed paths.

### `ContractScopeHook`

Source: `src/OpenClaw.Agent/ContractScopeHook.cs`

Enforces contract-scoped limits on tool calls. Implements `IToolHookWithContext`.

| Policy | Config Key | Behavior |
|--------|-----------|----------|
| `MaxToolCalls` | `ContractPolicy.MaxToolCalls` | Tracks tool calls per session. Denies when the limit is exceeded. |
| `AllowedPaths` | `ContractPolicy.AllowedPaths` | Restricts file system tools to explicitly allowed path prefixes. |
| Scoped shell denial | — | Denies `shell` and `code_exec` tools entirely when running under a scoped contract. |

**Purpose:** Resource governance. Prevents runaway tool usage and path escape in scoped sessions.

---

## Plugin Hooks

External plugins (Node.js bridge or native .NET) can register custom hooks.

### Plugin Bridge (`BridgedToolHook`)

Source: `src/OpenClaw.Agent/Plugins/BridgedToolHook.cs`

Forwards hook events to out-of-process Node.js plugins via JSON-RPC.

#### Registration

Plugins declare event subscriptions in their manifest:

```json
{
  "eventSubscriptions": ["tool:before", "tool:after"]
}
```

The wildcard `"tool:*"` subscribes to both. When the bridge host detects these subscriptions, it creates a `BridgedToolHook` for that plugin.

#### Before Phase

1. Sends a `"hook_before"` JSON-RPC request with `{ EventName, ToolName, Arguments }`.
2. **5-second timeout** — if the plugin doesn't respond, the hook defaults to **allow** with a warning log.
3. Plugin handler returns `true`/`false`. A single `false` from any plugin denies the tool.

#### After Phase

1. Sends a `"hook_after"` JSON-RPC request with `{ EventName, ToolName, Arguments, Result, DurationMs, Failed }`.
2. One-way fire-and-forget — exceptions are silently caught.
3. Plugin handler return value is ignored.

#### JavaScript Plugin API (`api.on(...)`)

Source: `src/OpenClaw.Agent/Plugins/plugin-bridge.mjs`

```javascript
// tool:before — return false to deny
api.on("tool:before", (ctx) => {
  // ctx.toolName, ctx.arguments
  if (ctx.toolName === "shell") return false;
  return true;
});

// tool:after — return value ignored
api.on("tool:after", (ctx) => {
  // ctx.toolName, ctx.arguments, ctx.result, ctx.durationMs, ctx.failed
  console.log(`Tool ${ctx.toolName} completed in ${ctx.durationMs}ms`);
});
```

| Compatibility | Runtime | Notes |
|---------------|---------|-------|
| Supported with caveats | JIT only | AOT blocks native dynamic hooks but allows bridge hooks. 5-second timeout enforced. |

### Native Dynamic Plugins

Source: `src/OpenClaw.Agent/Plugins/NativeDynamicPluginHost.cs`

.NET plugins running in-process can register hooks directly:

```csharp
public class MyPlugin : INativeDynamicPlugin
{
    public void Register(INativeDynamicPluginContext context)
    {
        context.RegisterHook(new MyCustomHook());
    }
}
```

Hooks implement `IToolHook` and follow the same execution contract as built-in hooks.

| Compatibility | Runtime | Notes |
|---------------|---------|-------|
| JIT only | JIT | Blocked in AOT via `PluginCapabilityPolicy.Hooks`. |

---

## Adapter-Specific Hooks

### `SemanticKernelPolicyHook`

Source: `src/OpenClaw.SemanticKernelAdapter/SemanticKernelPolicyHook.cs`

Governs Semantic Kernel-related tools (prefixed with `sk_`). Implements `IToolHookWithContext`.

- Per-tool allow/deny via glob patterns.
- Per-sender, per-tool, per-minute rate limiting (in-memory, with periodic cleanup).
- Non-SK tools pass through unchanged.

---

## Hook Execution Order

Hooks execute in the order they appear in `IReadOnlyList<IToolHook>`, which is determined at gateway startup:

1. `AuditLogHook`
2. `AutonomyHook`
3. `ContractScopeHook`
4. Plugin bridge hooks (one per subscribing plugin, in registration order)
5. Native dynamic plugin hooks (in registration order)

All hooks run on every tool call. There is no priority system, no short-circuit beyond hard-deny, and no hook-specific tool gating at the hook level (hooks that only care about specific tools filter internally).

---

## Configuration

There is no standalone `HooksConfig` class. Hook behavior is configured through related config sections:

| Hook | Config Section | Key Fields |
|------|---------------|------------|
| `AutonomyHook` | `ToolingConfig` | `AutonomyMode`, `WorkspaceOnly`, `ForbiddenPathGlobs`, `AllowShell`, `AllowedShellCommandGlobs` |
| `ContractScopeHook` | `ContractPolicy` | `MaxToolCalls`, `AllowedPaths` |
| `SemanticKernelPolicyHook` | `SemanticKernelPolicyOptions` | Allow/deny globs, rate limits |
| Plugin hooks | `PluginsConfig` | Discovery, capability gating |

---

## Adding a Custom Hook

1. Implement `IToolHook` (or `IToolHookWithContext` for session-aware hooks):

```csharp
public sealed class MyPolicyHook : IToolHook
{
    public string Name => "my_policy";

    public ValueTask<bool> BeforeExecuteAsync(
        string toolName, string arguments, CancellationToken ct)
    {
        // Return false to deny.
        return ValueTask.FromResult(true);
    }

    public ValueTask AfterExecuteAsync(
        string toolName, string arguments, string result,
        TimeSpan duration, bool failed, CancellationToken ct)
    {
        // Track metrics, log, etc.
        return ValueTask.CompletedTask;
    }
}
```

2. Register it in `RuntimeInitializationExtensions.CreateHooks()`:

```csharp
private static IReadOnlyList<IToolHook> CreateHooks(...)
{
    var hooks = new List<IToolHook>
    {
        new AuditLogHook(logger),
        new AutonomyHook(toolingConfig),
        new ContractScopeHook(logger),
        new MyPolicyHook()  // ← add here
    };
    // ... plugin hooks appended ...
    return hooks;
}
```

Hook registration is code-driven — there is no configuration-file registration.

---

## Related Documentation

- [CAPABILITY_MATRIX.md](CAPABILITY_MATRIX.md) — AOT/JIT hook support matrix.
- [COMPATIBILITY.md](COMPATIBILITY.md) — `api.on(...)` compatibility notes.
- [ARCHITECTURE_BOUNDARIES.md](ARCHITECTURE_BOUNDARIES.md) — Where hooks sit in the architecture.
- [GOAL_TECHNICAL_ARCHITECTURE.md](GOAL_TECHNICAL_ARCHITECTURE.md) — Hook trigger modes.

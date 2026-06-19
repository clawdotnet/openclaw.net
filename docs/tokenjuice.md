# TokenJuice: Rule-Driven Tool Output Reduction

**Status:** Implemented | **Upstream:** [vincentkoc/tokenjuice](https://github.com/vincentkoc/tokenjuice) (MIT) | **Plugin:** `OpenClaw.Plugins.TokenJuice`

## Overview

TokenJuice is a deterministic, rule-driven output reduction engine for AI agent runtimes. It intercepts raw tool outputs (shell commands, build logs, HTTP responses, CLI tools) *before* they enter the LLM context window, compressing them by 50–95% while preserving core semantics, diagnostic signals, and exit codes.

Unlike LLM-based summarization (which costs tokens to save tokens), TokenJuice uses statically declared JSON rules matched against tool name, command line arguments, exit codes, and output content patterns. The reduction is instantaneous, deterministic, and zero-cost.

## Why TokenJuice

In autonomous agent workflows, every tool call result is appended to the conversation history and re-sent to the model on subsequent turns. Raw outputs often contain:

- ANSI escape sequences in terminal output
- Redundant build artifact listings (hundreds of `→ .dll` lines)
- Repeated status lines in polling loops
- Full HTML pages from `curl` calls
- Adjacent duplicate lines in log output

Without reduction, token consumption grows quadratically with turn count:

$$T_{\text{total}} \approx \sum_{i=1}^{N} \left( C_{\text{base}} + \sum_{j=1}^{i-1} O_j \right)$$

Where $O_j$ is the raw output from turn $j$. With TokenJuice, each $O_j$ is multiplied by a compression coefficient $\alpha \in [0.05, 0.5]$:

$$T'_{\text{total}} \approx \sum_{i=1}^{N} \left( C_{\text{base}} + \sum_{j=1}^{i-1} \alpha_j \cdot O_j \right)$$

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│                   OpenClawToolExecutor                   │
│                                                         │
│  Tool.Execute() → Redaction → Interceptor Pipeline      │
│                                    │                    │
│                           ┌────────▼──────────┐         │
│                           │ TokenJuiceInterceptor│       │
│                           │                        │     │
│                           │  ┌──────────────────┐ │     │
│                           │  │ Escape Hatch?    │ │     │
│                           │  │ (--raw / --full) │ │     │
│                           │  └──────┬───────────┘ │     │
│                           │         │              │     │
│                           │    ┌────▼─────────┐    │     │
│                           │    │ Rule Matching │    │     │
│                           │    └────┬─────────┘    │     │
│                           │         │              │     │
│                           │    ┌────▼─────────┐    │     │
│                           │    │  Match?       │    │     │
│                           │    │  Yes → Reduce │    │     │
│                           │    │  No → Density │    │     │
│                           │    │  Check        │    │     │
│                           │    └──────────────┘    │     │
│                           └────────────────────────┘     │
│                                    │                    │
│                           Interceptor Pipeline          │
│                                    │                    │
│                           → IToolHook.AfterExecute      │
│                           → Return to LLM context       │
└─────────────────────────────────────────────────────────┘
```

## Rule Structure

Each rule is a JSON document with the following blocks:

```json
{
  "id": "build/dotnet",
  "family": "build-dotnet",
  "description": "Compact dotnet build output while preserving diagnostics.",
  "match": {
    "toolNames": ["exec"],
    "argv0": ["dotnet"],
    "argvIncludesAny": [["build"], ["restore"], ["publish"]]
  },
  "transforms": {
    "stripAnsi": true,
    "dedupeAdjacent": true,
    "trimEmptyEdges": true
  },
  "summarize": {
    "head": 12,
    "tail": 12
  },
  "failure": {
    "preserveOnFailure": true,
    "head": 18,
    "tail": 18
  },
  "counters": [
    { "name": "error", "pattern": "error [A-Z]+\\d+|Build FAILED|failed", "flags": "i" },
    { "name": "warning", "pattern": "warning [A-Z]+\\d+|warning", "flags": "i" }
  ]
}
```

### Match Dimensions

| Field | Behavior |
|---|---|
| `toolNames` | Exact tool name match. `exec` matches shell/process tools. |
| `argv0` | First token of the command line (e.g. `dotnet`, `git`, `npm`). |
| `argvIncludes` | All specified flags must be present. |
| `argvIncludesAny` | Any one of the specified flag groups must be present. |
| `commandIncludes` | Command text contains all keywords (case-insensitive). |
| `commandIncludesAny` | Command text contains any keyword. |
| `commandRegex` | Full command text regex match. |
| `exitCodes` | Match specific exit codes. |
| `outputRegex` | Match content via regex. |

### Reduction Pipeline

```
Raw Output
  ↓
1. StripAnsi        — Remove terminal escape sequences (if transforms.stripAnsi)
  ↓
2. TrimEmptyEdges   — Remove leading/trailing blank lines (if transforms.trimEmptyEdges)
  ↓
3. DedupeAdjacent   — Collapse consecutive identical lines (if transforms.dedupeAdjacent)
  ↓
4. SkipPatterns     — Remove lines matching skip regex (if filters.skipPatterns)
  ↓
5. KeepPatterns     — Keep only lines matching keep regex (if filters.keepPatterns)
  ↓
6. OutputMatches    — If output matches a pattern, return a predefined message
  ↓
7. Counters         — Count error/warning occurrences by pattern
  ↓
8. HeadTail         — Keep first N + last M lines, insert "... omitted X lines ..."
  ↓
9. Inline Format    — Combine exit code + facts + summary into a single message
```

### Semantic Density Fallback

When no rule matches, a semantic density check acts as a safety net:

$$\rho = \frac{U}{\max(L, 1)} \cdot \frac{N}{\max(C, 1)}$$

Where $L$ = total lines, $U$ = unique lines, $C$ = total characters, $N$ = non-whitespace characters.

If $\rho < 0.3$ (configurable), the `generic/fallback` rule is applied automatically.

## Three-Layer Rule Configuration

| Layer | Path | Priority | Use Case |
|---|---|---|---|
| **Builtin** | Embedded assembly resources | Lowest | Default rules for common tools (git, dotnet, npm, docker, curl...) |
| **User** | `~/.config/tokenjuice/rules/*.json` | Medium | Personal preferences across all projects |
| **Project** | `.tokenjuice/rules/*.json` | Highest | Team standards, committed to Git |

Rules with the same `id` are overridden by higher-priority layers. The `generic/fallback` rule is always evaluated last.

## Escape Hatch

Two mechanisms prevent reduction when byte-exact output is required:

1. **Argument scanning:** If `argumentsJson` contains `--raw` or `--full`, reduction is skipped.
2. **Programmatic bypass:** Setting `ReductionContext.BypassReduction = true` in upstream code skips all interceptors.

Use case: `git diff` output where whitespace changes must be preserved exactly, cryptographic operations requiring precise output, or binary data.

## Performance

| Metric | Target |
|---|---|
| Per-call latency (100 KB input) | < 5 ms |
| Cold-start rule loading (129 rules) | < 50 ms |
| Memory overhead per reduction | < 1.2× input size |
| NativeAOT binary size increase | < 500 KB |

## Error Handling

**Fail-open by design.** If any component of the reduction pipeline throws:

- Malformed rule JSON → skipped, logged as warning
- Invalid regex pattern → that filter is skipped
- Empty reduction result → original output returned
- Reduction larger than original → original output returned
- Interceptor exception → caught, logged, original output returned

The pipeline never blocks tool output from reaching the LLM.

## Integration

TokenJuice is a system-level built-in plugin (`OpenClaw.Plugins.TokenJuice`) with a static factory class `TokenJuicePluginRegistration` that creates the interceptor instance. It implements `IToolResultInterceptor`, which runs in the `OpenClawToolExecutor` pipeline *after* redaction and *before* `IToolHook.AfterExecute`:

```csharp
// Tool execution pipeline order:
// 1. IToolHook.BeforeExecute  (approval, audit)
// 2. ITool.ExecuteAsync       (actual tool invocation)
// 3. IRedactionPipeline       (secret redaction)
// 4. IToolResultInterceptor   (output reduction — TokenJuice lives here)
// 5. IToolHook.AfterExecute   (observability, logging)
// 6. Return to LLM context
```

### Registration

TokenJuice no longer uses `INativeDynamicPlugin` dynamic loading (the `openclaw.native-plugin.json` manifest has been removed). Instead, it is explicitly created and injected into the interceptor pipeline at Gateway startup:

```csharp
// RuntimeInitializationExtensions.cs (Gateway startup flow)
var interceptors = new List<IToolResultInterceptor>
{
    TokenJuicePluginRegistration.CreateInterceptor()
};

// Passed through: CreateAgentRuntime → AgentRuntimeFactoryContext.Interceptors
// → AgentRuntime → OpenClawToolExecutor (constructor injection)
```

`TokenJuicePluginRegistration` is a static factory class following the `PaymentPluginRegistration` pattern:

```csharp
// TokenJuicePluginRegistration.cs
public static class TokenJuicePluginRegistration
{
    public static TokenJuiceInterceptor CreateInterceptor(
        IReadOnlyList<TokenJuiceRule>? rules = null,
        SemanticDensityCalculator? density = null,
        int? maxInlineChars = null)
    {
        var mergedRules = rules ?? RuleLoader.LoadMergedRules();
        return new TokenJuiceInterceptor(mergedRules, density, maxInlineChars);
    }
}
```

### Interceptor Data Flow

```
Gateway Startup
  └─ TokenJuicePluginRegistration.CreateInterceptor()
       └─ CreateAgentRuntime(..., interceptors)
            └─ AgentRuntimeFactoryContext.Interceptors
                 └─ AgentRuntime(..., interceptors)
                      └─ OpenClawToolExecutor(..., interceptors: interceptors)
                           └─ Auto-applies TokenJuice compression after tool execution
```

This design aligns TokenJuice with `OpenClaw.Plugins.Payment`: both are system-level built-in plugins, explicitly registered at Gateway startup, with no reliance on dynamic loading.

## Testing

129 builtin rules across 20+ families. 11 integration tests covering:

- Rule matching engine (9 match dimensions)
- Reduction strategies (StripAnsi, TrimEdges, Dedupe, HeadTail, Counters)
- Semantic density calculation
- Escape hatch (--raw, --full)
- Inline text formatting (exit code + facts + summary)
- End-to-end interceptor pipeline

Full regression: 2181 tests pass (zero regressions).

## References

- Upstream: [vincentkoc/tokenjuice](https://github.com/vincentkoc/tokenjuice) (MIT License)
- Design spec: `docs/superpowers/specs/2026-06-19-tokenjuice-migration-design.md`
- Implementation plan: `docs/superpowers/plans/2026-06-19-tokenjuice-migration.md`
- Source: `src/OpenClaw.Plugins.TokenJuice/`
- Registration entry point: `src/OpenClaw.Plugins.TokenJuice/TokenJuicePluginRegistration.cs`
- Interceptor: `src/OpenClaw.Plugins.TokenJuice/Reduction/TokenJuiceInterceptor.cs`
- Gateway injection point: `src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.cs`
- Tests: `src/OpenClaw.Tests/TokenJuiceIntegrationTests.cs`

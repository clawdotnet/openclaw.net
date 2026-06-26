# Claude Code Dynamic Workflows vs OpenClaw MetaSKILL

## One-line positioning

| | Claude Code Dynamic Workflows | OpenClaw MetaSKILL |
|---|---|---|
| **Essence** | Executable JavaScript orchestration scripts driving multi-agent collaboration within a session | YAML-declared DAG orchestration engine, scheduled and executed by a .NET runtime |
| **Paradigm** | Code-as-Orchestration | Declaration-as-Orchestration |
| **Runtime** | Claude Code CLI session, Node.js sandbox | OpenClaw.NET Gateway server process |
| **Trigger** | User invokes `/workflow` or uses the Workflow tool | Natural-language trigger matching + `meta_invoke` tool |

## 1. Orchestration model comparison

### Claude Code Dynamic Workflows: JavaScript script orchestration

```javascript
export const meta = {
  name: 'review-changes',
  description: 'Review changed files across dimensions, verify each finding',
  phases: [{ title: 'Review' }, { title: 'Verify' }],
}

// Pipeline: items flow through Review → Verify stages, no barrier
const DIMENSIONS = [
  {key: 'bugs', prompt: 'Find security and correctness bugs'},
  {key: 'perf', prompt: 'Find performance issues'},
]
const results = await pipeline(
  DIMENSIONS,
  d => agent(d.prompt, {
    phase: 'Review',
    schema: FINDINGS_SCHEMA,
  }),
  review => parallel(review.findings.map(f => () =>
    agent(`Verify: ${f.title}`, {
      phase: 'Verify',
      schema: VERDICT_SCHEMA,
    }).then(v => ({...f, verdict: v}))
  ))
)
const confirmed = results.flat().filter(Boolean)
  .filter(f => f.verdict?.isReal)
```

**Key characteristics:**

- Orchestration logic is **executable JavaScript code**
- `agent()` — spawn a sub-agent for a specific task
- `pipeline()` — streaming multi-stage processing, no synchronization barrier
- `parallel()` — parallel execution of multiple tasks, with a barrier
- `phase()` — progress group display
- `log()` — real-time log output
- `budget` — token budget awareness
- `isolation: 'worktree'` for isolated parallel mutations

### OpenClaw MetaSKILL: YAML declarative DAG

```yaml
name: review-changes
kind: meta
composition:
  steps:
    - id: review_all
      kind: fan_out
      iterable: "['bugs', 'perf', 'security']"
      fan_out_max_concurrency: 3
      fan_out_template:
        kind: llm_chat
        with:
          instruction: "Review {{ item }} issues"
    - id: verify_all
      kind: fan_out
      iterable: "{{ outputs.review_all | from_json }}"
      fan_out_max_concurrency: 3
      depends_on: [review_all]
      fan_out_template:
        kind: llm_chat
        with:
          instruction: "Verify finding: {{ item }}"
```

**Key characteristics:**

- Orchestration logic is **declarative YAML**
- 7 kinds of steps covering different execution needs
- `depends_on` declares DAG dependencies
- `fan_out` dynamically expands parallel child steps
- `routes` conditional routing branches
- `on_failure` failure substitute steps
- `user_input` human-in-the-loop pause points

## 2. Core differences

| Dimension | Claude Code Workflows | OpenClaw MetaSKILL |
|---|---|---|
| **Orchestration language** | JavaScript (Turing-complete) | YAML (declarative, not Turing-complete) |
| **Learning curve** | Requires JS programming ability | Requires understanding YAML structure and runtime semantics |
| **Expressiveness** | Extremely high: loops, conditionals, dynamic computation, try-catch | Moderate: DAG + conditional routing + fan_out covers mainstream scenarios |
| **Security** | Runtime sandbox constraints (no filesystem/network) | Three-tier gating: `tool_allowlist` + `capabilities` + `MetaSkill.Enabled` |
| **State recovery** | Session-level checkpoint + resume | SessionMetaRunRecord + CLI replay/reconstruct |
| **Auditability** | In-session tracing | Persistent audit records, CLI-queriable |
| **Timeout protection** | Agent-level timeout | 4 layers: step / retry / session contract / agent loop |
| **Output validation** | JSON Schema (`schema` option) | `output_contract` per-step JSON Schema |
| **Parallel strategy** | `parallel()` barrier, `pipeline()` streaming | Wave-based scheduling, parallel within wave |

## 3. Orchestration primitive comparison

| Claude Code Workflows | MetaSKILL | Notes |
|---|---|---|
| `agent(prompt, {schema})` | `kind: agent` / `kind: llm_chat` | Single LLM call or sub-agent |
| `pipeline(items, stage1, stage2, ...)` | `depends_on` chain | Streaming multi-stage, no barrier |
| `parallel(thunks)` | `fan_out` + wave scheduling | Parallel execution |
| `while (condition) { agent() }` | No native loop | Workflows support Turing-complete loops |
| `if (result.foo) { ... }` | `routes` / `when` | Conditional branching |
| `phase('Verify')` | Step grouping (implicit) | Progress organization |
| `budget.remaining()` | `timeout_seconds` / contract | Resource bounding |
| — | `user_input` | Human-in-the-loop pause |
| — | `on_failure` | Declarative failure substitution |
| — | `skill_exec` (subprocess) | Deterministic script execution |

## 4. Design philosophy comparison

### Claude Code Workflows: developer-friendly, maximize flexibility

> "Orchestration is code" — developers express orchestration logic in familiar JavaScript. Loops, conditionals, recursion, try-catch all available. Ideal for scenarios requiring dynamic, ad-hoc decisions.

**Strengths:**
- Turing-complete, no expressiveness ceiling
- Zero learning cost for developers (just JS)
- Token-budget-aware, dynamically adjusts strategy
- `pipeline()` streaming avoids unnecessary barriers

**Costs:**
- Bugs in scripts can cause unexpected behavior
- Lacks compile-time safety of declarative constraints
- No `user_input` pause points
- No persistent audit records

### OpenClaw MetaSKILL: security-first, declaration is constraint

> "Declaration is constraint" — describe DAG structure in YAML, runtime guarantees execution correctness. Ideal for production workflows requiring long-term maintenance and multi-person collaboration.

**Strengths:**
- Parse-time DAG validation (cycle detection, 5 on_failure constraints)
- Three-tier security gating guarantees tool access scope
- 7 kinds of steps for fine-grained execution cost control
- 4-layer timeout protection
- Full audit trail + CLI replay/reconstruct
- Dual-runtime (AgentRuntime + MafAgentRuntime)

**Costs:**
- Expressiveness limited to DAG (no loops, MetaSkills cannot invoke MetaSkills)
- Learning YAML structure and 7 kinds of steps
- Requires OpenClaw.NET Gateway runtime

## 5. Suitability

| Scenario | Recommended |
|---|---|
| Quick PR review, bug hunting | Claude Code Workflows (flexible ad-hoc scripting) |
| Multi-dimensional research analysis | Claude Code Workflows (loop exploration, dynamic adjustment) |
| Production CI/CD workflows | MetaSKILL (audit, CLI, persistence) |
| Human-approval pause needed | MetaSKILL (`user_input` checkpoints) |
| Long-term maintained repeatable tasks | MetaSKILL (declarative changes have clear boundaries) |
| Cross-runtime consistent execution needed | MetaSKILL (dual-runtime parity guarantee) |
| One-off exploratory analysis | Claude Code Workflows |
| Deterministic script + LLM hybrid orchestration | MetaSKILL (`skill_exec` + `llm_chat` hybrid) |

## 6. Complementary relationship

They are not competitors — they cover opposite ends of the orchestration spectrum:

- **Claude Code Workflows** covers **in-session dynamic orchestration** — developers express intent quickly in JS, ideal for exploratory work
- **MetaSKILL** covers **persistent production orchestration** — declarative definition, runtime guarantees audit, security, and recoverability

Ideal combination: use **Claude Code Workflows for prototyping and exploration**, then once patterns stabilize, **solidify them as MetaSKILL templates** into auditable, replayable production-grade workflows.

## Summary

| | Claude Code Workflows | MetaSKILL |
|---|---|---|
| **Orchestration form** | JavaScript executable scripts | YAML declarative DAG |
| **Turing-complete** | Yes | No (DAG, no loops) |
| **Validation timing** | Runtime | Parse-time + runtime |
| **Security gating** | Sandbox | Three-tier tool_allowlist + capabilities + policy |
| **Audit persistence** | In-session | Persistent + CLI query |
| **Human-in-the-loop** | No native pause points | `user_input` checkpoints |
| **Token budget awareness** | `budget` global variable | 4-layer timeout protection |
| **Deployment** | Claude Code CLI session | .NET Gateway server |
| **Best for** | Exploratory, one-off, developer-driven | Production, auditable, long-term maintenance |

Both share the same core insight: **complex AI workflows cannot be driven by a single long prompt — they need explicit orchestration structures**. The difference is Claude Code chooses "express orchestration in code", while MetaSKILL chooses "constrain orchestration through declaration".

---

## References

- [Meta-Skills](meta-skills.md) — OpenClaw.NET project documentation
- [MetaSkill Orchestration Architecture](meta-skill-orchestration.md)
- [MetaSkill User Guide](meta-skill-user-guide.md)
- [Meta-Skill Authoring Guide](authoring/meta-skills.md)

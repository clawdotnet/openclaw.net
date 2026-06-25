# Meta-Skills

Meta-skills package repeatable multi-step work as reusable, inspectable DAG
workflows. Use them when a request needs more than one normal skill, tool,
checkpoint, or final synthesis pass.

For the full user-facing guide, read
[`meta-skill-user-guide.md`](meta-skill-user-guide.md). For authoring rules,
read [`authoring/meta-skills.md`](authoring/meta-skills.md).
For a deep dive into the orchestration architecture, read
[`meta-skill-orchestration.md`](meta-skill-orchestration.md)
([中文版](zh-CN/meta-skill-orchestration.md)).

## Skills vs Meta-Skills

| Capability | Use it for |
| --- | --- |
| **Skill** (`kind: standard`) | One focused task — instructions injected as system prompt. 1 step, no DAG, no pause, no fallback. |
| **Meta-skill** (`kind: meta`) | A reusable DAG of 3-12 steps with `depends_on`, `on_failure`, `user_input` pause points, and full audit trails. |

One example: "summarize this document" is skill-shaped. "Turn this contract,
quote, and email into a sign/reject/negotiate recommendation with risks and next
actions" is meta-skill-shaped.

## Built-In MetaSkills

OpenClaw.NET Gateway ships with a focused set of meta-skill templates:

| MetaSkill | Purpose |
| --- | --- |
| `meta-skill-creator` | Turns repeated multi-skill collaboration patterns into new MetaSkill proposals. Supports 3 DAG patterns: `p1_sequential`, `p2_fan_out_merge`, `p3_condition_gated`. |
| `history-explorer` | Inspects and surfaces recent session history for downstream steps. |

Bundled standard skills include:

| Skill | Purpose |
| --- | --- |
| `summarize` | Produces structured summaries with key points, details, and action items. |
| `daily-news-digest` | Produces daily news briefs with links, takeaways, and watch items. |

Additional domain-specific meta-skills can be installed via `openclaw skills
install` or placed in plugin skill directories.

## Key Capabilities

### DAG Execution

Steps declare `depends_on` to form a directed acyclic graph. Independent steps
run in parallel (wave-based scheduling). The DAG engine is shared between
`AgentRuntime` (native) and `MafAgentRuntime` (Microsoft Agent Framework adapter)
— both execute MetaSkills identically. The runtime enforces dependency order,
cycle detection, and routing. All parity tests pass on both runtimes.

```yaml
composition:
  steps:
    - id: fetch
      kind: skill_exec
      skill: data-fetcher
    - id: analyze
      kind: llm_chat
      depends_on: [fetch]
```

### Failure Handling (`on_failure`)

Each step can declare an `on_failure` substitute. When the primary step fails
(timeout, tool error, validation failure), the runtime activates the fallback
and mirrors its output to the primary step ID — downstream steps see no
difference.

**5 engineering constraints** enforced at parse-time and runtime:
1. Fallback target must exist
2. Step cannot self-reference
3. Fallback cannot have `on_failure` (no chains)
4. Each fallback can only be referenced by one primary
5. Fallback cannot have `depends_on`

### Pause & Resume (`user_input`)

Steps with `kind: user_input` pause the DAG for human input. The runtime saves a
full checkpoint (`pending`/`blocked`/`outputs`/`stepResults`) to the Session and
resumes when the user provides input. Configurable `timeout_seconds` with
`on_failure` fallback prevents indefinite waits.

### Audit & Recovery

Every execution records a `SessionMetaRunRecord` with per-step timing,
failure codes, and execution evidence. Operators can inspect, replay-preview,
and audit-reconstruct runs via CLI:

```sh
openclaw skills meta-runs <sid> --run <id> --verbose --json
openclaw skills meta-runs replay <sid> --run <id>
openclaw skills meta-runs reconstruct <sid> --run <id>
```

### Bounded Execution

Four layers of timeout protection:
1. **Per-step**: `timeout_seconds` with `CancellationToken`
2. **Per-step retry**: `retry.max_attempts` + `backoff_ms`
3. **Session contract**: `ContractPolicy.MaxRuntimeSeconds` (gateway-level)
4. **Agent loop**: `maxIterations` + circuit breaker

## Step Types

| Kind | Use for |
| --- | --- |
| `llm_chat` | One bounded LLM generation with no tool loop |
| `llm_classify` | Return exactly one value from a closed set (routing) |
| `agent` | Delegate to another skill's instructions via LLM |
| `tool_call` | Direct tool execution with `tool_allowlist` |
| `skill_exec` | Run a skill's entrypoint as a subprocess |
| `user_input` | Pause for structured human input |
| `fan_out` | Dynamic step expansion over a runtime-generated list (batch parallelism) |

### Dynamic Step Expansion (`fan_out`)

When the number of sub-tasks depends on a previous step's output, `fan_out`
evaluates a Jinja expression into a list, clones a step template for each item,
and executes children in controlled parallel batches. Shared logic lives in
`MetaFanOutExecutor` — no code duplication between runtimes.

```yaml
- id: search_all
  kind: fan_out
  iterable: "{{ outputs.extract | from_json }}"
  fan_out_max_concurrency: 3
  fan_out_merge_mode: json_array
  fan_out_template:
    kind: tool_call
    tool: web_search
    with:
      query: "{{ item }}"
    continue_on_error: true
```

### Runtime Tool Discovery (`list_tools`)

A built-in `list_tools` tool returns name/description/schema for every registered
tool. Use it in a `tool_call` step before invoking unknown tools:

```yaml
- id: discover
  kind: tool_call
  tool: list_tools
  with:
    filter: "web"
```

## How to Activate

Natural language triggers:

```text
Generate a weekly report from my team's commits this week.
```

Explicit skill name:

```text
Use meta-skill `weekly-report`.
```

## Configuration

Gateway-level meta-skill policy:

```json
{
  "Skills": {
    "MetaSkill": {
      "Enabled": true,
      "AllowedRiskLevels": ["low", "medium"],
      "RequiredCapabilities": []
    }
  }
}
```

Per-skill overrides:

```json
{
  "Skills": {
    "Entries": {
      "weekly-report": { "Enabled": true, "MetaPriority": 80 }
    }
  }
}
```

## Proposal Lifecycle

Generated meta-skills enter as proposals before becoming installed skills:

```
CREATE (draft) → LINT → SMOKE → RUNTIME_E2E → PERSIST (proposal)
                                                 ↓
                                           ACCEPT / DISMISS
                                                 ↓
                                           Installed skill
```

Each lifecycle transition records `audit` fields (`actorId`, `changedAtUtc`,
`transitionAction`) and `provenanceHistory`.

---

[User Guide](meta-skill-user-guide.md) · [Authoring Guide](authoring/meta-skills.md) · [Orchestration Architecture](meta-skill-orchestration.md) · [Site Map](SITE_MAP.md)

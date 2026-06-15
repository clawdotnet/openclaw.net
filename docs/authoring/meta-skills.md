# Meta-Skill Authoring Guide

This guide is for authors and maintainers who write, validate, and review
OpenClaw.NET MetaSkills. For user-facing guidance, read
[`../meta-skill-user-guide.md`](../meta-skill-user-guide.md).

## What a MetaSkill Is

A MetaSkill is a `SKILL.md` file with:

- `kind: meta`
- one or more natural-language `triggers`
- a `composition:` block that defines a directed acyclic graph of steps

At runtime, OpenClaw.NET's `AgentRuntime.ExecuteMetaSkillAsync` (or
`MafAgentRuntime.ExecuteMetaSkillAsync` when using the Microsoft Agent Framework
adapter) executes the declared composition step by step — enforcing dependency order, template
rendering, meta policy gating, tool allowlists, pause/resume checkpoints, and
failure branch activation. The user's natural-language intent triggers the
workflow via the Gateway's skill matching layer.

Operators can disable meta-skill invocation per skill or globally:

```json
{
  "Skills": {
    "MetaSkill": { "Enabled": false }
  }
}
```

When disabled, meta-skills remain loaded for inventory and history inspection
but are not activated.

## When to Use a MetaSkill

Use a MetaSkill when a task is repeatable and naturally decomposes into a 3-12
step DAG:

- classify the user request, then route to the right specialist skill
- run two independent analysis skills, then merge their outputs
- search or inspect context, then summarize it into a user-facing answer
- execute a deterministic CLI-backed skill, then review or persist the result
- pause for structured user input before continuing

**Do not** use a MetaSkill for:

- one-off instructions (use a standard Skill)
- open-ended planning that should remain conversational
- flows that need arbitrary recursion
- tasks with >12 steps (split into multiple MetaSkills or use external
  orchestrators like Microsoft Agent Framework or LangGraph)

A MetaSkill cannot invoke another MetaSkill (`TryValidateMetaPlan` rejects
`kind: meta` delegated skills).

## Where to Put a MetaSkill

```
src/OpenClaw.Gateway/skills/<skill-name>/SKILL.md    # Gateway bundled
~/.openclaw/skills/<skill-name>/SKILL.md             # Local managed
```

Generated proposals are reviewed before installation. After accepting a
proposal, OpenClaw.NET promotes it and refreshes the skill loader.

## Required Frontmatter

```yaml
---
name: short-stable-name
kind: meta
description: One sentence that tells the model when this workflow applies.
triggers:
  - short phrase users naturally type
meta_priority: 50
always: false
final_text_mode: auto
composition:
  steps: []
---
```

| Field | Required | Purpose |
| --- | --- | --- |
| `name` | Yes | Stable identifier for CLI and cross-skill references |
| `kind` | Yes | Must be `meta` |
| `description` | Yes | Model-facing description of when to activate |
| `triggers` | Yes | Natural-language phrases for intent matching |
| `meta_priority` | No | Sort key when multiple meta-skills may match (default 50) |
| `always` | No | Should be `false`. Meta-skills are not injected unconditionally |
| `final_text_mode` | No | How the final answer is derived (see below) |
| `composition.steps` | Yes | Ordered DAG definition |

## Step Types

### `llm_chat`

One bounded LLM generation with no tool loop. Best for intake normalization,
compact drafting, or lightweight synthesis.

```yaml
- id: normalize
  kind: llm_chat
  with:
    system: "Extract the request fields. Do not ask a question."
    task: "{{ input | xml_escape | truncate(1000) }}"
```

### `llm_classify`

Return exactly one value from a closed set. Best for routing and triage.

```yaml
- id: classify
  kind: llm_classify
  output_choices: [BUG, FEATURE, QUESTION]
  with:
    text: "{{ input | xml_escape | truncate(512) }}"
```

### `agent`

Delegate to another skill's instructions via LLM. The best default for
user-facing reasoning and synthesis.

```yaml
- id: summarize
  kind: agent
  skill: summarize
  with:
    text: "{{ outputs.search | truncate(2000) }}"
```

### `tool_call`

Direct tool execution. Declare `tool_allowlist` and keep arguments narrow.

```yaml
- id: persist
  kind: tool_call
  tool: memory_save
  tool_allowlist: [memory_save]
  with:
    text: "{{ outputs.summary | truncate(2000) }}"
```

### `skill_exec`

Run a skill's entrypoint as a subprocess. Best for deterministic CLI-backed
skills.

```yaml
- id: render
  kind: skill_exec
  skill: html-to-pdf
  skill_exec_entrypoint: scripts/render.py
  skill_exec_args:
    - "{{ outputs.report | truncate(12000) }}"
  skill_exec_parse_mode: json
```

### `user_input`

Pause for structured human input with `clarify` schema validation.

```yaml
- id: collect_project
  kind: user_input
  when: "outputs.intake contains 'NEEDS_CLARIFICATION'"
  clarify:
    mode: form
    fields:
      - name: topic
        type: string
        required: true
        min_length: 3
      - name: priority
        type: enum
        options: [low, medium, high]
        default: "medium"
    cancel_words: [cancel, 取消]
    timeout_seconds: 300
    skip_if: "outputs.auto_approve == '1'"
```

Supported field types: `string`, `enum`, `integer`, `boolean`. Use `skip_if` to
bypass when context is sufficient.

## Dependencies and Parallelism

Steps without `depends_on` may run in parallel (wave-based scheduling). A step
with `depends_on` waits for all named steps to finish.

```yaml
steps:
  - id: inspect_code
    kind: agent
    skill: code-reviewer

  - id: inspect_tests
    kind: agent
    skill: test-engineer

  - id: merge
    kind: llm_chat
    depends_on: [inspect_code, inspect_tests]
    with:
      task: |
        Code: {{ outputs.inspect_code | truncate(2000) }}
        Tests: {{ outputs.inspect_tests | truncate(2000) }}
```

The graph must be acyclic. A step may only depend on step IDs declared in the
same composition.

## Error Handling

### `on_failure` — Substitute Step

```yaml
- id: llm_summarize
  kind: llm_chat
  on_failure: fallback_template
  timeout_seconds: 15

- id: fallback_template
  kind: tool_call
  tool: emit_text
  with:
    text: "Summary unavailable — using template."
```

**5 constraints** (enforced at parse and runtime):
1. Fallback target must exist in the composition
2. Step cannot reference itself
3. Fallback cannot have `on_failure` (no chains)
4. Each fallback can only serve one primary
5. Fallback cannot have `depends_on`

### `continue_on_error` — Skip on Failure

```yaml
- id: optional_step
  kind: skill_exec
  skill: analytics
  with:
    continue_on_error: true
```

Failure marks the step as `Continued: true` and the DAG proceeds.

## Routing

Use `route` on `agent` or `skill_exec` steps to branch based on outputs:

```yaml
- id: classify
  kind: llm_classify
  output_choices: [DOCS, BUG, SECURITY]

- id: handle
  kind: agent
  skill: summarize
  depends_on: [classify]
  route:
    - when: "outputs.classify == 'DOCS'"
      to: writer
    - when: "outputs.classify == 'BUG'"
      to: debugger
    - when: "outputs.classify == 'SECURITY'"
      to: security-reviewer
```

A route without `when` acts as the default fallback.

## Final Text Modes

| Mode | Behavior |
| --- | --- |
| `auto` | Default. The runtime summarizes step outputs into a concise final answer |
| `raw` | Return the last non-substitute step output verbatim |
| `step:<id>` | Return one specific step output verbatim |
| `structured` | Return a JSON envelope with `error_code` and per-step `status`/`failure_code` |

```yaml
final_text_mode: auto
final_text_mode: raw
final_text_mode: "step:summarize"
final_text_mode: structured
```

## Template Safety

Templates are Jinja2 expressions rendered by `MetaTemplateRenderer`. Only 4
filters are allowed: `xml_escape`, `slugify`, `truncate`, `tojson`.

Always filter user input and previous step outputs:

```yaml
# Safe
query: "{{ input | xml_escape | truncate(512) }}"
text: "{{ outputs.search | truncate(2000) }}"
slug: "{{ input | slugify | truncate(80) }}"
payload: "{{ outputs.plan | tojson }}"
```

```yaml
# Unsafe — never do this
query: "{{ input }}"
text: "{{ outputs.search }}"
```

The Jinja2 sandbox enforces three lines of defense:
1. Classic escape vectors (`__class__`, `__bases__`, `.GetType()`) are blocked
2. Only 4 registered filters work — 38+ built-in Jinja2 filters are overridden
3. Global functions (`range()`, `dict()`) throw `NotSupportedException`, caught
   by the renderer

## Bounded Execution

```yaml
- id: api_call
  kind: tool_call
  tool: external_api
  timeout_seconds: 30
  retry:
    max_attempts: 2
    backoff_ms: 500
```

## Activation Guidance

- Write triggers as short phrases users naturally type: `summarize recent
  history`, not `run the internal DAG composition meta skill`
- Use 2-5 triggers unless a tested reason exists for more
- Avoid triggers that collide with explanations ("how does this meta-skill
  work?")
- Set `description` to guide model selection. The model primarily sees
  frontmatter and injected skill summary

## Validation Checklist

Before enabling a MetaSkill:

1. Frontmatter parses as valid YAML
2. `kind: meta` and `composition.steps` are present
3. All `depends_on`, `route.to`, and `on_failure` targets exist
4. No cycles in the dependency graph
5. All user input and step outputs are filtered through `xml_escape`/`truncate`
6. `on_failure` targets pass all 5 constraints
7. Trigger phrases are tested against false positives
8. `final_text_mode` matches the intended deliverable shape

## Troubleshooting

**MetaSkill does not activate**:
- Confirm `SKILL.md` is under a loaded skill directory
- Confirm `kind: meta` and non-empty `composition.steps`
- Confirm user wording matches triggers or description
- Check `Skills.MetaSkill.Enabled` is not `false`

**Parsing fails**:
- Check duplicate step IDs
- Check unknown `kind` values
- Check missing `skill` for `agent` or `skill_exec` steps
- Check missing `output_choices` for `llm_classify`
- Check missing `clarify.fields` for `user_input`
- Check cycles and undefined `depends_on` references

**Fallback not activating**:
- Check the 5 `on_failure` constraints are not violated
- Verify the fallback step exists and has no `depends_on`

---

[User Guide](../meta-skill-user-guide.md) · [Feature Overview](../meta-skills.md) · [Site Map](../SITE_MAP.md)

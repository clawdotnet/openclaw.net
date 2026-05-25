# Learning Proposals

OpenClaw.NET uses Learning Proposals to turn repeated operator behavior into reviewable suggestions. The learning loop is intentionally review-first: it can create proposals, metadata, previews, and audit feedback, but durable behavior changes only after an operator approves them.

Learning proposals are designed for:

- operators reviewing repeated work patterns
- maintainers improving runtime behavior safely
- contributors adding learning features without silent self-modification
- CI and release checks that must preserve proposal serialization and review semantics
- future Harness Evolution and Plan-Execute-Verify workflows

They help answer:

- What repeated behavior did the runtime observe?
- Is the suggestion actionable, or only a weak learning signal?
- What would change if an operator approves it?
- Which evidence supports the suggestion?
- What quality or safety issues should the operator review first?
- What feedback did the operator provide by approving, editing, or rejecting it?

## Proposal Kinds

The learning queue can contain several proposal kinds:

| Kind | Purpose | Approval behavior |
| --- | --- | --- |
| `profile_update` | Suggests stable user-profile facts, preferences, or project context. | Applies the reviewed profile update. |
| `skill_draft` | Suggests a managed `SKILL.md` draft from repeated successful work. | Installs a managed learning skill after validation. |
| `automation_suggestion` | Suggests a disabled automation draft or records a learning-only automation idea. | Saves only reviewable disabled drafts; low-quality signals stay learning-only. |
| `harness_change` | Suggests review-first improvements to harness behavior. | Manual-only approval; does not silently mutate harness configuration. |

The common review flow is:

1. Observe repeated behavior or explicit harness signals.
2. Create a pending proposal with evidence, validation status, risk, and warnings.
3. Let an operator inspect the proposal in the admin learning queue.
4. Record approval, rejection, rollback, or later edit feedback.
5. Preserve enough metadata for future evaluation and regression tests.

## Automation Suggestion Quality Pipeline

`automation_suggestion` proposals use an extra quality pipeline because a repeated prompt is not automatically a safe scheduled automation. The runtime should avoid creating misleading drafts such as a daily automation whose name and prompt are both `Compare the current conversation and give an overall assessment`.

The pipeline is deterministic and conservative:

1. **Intent extraction** identifies the likely automation intent, target object, expected outcome, cadence hint, trigger evidence, and ambiguities.
2. **Refinement** converts recognized high-value intents into stable disabled draft candidates. For example, a vague conversation-review request becomes a daily review over the past 24 hours with explicit output sections.
3. **Quality gating** scores the candidate across intent clarity, input scope, output clarity, schedule match, safety, noise risk, user value, and duplicate risk.
4. **Preview building** records why the proposal exists, the original prompt, the refined prompt, warnings, expected output sections, and the quality decision.
5. **Feedback recording** captures accept, reject, and post-approval edit signals so future learning can distinguish useful suggestions from noise.

The quality gate can return these decisions:

| Decision | Meaning |
| --- | --- |
| `ready_draft` | The suggestion is clear enough to create a disabled automation draft for review. |
| `needs_review_draft` | The suggestion is usable but should be reviewed carefully before approval. |
| `learning_only` | The signal is worth retaining, but the runtime should not create an automation draft. |
| `suppressed` | The signal is too weak or noisy to surface as an automation proposal. |

Hard blockers keep the proposal out of draft form. They include missing name, prompt, schedule, or delivery channel; identical normalized name and prompt; unstable scheduled input scope; unclear output format; external side effects without explicit confirmation; and duplicate automations.

## Conversation Review Example

A repeated request such as:

```text
Compare the current conversation and give an overall assessment.
```

is ambiguous as a scheduled automation because `current conversation` has no stable meaning at daily runtime, the comparison baseline is missing, and the output format is unspecified.

The improved automation-suggestion path keeps the original prompt as evidence, extracts a `daily_conversation_review` intent, and refines the candidate into a disabled draft shaped like:

```text
Every day, review the conversations from the past 24 hours. Output only: 1) unfinished items; 2) preferences the user explicitly asked to remember; 3) risks that need follow-up; 4) recommended next actions. Do not provide a generic summary, do not evaluate the user, and do not repeat completed items. If there is nothing worth following up on, output that there are no follow-up items today.
```

The preview explains that `current` was replaced with `past 24 hours` so the scheduled task has a stable input range. Expected output sections are recorded as machine-readable metadata such as `unfinishedItems`, `rememberedPreferences`, `risks`, and `nextActions`.

## Feedback Events

Automation suggestions record review feedback through `LearningProposalFeedbackEvent` entries:

| Action | When it is recorded |
| --- | --- |
| `accepted_without_edits` | An operator approves an automation suggestion as proposed. |
| `edited_then_accepted` | Reserved for flows that accept a proposal after editing its draft before approval. |
| `rejected` | An operator rejects the suggestion. |
| `edited_after_approval` | A learned automation is changed after approval. |

Feedback events include changed fields, before/after quality scores when known, a summary, and a timestamp. This keeps the learning loop inspectable without treating every repeated prompt as a reliable automation template.

## Relationship To Harness Tests

Learning proposals are covered by normal unit and gateway tests, and they connect to the broader harness model in three ways:

- **Serialization guarantees:** learning proposal metadata, automation quality results, previews, feedback events, and harness evolution payloads must round-trip through the source-generated JSON context and file store.
- **Review-first behavior:** approval and rejection tests verify that proposals record operator decisions without bypassing required review semantics.
- **Regression intent:** harness evolution proposals can recommend falsification tests and regression categories, while automation-suggestion quality tests verify that vague prompts stay learning-only and refined prompts become disabled drafts.

Before trusting changes to learning behavior, run the normal test suite and the relevant harness checks:

```bash
dotnet test
openclaw harness test --category harness
openclaw harness test --category memory
```

From source, replace `openclaw ...` with:

```bash
dotnet run --project src/OpenClaw.Cli -c Release -- harness test --category harness
dotnet run --project src/OpenClaw.Cli -c Release -- harness test --category memory
```

Use the full [Harness Regression Suite](HARNESS_REGRESSION.md) before release or when learning changes touch serialization, approval policy, memory retrieval, provider shape, MCP/OpenAI-compatible routes, or other harness-owned contracts.

## What This Does Not Do

- It does not auto-enable generated automations.
- It does not turn every repeated prompt into an automation draft.
- It does not bypass operator review for skills, profiles, automations, or harness changes.
- It does not guarantee that a proposal is correct.
- It does not replace unit tests, harness regression checks, or release smoke tests.
- It does not automatically run regressions when a proposal is approved.

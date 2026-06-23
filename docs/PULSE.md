# Runtime Pulse

Scheduled heartbeat turns for surfacing what needs attention without creating background task records.

Runtime Pulse gives OpenClaw.NET a quiet way to check whether anything needs attention.

A pulse is a scheduled agent turn. It can read a small `HEARTBEAT.md` checklist, inspect lightweight runtime context, surface urgent alerts, and nudge the review-first learning system to create proposals. If nothing needs attention, the agent replies `HEARTBEAT_OK` and OpenClaw.NET suppresses the message by default.

Pulse is different from cron or automations. Cron creates detached scheduled work. Pulse runs as a main-session or configured-session wake-up. It is for awareness, check-ins, maintenance signals, and reviewable suggestions, not for silently launching background jobs.

## Default Behavior

- Runs every 30 minutes when enabled.
- Reads `HEARTBEAT.md` from the workspace root if present.
- Sends no visible message when the result is `HEARTBEAT_OK`.
- Keeps alerts operator-visible by default with `Target=none`.
- Can be restricted to active hours.
- Can use light context and isolated sessions to reduce token cost.
- Defers while other runtime lanes are busy.
- Durable learning changes remain review-first.

## Example Config

```json
{
  "OpenClaw": {
    "Pulse": {
      "Enabled": true,
      "Every": "30m",
      "Target": "none",
      "DirectPolicy": "allow",
      "LightContext": true,
      "IsolatedSession": true,
      "SkipWhenBusy": true,
      "Prompt": "Read HEARTBEAT.md if it exists in the workspace context. Follow it strictly. Do not infer or repeat old tasks from prior chats. If nothing needs attention, reply HEARTBEAT_OK.",
      "AckMaxChars": 300
    }
  }
}
```

Set `Every` to `0m` to disable scheduled pulse runs.

## HEARTBEAT.md

Create `HEARTBEAT.md` in the workspace root:

```markdown
# Heartbeat checklist

- Check whether any learning proposals need review.
- Check whether recent sessions produced repeated failures.
- Check whether a pending automation or skill draft is blocked.
- If nothing needs attention, reply HEARTBEAT_OK.

tasks:
- name: proposal-review
  interval: 2h
  prompt: "Check pending learning proposals and surface only high-risk or stale items."
- name: runtime-health
  interval: 1h
  prompt: "Check recent runtime warnings and provider/tool failures."
```

Keep heartbeat prompts short. Do not put secrets in `HEARTBEAT.md`; it is prompt context.

## Response Contract

- `HEARTBEAT_OK` by itself is treated as OK and suppressed by default.
- Only an exact match of `HEARTBEAT_OK` (after trimming whitespace) suppresses delivery.
  A reply that starts or ends with `HEARTBEAT_OK` but contains other text alongside it is not suppressed.
- `HEARTBEAT_OK` in the middle of a longer reply is not treated specially.
- Alerts should not include `HEARTBEAT_OK`.

## Delivery And Visibility

`Target=none` runs the pulse without external delivery and records alerts for operators. `ShowOk=false`, `ShowAlerts=true`, and `UseIndicator=true` are the low-noise defaults.

If `ShowOk`, `ShowAlerts`, and `UseIndicator` are all false, OpenClaw.NET skips the model call.

Use `target=none` for internal-only checks. Use active hours for human-facing notifications. Keep `IncludeReasoning` disabled in group channels.

The first Runtime Pulse slice keeps alerts operator-visible. Direct delivery for `target=last` and explicit channel ids is reserved for the channel-routing follow-up so pulse cannot surprise operators by sending external messages before routing policy is complete.

## Manual Wake

```bash
openclaw pulse status
openclaw pulse run --text "Check for urgent follow-ups"
openclaw pulse run --text "Check the release checklist" --mode next-heartbeat
openclaw pulse events
```

`openclaw heartbeat run` is accepted as an alias for users familiar with heartbeat terminology.

## Learning Loop

Runtime Pulse may surface pending or stale learning proposals and suggest profile updates, automation suggestions, skill drafts, or future heartbeat checklist updates. It does not auto-approve or silently apply durable learning changes.

## Troubleshooting

- `disabled`: `OpenClaw:Pulse:Enabled=false` or `Every=0m`.
- `outside-active-hours`: current local time is outside configured active hours.
- `busy`: `SkipWhenBusy=true` and another runtime lane is active.
- `empty-heartbeat-file`: `HEARTBEAT.md` exists but has no actionable text or due tasks.
- `visibility-disabled`: all visibility controls are false.
- `model-unavailable`: the configured model/provider route is unavailable.

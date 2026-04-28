# Canvas and A2UI

Canvas and A2UI are supported as a first-party, session-scoped visual workspace for websocket clients. The v1 implementation focuses on local Canvas content and A2UI v0.8 JSONL rendering. It is not a replacement for the browser tool and does not support arbitrary remote webpage navigation or remote-page script execution.

## What Ships

- Gateway Canvas configuration under `OpenClaw:Canvas`.
- Typed websocket envelopes for Canvas commands, A2UI pushes, results, and user events.
- Native agent tools: `canvas_present`, `canvas_hide`, `canvas_navigate`, `canvas_snapshot`, `a2ui_push`, `a2ui_reset`, and `a2ui_eval`.
- A broker that forwards commands only to the current websocket session, tracks request acknowledgements/results, records runtime events, and times out stalled commands.
- Webchat Canvas host with a side panel, A2UI renderer, sandboxed local HTML iframe, and lightweight snapshots.
- Companion Canvas tab with native Avalonia A2UI rendering and event/snapshot feedback.

## Capability Scope

Supported:

- A2UI v0.8 JSONL frames.
- Components: `text`, `markdown`, `card`, `button`, `input`, `select`, `checklist`, `table`, `image`, `progress`, and simple `chart`.
- Present/hide/reset/push/snapshot command flow.
- Local HTML navigation by passing `html` to `canvas_navigate` in webchat.
- `about:blank` navigation.
- A2UI events returned to the same conversation as structured session turns.
- Lightweight JSON snapshots containing rendered tree state, component values, diagnostics, and local HTML text when available.

Unsupported:

- `http:` and `https:` Canvas navigation.
- Script execution against remote webpages.
- A2UI v0.9 `createSurface`.
- Cross-session Canvas sharing.
- Native local HTML/WebView rendering in Companion. Companion returns an explicit unsupported diagnostic for local HTML navigation and A2UI eval.

## Configuration

Canvas settings live under `OpenClaw:Canvas`:

| Setting | Default | Notes |
| --- | --- | --- |
| `Enabled` | `true` | Enabled for loopback/local use. |
| `AllowOnPublicBind` | `false` | Required to keep Canvas enabled on non-loopback binds. |
| `MaxCommandBytes` | `262144` | Maximum serialized command size. |
| `MaxSnapshotBytes` | `262144` | Maximum returned snapshot size. |
| `CommandTimeoutSeconds` | `10` | Pending command timeout. |
| `MaxFramesPerPush` | `100` | Maximum A2UI frames in one push. |
| `EnableLocalHtml` | `true` | Allows `canvas_navigate` with inline `html`. |
| `EnableRemoteNavigation` | `false` | Remote webpage Canvas navigation remains unsupported. |
| `EnableEval` | `true` | Allows capability-gated `a2ui_eval` commands when a client advertises `a2ui.eval`. First-party v1 clients do not advertise it. |

When strict public-bind hardening is applied and `AllowOnPublicBind=false`, Canvas is disabled. Without strict mode, public-bind startup refuses unsafe Canvas forwarding unless you explicitly opt in.

## Protocol

Server-to-client websocket envelope types:

- `canvas_present`
- `canvas_hide`
- `canvas_navigate`
- `canvas_snapshot`
- `a2ui_push`
- `a2ui_reset`
- `a2ui_eval`

Client-to-server websocket envelope types:

- `canvas_ready`
- `canvas_ack`
- `canvas_snapshot_result`
- `canvas_eval_result`
- `a2ui_event`

Common Canvas fields include `requestId`, `sessionId`, `surfaceId`, `contentType`, `frames`, `html`, `url`, `script`, `snapshotMode`, `snapshotJson`, `componentId`, `event`, `valueJson`, `sequence`, `capabilities`, `success`, and `error`.

Example A2UI push:

```json
{
  "type": "a2ui_push",
  "sessionId": "session_123",
  "surfaceId": "main",
  "contentType": "application/x-a2ui+jsonl;version=0.8",
  "frames": "{\"type\":\"text\",\"id\":\"summary\",\"text\":\"Ready\"}\n{\"type\":\"button\",\"id\":\"approve\",\"label\":\"Approve\"}"
}
```

Example event feedback:

```json
{
  "type": "a2ui_event",
  "sessionId": "session_123",
  "surfaceId": "main",
  "componentId": "approve",
  "event": "click",
  "valueJson": "true",
  "sequence": 7
}
```

## A2UI Validation

`a2ui_push` accepts JSONL only: one JSON object per line. The gateway validates payload size, frame count, required `type` and `id` fields, component-specific required fields, and supported component types before forwarding.

Examples:

```jsonl
{"type":"markdown","id":"summary","text":"## Run ready\nReview the table before approving."}
{"type":"table","id":"checks","columns":["Check","Status"],"rows":[["Tests","Passed"],["Policy","Pending"]]}
{"type":"input","id":"note","label":"Operator note","value":""}
{"type":"button","id":"approve","label":"Approve"}
{"type":"progress","id":"deploy","value":0.45}
```

The validator rejects v0.9 `createSurface` frames with an explicit diagnostic.

## Client Behavior

Webchat advertises:

- `a2ui.v0_8`
- `canvas.present`
- `canvas.hide`
- `canvas.local_html`
- `snapshot.state`

Companion advertises:

- `a2ui.v0_8`
- `canvas.present`
- `canvas.hide`
- `snapshot.state`

The gateway uses advertised capabilities before forwarding commands. If a session is not websocket-backed, if the client is disconnected, or if the client lacks a required capability, Canvas tools fail with a normal tool error instead of attempting a best-effort send. No first-party v1 client advertises `a2ui.eval`; the tool remains capability-gated for future local sandboxes.

## Security Model

Canvas inherits the gateway’s local-first posture:

- Commands are scoped to the current websocket session sender.
- Non-websocket sessions cannot receive Canvas commands.
- Public-bind deployments must explicitly opt into Canvas forwarding.
- Remote webpage Canvas navigation is rejected.
- A2UI eval is capability-gated and never targets third-party pages.
- Command and result sizes are bounded.
- Canvas command lifecycle events are written to the runtime event log.

Use the existing browser tool for webpage automation, DOM inspection, and remote navigation.

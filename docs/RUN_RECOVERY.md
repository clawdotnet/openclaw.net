# Run explanation and recovery

Open a session in the admin console, Dashboard, or Companion's Sessions tab. The **Run explanation and recovery** section shows a snapshot of the recorded state and the next steps appropriate to it. Refresh the session to see changes.

The view combines:

- Run state and the last recorded background stop reason.
- Goal status, its status note, token usage, and budget when goals are enabled.
- Pending approvals for this exact session, identified by approval ID and tool name.
- The latest native tool-batch checkpoint and its recorded state.
- Recorded failure codes and messages from the last tool batch for failed or blocked runs.

Use the existing session timeline and approval history for the surrounding events. Missing evidence is not a diagnosis: a saved running state does not prove a worker is still alive, and an old checkpoint cannot establish whether a later interrupted external action took effect.

## Recovery guidance

| State | Next step |
| --- | --- |
| Approval pending | Inspect the matching IDs in Approvals and decide whether each action should proceed. Refresh after deciding. |
| Goal paused or blocked | Resolve the recorded condition, send `/goal resume` in the same session, then send a follow-up message. |
| Budget limited | Review token usage or continuation limits first. `/goal resume` does not increase a goal's token budget. Preserve its objective and results before replacing an exhausted goal. |
| Failed | Inspect the timeline and failed tool evidence, resolve the cause, and verify any external effects before retrying. |
| Running or continuing | Refresh the timeline for new activity before submitting duplicate work. |
| Completed | Review the result; create a new goal for additional work. |

These are instructions for an operator, not automatic recovery controls. Reading the view never changes a goal, decides an approval, dispatches a message, or replays a tool. Decisions remain subject to the existing permissions and approval policies.

## API compatibility

The existing authenticated session detail endpoints now include an additive `recovery` object:

- `GET /admin/sessions/{id}`
- `GET /api/integration/sessions/{id}` (also used by Companion and the client SDK)

It contains `status`, `summary`, `evidence`, and `nextSteps`. The arrays contain display text, not executable commands or action authorization. Older gateways may omit this object; clients continue showing their existing session details.

Tool arguments and raw results are not copied into the explanation. Goal notes and failure messages retain the same operator access boundary as session details. A corrupt or unavailable goal store yields an unknown explanation while session details remain readable.

Unavailable or corrupt goal data produces an unknown explanation; session details remain readable. An exhausted background continuation cap requires a new session for further automatic continuation.

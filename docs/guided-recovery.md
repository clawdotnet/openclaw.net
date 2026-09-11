# Guided operator recovery

> **Release availability:** Available on `main` and targeted for v0.3.0; not included in v0.2.0. See the [roadmap](ROADMAP.md).

The admin session page now includes recovery controls beside session details. The explanation view in PR #213 complements these controls; it is not required to operate them.

- **Stop active execution** uses the existing abort endpoint. Cancellation alone does not establish an external action's outcome.
- **Pause goal** pauses an active goal after execution is idle.
- **Resume for next message** makes a paused/blocked goal active without dispatching work. It does not increase budgets, reset usage limits, bypass approvals, or restart an expired/paused session.
- **Confirm completed outcome** requires provider evidence and a verified result for an uncertain action. For an already recorded completion, the existing result is retained. The result is saved into session history before the journal's blocker is cleared.
- **Confirm action did not execute** requires definitive provider evidence. It enables a later authorized retry using the original action's key. It cannot override an already recorded completion.

All mutations require Operator or Admin role, browser CSRF protection when applicable, rate-limit admission, a current recovery revision, and current per-action revision. Controls are hidden for viewers. The server rechecks session execution, pending approvals, goal status, and goal/session/background budgets under the session lock. Usage-limited and budget-limited goals must be handled through their normal workflows after the underlying limit is resolved. Success/failure is written to the operator audit; sensitive result/evidence text is redacted before persistence and omitted from audit summaries.

API: `GET /admin/sessions/{id}/recovery` returns eligibility, explanations, revision, and unresolved action IDs. `POST` to that path accepts `revision`, `command` (`pause`, `resume`, `completed`, `not_executed`), and for action reconciliation `actionId`, `actionRevision`, `evidence`, and optional `result`. HTTP 409 means refresh and review changed state; HTTP 400 means invalid evidence/input. A missing browser CSRF token follows existing gateway behavior and returns 401.

Journaling must be enabled to expose action reconciliation. No recovery request calls an external provider or sends an inbound execution message. A recorded receipt is an operator assertion: verify it against the provider; the control cannot determine whether free-form evidence is truthful. Recovery data and journal records remain private instance data.

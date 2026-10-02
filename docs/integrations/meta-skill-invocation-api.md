# MetaSkill Invocation API

`POST /api/integration/meta-invocations` directly executes one explicitly named MetaSkill DAG in the existing session. It does not submit a chat message or start an external workflow.

## Authentication

Use the Gateway's normal operator authentication with the `integration.mutate` permission. A Bearer operator token can be sent in the `Authorization` header. Browser-session requests must also send the `X-CSRF-Token` header returned at sign-in. The endpoint applies the existing operator authorization and rate-limit checks before reading the idempotency key or request body.

The caller must be allowed to write to the requested existing session. Session ownership is checked before looking up or claiming the idempotency key, so a denied request does not consume that key.

## Request

Send a unique `Idempotency-Key` header (maximum 200 characters) and a JSON body with all three fields:

```http
POST /api/integration/meta-invocations HTTP/1.1
Authorization: Bearer <operator-token>
Idempotency-Key: incident-2026-10-02-run-1
Content-Type: application/json

{
  "skill": "incident-review",
  "input": "Review incident INC-123 and identify the next action.",
  "sessionId": "incident-123"
}
```

`skill` and `sessionId` must be non-empty. `input` may be a string or `null`. The key is scoped to the authenticated caller; a different caller may use the same key independently. Reusing a key with the same `skill`, `input`, and `sessionId` returns the original invocation state or completed result. Reusing it with a different request returns a conflict.

## Responses

| HTTP status | Meaning |
| --- | --- |
| `200 OK` | The invocation completed. A replay returns the persisted result. |
| `202 Accepted` | The same request is still running. It includes the original `invocationId` and `Running` status; retry the same request with the same key to check again. |
| `409 Conflict` | Either the key was used for a different request, or the invocation is `Uncertain`. A key conflict returns an error object; an uncertain invocation returns its `invocationId`, `Uncertain` status, and error. |

Completed response example:

```json
{
  "invocationId": "3a1d3589-2856-4f6b-b572-9f9e060db134",
  "status": "Completed",
  "result": "The next action is to rotate the affected credential.",
  "createdAtUtc": "2026-10-02T12:34:56Z"
}
```

Invalid JSON or missing fields return `400 Bad Request`; a missing session returns `404 Not Found`; insufficient session access returns `403 Forbidden`.

## Idempotency and Recovery

Invocation claims and terminal results are stored under the configured Gateway `Memory.StoragePath`. `Gateway.MetaInvocations.RetentionDays` controls how long completed and uncertain records are retained; it defaults to `30` days. Set it to at least the longest retry window of any caller, including the DrasiWake outbox, so an old retry cannot outlive its deduplication record.

If execution fails, is cancelled, or a process stops before persisting a terminal result, the invocation is marked `Uncertain`. An uncertain invocation is not automatically retried; the caller or operator must reconcile its outcome before choosing how to proceed. Retrying the same key reports the stored uncertain state.

This API provides durable deduplication and replay, not an exactly-once guarantee across arbitrary process, machine, or external side-effect failures. A caller should preserve the same key for every retry of one logical request and use a new key only for a deliberately new invocation.
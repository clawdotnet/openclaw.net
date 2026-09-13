# Durable action reconciliation

> **Release availability:** Included in v0.3.0 and later; not included in v0.2.0. See the [roadmap](ROADMAP.md).

Set `OpenClaw:Tooling:DurableActionJournal` to `true` to journal native and MAF tool executor dispatches under `Memory.StoragePath/action-journal`. This is opt-in because it deliberately blocks work that older versions retried automatically.

The executor obtains an exclusive session lease after policy and approval checks, persists a `started` record before invoking the tool, and persists its redacted result before returning. Records contain a stable session/call-derived idempotency key and an argument hash, never raw arguments. Protect the storage directory: returned results and reconciliation evidence can still contain application data. Files are atomically replaced and private on Unix.

A timeout, cancellation, exception, or process termination leaves the outcome uncertain. Neither a new call ID nor a new runtime can bypass an uncertain action. Completed results absent from persisted history also block new calls after restart, preventing the checkpoint gap from silently duplicating an action. An exact retry returns the saved result without calling the provider. Calls within a live parallel batch can complete; the session lease serializes their dispatch.

Provider adapters can implement `IReconcilableTool`. `ExecuteWithIdempotencyAsync` must pass the supplied key to the provider's documented deduplication mechanism. `ReconcileAsync` must only query evidence and return `completed` with a result, `not_executed` only with definitive non-execution evidence, or an unknown state. The executor never equates a missing/expired provider record with non-execution. Existing tools have no invented provider guarantees and require operator reconciliation after uncertainty. This interface uses direct provider execution and must not be added to sandbox-routed tools.

`DurableActionJournal.Lease.Resolve` supports an operator workflow with expected revision, evidence, and an explicit completed result or confirmed non-execution. It does not itself grant permission to resume a session or increase budgets. Use an authenticated operator surface. Do not edit journal files or mark an action not executed merely because its response was lost.

Scope: dispatches through `OpenClawToolExecutor`; `meta_invoke` delegates journaling to its nested tools to avoid recursive lease deadlock. Meta `skill_exec` scripts and background jobs that bypass this executor are not covered. Journal files have no automatic retention deletion. Use one private state directory per instance. File replacement flushes file contents; this is process-crash protection, not a guarantee against storage hardware or filesystem power-loss failure.

Built-in Ollama, meta-step and MAF dispatches generate session-unique call IDs. Third-party runtime adapters must also preserve unique IDs for distinct logical calls and reuse an ID only for recovery of that call. Established read-only tools bypass journaling; unknown tools remain conservative. Argument preparation failures are recorded as not executed. Lease contention waits for caller cancellation rather than imposing a fixed dispatch deadline.

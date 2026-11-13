# Confirmed issue repairs, November 13, 2026

Completed against local checkout `8ed2043`, preserving the earlier review's uncommitted repairs. This follow-up addresses the three business-logic findings in the [application review](application-review-2026-11-07.md).

## Machine-payment retries

The gateway now reserves a caller-scoped idempotency key before dispatching a machine payment. The reservation and completed result live under `Memory:StoragePath/payment-idempotency`. Atomic file creation coordinates processes using the same local directory. Request fingerprints cover all payment fields, with deterministic metadata ordering. Completed retries return the saved result. Conflicting reuse returns HTTP 409.

Reservations remain after timeout, cancellation, interrupted execution, or a damaged journal record. Completed records are retained for seven days; pending and unreadable records are retained until an operator reconciles them. An uncertain provider response is reported as `payment_outcome_unknown`. Such a request cannot automatically dispatch another payment. Operators must reconcile with provider transaction records before attempting a new key. This protects OpenClaw dispatch and does not claim provider-side exactly-once execution.

The REST endpoint scopes keys to authenticated identity, independently of sender query parameters. Machine payments now require `idempotencyKey` in REST requests, `--idempotency-key` in CLI commands, `idempotency_key` in tool arguments, and a payment reference in Companion. The HTTP-402 handler accepts an explicit request option or derives its key from the request and structured challenge, including challenges without an ID. Completed replays occur before a new policy or approval prompt; an already consumed scoped authorization token is never replenished.

Embedded runtimes default to a process-local store and must inject durable storage for restart protection. Virtual-card issuance is outside this machine-payment retry contract. The existing Stripe Link command adapter has no verified idempotency flag, so uncertain requests are blocked rather than reissued. Live-provider acceptance remains deployment work. See [payment security](../security/payments.md#machine-payment-retry-safety) for storage, reconciliation, and provider compatibility limits.

Implementation and regression evidence are in [PaymentIdempotencyStore](../../src/OpenClaw.Payments.Core/PaymentIdempotencyStore.cs), [PaymentRuntimeService](../../src/OpenClaw.Payments.Core/PaymentRuntimeService.cs), [payment retry tests](../../src/OpenClaw.Tests/PaymentIdempotencyTests.cs), and [REST retry tests](../../src/OpenClaw.Tests/GatewayAdminEndpointTests.PaymentRetries.cs).

## Outbound delivery reporting

Telegram validates the Bot API response envelope for text and media sends. HTTP 200 with `ok: false`, invalid recipients, and unsupported media-only messages now fail explicitly. Errors carry the number of confirmed delivered parts, whether the remaining outcome is unknown, whether retry is safe, and any provider retry delay.

The outbound worker records success only after the adapter completes successfully. It does not replay permanent failures, partially delivered messages, or uncertain transport outcomes. Explicit transient rejection before any successful part may be retried, respecting Telegram's retry delay. Shutdown cancellation propagates normally. A worker-level regression verifies that a real Telegram rejection cannot set the heartbeat delivery timestamp.

Implementation and regression evidence are in [TelegramChannel](../../src/OpenClaw.Channels/TelegramChannel.cs), [GatewayOutboundDeliveryWorker](../../src/OpenClaw.Gateway/Extensions/GatewayOutboundDeliveryWorker.cs), and [Telegram delivery tests](../../src/OpenClaw.Tests/TelegramDeliveryTests.cs).

Durable receipts, an outbound recovery queue, and safe resumption of individual unsent parts remain feature work. They retain the highest general-use priority from the original review.

## Loop intervals

Chat `/loop` now schedules elapsed durations from registration instead of translating them into calendar cron fields. Supported intervals start at 60 seconds; tests cover 60 seconds, 90 seconds, 45 minutes, 90 minutes, 25 hours, and uppercase units across day boundaries. A lightweight hosted service polls every second. Late polls dispatch once, skip missed occurrences, and retain the original cadence instead of drifting or emitting a catch-up burst.

Explicit cron scheduling remains available elsewhere in the runtime. The legacy conversion helper rejects intervals that cannot be represented exactly. Supported `/loop` duration inputs are whole seconds, minutes, or hours from 60 seconds through 365 days. Loops remain in memory and do not survive gateway restart.

Implementation and regression evidence are in [ClawLoopScheduler](../../src/OpenClaw.Core/Loops/ClawLoopScheduler.cs), [AgentLoopJob](../../src/OpenClaw.Core/Loops/AgentLoopJob.cs), and [loop interval tests](../../src/OpenClaw.Tests/LoopIntervalTests.cs).

## Verification

- Official .NET SDK 10.0.403 was SHA-512 verified and installed in a temporary directory. Solution restore passed with NuGet auditing enabled.
- Full Release solution build passed with zero warnings and zero errors.
- Focused payment, loop, gateway security, workspace, and outbound-delivery regression suites passed.
- Full solution suite passed with **3,465 passed, 11 skipped, and zero failures**. This includes 3,373 main application tests, 25 Nacos harness tests, and 67 Laya service tests.
- The 11 skipped cases require opt-in Vault, FractalMemory, or Nacos services. The browser integration test completed after downloading its required Chromium version.
- HelloAgent, the desktop first-success fixture, release documentation checks, Vault publish-boundary checks, and `git diff --check` passed.

The first focused run caught omitted JSON challenge metadata entering the new request fingerprinting path. That field is now normalized, and both REST regressions pass.

The implementation adds no dependencies and uses source-generated JSON for persisted payment state. It supports the existing AOT-friendly and JIT code paths. The macOS arm64 Gateway NativeAOT publish lane passed; live payment-provider acceptance and manual Companion GUI acceptance were not exercised.

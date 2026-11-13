# Application review, November 7, 2026

Reviewed the local checkout based on `1645940`. Scope includes completeness, payment policy, account permissions, webhook delivery, file handling, operational reliability, and opportunities ranked by user impact. The follow-up implementation starts with payment approvals and viewer permissions.

## Completeness

The application has implemented agent execution, native and Microsoft Agent Framework runtimes, sessions and ownership, approvals, durable action recovery, web chat, desktop Companion, CLI, optional channels and plugins, and offline backup/restore. The largest gaps found in this review concern authorization across surfaces, payment retry semantics, delivery outcomes, and operational recovery.

| Area | Assessment |
| --- | --- |
| Agent and tool execution | Implemented, including checkpoints and opt-in durable action journals. Provider-specific side effects still need individual reconciliation support. |
| Operator workflows | Implemented authentication, roles, session ownership, approval history, and per-session recovery. The permission defects below required repairs. |
| Payments | Implemented optional runtime, policy, approval flow, vault, mock provider, and Stripe Link CLI adapter. Live provider acceptance and payment idempotency remain important gaps. |
| Channels and webhooks | Implemented adapters and inbound deduplication. Outbound success reporting and partial delivery need more complete handling. |
| Operations | Implemented setup verification, diagnostics, audit, backup validation, and isolated restore. Backup inventory and cross-session recovery still require substantial operator effort. |

This assessment follows the repository's explicit optional, experimental, and JIT-only capability boundaries. Real provider credentials and a NativeAOT-published executable require environment-specific verification.

## Repaired findings

### P1 — Payment authorization used a different environment from execution

A request declaring `test` passed test-mode policy while a live-configured Stripe Link adapter sent `--mode live`. The isolated gateway returned HTTP 200 for an amount above its live limit, and a fake CLI recorded the live command.

The adapter now requires the authorized environment to match its configured mode before dispatching any command. Both money-moving operations have regression cases for mismatched modes, declined approvals, the live cap, and legitimate test/live execution. Browser payment mutations require CSRF. Sentinel authorization uses the vaulted provider and environment. One-time secret retrieval uses an atomic conditional removal.

Relevant code is in [StripeLinkPaymentProvider](../../src/OpenClaw.Payments.StripeLink/StripeLinkPaymentProvider.cs), [PaymentRuntimeService](../../src/OpenClaw.Payments.Core/PaymentRuntimeService.cs), and [PaymentRuntimeTests](../../src/OpenClaw.Tests/PaymentRuntimeTests.cs).

### P1 — Viewer permissions allowed administrative code execution and secret access

An actual viewer account with `canExecuteAgent=false` successfully updated workspace MCP configuration. A harmless fixture executable ran with MCP disabled. The same account read a resolved Feishu secret, updated DingTalk configuration, and uploaded a workspace file.

Workspace file access, MCP configuration, digital-employee installation, and channel configuration now require admin privileges. Media upload requires operator privileges and browser CSRF. The production watcher respects the MCP enable flag. Feishu configuration reads preserve secret references.

Admin-only access for workspace and channel configuration is an intentional policy decision because those surfaces expose credentials and control executable integrations. Existing diagnostic reads remain available according to their existing roles.

Relevant code is in [EndpointHelpers](../../src/OpenClaw.Gateway/Endpoints/EndpointHelpers.cs), [McpWorkspaceWatcherService](../../src/OpenClaw.Gateway/McpWorkspaceWatcherService.cs), and [security boundary regression tests](../../src/OpenClaw.Tests/GatewayAdminEndpointTests.SecurityBoundaries.cs).

### P1 — Filesystem links and tool-result markers bypassed file boundaries

The isolated gateway overwrote a fixture outside the workspace through an existing upload-target symlink. A `read_file` result containing a forged file marker also published that outside fixture.

Workspace operations now reject links beneath their configured root, and listings and directory archives skip links. Digital-employee archives have an expanded-content limit. Directory archives are staged in private, delete-on-close temporary files so ZIP finalization succeeds with ASP.NET synchronous response I/O disabled. Automatic publication accepts a completed `publish_file` result and rechecks the resolved destination against the tool write policy. Authorized read-only sources can still be staged by the publisher.

Relevant code is in [WorkspacePathHelpers](../../src/OpenClaw.Gateway/Endpoints/WorkspacePathHelpers.cs) and [GatewayInboundMessageWorker](../../src/OpenClaw.Gateway/Extensions/GatewayInboundMessageWorker.cs).

### P1 — Untrusted approval and webhook fields entered admin HTML

Pending approvals, approval history, reusable grants, and dead letters inserted record fields into `innerHTML` without encoding. Tool arguments and webhook bodies can contain attacker-controlled markup.

These renderers now use the existing HTML encoder. Source regression tests cover the four renderers, and executing the actual JavaScript with synthetic injected records confirmed that markup and quotes were encoded.

Relevant code is in [admin.html](../../src/OpenClaw.Gateway/wwwroot/admin.html).

### P1 — Unauthenticated webhook attempts consumed delivery identifiers

An unsigned request received HTTP 401, then a correctly signed request for the same delivery received “Webhook already processed” without enqueueing.

Generic webhook deduplication now follows HMAC verification. Twilio, Slack, and both WhatsApp webhook variants reserve deliveries inside their handlers after authentication. Regression tests cover invalid requests, accepted retries, and duplicates.

Relevant code is in [WebhookEndpoints](../../src/OpenClaw.Gateway/Endpoints/WebhookEndpoints.cs) and [WebhookDeliveryReservationTests](../../src/OpenClaw.Tests/WebhookDeliveryReservationTests.cs).

### P1/P2 — Uploaded content could execute in the gateway origin or overwrite itself

SVG uploads were served inline without a sandbox policy. JSON payloads and cache metadata used the same filename, so downloading a JSON upload returned internal metadata and an absolute server path.

Media responses now carry sandbox and content-type protection headers, and active image formats use attachment disposition. Payload and metadata filenames are distinct. Legacy metadata still loads, while already-overwritten JSON payloads are rejected.

Relevant code is in [MediaEndpoints](../../src/OpenClaw.Gateway/Endpoints/MediaEndpoints.cs) and [MediaCacheStore](../../src/OpenClaw.Gateway/MediaCacheStore.cs).

### P2 — Default integration conversations collided across accounts

The first account's text-only message established the shared `integration-api:http-client` conversation. A second account's text-only message then received HTTP 403.

Authenticated accounts now receive separate default routing identities. Explicit session routing continues through ownership checks. Bootstrap callers retain the legacy default, and existing conversations remain addressable by their stored IDs.

Relevant code is in [IntegrationApiFacade](../../src/OpenClaw.Gateway/Composition/IntegrationApiFacade.cs).

### P2 — Accounts created through JSON were disabled by default

The source-generated deserializer assigned false to the init-only `Enabled` property when callers omitted it, overriding the intended true initializer. A real account creation request reproduced the disabled account and subsequent authentication failure.

The create DTO now uses a settable `Enabled` property so deserialization preserves its initializer. Regression cases cover omitted, explicit true, and explicit false values.

Relevant code is in [OperatorGovernanceModels](../../src/OpenClaw.Core/Models/OperatorGovernanceModels.cs).

## Business-logic issues identified for follow-up

These three findings were repaired on November 13. See the [implementation and verification follow-up](confirmed-issues-2026-11-13.md). The original findings below are retained as review history.

1. **P1 — Payment retries lack an enforced idempotency contract.** `MachinePaymentRequest.IdempotencyKey` is accepted, but the runtime and Stripe Link command builder do not consume it. Repeating a request can dispatch another money-moving command. A complete repair needs provider-supported idempotency, durable request/result storage, request-content conflict checks, and reconciliation after an uncertain provider response. The repository's durable action journal provides a foundation, while direct payment API/CLI calls need coverage too. [Payment models](../../src/OpenClaw.Payments.Abstractions/PaymentModels.cs), [payment runtime](../../src/OpenClaw.Payments.Core/PaymentRuntimeService.cs)

2. **P1 — Outbound delivery can be reported as successful after provider failure.** Telegram catches send exceptions and returns normally. The outbound worker treats a normal return as success and can record heartbeat delivery success. Multi-part sends also need explicit partial-success handling to support safe retries. This finding is established by the adapter and worker control flow. [TelegramChannel](../../src/OpenClaw.Channels/TelegramChannel.cs), [GatewayOutboundDeliveryWorker](../../src/OpenClaw.Gateway/Extensions/GatewayOutboundDeliveryWorker.cs)

3. **P2 — Loop intervals are converted to different schedules.** `90s` becomes a one-minute cron interval. `45m` runs at minutes 0 and 45, producing alternating 45- and 15-minute gaps. The loop poller also runs once per minute. Duration-based scheduling needs its own next-run calculation and supported resolution. [ClawLoopScheduler](../../src/OpenClaw.Core/Loops/ClawLoopScheduler.cs)

## Feature ideas ranked by user impact

| Rank | Feature | User impact and bounded first delivery |
| --- | --- | --- |
| 1 | Durable outbound delivery receipts and recovery | Benefits every unattended channel workflow. Show queued, sent, suppressed, partial, failed, and uncertain outcomes with provider IDs. Retain unsent messages across restart and offer safe retry. Measure lost messages, duplicate deliveries, and recovery time. |
| 2 | Payment transaction history with retry reconciliation | Highest impact for payment-enabled deployments. Build on the existing audit/status endpoints with durable transaction records connecting approvals, provider references, idempotency keys, spend limits, and uncertain outcomes. Measure duplicate charges and unresolved payments. Runtime retry protection is now implemented. An operator-facing transaction history and provider reconciliation remain feature work. |
| 3 | Cross-session operator recovery queue | Extend existing per-session recovery into one queue for approvals, budget pauses, failed deliveries, and uncertain actions. Include ownership and revision-checked actions. Measure time to identify and recover blocked work. |
| 4 | Configuration-derived backup plans and restore checks | Extend the existing backup CLI with inventory discovery, missing-root detection, secret-reference checks, and an isolated restore report. Measure omitted state and time to a verified recovery. |

The ranking assumes a general self-hosted assistant deployment. Payment-heavy deployments should place transaction reconciliation first.

## Verification

| Check | Result |
| --- | --- |
| Solution restore with .NET SDK 10.0.403 | Passed with NuGet auditing enabled |
| Full Release solution build | Passed, zero warnings and zero errors |
| Main application suite | 3,317 passed, 11 skipped, zero failures |
| Nacos acceptance-harness unit suite | 25 passed |
| Laya service unit suite | 67 passed |
| New regression cases | All 33 passed |
| HelloAgent sample | Passed through the deterministic tool execution path |
| Desktop first-success Python fixture | Passed |
| Release documentation and Vault publish-boundary checks | Passed |
| NuGet vulnerability scan, including transitive packages | Zero vulnerable entries reported across 43 projects |
| Admin renderer injection probes | All four renderers encoded the injected markup |
| Actual gateway HTTP probes | Verified denied and allowed payment paths, role restrictions, MCP disabled behavior, default account state, independent conversations, file boundaries, media bytes/headers, webhook authentication order, and directory download |

The final solution suite completed with **3,409 passed, 11 skipped, and zero failures**. Skipped cases require opt-in Vault, FractalMemory, or Nacos environments. The Nacos acceptance-harness unit results above exercise the harness itself. Live-provider credentials, desktop GUI interaction, and a NativeAOT-published executable remain environment acceptance work.

The first full run exposed an existing timing-dependent MAF concurrency test. Its two 120 ms delays were replaced with an explicit asynchronous rendezvous. Credential propagation and actual concurrent-entry assertions are preserved. The isolated test and subsequent complete solution run passed.

Verification used an isolated SDK 10.0.403 under a temporary directory. The initially installed SDKs selected NativeAOT compiler packages rejected by NuGet with NU1903. The isolated SDK restored successfully with vulnerability checks enabled. [NativeAOT compiler advisory](https://github.com/advisories/GHSA-p3jp-9v83-76w9)

Runtime reproductions used temporary accounts, local fixtures, a fake model endpoint, and a fake payment CLI. The implementation uses existing dependencies and source-generated JSON contracts across the NativeAOT-friendly and JIT paths.

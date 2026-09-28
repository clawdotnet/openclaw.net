# Changelog

All notable changes to this project are tracked in this file.

## [Unreleased]

### Release Engineering

- Standardized the desktop product name as AgentQi Companion while preserving OpenClaw.NET runtime, package, protocol, and storage identities.
- Added a release-blocking desktop first-success contract covering Companion preset persistence, shared preset capabilities, sequential multi-tool selection, and an Ollama-compatible tool round trip.
- Added a release documentation gate that rejects tags still described as future or unavailable work, and updated v0.3.0 availability guidance.
- Made the deterministic pinned public-plugin compatibility smoke run on pull requests and `main` pushes, while keeping the moving latest-package canary scheduled/manual and non-blocking.
- Added the pinned public-plugin compatibility smoke as a prerequisite for release asset builds.
- Pinned Supermemory's complete plugin/peer test set and kept a separate latest-peer canary so release verification is reproducible while upstream drift remains visible.
- Clarified exact-head merge checks, v0.2.0 verification scope, main-only reliability features, and the OpenClaw.NET/AgentQi/AgentQiX product boundary.

### MCP v2 Upgrade

- Upgraded MCP integration surfaces to C# SDK `2.0.0` behavior across gateway, MCP App, and client-facing SDK helpers.
- Enforced stricter MCP App tool schema handling: tools with missing/defaulted `inputSchema` are skipped during enumeration instead of silently normalizing to `{}`.
- Added MCP App regression coverage for missing schema tool descriptors and preserved structured-content suppression behavior.
- Added `OpenClawHttpClient.DiscoverMcpAsync` and MCP discover models with protocol-aware fallback to initialize-first servers.
- Added `McpCallToolResult.StructuredContent` to `OpenClaw.Client` models for v2 response parity.
- Registered MCP Tasks extension on gateway MCP server via `ModelContextProtocol.Extensions.Tasks` with in-memory task store.
- Added gateway MCP proxy regression test to validate MCP server availability after Tasks extension enablement.

### Integration API, MCP, and SDK

- Added a gateway-hosted typed integration API under `/api/integration` for operational reads and inbound message enqueueing.
- Added typed integration read models for dashboard snapshots, approvals, approval history, providers, plugins, operator audit, session detail, and session timelines.
- Added a gateway-hosted MCP JSON-RPC facade at `/mcp` over the shared integration/runtime surface.
- Added starter MCP contracts for initialize, tool listing/calling, resource listing/reading, resource templates, and prompt listing/retrieval.
- Added a reusable `IntegrationApiFacade` so the integration API, MCP facade, and operator dashboard share the same gateway-side read logic.
- Added a shared `OpenClaw.Client` package and expanded `OpenClawHttpClient` with typed integration API methods plus MCP helpers such as `InitializeMcpAsync`, `ListMcpToolsAsync`, `ReadMcpResourceAsync`, `GetMcpPromptAsync`, and `CallMcpToolAsync`.
- Repointed the operator dashboard read paths to the typed integration API while keeping the existing admin mutation flows intact.

### Security

- Required the `operator` role wherever a request runs the agent or mutates state, matching `POST /api/integration/messages`. Previously any authenticated identity, including viewer account tokens, viewer browser sessions, and OIDC users without an operator role claim, could run the agent with tools through these surfaces. New operator accounts default to `viewer`, so grant `operator` to accounts used for Companion, CLI/TUI chat, and API clients.
  - `/ws`: closed with code 1008, which web chat reports as an authorization failure.
  - `/ws/live`: closed with code 1008. The live model bridge runs no tools but spends provider credentials.
  - `POST /v1/chat/completions` and `POST /v1/responses`: 403 with an OpenAI-style `permission_error` body.
  - A2A execution paths: 403. Discovery stays public.
  - `POST /apps/chat`: 403.
  - `tools/call` through the `/apps/mcp/{appId}` MCP App proxy: tool error result. Listing and reading App tools and resources stay available to any authenticated role.
  - MCP `openclaw.send_message`, `openclaw.run_workflow`, and `openclaw.respond_workflow`: tool error result. Read-only MCP tools stay available to viewers.
  - Each denial is logged under `OpenClaw.Gateway.Authorization` with the surface, account, and role, so admins can find accounts to promote.
  - Migration aid: `OpenClaw:Security:AllowViewerAgentExecution=true` restores the previous behavior for authenticated identities below `operator`, logs each such request, and adds the `viewer_agent_execution_allowed` risk flag to `admin posture`. It is temporary and will be removed in the next release.
- Stopped trusting a loopback client IP on `/apps/health`, `/apps/chat`, and `/apps/mcp/{appId}`. Behind a same-host reverse proxy without `TrustForwardedHeaders`, every caller has a loopback IP, so these routes answered unauthenticated requests, including agent runs and MCP App tool calls, and ignored `AlwaysRequireAuth`. They now follow the gateway's bind-based rule: open only on a loopback-bound gateway without `AlwaysRequireAuth`.
- Turns now run as the signed-in account instead of a caller-supplied sender id. `Session.AuthenticatedUserId` scopes per-user capability bindings and is passed to MCP servers as `_meta.userId`. Previously only `/ws` set it, so REST messages, MCP `send_message`, A2A, `/v1/*`, and `/apps/chat` turns ran as whatever `senderId` or `contextId` the caller supplied. MCP servers that read `_meta.userId` now receive account ids for those turns.
- A turn from an external sender without an account no longer inherits the previous writer's account. Previously, after an operator posted into a Telegram session, the Telegram user's next turns ran as that operator. System, scheduled, automation, and background-continuation turns keep the session's identity.
- Added `OpenClawWebSocketClient.OnClosed`, raised with the gateway's close status and reason. Companion now marks itself disconnected and shows the reason instead of appearing connected after the gateway closes the socket. It also checks the account's role before connecting: a gateway-reported role below `operator` gets an explanation instead of a chat connection, and the read-only status views stay available.
- Bound tool-approval decisions to the original requester (`channelId` + `senderId`) for non-loopback/public binds.
- Kept `POST /tools/approve` as an explicit admin override path.
- Added WhatsApp official webhook signature validation support (`ValidateSignature`, `WebhookAppSecret`/`WebhookAppSecretRef`).
- Added WhatsApp bridge inbound auth validation via `Authorization: Bearer <BridgeToken>` or `X-Bridge-Token`.
- Enforced additional non-loopback startup hardening:
  - WhatsApp official mode requires signature validation + app secret.
  - WhatsApp bridge mode requires a bridge token.
- Hardened generic webhook HMAC verification:
  - `ValidateHmac=true` now requires a secret at config validation time.
  - Signature checks now use constant-time byte comparison and support `sha256=<hex>` header format.
- Hardened SQL write detection in `database` tool by tokenizing SQL and detecting write/admin keywords beyond naive prefix checks.
- Hardened `inbox_zero` IMAP command construction:
  - Quoted IMAP credentials and folders.
  - Sanitized user-provided folder names for analyze/cleanup/trash-sender actions.
- Added Vault / OpenBao secret resolver backend (`OpenClaw.Security.Vault`): KV v2 read with token auth, TTL cache with single-flight and refresh-ahead, startup pre-warm via `IHostedService`, and `vault:<mount>/data/<path>#<key>` reference syntax. See `docs/security/vault.md`.
- Added `Security.Vault.Tls.CaCertPath` support: load a custom CA bundle (PEM file or directory) for Vault TLS validation; mutually exclusive with `Tls.SkipVerify`.
- Made `vault:` references fail closed: resolving a `vault:` ref while the Vault backend is disabled now throws `VaultNotConfiguredException` instead of silently falling back to the literal string.

### Memory Retention and Hardening

- Added opt-in memory retention configuration at `OpenClaw:Memory:Retention`:
  - `Enabled` (default `false`)
  - `RunOnStartup` (default `true`)
  - `SweepIntervalMinutes` (default `30`)
  - `SessionTtlDays` (default `30`)
  - `BranchTtlDays` (default `14`)
  - `ArchiveEnabled` (default `true`)
  - `ArchivePath` (default `./memory/archive`)
  - `ArchiveRetentionDays` (default `30`)
  - `MaxItemsPerSweep` (default `1000`)
- Added retention store abstraction (`IMemoryRetentionStore`) and new retention models:
  - `RetentionSweepRequest`
  - `RetentionSweepResult`
  - `RetentionStoreStats`
  - `RetentionRunStatus`
- Implemented retention sweep support in both file and sqlite memory stores.
- Added archive-before-delete behavior for expired sessions/branches with raw JSON archive envelopes.
- Added archive TTL purge behavior for old archive files.
- Added sqlite indexes to improve retention candidate queries:
  - `idx_sessions_updated_at`
  - `idx_branches_updated_at`
- Added proactive in-memory active-session expiry sweep (`SessionManager.SweepExpiredActiveSessions`) and wired it into periodic cleanup.
- Added background retention sweeper service (`PeriodicTimer`, overlap-safe with semaphore).
- Added retention admin endpoints:
  - `GET /memory/retention/status`
  - `POST /memory/retention/sweep` (supports `dryRun=true`)
- Extended doctor outputs (`/doctor`, `/doctor/text`) with retention config/status/stats and disabled-retention warnings for large persisted counts.
- Extended runtime metrics with retention counters and last-run status gauges.
- Corrected compaction validation semantics: when compaction is enabled, `CompactionThreshold` must be greater than `MaxHistoryTurns`.

### Usability/Safety Balance

- WebChat token persistence now defaults to session-only storage (`sessionStorage`).
- Added a `Remember` toggle to opt into persistent token storage (`localStorage`).

### Tests

- Added `ToolApprovalServiceTests` for requester-bound approvals and admin override behavior.
- Added `GatewaySecurityHardeningTests` for public-bind hardening checks (WhatsApp and raw refs).
- Expanded `GatewaySecurityTests` for HMAC signature validation.
- Expanded `ConfigValidatorTests` for webhook-HMAC-secret and WhatsApp-app-secret validation.
- Added retention validation coverage in `ConfigValidatorTests`.
- Added `FileMemoryStoreRetentionTests` (archive/delete, protected sessions, archive failure handling, archive purge).
- Added `SqliteMemoryStoreRetentionTests` (archive/delete, protected sessions, max item cap, index creation, archive failure handling).
- Added `MemoryRetentionSweeperServiceTests` (manual sweep status/metrics and overlap prevention).
- Added proactive expiry coverage in `SessionManagerTests` (`SweepExpiredActiveSessions`).
- Expanded `NativePluginTests` with SQL write-bypass regression cases.
- Expanded `SecurityTests` with InboxZero folder-sanitization coverage.
- Added focused gateway/admin endpoint coverage for the typed integration API, MCP facade, route exposure, and the shared `OpenClaw.Client` MCP surface.

### Documentation

- Updated:
  - `README.md`
  - `QUICKSTART.md`
  - `USER_GUIDE.md`
  - `SECURITY.md`
  - `CHANGELOG.md`
  - `TOOLS_GUIDE.md`
- Updated dynamic routing docs to state current limitations, experimental maturity, and the need to avoid OpenSquilla-equivalent accuracy claims.

### Docker

- Fixed Docker runtime env var binding to use `OpenClaw__...` (ASP.NET configuration) for gateway bind/port/memory settings.
- Docker defaults now disable the JS plugin bridge on non-loopback binds (`OpenClaw__Plugins__Enabled=false`) unless explicitly enabled.
- Standardized default image name to `openclaw.net` for local builds and compose.
- Re-pushed Docker Hub images without provenance/SBOM to improve Docker Hub UI compatibility.
- Added `DOCKERHUB.md` as paste-ready repository overview content for Docker Hub.

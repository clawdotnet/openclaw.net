# Changelog

All notable changes to this project are tracked in this file.

## [Unreleased]

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

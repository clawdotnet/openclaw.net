# External Coding Backends

## Summary

OpenClaw.NET now supports an OSS-first external coding backend lane for session-capable coding CLIs. This lane stays separate from the native OpenClaw agent runtime and treats Codex CLI, Gemini CLI, and GitHub Copilot CLI as external backends that OpenClaw can probe, start, stream, and stop.

The startup boundaries match the current gateway architecture:

- `Bootstrap/`
  - binds and normalizes `GatewayConfig.CodingBackends`
  - validates backend ids, timeout values, workspace paths, and credential-source shape before build
- `Composition/`
  - registers account services, credential resolution, backend registry/coordinator, subprocess host, and real CLI backends in `BackendServicesExtensions`
- `Endpoints/`
  - exposes modular integration/admin endpoints for accounts and backends without bloating `Program.cs`

## Architecture

- `ConnectedAccountService`
  - creates, lists, loads, and deletes connected accounts
  - protects stored secret blobs with ASP.NET Data Protection
- `BackendCredentialResolver`
  - resolves credentials from env refs, raw secrets, token files, or stored connected accounts
- `CodingAgentBackendRegistry`
  - exposes registered backends and their definitions
- `BackendSessionCoordinator`
  - starts/stops backend sessions and persists session records plus ordered events
- `CodingBackendProcessHost`
  - owns `System.Diagnostics.Process`, async stdout/stderr streaming, stdin writes, timeout/cancellation, and exit-code handling

Persisted records reuse the existing feature-store seams:

- `connected-accounts`
  - file-backed directory or `connected_accounts` sqlite table
- `backend-sessions`
  - file-backed directory or `backend_sessions` sqlite table
- `backend-session-events`
  - file-backed directory or `backend_session_events` sqlite table

Backend sessions are separate from native OpenClaw `Session` history. A backend session may reference an owning OpenClaw session id, but backend events are not dual-written into the core session transcript in this version.
When `OwnerSessionId` is supplied, OpenClaw now mirrors key backend events into that owner session history as tagged `user`, `assistant`, or `system` turns so the control-plane session remains readable.

## Account And Credential Resolution

Supported OSS account modes:

- direct secret refs
  - `env:MY_TOKEN`
  - `raw:actual-secret`
- direct token file paths
  - absolute or expanded local file paths
- stored connected accounts
  - protected secret blob
  - stored secret ref
  - stored token file path

Resolution priority is explicit request source first, then backend default credential config. Stored connected accounts are rejected when inactive or expired.

No OAuth flow is required for local mode.

## Config

The canonical gateway config gains `CodingBackends` with three backend sections:

- `Codex`
- `GeminiCli`
- `GitHubCopilotCli`

Each section uses the same shape:

```json
{
  "Enabled": true,
  "BackendId": "codex",
  "Provider": "codex",
  "DisplayName": "Codex CLI",
  "ExecutablePath": "codex",
  "Args": ["exec"],
  "ProbeArgs": ["--help"],
  "TimeoutSeconds": 600,
  "Environment": {
    "OPENAI_BASE_URL": "env:OPENAI_BASE_URL"
  },
  "DefaultModel": "gpt-5-codex",
  "RequireWorkspace": true,
  "DefaultWorkspacePath": "/absolute/workspace",
  "ReadOnlyByDefault": false,
  "WriteEnabled": true,
  "PreferStructuredOutput": true,
  "Credentials": {
    "SecretRef": "env:OPENAI_API_KEY",
    "TokenFilePath": null,
    "ConnectedAccountId": null
  }
}
```

Example Codex CLI config:

```json
{
  "CodingBackends": {
    "Codex": {
      "Enabled": true,
      "BackendId": "codex",
      "Provider": "codex",
      "ExecutablePath": "codex",
      "Args": ["exec"],
      "DefaultModel": "gpt-5-codex",
      "RequireWorkspace": true,
      "ReadOnlyByDefault": false,
      "WriteEnabled": true,
      "Credentials": {
        "SecretRef": "env:OPENAI_API_KEY"
      }
    }
  }
}
```

Example Gemini CLI config:

```json
{
  "CodingBackends": {
    "GeminiCli": {
      "Enabled": true,
      "BackendId": "gemini-cli",
      "Provider": "gemini-cli",
      "ExecutablePath": "gemini",
      "Args": ["--checkpointing"],
      "DefaultModel": "gemini-2.5-pro",
      "ReadOnlyByDefault": true,
      "WriteEnabled": true,
      "Credentials": {
        "TokenFilePath": "/absolute/path/to/gemini.token"
      }
    }
  }
}
```

Example GitHub Copilot CLI config:

```json
{
  "CodingBackends": {
    "GitHubCopilotCli": {
      "Enabled": true,
      "BackendId": "copilot-cli",
      "Provider": "github-copilot-cli",
      "ExecutablePath": "copilot",
      "Args": ["chat"],
      "DefaultModel": "gpt-4.1",
      "ReadOnlyByDefault": false,
      "WriteEnabled": true,
      "Credentials": {
        "ConnectedAccountId": "acct_1234567890abcd"
      }
    }
  }
}
```

## HTTP Endpoints

Integration endpoints:

- `GET /api/integration/accounts`
- `GET /api/integration/accounts/{id}`
- `POST /api/integration/accounts`
- `DELETE /api/integration/accounts/{id}`
- `GET /api/integration/backends`
- `GET /api/integration/backends/{id}`
- `POST /api/integration/backends/{id}/probe`
- `POST /api/integration/backends/{id}/sessions`
- `GET /api/integration/backends/{id}/sessions/{sessionId}`
- `POST /api/integration/backends/{id}/sessions/{sessionId}/input`
- `DELETE /api/integration/backends/{id}/sessions/{sessionId}`
- `GET /api/integration/backends/{id}/sessions/{sessionId}/events?afterSequence=0&limit=100`
- `GET /api/integration/backends/{id}/sessions/{sessionId}/events/stream?afterSequence=0&limit=100`

Admin endpoints:

- `POST /admin/accounts/test-resolution`
- `GET /admin/backends`
- `GET /admin/backends/{id}`
- `POST /admin/backends/{id}/probe`

## CLI Usage

Accounts:

```bash
openclaw accounts list
openclaw accounts add codex --display-name "Local Codex" --secret-ref env:OPENAI_API_KEY
openclaw accounts add gemini-cli --display-name "Gemini Token File" --token-file ~/.config/gemini/token
openclaw accounts remove acct_1234567890abcd
openclaw accounts probe acct_1234567890abcd
openclaw accounts probe codex --backend codex
```

Backends:

```bash
openclaw backends list
openclaw backends probe codex --workspace /absolute/workspace
openclaw backends run codex --workspace /absolute/workspace --prompt "Review the current diff"
openclaw backends session send codex bks_1234567890abcdef --text "continue"
```

## Event Model

Backends emit normalized `BackendEvent` records. Current event types include:

- assistant message
- stdout output
- stderr output
- tool call requested
- shell command proposed
- shell command executed
- patch proposed
- patch applied
- file read
- file write
- error
- session completed

Every backend event can also carry `RawLine` when it originated from line-oriented process output. This preserves the original CLI output even when OpenClaw also normalizes that line into a higher-level event.

Polling remains available through the JSON events endpoint. Live event delivery is now available through an SSE stream endpoint for backend sessions.

## Limitations

- Backend adapters are best-effort wrappers around vendor CLIs. They normalize what they can, then fall back to raw stdout/stderr events.
- Structured event parsing only works when the underlying CLI exposes a usable structured mode.
- Codex CLI now prefers structured output when the configured args are compatible and probe detection confirms `--json` support.
- Gemini CLI and GitHub Copilot CLI currently use text-first normalization because this repo does not assume a stable structured streaming protocol for them yet.
- Plain text fallback is broader now, but still heuristic. Shell, tool, file, and patch events can still fall back to raw stdout/stderr when a vendor CLI changes its output format.
- Authentication is BYO only. This change does not add billing, entitlements, hosted identity, or SaaS plan concepts.
- GitHub Copilot CLI auth is not tied to GitHub repository access in this lane.
- External backend sessions are still persisted separately from native OpenClaw session records even when owner-session history sync is enabled.

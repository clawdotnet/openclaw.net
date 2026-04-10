# OSS External Coding Backends Summary

## What Was Added

- connected account storage and CRUD for OSS credential handling
- backend credential resolution from secret refs, raw secrets, token files, and stored connected accounts
- backend abstractions, registry, coordinator, and fake backend
- subprocess host for session-capable CLI backends
- Codex CLI, Gemini CLI, and GitHub Copilot CLI backend adapters
- integration and admin HTTP endpoints for accounts, backend discovery, probe, sessions, and event polling
- SSE streaming for backend session events
- owner-session history sync for key backend events when `OwnerSessionId` is supplied
- CLI commands for `openclaw accounts` and `openclaw backends`
- tests for stores, credential resolution, registry/session lifecycle, process host, gateway endpoints, CLI parsing, and adapter argument/parser behavior

## New Config Keys

- `GatewayConfig.CodingBackends`
- `CodingBackends.Codex`
- `CodingBackends.GeminiCli`
- `CodingBackends.GitHubCopilotCli`
- `CodingCliBackendConfig.Enabled`
- `CodingCliBackendConfig.BackendId`
- `CodingCliBackendConfig.Provider`
- `CodingCliBackendConfig.DisplayName`
- `CodingCliBackendConfig.ExecutablePath`
- `CodingCliBackendConfig.Args`
- `CodingCliBackendConfig.ProbeArgs`
- `CodingCliBackendConfig.TimeoutSeconds`
- `CodingCliBackendConfig.Environment`
- `CodingCliBackendConfig.DefaultModel`
- `CodingCliBackendConfig.RequireWorkspace`
- `CodingCliBackendConfig.DefaultWorkspacePath`
- `CodingCliBackendConfig.ReadOnlyByDefault`
- `CodingCliBackendConfig.WriteEnabled`
- `CodingCliBackendConfig.PreferStructuredOutput`
- `CodingCliBackendConfig.Credentials.SecretRef`
- `CodingCliBackendConfig.Credentials.TokenFilePath`
- `CodingCliBackendConfig.Credentials.ConnectedAccountId`

## Supported Account Modes

- direct env or raw secret refs through `SecretRef`
- direct raw secret submission through `openclaw accounts add ... --secret ...`
- local token file paths
- stored connected accounts backed by:
  - protected secret blobs
  - stored secret refs
  - stored token file paths

## Supported Backends

- Codex CLI
- Gemini CLI
- GitHub Copilot CLI
- Fake backend for tests and local non-binary validation

## Limitations

### Codex CLI

- structured event normalization is best-effort and depends on CLI output mode
- OpenClaw now prefers Codex structured output when the backend is configured in a compatible mode and probe detection confirms `--json` support
- workspace/read-only flags are mapped from currently observed CLI behavior and may need adjustment as the CLI evolves
- fallback lines retain `RawLine` and drop to explicit stdout/stderr events when normalization is low-confidence

### Gemini CLI

- this is separate from the native OpenClaw Gemini provider integration
- event normalization falls back to text parsing when the CLI does not emit structured lines
- text fallback recognizes more action-oriented lines, but vendor output changes can still reduce fidelity
- low-confidence lines are preserved as raw stdout/stderr events instead of being forced into assistant text

### GitHub Copilot CLI

- auth is handled through BYO credentials only
- backend auth remains separate from GitHub repo access and existing GitHub connector behavior
- event normalization is best-effort and text-first when output is not structured
- low-confidence lines are preserved as raw stdout/stderr events instead of being forced into assistant text
- live streaming is available through the gateway SSE endpoint, but CLI semantics still depend on the underlying process staying session-capable

## Recommended Next Step

The next follow-up should be a small `Claude Code` backend adapter that reuses the same account resolution, subprocess host, registry, and endpoint seams introduced here. It should stay out of this PR and avoid changing the current backend/event contracts unless a concrete protocol need appears.

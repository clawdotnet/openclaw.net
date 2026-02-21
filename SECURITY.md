# Security Policy

## Supported Versions

Only the most recent stable release of OpenClaw is officially supported with security updates. We also accept security reports for the `main` branch.

| Version | Supported          |
| ------- | ------------------ |
| v1.x    | :white_check_mark: |
| < 1.0   | :x:                |

## Reporting a Vulnerability

We take security seriously. If you discover a security vulnerability in OpenClaw, please **do not open a public issue**. Instead:

1. Email your findings to `security@openclaw.net` (or specific address if desired).
2. Include a description of the vulnerability, steps to reproduce, and potential impact.
3. We will acknowledge receipt within 48 hours.
4. We will coordinate a fix and release timeline with you.

### What qualifies as a vulnerability?

- **Sandbox escapes**: Reading/writing files outside allowed directories (`AllowedReadRoots`/`AllowedWriteRoots`).
- **Remote Code Execution (RCE)**: Executing unauthorized code or commands (when `AllowShell=false` or beyond tool scope).
- **Authentication bypass**: Accessing the gateway without a valid `OPENCLAW_AUTH_TOKEN` (on public binds).
- **Data leakage**: Exposure of sensitive environment variables or file contents through unintended channels.
- **Denial of Service (DoS)**: Crashing the gateway with malformed input (NativeAOT panic).

### What is NOT a vulnerability?

- **"Self-XSS"**: Executing JS in the user's own browser console.
- **LLM hallucinations**: The model generating incorrect or biased content (this is an upstream provider issue).
- **Social engineering**: Phishing or tricking users into running dangerous commands via `shell` tool (when authorized).
- **Resource exhaustion**: When limits (`SessionTokenBudget`, `RateLimit`) are explicitly disabled by configuration.

## Security Best Practices

When running OpenClaw in production:

1. **Always set `OPENCLAW_AUTH_TOKEN`**: Especially when binding to `0.0.0.0`.
2. **Use TLS**: Run behind a reverse proxy (Caddy/nginx) or configure Kestrel HTTPS.
3. **Restrict Tools**: Set `AllowShell=false` unless strictly necessary.
4. **Isolate Scope**: Run in a container with minimal privileges (non-root).
5. **Monitor Logs**: Watch for `EventId=Security` warnings in structured logs.
6. **Limit Roots**: Configure `AllowedReadRoots` and `AllowedWriteRoots` to specific subdirectories.
7. **Set Budgets**: Use `SessionTokenBudget` to prevent runaway costs.

## Tool Execution

By design, OpenClaw executes tools (`shell`, `git`, `code_exec`) that can be dangerous.
- **`delegate_agent`**: Sub-agents inherit the permissions of the parent session.
- **`shell`**: Disabled by default in `Production` environment.
- **`read_file`/`write_file`**: Path traversal is blocked, but ensure roots are scoped correctly.

## Dependencies

We use `dotnet list package --vulnerable` in CI to check for known vulnerabilities in dependencies.
Docker images are rebuilt weekly to pick up OS security patches in the base image.

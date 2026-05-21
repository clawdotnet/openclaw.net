# Tailscale Deployment

OpenClaw.NET can be exposed privately inside a tailnet using Tailscale Serve.

In this setup, OpenClaw.NET remains the local agent runtime and gateway. Tailscale provides private network access and identity-aware connectivity between devices.

```text
Tailnet user/device
    |
Tailscale Serve
    |
http://127.0.0.1:18789
    |
OpenClaw.NET Gateway
    |-- /chat
    |-- /admin
    |-- /mcp
    |-- /api/integration/*
    |-- /ws
    |-- /health
    `-- /doctor/text
```

## Recommended Pattern: Loopback + Serve

Run OpenClaw.NET locally:

```bash
dotnet run --project src/OpenClaw.Cli -c Release -- start
```

or:

```bash
openclaw start
```

OpenClaw.NET listens locally, usually at:

```text
http://127.0.0.1:18789
```

Then expose it privately:

```bash
tailscale serve --bg http://127.0.0.1:18789
```

Open the Tailscale-provided HTTPS URL from a device in your tailnet.

Do not bind OpenClaw.NET publicly just to use Tailscale Serve. Keep the gateway loopback-bound and let Tailscale handle private tailnet reachability.

## Useful Paths

- `/chat`
- `/admin`
- `/mcp`
- `/api/integration/status`
- `/ws`
- `/health/ready`
- `/doctor/text`

## Why Use Tailscale Serve?

- Avoids direct public internet exposure.
- Keeps OpenClaw.NET bound to `127.0.0.1`.
- Works well for private admin access.
- Supports tailnet ACLs.
- Helps teams and multi-device development without changing provider, tool, memory, approval, or channel settings.

## Serve vs Funnel

Tailscale Serve is for private tailnet access.

Tailscale Funnel is for public internet exposure.

Use Funnel only for:

- short-lived demos
- webhook testing
- public integrations that require internet reachability

Do not expose `/admin` through Funnel unless:

- operator auth is enabled
- public-bind hardening has been reviewed
- unsafe tools are disabled or approval-gated
- webhook signatures are configured
- admin posture and doctor checks are clean

Treat Funnel like any other public deployment path.

## Suggested Setup Command

Use the guided setup helper to print the recommended Serve command and validation steps:

```bash
openclaw setup tailscale serve
```

With an external config:

```bash
openclaw setup tailscale serve --config ~/.openclaw/config/openclaw.settings.json
```

The command does not enable Tailscale automatically and does not change provider config. It prints private-access instructions, checks the local Tailscale CLI when available, and reminds you to keep OpenClaw.NET loopback-bound.

To generate a config that records the deployment profile while preserving local defaults:

```bash
openclaw setup --profile tailscale-serve --workspace ./workspace --provider openai --model gpt-4o --api-key env:MODEL_PROVIDER_KEY
```

The `tailscale-serve` profile behaves like the local setup profile and adds deployment metadata for status/doctor visibility. It does not set `OpenClaw:Tailscale:Enabled`.

## Security Checklist

- Keep OpenClaw.NET bound to `127.0.0.1` when using Serve.
- Keep operator accounts enabled.
- Use operator tokens for CLI, API, and WebSocket clients.
- Do not put provider keys in URLs.
- Do not disable approvals for high-risk tools.
- Run `openclaw setup status`, `/doctor/text`, and `openclaw admin posture` before any public exposure.
- Treat Funnel as public exposure.
- Do not trust Tailscale identity headers for OpenClaw.NET operator auth unless a reviewed auth integration explicitly enables that behavior.

## Troubleshooting

- `tailscale` command not found: install Tailscale or configure Serve manually from the Tailscale app.
- Tailnet device cannot connect: run `tailscale status`, confirm both devices are in the same tailnet, and review ACLs.
- Gateway not running: start OpenClaw.NET and check `http://127.0.0.1:18789/health/ready`.
- Wrong local port: pass `--local-url http://127.0.0.1:<port>` to `openclaw setup tailscale serve` and update the Serve command.
- `/admin` auth fails: confirm the browser session, bootstrap token, account token, or `OPENCLAW_AUTH_TOKEN` flow you intend to use.
- `/ws` connection fails: use the Tailscale HTTPS URL, confirm auth headers/tokens, and verify WebSocket clients are allowed by the gateway config.
- Mixed Funnel/Serve confusion: run `tailscale serve status` and confirm you are not using Funnel for admin surfaces unintentionally.
- Identity headers not present: this is expected for many setups. OpenClaw.NET only reports them for visibility and does not use them for auth in this feature.

## Aperture Note

Aperture by Tailscale is a separate model gateway integration. Use Aperture when you want centralized model access, provider routing, usage telemetry, or spend controls. Use Tailscale Serve when you want private access to the OpenClaw.NET runtime itself.

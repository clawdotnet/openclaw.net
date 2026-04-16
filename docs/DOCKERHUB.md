# tellikoroma/openclaw.net

Self-hosted OpenClaw.NET gateway + agent runtime in .NET (NativeAOT-friendly).

Registry mirrors for the same published image:

- `ghcr.io/clawdotnet/openclaw.net:latest`
- `tellikoroma/openclaw.net:latest`
- `public.ecr.aws/u6i5b9b7/openclaw.net:latest`

This image runs the gateway on port `18789` and persists memory under `/app/memory`.

> **Breaking change**: `OPENCLAW_AUTH_TOKEN` is now the bootstrap and breakglass credential for the container, not the recommended day-to-day operator login. After first start, create a named operator account and use that for browser admin sessions and operator account tokens.

## Quick start

```bash
docker run -d --name openclaw \
  -p 18789:18789 \
  -e MODEL_PROVIDER_KEY="sk-..." \
  -e OPENCLAW_AUTH_TOKEN="$(openssl rand -hex 32)" \
  -v openclaw-memory:/app/memory \
  -v "$(pwd)/workspace:/app/workspace" \
  tellikoroma/openclaw.net:latest
```

Open the admin UI at `http://127.0.0.1:18789/admin`, use the bootstrap token once to create your first operator account, then sign in with that account for normal browser admin use.

For chat-only local usage, `/chat` can still use the token flow it prompts for.

If you are deploying outside Docker, the supported bootstrap path is:

- `openclaw setup`
- `openclaw setup launch`
- `openclaw setup service`
- `openclaw setup status`

If you prefer a different registry, the equivalent commands are:

```bash
docker run -d --name openclaw \
  -p 18789:18789 \
  -e MODEL_PROVIDER_KEY="sk-..." \
  -e OPENCLAW_AUTH_TOKEN="$(openssl rand -hex 32)" \
  -v openclaw-memory:/app/memory \
  -v "$(pwd)/workspace:/app/workspace" \
  ghcr.io/clawdotnet/openclaw.net:latest
```

```bash
docker run -d --name openclaw \
  -p 18789:18789 \
  -e MODEL_PROVIDER_KEY="sk-..." \
  -e OPENCLAW_AUTH_TOKEN="$(openssl rand -hex 32)" \
  -v openclaw-memory:/app/memory \
  -v "$(pwd)/workspace:/app/workspace" \
  public.ecr.aws/u6i5b9b7/openclaw.net:latest
```

## Required environment variables

- `MODEL_PROVIDER_KEY`: your LLM provider API key.
- `OPENCLAW_AUTH_TOKEN`: bootstrap / breakglass token for the non-loopback container bind.

## Common optional environment variables

- `MODEL_PROVIDER_MODEL` (default `gpt-4o`)
- `MODEL_PROVIDER_ENDPOINT` (default empty)

## Default hardening in the container

This image sets safe defaults for public binds:
- Shell tool disabled (`OpenClaw__Tooling__AllowShell=false`)
- File tool roots limited to `/app/workspace`
- JS plugin bridge disabled by default (`OpenClaw__Plugins__Enabled=false`)

If you intentionally want plugins, explicitly set:
- `OpenClaw__Plugins__Enabled=true`

## Volumes

- `/app/memory`: persisted sessions/branches/notes
- `/app/workspace`: optional workspace mount for read/write file tools

## Healthcheck

The image includes a healthcheck that runs:
- `/app/OpenClaw.Gateway --health-check`

## Docker Compose

Equivalent image values:

- `image: tellikoroma/openclaw.net:latest`
- `image: ghcr.io/clawdotnet/openclaw.net:latest`
- `image: public.ecr.aws/u6i5b9b7/openclaw.net:latest`

```yaml
services:
  openclaw:
    image: tellikoroma/openclaw.net:latest
    restart: unless-stopped
    ports:
      - "18789:18789"
    environment:
      - MODEL_PROVIDER_KEY=${MODEL_PROVIDER_KEY}
      - OPENCLAW_AUTH_TOKEN=${OPENCLAW_AUTH_TOKEN}
      - OpenClaw__BindAddress=0.0.0.0
      - OpenClaw__Port=18789
    volumes:
      - openclaw-memory:/app/memory
      - ./workspace:/app/workspace

volumes:
  openclaw-memory:
```

## Notes for WebChat

WebChat connects to `/ws` with a query token (`?token=`). For non-loopback binds, enable legacy query tokens if you use the built-in WebChat:
- `OpenClaw__Security__AllowQueryStringToken=true`

## Operator Auth Notes

- Browser admin UI is account/session-first.
- Companion, CLI automation, API clients, and websocket clients should use operator account tokens.
- The gateway exposes `POST /auth/operator-token` for credential-to-token exchange.
- Mutation access is role-gated across `viewer`, `operator`, and `admin`.

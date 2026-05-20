# Tailscale And Aperture Deployment

This guide explains how OpenClaw.NET can fit with Tailscale and Aperture by Tailscale without changing the default local-first runtime.

## Overview

OpenClaw.NET is the runtime and harness layer. It owns agents, tools, sessions, memory, approvals, MCP, channels, the Admin UI, runtime events, evidence, governance, and harness execution.

Tailscale is the private networking and identity-aware access layer. It owns tailnet reachability, ACLs, remote connectivity, and optional secure transport to the runtime.

Aperture is an optional upstream AI gateway layer. It owns model/provider routing, model access governance, usage telemetry, and spend or access controls. OpenClaw.NET does not become an Aperture replacement.

```text
Client / Operator
    |
Tailscale Serve / Tailnet
    |
OpenClaw.NET
    |
Aperture (optional provider profile)
    |
OpenAI / Anthropic / Gemini / others
```

## Recommended Deployment Patterns

- Local-only development: keep OpenClaw.NET bound to `127.0.0.1`, use the normal setup flow, and do not enable Tailscale or Aperture unless you need them.
- Private tailnet deployment: keep OpenClaw.NET loopback-bound and expose `/admin`, `/chat`, `/mcp`, and `/ws` through Tailscale Serve.
- Team/shared deployment: use Tailscale ACLs for operator access, keep OpenClaw.NET auth and approvals enabled, and review `/doctor/text` before sharing.
- Aperture-backed provider deployment: add an Aperture model profile while leaving existing Ollama, OpenAI, Anthropic, Gemini, Azure OpenAI, and OpenAI-compatible profiles unchanged.
- Public demo deployment: prefer a short-lived, hardened deployment. Avoid exposing admin surfaces publicly unless auth, tool posture, webhook signatures, and public-bind checks are all clean.

## Tailscale Serve

Tailscale Serve is the preferred way to make a private OpenClaw.NET runtime available to operators on a tailnet.

Recommended posture:

- Keep OpenClaw.NET bound to loopback, for example `127.0.0.1:18789`.
- Let Tailscale handle private tailnet access.
- Keep OpenClaw.NET auth tokens, operator accounts, and tool approvals enabled.

Example:

```bash
tailscale serve 18789
```

For explicit HTTPS serving, use the current Tailscale CLI syntax for your platform. OpenClaw.NET also has optional Serve/Funnel lifecycle support through `OpenClaw:Tailscale`, but it is disabled by default.

Serve is safer than a public bind because the gateway is not directly exposed to the internet. Access is limited to tailnet reachability and Tailscale identity/ACL policy, while OpenClaw.NET still enforces its own runtime governance.

## Tailscale Funnel

Tailscale Funnel exposes a service publicly. Use it only for deliberate cases such as temporary demos or webhook testing.

Do not use Funnel as the default admin exposure pattern.

Before using Funnel:

- Require OpenClaw.NET auth.
- Disable unsafe tools or route them through hardened execution backends.
- Validate webhook signatures for public webhooks.
- Review `/doctor/text` and public-bind posture.
- Keep exposure time short and revoke it when the demo or test is done.

## Aperture By Tailscale

Aperture is an optional upstream AI gateway. OpenClaw.NET remains responsible for agent runtime behavior, tools, sessions, approvals, memory, runtime governance, and evidence.

Aperture can handle provider routing, model access, spend controls, usage telemetry, and identity-aware provider access. Treat it as an upstream provider route, not as a replacement for OpenClaw.NET runtime governance.

## Example Aperture Configuration

Use the existing model profile system. A bearer-token Aperture route can be configured as an OpenAI-compatible profile:

```json
{
  "OpenClaw": {
    "Models": {
      "Profiles": [
        {
          "Id": "aperture-default",
          "Provider": "openai-compatible",
          "BaseUrl": "https://YOUR_APERTURE_ENDPOINT",
          "Model": "YOUR_APERTURE_MODEL_ROUTE",
          "ApiKey": "env:OPENCLAW_APERTURE_TOKEN",
          "Tags": ["aperture", "remote", "optional"]
        }
      ]
    }
  }
}
```

OpenClaw.NET also accepts `Provider = "aperture"` as a first-class alias for the same OpenAI-compatible transport:

```json
{
  "OpenClaw": {
    "Models": {
      "Profiles": [
        {
          "Id": "aperture-default",
          "Provider": "aperture",
          "BaseUrl": "https://YOUR_APERTURE_ENDPOINT",
          "Model": "YOUR_APERTURE_MODEL_ROUTE",
          "AuthMode": "tailnet-identity",
          "SendRequestMetadata": false,
          "Tags": ["aperture", "remote", "optional"]
        }
      ]
    }
  }
}
```

This profile is optional. Users can continue using Ollama, OpenAI, Anthropic, Gemini, Azure OpenAI, embedded local models, or other OpenAI-compatible endpoints normally.

The CLI helper writes the same profile shape:

```bash
openclaw setup provider aperture \
  --config ~/.openclaw/config/openclaw.settings.json \
  --endpoint https://YOUR_APERTURE_ENDPOINT \
  --model YOUR_APERTURE_MODEL_ROUTE \
  --auth-mode bearer \
  --env-var OPENCLAW_APERTURE_TOKEN
```

For tailnet identity mode:

```bash
openclaw setup provider aperture \
  --endpoint https://YOUR_APERTURE_ENDPOINT \
  --model YOUR_APERTURE_MODEL_ROUTE \
  --auth-mode tailnet-identity
```

## Tailscale Identity Headers

Future OpenClaw.NET deployments may map Tailscale identity to operator identity, roles, or approval scopes. That is not enabled by default.

Important trust boundaries:

- Only trust identity headers from a trusted Serve path or proxy boundary.
- Do not accept identity headers from arbitrary public clients.
- Keep OpenClaw.NET auth and approval policy active unless you have a reviewed deployment-specific replacement.

OpenClaw.NET can optionally send request metadata to an Aperture profile, but this is disabled by default. When enabled, the metadata is limited to OpenClaw-owned routing context such as session id, actor id, channel id, profile id, run mode, and purpose.

## Tailscale SSH Future Direction

Tailscale SSH is a future/experimental direction for OpenClaw.NET remote execution scenarios:

- Approval-gated remote execution backends.
- Private diagnostics on trusted machines.
- Remote builders.
- Remote agent workspaces.

Do not treat this as implemented runtime behavior unless a specific execution backend is configured and documented.

## Security Notes

- Loopback-first remains the safest default.
- Prefer Tailscale Serve over public binds for private operator access.
- Funnel requires explicit hardening and should be temporary.
- Tool approvals remain important even on a tailnet.
- OpenClaw.NET governance, evidence, runtime events, and audit behavior still apply when Tailscale or Aperture is used.
- Aperture/Tailscale metadata is never sent unless explicitly enabled.

## Troubleshooting

- Unreachable Aperture endpoint: verify the endpoint URL, tailnet reachability, DNS, and ACLs. `openclaw setup verify --offline` should still skip online probes rather than failing.
- Tailnet not connected: run `tailscale status` and confirm the machine is logged in to the expected tailnet.
- Serve misconfiguration: confirm Serve points to the OpenClaw.NET loopback port, usually `18789`.
- Invalid model route: confirm the Aperture route name matches the route configured in Aperture.
- Auth failures: for bearer mode, confirm `OPENCLAW_APERTURE_TOKEN` is set. For tailnet identity mode, confirm Aperture accepts requests without a bearer token from that tailnet identity.
- Metadata missing: confirm `SendRequestMetadata` is enabled on the selected profile or `OpenClaw:Llm:SendRequestMetadata` is enabled for the root provider.
- Metadata unexpected: disable `SendRequestMetadata`; it is opt-in and should stay off unless Aperture policy needs it.

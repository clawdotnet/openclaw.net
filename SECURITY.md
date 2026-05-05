# Security Guide

This document covers the operator-facing security posture for OpenClaw.NET deployments.

## Public Bind Baseline

If you bind the gateway to anything other than loopback:

- require gateway authentication
- treat `--doctor`, `GET /doctor`, and `GET /doctor/text` as a required preflight
- prefer sandbox `Require` mode for high-risk tools such as `shell`
- review plugin usage carefully before enabling third-party packages

The new posture surfaces make these checks visible in both:

- `GET /doctor`
- `GET /doctor/text`
- `GET /admin/posture`
- `openclaw admin posture`

## Tool Approval Semantics

HTTP tool approval has two distinct modes:

- `RequireRequesterMatchForHttpToolApproval=false`
  - any authenticated HTTP operator can approve a pending request by id
  - this preserves existing admin-override behavior
- `RequireRequesterMatchForHttpToolApproval=true`
  - the approver must match the original requester identity
  - this is the safer setting for Internet-facing binds

For public deployments, prefer requester-match mode unless you explicitly want centralized operator override.

Use the approval simulator to inspect effective behavior without mutating live queues:

- `POST /admin/approvals/simulate`
- `openclaw admin approvals simulate`

## Payments

Native payments are disabled by default. When enabled, payment actions are fail-closed: live virtual cards, machine payments, and browser payment-sentinel fills require critical approval unless policy explicitly allows deterministic test mode.

Payment secrets are stored behind opaque handles. Raw PAN, CVV, authorization headers, shared payment tokens, and provider secret JSON must not appear in tool results, audit records, traces, memory, or persisted session messages. The payment redaction pipeline runs before persistence/export paths, and `PaymentSecret` cannot be JSON-serialized.

See [docs/security/payments.md](docs/security/payments.md), [docs/plugins/payment.md](docs/plugins/payment.md), and [docs/cli/payment.md](docs/cli/payment.md).

## Browser Sessions And Proxies

Browser-session admin auth is only safe when the gateway can determine that the effective request scheme is HTTPS.

If you run behind a reverse proxy or TLS terminator:

- forward the standard `X-Forwarded-*` headers
- configure ASP.NET Core forwarded header trust correctly
- confirm `/doctor/text` does not warn about insecure browser-session cookie posture

Without trusted forwarded headers, the gateway may consider the request insecure and the posture report will warn accordingly.

## Plugin Bridge Security

JS/TS plugin IPC now uses hardened local transport defaults for `socket` and `hybrid` modes:

- per-plugin private socket directories
- authenticated local IPC handshake
- structured diagnostics in posture and plugin reports

This reduces the risk of a local process racing the intended plugin bridge connection.

Operational notes:

- `stdio` remains available and unchanged
- user-configured socket paths are still honored, so operators remain responsible for securing those paths
- plugin manifests and native dynamic plugin manifests must resolve entry paths under the discovered plugin root

## Plugins On Public Binds

Plugins increase the trusted-code surface area. Before enabling them on an Internet-facing gateway:

- keep plugin load paths tightly controlled
- prefer pinned internal packages over arbitrary local directories
- review `/doctor` posture warnings for plugin transport and raw secret refs
- keep `OpenClaw:Security:AllowPublicPlugins=false` unless the deployment has been reviewed

## Webhooks

Webhook endpoints should always use their native signature or token validation:

- Twilio uses request signature validation
- WhatsApp uses HMAC verification
- Bot Framework validates issuer, audience, and channel/service claims

Do not disable provider-side signing expectations just because the gateway already requires auth elsewhere.

## Incident Export

The admin incident export is intended for debugging and support workflows:

- `GET /admin/incident/export`
- `openclaw admin incident export`

It redacts obvious secrets and sensitive metadata keys by construction, but the bundle still contains operationally sensitive information. Restrict it to trusted admins.

## Recommended Deployment Loop

1. Run `--doctor` before first public exposure.
2. Check `openclaw admin posture` after the deployed config is live behind the real proxy.
3. Exercise `openclaw admin approvals simulate` for the approval paths you expect.
4. Export an incident bundle once in staging and verify the redaction level matches your expectations.

# OpenBao Credential Protection Design

## Status

Revised for specification review. This design covers JIT Gateway deployments; the OpenBao backend remains unavailable in NativeAOT publishes.

## Context

OpenClaw.NET already has a central `SecretResolver` and an optional OpenBao/Vault KV v2 provider. Existing consumers can resolve `vault:` references, and the gateway pre-warms configured references before serving. However, this support is not universal: a configured field is protected only when its consumer uses the resolver. In particular, the gateway-wide `AuthToken` currently comes from configuration or `OPENCLAW_AUTH_TOKEN` without resolver integration.

The goal is to protect long-lived, reusable service credentials consistently without turning OpenBao into a general configuration store or redesigning every credential consumer.

## Goals

- Reuse `SecretResolver`, the existing `vault:` grammar, KV v2 provider, cache, and startup pre-warm.
- Inventory credential-bearing settings and close high-risk gaps, beginning with the gateway `AuthToken`.
- Keep existing `env:` usage and local-development behavior compatible.
- Prevent public-bind deployments from starting with inline plaintext credentials by default.
- Keep secret values out of persisted administrative settings, API responses, logs, traces, exceptions, and diagnostic exports.
- Document JIT-only support and provide a repeatable migration and validation process.

## Non-goals

- Add NativeAOT support for the OpenBao backend.
- Replace `ISecretResolver` with a credential broker or register OpenBao as an `IConfiguration` provider.
- Issue, refresh, or centrally manage user OAuth tokens. Connected accounts may reference secrets managed in OpenBao, but their token lifecycle is out of scope.
- Store conversations, memory, attachments, personal data, WhatsApp protocol session files, one-time payment secrets, or TLS termination private keys in ordinary KV entries.
- Change payment secret handles or the payment vault lifecycle.

## Credential scope and priority

The credential inventory is maintained in `docs/security/vault.md` as a field-level coverage matrix: configuration path, owning component, credential purpose, resolver status, public-bind treatment, and migration guidance.

| Priority | Credential families | Design treatment |
|---|---|---|
| P0 | Gateway static `AuthToken` | Add an explicit secret reference and resolve it through `SecretResolver`. This is the first implementation target. |
| P1 | Default and profile model API keys; multimodal, search, image, and routing provider keys | Retain existing resolver-backed paths; migrate production values to `vault:` references. |
| P1 | Channel bot tokens, application secrets, webhook signing/verification secrets, and SMS provider credentials | Use existing `*Ref` fields and resolver-backed consumers; close any field-level gaps found by the coverage matrix. |
| P1 | Tool and integration credentials, including database connection strings, Home Assistant, Notion, email, MQTT, and Nacos | Use the existing resolver where present; add resolver calls only for credential fields currently consumed as literals. |
| P1 | Workflow backend tokens, OAuth client secrets, MCP delegated client secrets/signing keys, and generic webhook HMAC secrets | Keep references in configuration and resolve only at the consuming boundary. |
| P1 | Long-lived payment-provider credentials passed to CLI integrations | Classify secret-bearing entries in `Payments.StripeLink.EnvironmentVariables` and resolve them before process launch; do not confuse these with transient payment transaction secrets. |
| P2 | Coding backend and connected-account credentials | Support existing `SecretRef` references. Token-file and protected-blob modes remain valid; no OAuth refresh lifecycle is added. |

Identifiers such as application IDs, tenant IDs, resource URLs, scopes, and allowlists are not secrets by default. They remain ordinary configuration unless a specific deployment policy classifies them otherwise.

The active Gmail Pub/Sub webhook secret already uses a resolver-backed reference. The separately declared `GmailPubSub.CredentialsPath` setting is not currently consumed by a credential loader; it must not be reported as OpenBao-protected service-account content. If Gmail API credential loading is added, define whether the integration consumes a protected file path or secret content before adding it to this design's supported inventory.

## Architecture and data flow

1. Production configuration contains a reference, not a resolved secret. Local development may retain legacy direct values.
2. The credential consumer passes the reference to the existing `SecretResolver`.
3. The provider chain resolves `env:` and `vault:` references. OpenBao reads KV v2 values and uses the existing TTL cache and refresh-ahead behavior.
4. Gateway startup pre-warms configured `vault:` references before serving. Production deployments keep `PrewarmRequired=true`.
5. Consumers that capture credentials at construction may require service or gateway recreation before a rotation takes effect.

No separate credential API is introduced. Existing settings with a dedicated `*Ref` property use that property. `AuthToken` receives an explicit `AuthTokenRef`; precedence is:

1. If `AuthTokenRef` is set, resolve it and fail startup if resolution fails. Do not fall back to another source after a configured reference fails.
2. Otherwise preserve the legacy direct `AuthToken` setting.
3. If neither is supplied, preserve the existing `OPENCLAW_AUTH_TOKEN` environment fallback.

The Vault backend remains optional and disabled unless configured. `TokenRef` for OpenBao itself must resolve from an external bootstrap source such as the process environment; it must not reference OpenBao. The application uses a least-privilege read-only token scoped to the deployment environment and required paths, never an operator/root token.

## KV naming convention

Use KV v2 paths in the form:

```text
secret/data/openclaw/<environment>/<service>#<key>
```

Examples:

```text
vault:secret/data/openclaw/prod/gateway#auth_token
vault:secret/data/openclaw/prod/models/openai#api_key
vault:secret/data/openclaw/prod/channels/slack#bot_token
vault:secret/data/openclaw/prod/channels/slack#signing_secret
vault:secret/data/openclaw/prod/tools/home-assistant#token
```

Use stable lowercase service and key names. Do not put secret values, email addresses, or other personal data in paths or key names. OpenBao policies should separate environments and, where deployment topology permits, workloads.

## Public-bind and plaintext policy

- Public-bind deployments reject inline plaintext values in fields classified as credentials. The error identifies the configuration field but never includes its value.
- `env:` and `vault:` references remain allowed.
- The existing `Security.AllowRawSecretRefsOnPublicBind` remains an explicit compatibility escape hatch for `raw:` references; it stays disabled by default and enabling it produces a clear security warning.
- Local/non-public deployments retain legacy literal-value behavior during migration.
- The credential coverage matrix is the source of truth for which fields are subject to this validation. Non-secret identifiers and ordinary configuration must not be rejected as credentials.

This is an intentional public-bind compatibility change and must be called out in release notes with remediation guidance.

## Secret handling and failure behavior

- A configured `vault:` reference fails closed if Vault is disabled, unreachable, unauthorized, missing the path/key, or cannot be pre-warmed when pre-warm is required.
- A failed Vault lookup must not silently fall back to an inline value, environment variable, or the reference string.
- Existing `env:` behavior remains unchanged. Do not add a silent-success fallback for missing environment values.
- Resolved values are never serialized back into settings. Administrative APIs expose only configured state and reference metadata, not resolved values.
- Logs, traces, audit events, startup diagnostics, exception text, and support bundles must not contain resolved values. Where configuration paths are reported, report field names or reference types only.
- OpenBao access tokens and policies are operational bootstrap concerns; the application must not store its own OpenBao token in the same KV backend.

## Legacy admin-settings snapshots

`AdminSettingsSnapshot` currently carries some WhatsApp credential values, and an admin update can serialize that snapshot to `admin-settings.json`. New writes must omit inline credential values and preserve their `*Ref` settings. Keep the existing in-memory values for the running process, but do not copy them into the persisted snapshot; after restart, a credential that was only present as a legacy persisted literal will no longer be available.

For local/non-public deployments, continue loading existing legacy snapshot values during migration and emit a warning containing field names only. Do not silently claim those values are OpenBao-protected. Operators must move them to `env:`/`vault:` references before relying on them across a restart. Public-bind validation rejects inline credentials even when loaded from an old snapshot.

Admin settings responses, support bundles, and other exports must use a sanitized projection rather than serializing the credential-bearing runtime snapshot. Preserve the existing config API's configured/not-configured behavior while ensuring update responses cannot return secret values.

## Migration and rollout

1. **Inventory:** Produce and maintain the field-level credential coverage matrix in `docs/security/vault.md`. Identify fields that already use `SecretResolver` and fields still consumed as literal values, including secret-bearing Stripe Link process environment entries.
2. **Close gaps:** Add `AuthTokenRef`, then route only confirmed credential fields through the resolver, including classified Stripe Link credentials. Preserve direct-value behavior for local deployments.
3. **Protect persistence and APIs:** Preserve in-memory legacy values while excluding credential literals from new admin-settings writes. Sanitize settings responses, support bundles, and exports; warn when loading legacy local values.
4. **Pilot:** Configure HTTPS OpenBao, an externally supplied least-privilege token, KV paths, and required pre-warm in a non-public JIT environment. Validate every referenced secret before production rollout.
5. **Migrate and rotate:** Replace production inline credentials with `vault:` references, verify startup and dependent integrations, then rotate/revoke the old credentials. Account for cache TTL and clients that capture values at construction.
6. **Enforce public-bind policy:** Enable the credential validator and publish the breaking-change notice and field-specific remediation guidance.

## Validation and acceptance criteria

- Existing `env:` references and local direct-value configurations continue to work as documented.
- `AuthTokenRef` resolves through `SecretResolver`; an invalid configured reference fails closed and never falls back.
- Missing, disabled, unauthorized, and unresolved Vault references produce explicit startup or resolution failures without exposing secret values.
- Required pre-warm blocks serving when any configured required reference cannot be fetched.
- Public-bind validation rejects direct credential literals, accepts `env:` and `vault:` references, and reports only the field path. The explicit `raw:` override retains its documented warning behavior.
- Administrative settings round-trip references and do not return resolved values.
- Legacy local `admin-settings.json` credentials load with a field-only warning, remain available in the current process, and are not copied into a new snapshot; public-bind startup rejects those literals.
- Admin settings updates, support bundles, and exports do not include inline credential values.
- Tests cover secret redaction across API responses, diagnostics, logs, and exceptions.
- JIT Vault integration tests verify KV v2 resolution and rotation/cache behavior; NativeAOT publish checks confirm the backend is excluded and documentation states the limitation.
- Documentation lists credential families, reference examples, migration steps, public-bind behavior, bootstrap-token handling, and rotation/restart expectations.

## Decisions

- Architecture: incrementally extend existing `SecretResolver`.
- NativeAOT: OpenBao is JIT-only for this design.
- Connected-account OAuth: permit references to externally managed secrets; do not design token issuance/refresh/ownership.
- Public bind: reject inline credential values; retain the existing explicit `raw:` compatibility override.
- Legacy admin snapshots: preserve local loading with a field-only warning; omit inline values on new writes and require `env:`/`vault:` migration for persistence.

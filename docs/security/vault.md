# Vault / OpenBao Secret Resolution

OpenClaw.NET resolves secrets through `SecretResolver` (static facade) backed by a pluggable `ISecretProvider` chain. The Vault backend (`OpenClaw.Security.Vault`, built on VaultSharp) reads KV v2 secrets from HashiCorp Vault or OpenBao and caches them with TTL, single-flight, and refresh-ahead. The default posture is fail-closed:

- vault is disabled unless `OpenClaw:Security:Vault:Enabled=true`
- the NativeAOT gateway build excludes the Vault backend (VaultSharp is not trim-safe), so `vault:` refs fail closed in that build; JIT builds include the backend
- resolved values never appear in exception messages, logs, traces, or stack traces
- the token is referenced through the existing `env:`/`raw:` indirection, never written as plaintext config

## Credential Coverage and Priority

All configuration paths below are relative to the `OpenClaw` configuration root. `vault:` support is determined by the consumer, not by the presence of a configuration field or a `*Ref` property. The following inventory reflects current resolver call sites; entries marked as rollout gaps are not protected by OpenBao until the indicated consumer change is implemented.

| Priority | Configuration fields | Current coverage and guidance |
|---|---|---|
| P0 | `OpenClaw:AuthToken` / planned `OpenClaw:AuthTokenRef` | The direct gateway token is currently a literal-only gap. The rollout adds `AuthTokenRef`; keep direct values for local migration only and use `env:`/`vault:` on public binds. |
| P1 | `OpenClaw:Llm:ApiKey`, `OpenClaw:Models:Profiles[*]:ApiKey`, `OpenClaw:Multimodal:ElevenLabs:ApiKey`, provider keys consumed by image/search tools, and `OpenClaw:DynamicTurnRouting:Jev:ApiKeyRef` | Existing consumers resolve these values. Use references for production; confirm the selected provider/tool consumes the relevant field. |
| P1 | `OpenClaw:Channels:Telegram:BotTokenRef`, `WebhookSecretTokenRef`; `Slack:BotTokenRef`, `SigningSecretRef`; `Discord:BotTokenRef`, `PublicKeyRef`; `WhatsApp:WebhookVerifyTokenRef`, `WebhookAppSecretRef`, `CloudApiTokenRef`, `BridgeTokenRef`; `Teams:AppPasswordRef`; `Sms:Twilio:AuthTokenRef`; `Feishu:AppSecretRef`; `DingTalk:AppKeyRef`, `AppSecretRef`; `WeCom:BotSecretRef`, `CorpSecretRef` | These credential references are consumed by resolver-backed channel/webhook paths. The corresponding direct-value fields remain legacy configuration and are not OpenBao-protected unless a reference is configured. Channel IDs, app IDs, tenant IDs, robot codes, account IDs, and phone numbers are identifiers rather than credentials by default. |
| P1 | `OpenClaw:Plugins:Database:ConnectionString`, `Plugins:HomeAssistant:TokenRef`, `Plugins:Notion:ApiKeyRef`, `Plugins:Email:PasswordRef`, `Plugins:Mqtt:UsernameRef` / `PasswordRef`, and Nacos adapter username/password | Existing tool and adapter consumers call `SecretResolver`; use `env:`/`vault:` references where supported. |
| P1 | `OpenClaw:Workflows:Backends[*]:ApiTokenSecret`, `Authentication:ClientSecretRef`; MCP delegated `ClientSecretRef` / `SigningKeyRef`; `OpenClaw:Webhooks:Endpoints[*]:Secret`; `GmailPubSub:WebhookSecretRef` | These consuming paths resolve references. OAuth client IDs, audiences, endpoints, scopes, and webhook metadata are not secrets. |
| P1 | `OpenClaw:Payments:StripeLink:EnvironmentVariables[*]` | Current gap: Stripe Link passes this map to its CLI without resolving secret references. The rollout will classify secret-bearing variable names and resolve their references before process launch; non-secret identifiers remain unchanged. |
| P1 | `OpenClaw:Channels:WhatsApp:FirstPartyWorker:Proxy` and `Accounts[*]:Proxy` | Current gap: worker proxy URLs are sent to the worker without secret resolution. The rollout will resolve full-URL `env:`/`vault:` references at the worker boundary and treat URI userinfo as an inline credential. |
| P2 | `OpenClaw:CodingBackends:*:SecretRef` and connected-account `SecretRef` values | Existing credential consumers resolve these references. Token-file and protected-blob modes remain supported; OAuth issuance and refresh are outside this design. |
| Gap | `OpenClaw:GmailPubSub:CredentialsPath` / `CredentialsPathRef` | A path is declared, but no Gmail service-account credential loader consumes it. Do not claim that the file contents are protected by OpenBao. |
| Gap | WhatsApp values in legacy `admin-settings.json` snapshots and settings/export projections | Snapshots can contain inline webhook, Cloud API, bridge, and proxy credentials. The rollout will retain local legacy loading with a field-only warning, then omit inline values from new snapshots and sanitized responses/exports. |
| Out of scope | Conversations, memory, attachments, personal data, WhatsApp protocol session files, one-time payment secrets, TLS termination private keys, and application/business data | Do not put these in ordinary OpenBao KV as part of this credential rollout. Apply separate data-protection and key-management policies where needed. |

Use the KV v2 naming convention `secret/data/openclaw/<environment>/<service>#<key>`, for example `vault:secret/data/openclaw/prod/gateway#auth_token` or `vault:secret/data/openclaw/prod/channels/slack#bot_token`. Keep stable lowercase names and never put secret values or personal data in paths or key names. Configure a least-privilege OpenBao token through an external bootstrap source such as `env:VAULT_TOKEN`; do not store the Vault token in the same KV backend.

On public binds, the rollout rejects inline credentials and accepts `env:`/`vault:` references; the existing `raw:` compatibility opt-in remains disabled by default. Local deployments retain legacy literal behavior during migration. The OpenBao backend is JIT-only; NativeAOT publishes exclude it.

## Reference Grammar

```
vault:<mount>/data/<path>#<key>
```

| Component | Meaning | Example |
|---|---|---|
| `<mount>` | KV v2 mount point (default `secret` when omitted) | `secret` |
| `<path>` | secret path after `data/` | `openclaw/openai` |
| `<key>` | field name inside the secret | `api_key` |

Examples:

- `vault:secret/data/openclaw/openai#api_key`
- `vault:openclaw/data/payments/stripe#sk_live` (custom mount `openclaw`)
- `vault:data/config#nested_key` (default mount `secret`)

Refs work in settings whose consumers call `SecretResolver`. Existing secret-valued settings that have not been migrated to that resolver continue to use their documented formats.

## Configuration

```jsonc
{
  "OpenClaw": {
    "Security": {
      "Vault": {
        "Enabled": true,
        "Address": "https://vault.example.internal:8200",
        "TokenRef": "env:VAULT_TOKEN",
        "Namespace": "",
        "KvMount": "secret",
        "KvVersion": 2,
        "RequestTimeout": "00:00:10",
        "CacheTtl": "00:05:00",
        "RateLimit": { "RequestsPerSecond": 20 },
        "PrewarmRequired": true,
        "PrewarmRefs": [
          "vault:secret/data/openclaw/openai#api_key",
          "vault:secret/data/openclaw/slack#signing"
        ],
        "Tls": {
          "SkipVerify": false,
          "CaCertPath": null
        }
      }
    }
  }
}
```

| Field | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Enable the Vault backend. When disabled, `vault:` refs fail closed: resolving them throws `VaultNotConfiguredException` instead of falling back to a literal string. |
| `Address` | — | Vault server URL. Required when enabled; must be HTTPS and a valid URI. |
| `TokenRef` | — | Secret ref (`env:`/`raw:`) for the Vault token. Required when enabled. Must **not** start with `vault:` (recursion guard). |
| `Namespace` | — | Vault Enterprise namespace (optional). |
| `KvMount` | `secret` | KV v2 mount point used as the default `<mount>`. |
| `KvVersion` | `2` | KV version. Only `2` is supported. |
| `RequestTimeout` | 10s | Per-fetch HTTP timeout (1s–60s). |
| `CacheTtl` | 5min | Cache TTL (30s–24h). |
| `RateLimit.RequestsPerSecond` | `20` | Startup pre-warm concurrency cap (1–1000). |
| `PrewarmRequired` | `true` | Fail startup when a pre-warm ref fails to resolve. `false` logs and continues. |
| `PrewarmRefs` | `[]` | Refs resolved at startup. The gateway config tree is also scanned automatically for `vault:` values. |
| `Tls.SkipVerify` | `false` | Accept any server certificate. Integration/dev only; validation rejects it unless the global opt-in `Security.AllowInsecureTls=true` is set. |
| `Tls.CaCertPath` | — | Custom CA bundle (a PEM file, or a directory of `.pem`/`.crt`/`.cer` files). Loaded roots are trusted as custom roots while hostname checks stay enforced. Mutually exclusive with `SkipVerify`. |

Configuration is read from the existing `IConfiguration` sources: `appsettings.json`, environment variables (`OpenClaw__Security__Vault__Address`), command line, and files encrypted via `SecurityPostureBuilder`. No new configuration provider is introduced.

The same section is validated by `ConfigValidator`: when `Enabled=true`, `Address` and `TokenRef` are required, `Address` must be HTTPS, `CacheTtl`/`RequestTimeout`/`RequestsPerSecond` are range-checked, `Tls.SkipVerify` is rejected without the global opt-in `Security.AllowInsecureTls`, and a loopback `Address` is rejected when the gateway binds to a public (non-loopback) address.

## Sync vs Async Resolution

`SecretResolver.Resolve` (sync) is used by existing call sites:

- `env:`/`raw:` refs resolve through the environment provider as before.
- `vault:` refs return cached values on a cache hit.
- `vault:` refs on a cache miss fail fast with `SecretResolutionException` — the sync path never blocks on HTTP. Use `ResolveAsync` (async path) or rely on pre-warm for cold caches.

`SecretResolver.ResolveAsync` (async) always fetches on a cache miss, with single-flight per key (concurrent misses produce one HTTP call), TTL caching, and refresh-ahead (an expired entry serves the stale value while a background refresh runs; a failed refresh keeps the stale value and logs a warning).

## Startup Pre-warm

`VaultRefPrewarmService` (IHostedService) resolves `PrewarmRefs` plus any `vault:` refs found by scanning the gateway config tree, before the gateway starts serving. Failures are collected and:

- `PrewarmRequired=true` (default): startup fails (`HostingStartupException`) — fail closed.
- `PrewarmRequired=false`: startup continues with a warning; individual failures are logged.

Pre-warm concurrency is capped by `RateLimit.RequestsPerSecond`.

## TLS

- `Tls.SkipVerify=true` accepts any server certificate and is only appropriate for isolated integration environments. Validation rejects it unless the global opt-in `Security.AllowInsecureTls=true` is set, and that opt-in also disables verification in production. Keep both settings false outside isolated development environments.
- `Tls.CaCertPath` loads a custom CA bundle used as custom root trust (`CustomRootTrust`) for Vault TLS validation; hostname verification remains enforced. Missing or invalid certificate files fail startup.

## Token Recursion Guard

`TokenRef` must not start with `vault:` — resolving the token must never depend on the vault backend itself. `ConfigValidator` rejects such configurations.

## Error Handling

| Scenario | Sync path | Async path |
|---|---|---|
| `vault:` cache hit | returns value | returns value |
| `vault:` cache miss | throws `SecretResolutionException` | fetches, caches, returns |
| Vault 401/403 | throws `VaultAuthException` | throws `VaultAuthException` |
| Vault 5xx / network / timeout | throws `VaultUnavailableException` | throws; returns stale value + warning when a stale entry exists |
| Vault path-level 404 | throws `VaultPathNotFoundException` | throws `VaultPathNotFoundException` |
| Vault key-level 404 | throws `VaultKeyNotFoundException` | throws `VaultKeyNotFoundException` |
| Malformed ref | throws `VaultRefParseException` | throws `VaultRefParseException` |
| Vault disabled, `vault:` ref used | throws `VaultNotConfiguredException` | throws `VaultNotConfiguredException` |

Exception messages contain only path, key, HTTP status codes, and error type names — never the resolved value. Log output additionally passes through the `RedactionPipeline`.

## Local Integration Testing

```bash
docker compose -f deploy/docker-compose/openbao.yml up -d
```

Direct resolver integration tests construct the Vault client without gateway configuration validation and may use the following loopback-only HTTP settings. Do not copy this block into gateway configuration: an enabled gateway requires an HTTPS Vault address. Production and shared environments must use HTTPS.

```bash
OPENBAO_ADDR=http://127.0.0.1:8200 OPENBAO_TOKEN=root \
  dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj \
  --filter "Category=Integration"
```

See `docs/security/vault-integration-tests.md` for running the integration test suite.

## Upgrade / Rollback

- **Upgrade**: enable the backend (`Enabled=true`), set `PrewarmRequired=false` first to observe resolution failures in logs without blocking startup, then flip to `true` once refs resolve cleanly.
- **Rollback**: set `Enabled=false`; `vault:` refs now fail closed (`VaultNotConfiguredException`) while `env:`/`raw:` behavior is unchanged.
- **Rotation**: rotate values in Vault; caches expire after `CacheTtl` and are refreshed in the background (refresh-ahead), so rotation is picked up without a gateway restart.

Vault pre-warming runs before runtime initialization. The hosted service refreshes configured references every half cache TTL for synchronous consumers. Stale values are available for at most twice the TTL from the last successful fetch; failures do not extend that deadline. Existing clients that capture credentials at construction still require recreation to use a rotated value.

Vault is available in JIT publishes (`-p:PublishAot=false`). NativeAOT publishes exclude the integration and reject `vault:` references.

# Vault / OpenBao Secret Resolution

OpenClaw.NET resolves secrets through `SecretResolver` (static facade) backed by a pluggable `ISecretProvider` chain. The Vault backend (`OpenClaw.Security.Vault`, built on VaultSharp) reads KV v2 secrets from HashiCorp Vault or OpenBao and caches them with TTL, single-flight, and refresh-ahead. The default posture is fail-closed:

- vault is disabled unless `OpenClaw:Security:Vault:Enabled=true`
- resolved values never appear in exception messages, logs, traces, or stack traces
- the token is referenced through the existing `env:`/`raw:` indirection, never written as plaintext config

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

Refs work anywhere a secret value is configured: `env:`/`raw:` refs and `vault:` refs are interchangeable config values (channel credentials, LLM API keys, plugin configs, ...).

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
| `Enabled` | `false` | Enable the Vault backend. When disabled, `vault:` refs are treated as literal strings by the resolver chain. |
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
| `Tls.SkipVerify` | `false` | Accept any server certificate. Integration/dev only. |
| `Tls.CaCertPath` | — | Custom CA bundle (a PEM file, or a directory of `.pem`/`.crt`/`.cer` files). Loaded roots are trusted as custom roots while hostname checks stay enforced. Mutually exclusive with `SkipVerify`. |

Configuration is read from the existing `IConfiguration` sources: `appsettings.json`, environment variables (`OpenClaw__Security__Vault__Address`), command line, and files encrypted via `SecurityPostureBuilder`. No new configuration provider is introduced.

The same section is validated by `ConfigValidator`: when `Enabled=true`, `Address` and `TokenRef` are required, `Address` must be HTTPS, `CacheTtl`/`RequestTimeout`/`RequestsPerSecond` are range-checked, and a loopback `Address` is rejected when the gateway binds to a public (non-loopback) address.

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

- `Tls.SkipVerify=true` accepts any server certificate (integration/dev only; produces a validation warning unless `Security.AllowInsecureTls=true`).
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
| Vault disabled, `vault:` ref used | treated as literal string | treated as literal string |

Exception messages contain only path, key, HTTP status codes, and error type names — never the resolved value. Log output additionally passes through the `RedactionPipeline`.

## Local Integration Testing

```bash
docker compose -f deploy/docker-compose/openbao.yml up -d
```

Then configure the gateway:

```jsonc
"OpenClaw": { "Security": { "Vault": {
  "Enabled": true,
  "Address": "http://127.0.0.1:8200",
  "TokenRef": "raw:root"
} } }
```

See `docs/security/vault-integration-tests.md` for running the integration test suite.

## Upgrade / Rollback

- **Upgrade**: enable the backend (`Enabled=true`), set `PrewarmRequired=false` first to observe resolution failures in logs without blocking startup, then flip to `true` once refs resolve cleanly.
- **Rollback**: set `Enabled=false`; the resolver chain reverts to literal fallback for `vault:` refs and `env:`/`raw:` behavior is unchanged.
- **Rotation**: rotate values in Vault; caches expire after `CacheTtl` and are refreshed in the background (refresh-ahead), so rotation is picked up without a gateway restart.

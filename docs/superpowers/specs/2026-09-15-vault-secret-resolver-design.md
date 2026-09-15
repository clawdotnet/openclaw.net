# Vault / OpenBao Secret Resolver Backend Design

Date: 2026-09-15
Status: Draft

## Summary

Extend [`SecretResolver`](../../src/OpenClaw.Core/Security/SecretResolver.cs) — currently the single sync, static choke point for all secret resolution (`env:`, `raw:`, bare string → env var name → literal) — with an external Vault / OpenBao backend. The new backend is delivered as a new project `OpenClaw.Security.Vault` using [VaultSharp](https://github.com/rajanadar/VaultSharp), wired through a new `ISecretResolver` abstraction in `OpenClaw.Core`. Existing 67 call sites remain unchanged.

This brings OpenClaw.NET in line with [docs/security/payments.md:49](../../docs/security/payments.md) which already lists HashiCorp Vault (and OpenBao, AWS Secrets Manager, Azure Key Vault, DPAPI) as reserved extension points.

## Goals

### P0: Pluggable secret resolver

- `ISecretResolver` interface in `OpenClaw.Core.Security` exposes both `ResolveAsync(string?, CancellationToken)` and sync `Resolve(string?)`.
- A scheme-based dispatch (`ISecretProvider.Scheme`) selects the implementation per prefix.
- The existing `SecretResolver` static class remains as a thin facade that delegates to a DI-registered `ISecretResolver` instance, with a legacy fallback when DI is not yet bootstrapped.
- `EnvRawSecretProvider` preserves the current behavior for `env:`, `raw:`, and bare strings exactly (100% behavior parity).

### P0: Vault / OpenBao backend (v1)

- New project `OpenClaw.Security.Vault` (`IsAotCompatible=false`) wraps VaultSharp.
- KV v2 read only (`secret/data/<path>#<key>`). Transit, PKI, dynamic creds, AWS/Azure/GCP backends are out of scope for v1.
- Token-only authentication in v1; Kubernetes / AWS IAM / Azure / GCP / JWT auth are v2.
- TTL cache (default 5 minutes) with lazy refresh and single-flight to avoid stampede.
- Sync `Resolve("vault:...")` returns cached values; cache miss fails fast with `SecretResolutionException` (no sync-over-async, no deadlock risk).
- Async `ResolveAsync("vault:...")` fetches on miss, populates cache, returns.
- `IHostedService` (`VaultRefPrewarmService`) pre-warms `Security.Vault.PrewarmRefs` plus any `vault:`-prefixed `*Ref` fields discovered in config.
- `PrewarmRequired` flag controls hard-fail vs soft-fail startup semantics.

### P0: Backward compatibility

- All 67 existing call sites continue to work without modification.
- Existing 9 `SecretResolver` unit tests in `SecurityTests.cs` remain green unchanged.
- `Vault.Enabled=false` by default; deployments that do not opt in see no behavior change.
- The vault prefix `vault:` does not collide with `env:` or `raw:`.

### P1: Operational safety

- No secret value may appear in any log line, exception message, or stack trace. Exception messages contain only the path, key name, and HTTP status / error code.
- `RedactionPipeline` is invoked on all log output that touches vault lookups.
- `Address` validation rejects `localhost` / `127.0.0.1` / `::1` when `Security.PublicBind=true` (SSRF guard).
- Token recursion guard: `Security.Vault.TokenRef` is rejected by config validation if it starts with `vault:`.

## Non-Goals (v1)

- Kubernetes, AWS IAM, Azure managed identity, GCP, JWT auth methods (v2).
- Vault Transit, PKI, dynamic database / AWS credentials engines (v2).
- NativeAOT-compatible Vault build (`OpenClaw.Security.Vault` ships as `IsAotCompatible=false`).
- Persistent encrypted cache across restarts.
- Cross-process secret sharing.

## Architecture

```
src/
├── OpenClaw.Core/
│   └── Security/
│       ├── ISecretProvider.cs              (new)
│       ├── ISecretResolver.cs              (new)
│       ├── CompositeSecretResolver.cs      (new)
│       ├── EnvRawSecretProvider.cs         (new — extracted from SecretResolver)
│       ├── ResolverAccessor.cs             (new — bridges IServiceProvider)
│       ├── SecretResolutionException.cs    (new)
│       ├── SecretResolver.cs               (refactored — now a facade)
│       ├── AllowlistManager.cs             (unchanged)
│       ├── RedactionPipeline.cs            (unchanged)
│       └── ...
├── OpenClaw.Security.Vault/                (NEW project)
│   ├── OpenClaw.Security.Vault.csproj      (IsAotCompatible=false)
│   ├── VaultSecretProvider.cs
│   ├── VaultSecurityOptions.cs
│   ├── VaultRefParser.cs
│   ├── VaultRefCache.cs
│   ├── VaultRefPrewarmService.cs
│   ├── VaultServiceCollectionExtensions.cs
│   ├── VaultExceptions.cs
│   └── README.md
├── OpenClaw.Gateway/
│   └── Bootstrap/
│       └── GatewayBootstrapExtensions.cs   (modified — register Vault)
└── OpenClaw.Tests/
    └── Security/
        ├── SecretResolverFacadeTests.cs    (new)
        ├── VaultSecretProviderTests.cs     (new)
        ├── VaultRefParserTests.cs          (new)
        ├── VaultRefCacheTests.cs           (new)
        ├── VaultRefPrewarmServiceTests.cs  (new)
        └── VaultIntegrationTests.cs        (new — [Trait("Category","Integration")])
```

## Components

### `OpenClaw.Core/Security/ISecretProvider.cs`

```csharp
public interface ISecretProvider
{
    string Scheme { get; }                  // "env"|"raw"|"bare"|"vault"
    bool CanResolve(string secretRef);
    ValueTask<string?> ResolveAsync(string secretRef, CancellationToken ct);
}
```

### `OpenClaw.Core/Security/ISecretResolver.cs`

```csharp
public interface ISecretResolver
{
    ValueTask<string?> ResolveAsync(string? secretRef, CancellationToken ct = default);
    string? Resolve(string? secretRef);     // sync facade — cache hit OR throw
    bool IsRawRef(string? secretRef);
}
```

### `OpenClaw.Core/Security/CompositeSecretResolver.cs`

- Constructor takes `IEnumerable<ISecretProvider>` ordered by precedence.
- `ResolveAsync`: detects scheme → routes to first `CanResolve(ref)` provider → returns its result.
- Unrecognized prefix with no provider: logs warning (same heuristic as current `LooksLikeEnvVarName`), returns the literal fallback.
- `Resolve` (sync): same routing, but if the matched provider is `VaultSecretProvider` and the cache miss occurs, throws `SecretResolutionException`.

### `OpenClaw.Core/Security/ResolverAccessor.cs`

```csharp
public static class ResolverAccessor
{
    internal static IServiceProvider? ServiceProvider { get; private set; }
    internal static ISecretResolver? Resolver { get; private set; }

    public static void Use(IServiceProvider sp)
    {
        ServiceProvider = sp;
        Resolver = sp.GetService<ISecretResolver>();
    }

    public static ISecretResolver? Current => Resolver;
}
```

### `OpenClaw.Core/Security/SecretResolver.cs` (refactored facade)

```csharp
public static class SecretResolver
{
    public static string? Resolve(string? secretRef)
        => Resolve(secretRef, logger: null);

    public static string? Resolve(string? secretRef, ILogger? logger)
    {
        var resolver = ResolverAccessor.Current;
        if (resolver is not null)
            return resolver.Resolve(secretRef);

        // Legacy fallback when DI has not been bootstrapped (e.g. early CLI startup)
        return LegacyResolve(secretRef, logger);
    }

    public static ValueTask<string?> ResolveAsync(string? secretRef, CancellationToken ct = default)
    {
        var resolver = ResolverAccessor.Current;
        if (resolver is not null)
            return resolver.ResolveAsync(secretRef, ct);
        return ValueTask.FromResult<string?>(LegacyResolve(secretRef, null));
    }

    public static bool IsRawRef(string? secretRef)
        => secretRef is not null && secretRef.StartsWith("raw:", StringComparison.OrdinalIgnoreCase);

    private static string? LegacyResolve(string? secretRef, ILogger? logger) { /* original logic */ }
    private static bool LooksLikeEnvVarName(string value) { /* original logic */ }
}
```

Behavior invariants for the legacy path:
- `Resolve(null | whitespace)` → `null`
- `Resolve("env:X")` → `Environment.GetEnvironmentVariable("X")` → `null` if unset
- `Resolve("raw:X")` → `"X"`
- `Resolve("bare")` → env var lookup → fallback to literal with warning

### `OpenClaw.Security.Vault/VaultSecretProvider.cs`

```csharp
public sealed class VaultSecretProvider : ISecretProvider
{
    public string Scheme => "vault";
    private readonly IVaultClient _client;
    private readonly VaultRefCache _cache;
    private readonly VaultSecurityOptions _options;
    private readonly ILogger<VaultSecretProvider> _logger;

    public bool CanResolve(string secretRef) =>
        secretRef.StartsWith("vault:", StringComparison.OrdinalIgnoreCase);

    public ValueTask<string?> ResolveAsync(string secretRef, CancellationToken ct)
    {
        var parsed = VaultRefParser.Parse(secretRef);
        return new ValueTask<string?>(ResolveInternalAsync(parsed, ct));
    }

    private async Task<string?> ResolveInternalAsync(VaultRef parsed, CancellationToken ct) { /* see Data Flow */ }
}
```

`IVaultClient` is a thin internal abstraction wrapping VaultSharp's `IVaultClient` so unit tests can substitute without spinning up Vault.

### `OpenClaw.Security.Vault/VaultRefParser.cs`

```csharp
public readonly record struct VaultRef(string Path, string Key, int KvVersion, string Mount);

public static class VaultRefParser
{
    public static VaultRef Parse(string secretRef);   // throws VaultRefParseException on malformed input
}
```

Grammar: `vault:<mount>/data/<path>#<key>` where `<mount>` defaults to `"secret"` (configurable via `VaultSecurityOptions.KvMount`).
- `vault:secret/data/openclaw/openai#api_key` → `{ Mount="secret", Path="openclaw/openai", Key="api_key", KvVersion=2 }`
- `vault:data/openclaw/openai#api_key` (no mount segment) → `{ Mount=KvMount, Path="openclaw/openai", Key="api_key", KvVersion=2 }` — when the ref does not contain a `/data/` segment, the entire pre-`#` portion is treated as the path and `KvMount` is applied as the mount.
- Empty path (`vault:#key`, `vault:secret/data/#key`) or missing `#` separator (`vault:secret/data/openclaw/openai`) → `VaultRefParseException`.

### `OpenClaw.Security.Vault/VaultRefCache.cs`

```csharp
public sealed class VaultRefCache
{
    public bool TryGet(VaultRef key, out string value);   // sync read; used by sync Resolve
    public Task<string> GetOrFetchAsync(VaultRef key, Func<CancellationToken, Task<string>> fetch, CancellationToken ct);
    public void Invalidate(VaultRef key);
}
```

- Backed by `IMemoryCache` with `AbsoluteExpirationRelativeToNow = options.CacheTtl`.
- Single-flight via `SemaphoreSlim` per cache key.
- On TTL expiry: subsequent `ResolveAsync` returns the stale value synchronously and triggers a background refresh task (refresh-ahead).
- On fetch failure with stale value present: returns stale + warning log.
- On fetch failure with no cached value: throws `VaultUnavailableException` (async) or `SecretResolutionException` (sync).

### `OpenClaw.Security.Vault/VaultRefPrewarmService.cs`

```csharp
public sealed class VaultRefPrewarmService : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken);   // resolves PrewarmRefs + scanned refs
    public Task StopAsync(CancellationToken cancellationToken);    // no-op
}
```

- Concurrency cap from `options.RateLimit.RequestsPerSecond`.
- Per-ref timeout from `options.RequestTimeout`.
- Aggregates failures; honors `options.PrewarmRequired`.

### `OpenClaw.Security.Vault/VaultServiceCollectionExtensions.cs`

```csharp
public static class VaultServiceCollectionExtensions
{
    public static IServiceCollection AddOpenClawVaultSecrets(this IServiceCollection services, GatewayConfig config);
}
```

- Only registers when `config.Security.Vault?.Enabled == true`.
- Binds `VaultSecurityOptions` from `services.Configuration`.
- Registers `VaultSecretProvider` as `ISecretProvider` with `Scheme = "vault"`.
- Registers `VaultRefCache` as singleton.
- Registers `VaultRefPrewarmService` as `IHostedService`.
- Validates options; throws on invalid config.

## Data Flow

### Async happy path

```
caller → ResolveAsync("vault:secret/data/openclaw/openai#api_key", ct)
  → CompositeSecretResolver.ResolveAsync
    → VaultSecretProvider.CanResolve → true
    → VaultRefParser.Parse → VaultRef{ Mount="secret", Path="openclaw/openai", Key="api_key" }
    → VaultRefCache.TryGet → miss
    → VaultRefCache.GetOrFetchAsync
      → acquire per-key SemaphoreSlim
      → IVaultClient.ReadSecretAsync("secret/data/openclaw/openai") → Secret<Dictionary<string,object>>
      → extract "api_key" → "sk-..."
      → IMemoryCache.Set with TTL = 5 min
      → return value
    → return value
```

### Sync happy path (post-prewarm)

```
caller → SecretResolver.Resolve("vault:secret/data/openclaw/openai#api_key")
  → ResolverAccessor.Current is set → CompositeSecretResolver.Resolve
    → VaultSecretProvider.CanResolve → true
    → VaultRefCache.TryGet → hit
    → return cached value
```

### Sync fail-fast path

```
caller → SecretResolver.Resolve("vault:secret/data/openclaw/openai#api_key")
  → CompositeSecretResolver.Resolve
    → VaultSecretProvider.CanResolve → true
    → VaultRefCache.TryGet → miss
    → throw SecretResolutionException(
        "vault: ref 'secret/data/openclaw/openai#api_key' requires async path or pre-warm. " +
        "Configure Security.Vault.PrewarmRefs or call SecretResolver.ResolveAsync.")
```

### Pre-warm flow (startup)

```
VaultRefPrewarmService.StartAsync(ct)
  → resolve options.TokenRef via ResolveAsync (env:/raw: only)
  → build IVaultClient with token + address + namespace + tls
  → collect refs = options.PrewarmRefs ∪ ScanConfigForVaultRefs(gatewayConfig)
  → parallel-for (rate-limited) each ref:
      → ResolveAsync(ref, ct) → success count++
                                  → failure count++ (record path + error code, NEVER value)
  → if failureCount > 0:
      if options.PrewarmRequired: throw HostedServiceStartupException("N vault refs failed pre-warm: ...")
      else: log error summary, continue
```

### Refresh-ahead flow

```
VaultRefCache.GetOrFetchAsync(key)
  → TryGet → hit
    → if entry.Age > options.CacheTtl:
        → _ = Task.Run(() => RefreshAsync(key, ct))   // fire-and-forget background refresh
        → return stale value (do NOT await)
    → else:
        → return fresh value
  → TryGet → miss
    → fetch + cache + return
```

### Config scanning

`ScanConfigForVaultRefs(GatewayConfig config)` walks the gateway config tree (channels, tools, model profiles, plugin configs) collecting all `*Ref` properties whose value starts with `"vault:"`. Reflection-based, single pass, cached for the lifetime of the prewarm service.

## Configuration

### `appsettings.json` (additive)

```jsonc
{
  "Security": {
    "Vault": {
      "Enabled": false,
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
```

### C# binding

```csharp
public sealed class VaultSecurityOptions
{
    public bool Enabled { get; set; }
    public string? Address { get; set; }
    public string? TokenRef { get; set; }
    public string? Namespace { get; set; }
    public string KvMount { get; set; } = "secret";
    public int KvVersion { get; set; } = 2;
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromMinutes(5);
    public VaultRateLimitOptions RateLimit { get; set; } = new();
    public bool PrewarmRequired { get; set; } = true;
    public List<string> PrewarmRefs { get; set; } = [];
    public VaultTlsOptions Tls { get; set; } = new();
}

public sealed class VaultRateLimitOptions
{
    public int RequestsPerSecond { get; set; } = 20;
}

public sealed class VaultTlsOptions
{
    public bool SkipVerify { get; set; }
    public string? CaCertPath { get; set; }
}
```

Added to existing [`SecurityOptions`](../../src/OpenClaw.Core/Models/ConfigurationModels.cs) as `public VaultSecurityOptions? Vault { get; set; }`.

### Configuration validation (`ConfigValidator` additions)

When `Vault.Enabled == true`:
- `Address` required, must be HTTPS, must be a valid URI.
- `TokenRef` required, must not start with `"vault:"`.
- `CacheTtl` ∈ [30s, 24h].
- `RequestTimeout` ∈ [1s, 60s].
- `RateLimit.RequestsPerSecond` ∈ [1, 1000].
- If `Security.PublicBind == true`: reject `Address` whose host is `localhost`, `127.0.0.1`, or `::1`.
- `Tls.SkipVerify == true` produces a warning unless `Security.AllowInsecureTls == true` (global opt-in).

### Configuration sources

Reuses existing `IConfiguration` sources: `appsettings.json`, environment variables (`Security__Vault__Address`), command-line (`--security:vault:address=...`), encrypted files via `SecurityPostureBuilder`. No new configuration provider.

## Error Handling

### Exception hierarchy

```
SecretResolutionException                  (base, in OpenClaw.Core)
├── VaultRefParseException                 (Vault project)
├── VaultAuthException                     (Vault project, 401/403)
├── VaultUnavailableException              (Vault project, 5xx/timeout/network, has Retryable flag)
├── VaultPathNotFoundException             (Vault project, 404 on path)
└── VaultKeyNotFoundException              (Vault project, 404 on key)
```

`SecretResolutionException` lives in `OpenClaw.Core` because the legacy fallback path may throw it before Vault types load.

### Error matrix

| Scenario | Sync path | Async path |
|---|---|---|
| `vault:` cache hit | return value | return value |
| `vault:` cache miss, async caller | n/a (sync can't fetch) | fetch → cache → return |
| `vault:` cache miss, sync caller | throw `SecretResolutionException` | n/a |
| TokenRef itself unresolvable | throw `SecretResolutionException` | throw `SecretResolutionException` |
| Vault 401/403 | throw `VaultAuthException` (no value in message) | throw `VaultAuthException` |
| Vault 5xx, network, timeout | throw `VaultUnavailableException` | throw; if stale cached, return stale + warning |
| Vault 404 on path | throw `VaultPathNotFoundException` | throw |
| Vault 404 on key | throw `VaultKeyNotFoundException` | throw |
| Vault section disabled but `vault:` prefix used | throw `VaultNotConfiguredException` | throw |

### No-leak guarantees

- Exception messages: `path`, `key`, HTTP status code, error code name only. Never the resolved value.
- `ToString()` of exceptions does not include `Data["value"]` or any other payload field.
- `ILogger.Log*` calls never include the resolved value as a parameter.
- Log output passes through `RedactionPipeline` for double-safety.
- `VaultSecretProvider` does not store the token beyond the `IVaultClient` construction; no log captures it.

## Testing

### Unit tests (`OpenClaw.Tests/Security/Vault*Tests.cs`)

| Test | Asserts |
|---|---|
| `Parse_ValidRef_ReturnsMountPathKey` | Grammar parse |
| `Parse_MissingKey_Throws_VaultRefParseException` | Malformed input |
| `Parse_EmptyPath_Throws_VaultRefParseException` | Malformed input |
| `Parse_DefaultMount_Applied_WhenMissing` | `vault:data/x#k` defaults mount to "secret" |
| `ResolveAsync_CacheHit_NoHttpCall` | NSubstitute verifies `IVaultClient` not called |
| `ResolveAsync_CacheMiss_OneHttpCall_CachesAndReturns` | Fetch → cache → return |
| `ResolveAsync_Vault401_Throws_VaultAuthException_NoValueInMessage` | No value leak |
| `ResolveAsync_VaultTimeout_Throws_VaultUnavailableException_RetryableTrue` | Timeout path |
| `ResolveAsync_Vault404_Path_Throws_VaultPathNotFoundException` | 404 path |
| `ResolveAsync_Vault404_Key_Throws_VaultKeyNotFoundException` | 404 key |
| `ResolveAsync_CtsCancelled_Throws_OperationCanceledException` | Cancellation |
| `SyncResolve_VaultCacheHit_Returns` | Sync happy path |
| `SyncResolve_VaultCacheMiss_Throws_SecretResolutionException_MessageMentionsAsyncOrPrewarm` | Sync fail-fast |
| `LegacyResolve_BehavesIdenticalToPreRefactor` | Backward compat (snapshot 9 existing tests) |
| `Cache_ConcurrentMisses_SingleFlight_OneHttpCall` | Stampede prevention |
| `Cache_Expire_TriggersRefreshAhead_StaleValueReturned` | Refresh-ahead |
| `Cache_FetchFailureWithStale_ReturnsStaleAndLogs` | Failure fallback |
| `Cache_FetchFailureNoStale_Throws_VaultUnavailable` | Cold failure |
| `PrewarmService_AllSucceed_Starts` | Happy prewarm |
| `PrewarmService_OneFails_PrewarmRequired_Throws_HostStartupException` | Hard fail |
| `PrewarmService_OneFails_PrewarmNotRequired_Logs_NoThrow` | Soft fail |
| `PrewarmService_RateLimit_CapsConcurrency` | Throughput cap |
| `ConfigValidator_VaultEnabled_MissingAddress_Rejects` | Config validation |
| `ConfigValidator_TokenRefStartsWithVault_Rejects` | Recursion guard |
| `ConfigValidator_CacheTtlOutOfRange_Rejects` | Bounds |
| `ConfigValidator_PublicBind_RejectsLoopbackAddress` | SSRF guard |
| `RedactionPipeline_VaultRefValue_NeverAppearsInLog` | Cross-cutting no-leak |
| `ResolverAccessor_NotBootstrapped_LegacyFallbackUsed` | DI-not-ready path |
| `ResolverAccessor_Bootstrapped_RoutesToInstance` | DI-ready path |

### Integration tests (`VaultIntegrationTests.cs`)

```csharp
[Trait("Category", "Integration")]
public sealed class VaultIntegrationTests
{
    // Skipped unless OPENBAO_ADDR and OPENBAO_TOKEN are set.
    // eng/compose/openbao.yml spins up OpenBao for CI.
}
```

| Test | Asserts |
|---|---|
| `End2End_PutAndResolve_KvV2` | Real OpenBao round-trip |
| `End2End_TtlExpiry_FetchesAgain` | Real cache invalidation |
| `End2End_TokenUnauth_Throws_VaultAuthException` | Real auth failure |
| `End2End_RotatedValue_PickedUpAfterTtl` | Real rotation scenario |

CI workflow: optional job in `ci.yml` triggered by `[Category("Integration")]` filter, gated by repository variable `RUN_VAULT_INTEGRATION`. The compose file at `eng/compose/openbao.yml` is the source of truth for the OpenBao image and bootstrap script; it is new and lands in Phase 3.

### Coverage targets

- `OpenClaw.Security.Vault` line coverage ≥ 85%, branch coverage ≥ 75%.
- `OpenClaw.Core/Security/*` additions: 100% line coverage.
- No-leak property test (FsCheck or hand-written): generated random ref → resolved value, assert value never appears in any captured log message.

## Migration

### Phase 1 — Abstraction + facade (zero behavior change)

- Add `ISecretProvider`, `ISecretResolver`, `CompositeSecretResolver`, `EnvRawSecretProvider`, `ResolverAccessor`.
- Refactor `SecretResolver` to delegate to `ResolverAccessor.Current`, with `LegacyResolve` fallback.
- Add `SecretResolver.StartAsync(IServiceProvider)` extension called from `GatewayBootstrapExtensions` and from `CliProgram` startup.
- All 11 existing `SecretResolver.*` tests in `SecurityTests.cs` pass unmodified.
- No call site changes.

### Phase 2 — Vault implementation

- Create `OpenClaw.Security.Vault` project (`IsAotCompatible=false`, references `VaultSharp`).
- Implement `VaultSecretProvider`, `VaultRefParser`, `VaultRefCache`, `VaultExceptions`.
- Unit tests as above.

### Phase 3 — Wiring + config + prewarm

- Add `VaultSecurityOptions` to `SecurityOptions`; update `ConfigValidator`; add `GatewayConfig.Security.Vault` binding.
- Add `AddOpenClawVaultSecrets(IConfiguration)` extension; call from `GatewayBootstrapExtensions` when `Enabled == true`.
- Register `VaultRefPrewarmService` as `IHostedService`.
- Create `eng/compose/openbao.yml` (new directory; mirrors existing `eng/` testing convention); add optional integration test job in `ci.yml`.
- Write `docs/security/vault.md` (English) and `docs/zh-CN/security/vault.md` (Chinese).
- Update `CHANGELOG.md`.
- Update [`docs/security/payments.md:49`](../../docs/security/payments.md) from "reserved extension point" to "implemented (KV v2)".

### Phase 4 — Out of scope (deferred)

- Kubernetes / AWS IAM / Azure / GCP / JWT auth.
- Transit, PKI, dynamic credentials.
- Persistent encrypted cache.

## Documentation Deliverables

- `docs/security/vault.md` — user-facing reference: config schema, ref grammar, operational guide.
- `docs/security/vault-integration-tests.md` — running integration tests locally + CI.
- `src/OpenClaw.Security.Vault/README.md` — AOT compatibility note.
- `CHANGELOG.md` entry.
- Chinese translations in `docs/zh-CN/security/`.

## Risks

| Risk | Mitigation |
|---|---|
| VaultSharp heavy reflection bloats AOT binary | `IsAotCompatible=false`; only linked when `Vault.Enabled=true`; documented in vault README |
| 67 callers currently sync-resolve; new `vault:` throws on cold sync cache | Phase 1 introduces no `vault:` semantics; Phase 3 sync fail-fast is documented and prewarm is opt-in/opt-out via `PrewarmRequired` |
| Pre-warm blocks startup on Vault outage | `PrewarmRequired` switch; per-ref timeout; rate-limited concurrency |
| Vault single point of failure | TTL cache returns stale values on failure; refresh-ahead hides latency; ops runbook documents HA Vault topology |
| Recursive `vault:TokenRef` causes deadlock / loop | `ConfigValidator` rejects `TokenRef` starting with `"vault:"` at config load time |
| Misconfigured `Address` exfiltrates to attacker-controlled host | HTTPS required; loopback rejected when `PublicBind=true`; warning on `SkipVerify=true` |
| Log re-introduction of secret value via future refactor | Property test `RedactionPipeline_VaultRefValue_NeverAppearsInLog` runs in CI |
| Token leak via exception message | `VaultAuthException` includes only path + status, never token bytes; reviewed in PR checklist |

## Open Questions

None at design freeze. Items deferred to Phase 4 are explicitly non-goals for v1.

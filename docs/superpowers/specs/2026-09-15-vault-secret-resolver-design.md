# Vault / OpenBao 密钥解析后端设计

日期：2026-09-15
状态：草案

## 摘要

扩展 [`SecretResolver`](../../../src/OpenClaw.Core/Security/SecretResolver.cs)——目前唯一的同步、静态密钥解析咽喉点（支持 `env:`、`raw:`、裸串当作环境变量名/字面量回退）——加入外部 Vault / OpenBao 后端。新后端作为独立项目 `OpenClaw.Security.Vault` 交付，通过 [VaultSharp](https://github.com/rajanadar/VaultSharp) 对接 Vault / OpenBao HTTP API，并在 `OpenClaw.Core` 中新增 `ISecretResolver` 抽象进行编排。现有 67 个调用点保持零改动。

本设计与 [docs/security/payments.md:49](../../security/payments.md) 已列出的"保留扩展点"对齐（HashiCorp Vault、OpenBao、AWS Secrets Manager、Azure Key Vault、DPAPI），将 Vault / OpenBao 从"占位声明"落地为"已实现"。

## 目标

### P0：可插拔的密钥解析器

- `OpenClaw.Core.Security` 中的 `ISecretResolver` 接口同时暴露 `ResolveAsync(string?, CancellationToken)` 与同步 `Resolve(string?)`。
- 基于 scheme 的派发（`ISecretProvider.Scheme`）按前缀选择实现。
- 现有静态类 `SecretResolver` 保留为薄门面，委托给 DI 注册的 `ISecretResolver` 实例；当 DI 尚未引导时回退到原有逻辑。
- `EnvRawSecretProvider` 完全保留 `env:`、`raw:`、裸串三种解析的现行行为（100% 行为兼容）。

### P0：Vault / OpenBao 后端（v1）

- 新项目 `OpenClaw.Security.Vault`（`IsAotCompatible=false`）包装 VaultSharp。
- v1 仅支持 KV v2 读取（`secret/data/<path>#<key>`）。Transit、PKI、动态凭证、AWS / Azure / GCP 后端列入 v2。
- v1 仅支持 Token 认证；Kubernetes / AWS IAM / Azure / GCP / JWT 认证列入 v2。
- TTL 缓存（默认 5 分钟）+ 懒刷新 + 单飞（single-flight）防雪崩。
- 同步 `Resolve("vault:...")` 仅在缓存命中时返回；缓存未命中时**快速失败**抛 `SecretResolutionException`（不做 sync-over-async，避免死锁风险）。
- 异步 `ResolveAsync("vault:...")` 在缓存未命中时拉取、写入缓存、返回。
- `IHostedService`（`VaultRefPrewarmService`）启动时预热 `Security.Vault.PrewarmRefs` + 扫描配置中所有 `vault:` 前缀的 `*Ref` 字段。
- `PrewarmRequired` 标志控制启动期"硬失败 / 软失败"语义。

### P0：向后兼容

- 现有 67 个调用点继续工作，**零修改**。
- 现有 11 个 `SecurityTests.cs` 中的 `SecretResolver.*` 单测保持全绿，不修改。
- `Vault.Enabled=false` 为默认值；未启用的部署看不到任何行为变化。
- `vault:` 前缀与 `env:` / `raw:` 不冲突。

### P1：运维安全

- 任何日志行、异常消息、堆栈跟踪中**禁止**出现密钥 value。异常消息仅含 path、key 名、HTTP 状态码 / 错误码。
- 所有涉及 vault 查找的日志输出经过 `RedactionPipeline`（[RedactionPipeline.cs](../../../src/OpenClaw.Core/Security/RedactionPipeline.cs)）。
- 当 `Security.PublicBind=true` 时，`Address` 校验拒绝 `localhost` / `127.0.0.1` / `::1`（SSRF 防御）。
- Token 递归防护：`Security.Vault.TokenRef` 若以 `vault:` 开头，配置校验直接拒绝。

## 非目标（v1 不做）

- Kubernetes、AWS IAM、Azure managed identity、GCP、JWT 认证方式（v2）。
- Vault Transit、PKI、动态数据库 / AWS 凭证引擎（v2）。
- NativeAOT 兼容的 Vault 构建（`OpenClaw.Security.Vault` 出厂即 `IsAotCompatible=false`）。
- 跨进程持久加密缓存。
- 跨进程密钥共享。

## 架构

```
src/
├── OpenClaw.Core/
│   └── Security/
│       ├── ISecretProvider.cs              （新增）
│       ├── ISecretResolver.cs              （新增）
│       ├── CompositeSecretResolver.cs      （新增）
│       ├── EnvRawSecretProvider.cs         （新增——从 SecretResolver 抽出）
│       ├── ResolverAccessor.cs             （新增——桥接 IServiceProvider）
│       ├── SecretResolutionException.cs    （新增）
│       ├── SecretResolver.cs               （重构——改为门面）
│       ├── AllowlistManager.cs             （不变）
│       ├── RedactionPipeline.cs            （不变）
│       └── ...
├── OpenClaw.Security.Vault/                （新增项目）
│   ├── OpenClaw.Security.Vault.csproj      （IsAotCompatible=false）
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
│       └── GatewayBootstrapExtensions.cs   （修改——注册 Vault）
└── OpenClaw.Tests/
    └── Security/
        ├── SecretResolverFacadeTests.cs    （新增）
        ├── VaultSecretProviderTests.cs     （新增）
        ├── VaultRefParserTests.cs          （新增）
        ├── VaultRefCacheTests.cs           （新增）
        ├── VaultRefPrewarmServiceTests.cs  （新增）
        └── VaultIntegrationTests.cs        （新增——[Trait("Category","Integration")]）
```

## 组件

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
    string? Resolve(string? secretRef);     // 同步门面：缓存命中即返回；否则抛
    bool IsRawRef(string? secretRef);
}
```

### `OpenClaw.Core/Security/CompositeSecretResolver.cs`

- 构造函数接收按优先级排序的 `IEnumerable<ISecretProvider>`。
- `ResolveAsync`：识别 scheme → 路由到首个 `CanResolve(ref)` 为 true 的 provider → 返回其结果。
- 未识别前缀且无 provider 命中：沿用现有 `LooksLikeEnvVarName` 启发式记 warning，返回字面量回退（与现行行为一致）。
- 同步 `Resolve`：相同路由；若匹配的 provider 是 `VaultSecretProvider` 且缓存未命中，抛 `SecretResolutionException`。

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

### `OpenClaw.Core/Security/SecretResolver.cs`（重构后的门面）

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

        // DI 尚未引导时的 legacy 回退（如 CLI 启动早期）
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

    private static string? LegacyResolve(string? secretRef, ILogger? logger) { /* 原逻辑 */ }
    private static bool LooksLikeEnvVarName(string value) { /* 原逻辑 */ }
}
```

legacy 路径行为不变式：
- `Resolve(null | 全空白)` → `null`
- `Resolve("env:X")` → `Environment.GetEnvironmentVariable("X")`，未设置时返回 `null`
- `Resolve("raw:X")` → `"X"`
- `Resolve("裸串")` → 先查 env，未命中回退到字面量并记 warning

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

    private async Task<string?> ResolveInternalAsync(VaultRef parsed, CancellationToken ct) { /* 见数据流 */ }
}
```

`IVaultClient` 是对 VaultSharp 自带 `IVaultClient` 的薄包装，便于单测用 NSubstitute 替换，避免真实 Vault 依赖。

### `OpenClaw.Security.Vault/VaultRefParser.cs`

```csharp
public readonly record struct VaultRef(string Path, string Key, int KvVersion, string Mount);

public static class VaultRefParser
{
    public static VaultRef Parse(string secretRef);   // 解析失败抛 VaultRefParseException
}
```

语法：`vault:<mount>/data/<path>#<key>`，其中 `<mount>` 缺省时使用 `"secret"`（可由 `VaultSecurityOptions.KvMount` 配置）。
- `vault:secret/data/openclaw/openai#api_key` → `{ Mount="secret", Path="openclaw/openai", Key="api_key", KvVersion=2 }`
- `vault:data/openclaw/openai#api_key`（无 mount 段） → `{ Mount=KvMount, Path="openclaw/openai", Key="api_key", KvVersion=2 }`——当 ref 不含 `/data/` 段时，`#` 之前的整段视为 path，mount 取 `KvMount`。
- 空 path（`vault:#key`、`vault:secret/data/#key`）或缺 `#` 分隔符（`vault:secret/data/openclaw/openai`） → 抛 `VaultRefParseException`。

### `OpenClaw.Security.Vault/VaultRefCache.cs`

```csharp
public sealed class VaultRefCache
{
    public bool TryGet(VaultRef key, out string value);   // 同步读取；供 sync Resolve 使用
    public Task<string> GetOrFetchAsync(VaultRef key, Func<CancellationToken, Task<string>> fetch, CancellationToken ct);
    public void Invalidate(VaultRef key);
}
```

- 基于 `IMemoryCache`，`AbsoluteExpirationRelativeToNow = options.CacheTtl`。
- 单飞：`SemaphoreSlim` 按 cache key 维度加锁，防止并发击穿。
- TTL 到期后：下一次 `ResolveAsync` 同步返回旧值，同时后台异步刷新（refresh-ahead），热路径不阻塞。
- 拉取失败 + 存在旧值：返回旧值 + warning 日志。
- 拉取失败 + 无缓存值：异步抛 `VaultUnavailableException`，同步抛 `SecretResolutionException`。

### `OpenClaw.Security.Vault/VaultRefPrewarmService.cs`

```csharp
public sealed class VaultRefPrewarmService : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken);   // 解析 PrewarmRefs + 扫描出的引用
    public Task StopAsync(CancellationToken cancellationToken);    // no-op
}
```

- 并发上限：`options.RateLimit.RequestsPerSecond`。
- 单引用超时：`options.RequestTimeout`。
- 汇总失败；遵守 `options.PrewarmRequired`。

### `OpenClaw.Security.Vault/VaultServiceCollectionExtensions.cs`

```csharp
public static class VaultServiceCollectionExtensions
{
    public static IServiceCollection AddOpenClawVaultSecrets(this IServiceCollection services, GatewayConfig config);
}
```

- 仅在 `config.Security.Vault?.Enabled == true` 时注册。
- 从 `services.Configuration` 绑定 `VaultSecurityOptions`。
- 将 `VaultSecretProvider` 注册为 `ISecretProvider`（`Scheme = "vault"`）。
- 将 `VaultRefCache` 注册为单例。
- 将 `VaultRefPrewarmService` 注册为 `IHostedService`。
- 启动时校验 options，校验失败抛异常。

## 数据流

### 异步正常路径

```
caller → ResolveAsync("vault:secret/data/openclaw/openai#api_key", ct)
  → CompositeSecretResolver.ResolveAsync
    → VaultSecretProvider.CanResolve → true
    → VaultRefParser.Parse → VaultRef{ Mount="secret", Path="openclaw/openai", Key="api_key" }
    → VaultRefCache.TryGet → miss
    → VaultRefCache.GetOrFetchAsync
      → 获取该 key 的 SemaphoreSlim
      → IVaultClient.ReadSecretAsync("secret/data/openclaw/openai") → Secret<Dictionary<string,object>>
      → 提取 "api_key" → "sk-..."
      → IMemoryCache.Set，TTL = 5 分钟
      → 返回 value
    → 返回 value
```

### 同步正常路径（预热后）

```
caller → SecretResolver.Resolve("vault:secret/data/openclaw/openai#api_key")
  → ResolverAccessor.Current 已设置 → CompositeSecretResolver.Resolve
    → VaultSecretProvider.CanResolve → true
    → VaultRefCache.TryGet → hit
    → 返回缓存 value
```

### 同步快速失败路径

```
caller → SecretResolver.Resolve("vault:secret/data/openclaw/openai#api_key")
  → CompositeSecretResolver.Resolve
    → VaultSecretProvider.CanResolve → true
    → VaultRefCache.TryGet → miss
    → 抛 SecretResolutionException(
        "vault: ref 'secret/data/openclaw/openai#api_key' requires async path or pre-warm. " +
        "Configure Security.Vault.PrewarmRefs or call SecretResolver.ResolveAsync.")
```

### 启动预热流程

```
VaultRefPrewarmService.StartAsync(ct)
  → 通过 ResolveAsync 解析 options.TokenRef（仅支持 env:/raw:）
  → 用 token + address + namespace + tls 构造 IVaultClient
  → 收集 refs = options.PrewarmRefs ∪ ScanConfigForVaultRefs(gatewayConfig)
  → 并行 for（限流）每个 ref：
      → ResolveAsync(ref, ct) → successCount++
                                  → failureCount++（仅记录 path + 错误码，永不记 value）
  → 若 failureCount > 0：
      若 options.PrewarmRequired：抛 HostedServiceStartupException("N vault refs failed pre-warm: ...")
      否则：记 error summary 日志，继续
```

### 懒刷新（refresh-ahead）流程

```
VaultRefCache.GetOrFetchAsync(key)
  → TryGet → hit
    → 若 entry.Age > options.CacheTtl：
        → _ = Task.Run(() => RefreshAsync(key, ct))   // fire-and-forget 后台刷新
        → 返回旧值（不 await）
    → 否则：
        → 返回新值
  → TryGet → miss
    → 拉取 + 写缓存 + 返回
```

### 配置扫描

`ScanConfigForVaultRefs(GatewayConfig config)` 遍历 gateway config 树（channels、tools、model profiles、plugin configs），收集所有值以 `"vault:"` 开头的 `*Ref` 属性。基于反射、单遍扫描，结果在预热服务生命周期内缓存。

## 配置

### `appsettings.json`（追加节）

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

### C# 绑定

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

追加到既有 [`SecurityOptions`](../../../src/OpenClaw.Core/Models/GatewayConfig.cs) 作为 `public VaultSecurityOptions? Vault { get; set; }`。

### 配置校验（`ConfigValidator` 新增）

当 `Vault.Enabled == true` 时：
- `Address` 必填，必须是 HTTPS，必须是合法 URI。
- `TokenRef` 必填，**禁止**以 `"vault:"` 开头。
- `CacheTtl` ∈ [30s, 24h]。
- `RequestTimeout` ∈ [1s, 60s]。
- `RateLimit.RequestsPerSecond` ∈ [1, 1000]。
- 若 `Security.PublicBind == true`：拒绝 host 为 `localhost`、`127.0.0.1`、`::1` 的 `Address`。
- `Tls.SkipVerify == true` 产生 warning，除非 `Security.AllowInsecureTls == true`（全局 opt-in）。

### 配置来源

复用现有 `IConfiguration` 来源：`appsettings.json`、环境变量（`Security__Vault__Address`）、命令行（`--security:vault:address=...`）、经 `SecurityPostureBuilder` 加密的文件。**不**新增独立的 configuration provider。

## 错误处理

### 异常层次

```
SecretResolutionException                  （基类，在 OpenClaw.Core）
├── VaultRefParseException                 （Vault 项目）
├── VaultAuthException                     （Vault 项目，401/403）
├── VaultUnavailableException              （Vault 项目，5xx/超时/网络，含 Retryable 标记）
├── VaultPathNotFoundException             （Vault 项目，path 级 404）
└── VaultKeyNotFoundException              （Vault 项目，key 级 404）
```

`SecretResolutionException` 放在 `OpenClaw.Core`：legacy 回退路径在 Vault 类型加载前就可能抛出。

### 错误矩阵

| 场景 | 同步路径 | 异步路径 |
|---|---|---|
| `vault:` 缓存命中 | 返回 value | 返回 value |
| `vault:` 缓存未命中，异步调用方 | n/a（同步不能拉取） | 拉取 → 缓存 → 返回 |
| `vault:` 缓存未命中，同步调用方 | 抛 `SecretResolutionException` | n/a |
| TokenRef 本身不可解析 | 抛 `SecretResolutionException` | 抛 `SecretResolutionException` |
| Vault 401/403 | 抛 `VaultAuthException`（消息中不含 value） | 抛 `VaultAuthException` |
| Vault 5xx、网络、超时 | 抛 `VaultUnavailableException` | 抛；若缓存有旧值则返回旧值 + warning |
| Vault path 级 404 | 抛 `VaultPathNotFoundException` | 抛 |
| Vault key 级 404 | 抛 `VaultKeyNotFoundException` | 抛 |
| Vault 节未启用但使用了 `vault:` 前缀 | 抛 `VaultNotConfiguredException` | 抛 |

### 不泄漏保证

- 异常消息：仅含 path、key、HTTP 状态码、错误码名称。绝不包含解析后的 value。
- 异常的 `ToString()` 不暴露 `Data["value"]` 或任何承载 payload 的字段。
- `ILogger.Log*` 调用永不以参数形式传入解析后的 value。
- 日志输出统一经 `RedactionPipeline` 做二次防护。
- `VaultSecretProvider` 不在 `IVaultClient` 构造之外持久化 token；无日志捕获 token。

## 测试

### 单元测试（`OpenClaw.Tests/Security/Vault*Tests.cs`）

| 测试 | 断言 |
|---|---|
| `Parse_ValidRef_ReturnsMountPathKey` | 语法解析 |
| `Parse_MissingKey_Throws_VaultRefParseException` | 格式错误 |
| `Parse_EmptyPath_Throws_VaultRefParseException` | 格式错误 |
| `Parse_DefaultMount_Applied_WhenMissing` | `vault:data/x#k` 默认 mount 为 "secret" |
| `ResolveAsync_CacheHit_NoHttpCall` | NSubstitute 验证 `IVaultClient` 未被调用 |
| `ResolveAsync_CacheMiss_OneHttpCall_CachesAndReturns` | 拉取 → 缓存 → 返回 |
| `ResolveAsync_Vault401_Throws_VaultAuthException_NoValueInMessage` | 不泄漏 value |
| `ResolveAsync_VaultTimeout_Throws_VaultUnavailableException_RetryableTrue` | 超时路径 |
| `ResolveAsync_Vault404_Path_Throws_VaultPathNotFoundException` | path 级 404 |
| `ResolveAsync_Vault404_Key_Throws_VaultKeyNotFoundException` | key 级 404 |
| `ResolveAsync_CtsCancelled_Throws_OperationCanceledException` | 取消传播 |
| `SyncResolve_VaultCacheHit_Returns` | 同步正常路径 |
| `SyncResolve_VaultCacheMiss_Throws_SecretResolutionException_MessageMentionsAsyncOrPrewarm` | 同步快速失败 |
| `LegacyResolve_BehavesIdenticalToPreRefactor` | 向后兼容（11 个既有测试的快照） |
| `Cache_ConcurrentMisses_SingleFlight_OneHttpCall` | 防雪崩 |
| `Cache_Expire_TriggersRefreshAhead_StaleValueReturned` | refresh-ahead |
| `Cache_FetchFailureWithStale_ReturnsStaleAndLogs` | 失败回退 |
| `Cache_FetchFailureNoStale_Throws_VaultUnavailable` | 冷启动失败 |
| `PrewarmService_AllSucceed_Starts` | 预热正常 |
| `PrewarmService_OneFails_PrewarmRequired_Throws_HostStartupException` | 硬失败 |
| `PrewarmService_OneFails_PrewarmNotRequired_Logs_NoThrow` | 软失败 |
| `PrewarmService_RateLimit_CapsConcurrency` | 吞吐上限 |
| `ConfigValidator_VaultEnabled_MissingAddress_Rejects` | 配置校验 |
| `ConfigValidator_TokenRefStartsWithVault_Rejects` | 递归防护 |
| `ConfigValidator_CacheTtlOutOfRange_Rejects` | 边界校验 |
| `ConfigValidator_PublicBind_RejectsLoopbackAddress` | SSRF 防护 |
| `RedactionPipeline_VaultRefValue_NeverAppearsInLog` | 横切不泄漏 |
| `ResolverAccessor_NotBootstrapped_LegacyFallbackUsed` | DI 未引导路径 |
| `ResolverAccessor_Bootstrapped_RoutesToInstance` | DI 已引导路径 |

### 集成测试（`VaultIntegrationTests.cs`）

```csharp
[Trait("Category", "Integration")]
public sealed class VaultIntegrationTests
{
    // 除非 OPENBAO_ADDR 与 OPENBAO_TOKEN 已设置，否则跳过。
    // deploy/docker-compose/openbao.yml 为 CI 提供 OpenBao 实例。
}
```

| 测试 | 断言 |
|---|---|
| `End2End_PutAndResolve_KvV2` | 真实 OpenBao 端到端往返 |
| `End2End_TtlExpiry_FetchesAgain` | 真实缓存失效 |
| `End2End_TokenUnauth_Throws_VaultAuthException` | 真实认证失败 |
| `End2End_RotatedValue_PickedUpAfterTtl` | 真实轮换场景 |

CI 工作流：`ci.yml` 中可选 job，通过 `[Category("Integration")]` filter 触发，由仓库变量 `RUN_VAULT_INTEGRATION` 控制开关。`deploy/docker-compose/openbao.yml` 是 OpenBao 镜像与引导脚本的单一事实源；该文件在 Phase 3 新建。

### 覆盖率目标

- `OpenClaw.Security.Vault` 行覆盖率 ≥ 85%，分支覆盖率 ≥ 75%。
- `OpenClaw.Core/Security/*` 新增部分：行覆盖率 100%。
- 不泄漏属性测试（FsCheck 或手写）：生成随机 ref → 解析 value，断言任何捕获到的日志消息中均不出现该 value。

## 迁移

### 阶段 1——抽象 + 门面（零行为变化）

- 新增 `ISecretProvider`、`ISecretResolver`、`CompositeSecretResolver`、`EnvRawSecretProvider`、`ResolverAccessor`。
- 重构 `SecretResolver`，委托到 `ResolverAccessor.Current`，提供 `LegacyResolve` 回退。
- 增加 `SecretResolver.StartAsync(IServiceProvider)` 扩展，由 `GatewayBootstrapExtensions` 与 `CliProgram` 启动流程调用。
- 既有 11 个 `SecretResolver.*` 测试**不修改**全部通过。
- 调用点零变更。

### 阶段 2——Vault 实现

- 创建项目 `OpenClaw.Security.Vault`（`IsAotCompatible=false`，引用 `VaultSharp`）。
- 实现 `VaultSecretProvider`、`VaultRefParser`、`VaultRefCache`、`VaultExceptions`。
- 按上表编写单测。

### 阶段 3——接线 + 配置 + 预热

- 在 `SecurityOptions` 中新增 `VaultSecurityOptions`；更新 `ConfigValidator`；在 `GatewayConfig.Security.Vault` 增加绑定。
- 增加 `AddOpenClawVaultSecrets(IConfiguration)` 扩展；在 `GatewayBootstrapExtensions` 中当 `Enabled == true` 时调用。
- 将 `VaultRefPrewarmService` 注册为 `IHostedService`。
- 创建 `deploy/docker-compose/openbao.yml`；在 `ci.yml` 中加入可选集成测试 job。
- 撰写 `docs/security/vault.md`（英文）与 `docs/zh-CN/security/vault.md`（中文）。
- 更新 `CHANGELOG.md`。
- 将 [docs/security/payments.md:49](../../security/payments.md) 从"保留扩展点"更新为"已实现（KV v2）"。

### 阶段 4——越界（推迟）

- Kubernetes / AWS IAM / Azure / GCP / JWT 认证。
- Transit、PKI、动态凭证。
- 持久加密缓存。

## 文档交付

- `docs/security/vault.md`——用户参考：配置 schema、引用语法、运维手册。
- `docs/security/vault-integration-tests.md`——本地 + CI 跑集成测试。注：OpenBao compose 文件位于 `deploy/docker-compose/openbao.yml`，跑 `[Category("Integration")]` 测试前先 `docker compose -f deploy/docker-compose/openbao.yml up -d`。
- `src/OpenClaw.Security.Vault/README.md`——AOT 兼容性说明。
- `CHANGELOG.md` 条目。
- `docs/zh-CN/security/` 下中文翻译。

## 风险

| 风险 | 缓解 |
|---|---|
| VaultSharp 重依赖反射且当前 gateway 无条件引用该项目 | `IsAotCompatible=false`；当前依赖会进入标准发布产物，需通过条件构建边界或 AOT 安全实现解决；vault README 中文档化 |
| 67 个调用方当前 sync resolve；新 `vault:` 在冷缓存同步路径抛异常 | Phase 1 不引入 `vault:` 语义；Phase 3 同步快速失败有文档 + `PrewarmRequired` 显式开关 |
| 启动预热被 Vault 故障阻塞 | `PrewarmRequired` 开关；单引用超时；限流并发 |
| Vault 单点故障 | TTL 缓存失败回退旧值；refresh-ahead 隐藏延迟；运维 runbook 文档化 HA Vault 拓扑 |
| `vault:TokenRef` 递归导致死锁 / 环路 | `ConfigValidator` 在配置加载期直接拒绝 `vault:` 开头的 `TokenRef` |
| `Address` 误配导致流量外泄到攻击者控制的主机 | 强制 HTTPS；`PublicBind=true` 时拒绝 loopback；`SkipVerify=true` 触发 warning |
| 日志中重新出现 secret value（未来重构回归） | 属性测试 `RedactionPipeline_VaultRefValue_NeverAppearsInLog` 在 CI 中执行 |
| 异常消息泄漏 token | `VaultAuthException` 仅含 path + 状态码，永不含 token 字节；PR 检查清单覆盖 |

## 开放问题

无（v1 设计冻结）。推迟到阶段 4 的项目已显式列为非目标。

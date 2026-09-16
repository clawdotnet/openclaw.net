# Vault / OpenBao Secret Resolver Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 给 `SecretResolver` 加入 Vault / OpenBao 外部密钥库后端（基于 VaultSharp），保持现有 67 个调用点零改动。

**Architecture:** 在 `OpenClaw.Core` 新增 `ISecretProvider` / `ISecretResolver` 抽象与 `CompositeSecretResolver`；新建 `OpenClaw.Security.Vault` 项目（`IsAotCompatible=false`）实现 `VaultSecretProvider`、`VaultRefCache`、`VaultRefPrewarmService`；现有静态 `SecretResolver` 改为门面，DI 已引导时委托给 `ISecretResolver`，否则回退到 legacy 逻辑。同步 `vault:` 引用在缓存命中时直接返回，未命中时快速失败；异步 `ResolveAsync` 走 TTL 缓存 + 懒刷新 + 单飞；启动期 `IHostedService` 预热配置中的引用。

**Tech Stack:** .NET 10 / C# 14, xUnit v3, NSubstitute, VaultSharp, Microsoft.Extensions.Caching.Memory, Microsoft.Extensions.Hosting, ASP.NET Core (Gateway).

**Spec:** [docs/superpowers/specs/2026-09-15-vault-secret-resolver-design.md](../specs/2026-09-15-vault-secret-resolver-design.md)

---

## File Map

| 文件 | 操作 | 职责 |
|---|---|---|
| `src/OpenClaw.Core/Security/SecretResolutionException.cs` | 新增 | 同步路径快速失败 / 异步路径错误基类 |
| `src/OpenClaw.Core/Security/ISecretProvider.cs` | 新增 | 单 scheme 的密钥 provider 接口 |
| `src/OpenClaw.Core/Security/ISecretResolver.cs` | 新增 | 同步门面 + 异步入口 |
| `src/OpenClaw.Core/Security/ResolverAccessor.cs` | 新增 | 静态桥接 IServiceProvider |
| `src/OpenClaw.Core/Security/EnvRawSecretProvider.cs` | 新增 | 从 SecretResolver 抽出 env:/raw:/裸串 逻辑 |
| `src/OpenClaw.Core/Security/CompositeSecretResolver.cs` | 新增 | 按 scheme 派发到 ISecretProvider |
| `src/OpenClaw.Core/Security/SecretResolver.cs` | 修改 | 改为门面，保留 LegacyResolve 回退 |
| `src/OpenClaw.Core/Models/ConfigurationModels.cs` | 修改 | 新增 VaultSecurityOptions + 嵌套类型，SecurityOptions 加 Vault 字段 |
| `src/OpenClaw.Core/Validation/ConfigValidator.cs` | 修改 | 新增 Vault 配置校验规则 |
| `src/OpenClaw.Security.Vault/OpenClaw.Security.Vault.csproj` | 新增 | IsAotCompatible=false，引用 VaultSharp + Core |
| `src/OpenClaw.Security.Vault/VaultExceptions.cs` | 新增 | VaultRefParseException / VaultAuthException / VaultUnavailableException / VaultPathNotFoundException / VaultKeyNotFoundException / VaultNotConfiguredException |
| `src/OpenClaw.Security.Vault/VaultSecurityOptions.cs` | 新增 | 与 Core 中的同名类一致（或 re-export） |
| `src/OpenClaw.Security.Vault/VaultRefParser.cs` | 新增 | 解析 `vault:<mount>/data/<path>#<key>` |
| `src/OpenClaw.Security.Vault/VaultRefCache.cs` | 新增 | IMemoryCache 包装，TTL + 单飞 + refresh-ahead |
| `src/OpenClaw.Security.Vault/IVaultClient.cs` | 新增 | VaultSharp 客户端薄包装（便于 NSubstitute） |
| `src/OpenClaw.Security.Vault/VaultSecretProvider.cs` | 新增 | ISecretProvider, scheme="vault"，async resolve + 缓存 |
| `src/OpenClaw.Security.Vault/VaultServiceCollectionExtensions.cs` | 新增 | `AddOpenClawVaultSecrets` |
| `src/OpenClaw.Security.Vault/VaultRefPrewarmService.cs` | 新增 | IHostedService，启动期预热 PrewarmRefs + 扫描引用 |
| `src/OpenClaw.Security.Vault/README.md` | 新增 | AOT 兼容性说明 |
| `src/OpenClaw.Gateway/OpenClaw.Gateway.csproj` | 修改 | 新增 ProjectReference OpenClaw.Security.Vault |
| `src/OpenClaw.Gateway/Bootstrap/GatewayBootstrapExtensions.cs` | 修改 | 注册 Vault 服务 + 预热 hosted service + ResolverAccessor.Use |
| `OpenClaw.Net.slnx` | 修改 | 加入 OpenClaw.Security.Vault 项目 |
| `deploy/docker-compose/openbao.yml` | 新增 | 集成测试用 OpenBao compose |
| `.github/workflows/ci.yml` | 修改 | 新增可选 [Category("Integration")] job |
| `docs/security/vault.md` | 新增 | 配置 + 引用语法 + 运维手册（英文） |
| `docs/security/vault-integration-tests.md` | 新增 | 集成测试运行指南（英文） |
| `docs/zh-CN/security/vault.md` | 新增 | 中文版 vault.md |
| `docs/zh-CN/security/vault-integration-tests.md` | 新增 | 中文版 integration-tests |
| `docs/security/payments.md` | 修改 | 将 Vault 状态从"保留扩展点"改为"已实现（KV v2）" |
| `CHANGELOG.md` | 修改 | 新增 v1 条目 |
| `src/OpenClaw.Tests/Security/SecretResolutionExceptionTests.cs` | 新增 | 异常测试 |
| `src/OpenClaw.Tests/Security/ResolverAccessorTests.cs` | 新增 | 桥接逻辑测试 |
| `src/OpenClaw.Tests/Security/CompositeSecretResolverTests.cs` | 新增 | 派发逻辑测试 |
| `src/OpenClaw.Tests/Security/EnvRawSecretProviderTests.cs` | 新增 | 提取后行为兼容测试 |
| `src/OpenClaw.Tests/Security/SecretResolverFacadeTests.cs` | 新增 | 门面委托 + legacy 回退测试 |
| `src/OpenClaw.Tests/Security/VaultRefParserTests.cs` | 新增 | 语法解析测试 |
| `src/OpenClaw.Tests/Security/VaultRefCacheTests.cs` | 新增 | TTL / 单飞 / refresh-ahead / 失败回退 |
| `src/OpenClaw.Tests/Security/VaultSecretProviderTests.cs` | 新增 | 缓存命中/未命中/错误矩阵 + 不泄漏 |
| `src/OpenClaw.Tests/Security/VaultSecurityOptionsTests.cs` | 新增 | ConfigValidator Vault 规则 |
| `src/OpenClaw.Tests/Security/VaultRefPrewarmServiceTests.cs` | 新增 | 硬失败/软失败/限流 |
| `src/OpenClaw.Tests/Security/VaultIntegrationTests.cs` | 新增 | [Trait("Category","Integration")] 真实 OpenBao |

---

## Global Constraints

- **TargetFramework**: `net10.0`；**LangVersion**: 14；**Nullable**: enable；**TreatWarningsAsErrors**: true；**TrimMode**: link；**InvariantGlobalization**: true（来自 `Directory.Build.props`）。
- **OpenClaw.Core**：`IsAotCompatible=true`、`IsPackable=true`。Core 不能引用 VaultSharp。
- **OpenClaw.Security.Vault**：`IsAotCompatible=false`（与 `OpenClaw.SemanticKernelAdapter`、`OpenClaw.Plugins.Mempalace` 同模式）。`IsPackable=false`。
- **测试框架**：xUnit v3 + NSubstitute（既有）。`OpenClaw.Tests` 已 `ProjectReference` 全部主要项目，新增 `OpenClaw.Security.Vault` 引用。
- **.NET 已有命名空间约定**：`OpenClaw.Core.Security` 是密钥相关代码所在命名空间；新增接口/类沿用。
- **配置 binding**：使用现有 `IConfiguration` 来源（appsettings.json / env vars / 命令行 / `SecurityPostureBuilder` 加密文件）。**不**新增 IConfigurationProvider。
- **不破坏既有调用点**：现有 67 个 `SecretResolver.Resolve(...)` 调用保持零修改；现有 11 个 `SecretResolver.*` 单测保持不修改全绿。
- **不泄漏密钥 value**：异常消息、日志、堆栈中绝不出现解析后的 value。仅 path / key / 状态码 / 错误码名。日志经 `RedactionPipeline`。
- **Token 递归防护**：`Security.Vault.TokenRef` 以 `vault:` 开头时 `ConfigValidator` 必须拒。
- **缓存不变量**：TTL 默认 5 分钟（`CacheTtl` ∈ [30s, 24h]）；单飞；TTL 到期后下次访问触发后台刷新（refresh-ahead），热路径不阻塞。
- **预热语义**：硬失败（`PrewarmRequired=true`）vs 软失败（`=false`）；并发限流由 `RateLimit.RequestsPerSecond` 控制（默认 20）。
- **集成测试默认跳过**：除非 `OPENBAO_ADDR` 与 `OPENBAO_TOKEN` 环境变量已设置。

---

## Phase 1 — Core 抽象 + 门面（零行为变化）

### Task 1: 核心抽象（异常、接口、Accessor）

**Files:**
- Create: `src/OpenClaw.Core/Security/SecretResolutionException.cs`
- Create: `src/OpenClaw.Core/Security/ISecretProvider.cs`
- Create: `src/OpenClaw.Core/Security/ISecretResolver.cs`
- Create: `src/OpenClaw.Core/Security/ResolverAccessor.cs`
- Create: `src/OpenClaw.Tests/Security/SecretResolutionExceptionTests.cs`
- Create: `src/OpenClaw.Tests/Security/ResolverAccessorTests.cs`

**Interfaces:**
- Consumes: 无（最底层契约）
- Produces:
  - `SecretResolutionException(string message)`
  - `ISecretProvider { string Scheme; bool CanResolve(string); ValueTask<string?> ResolveAsync(string, CancellationToken) }`
  - `ISecretResolver { ValueTask<string?> ResolveAsync(string?, CancellationToken); string? Resolve(string?); bool IsRawRef(string?) }`
  - `ResolverAccessor { static ISecretResolver? Current; static void Use(IServiceProvider) }`

- [ ] **Step 1: 写失败测试 — SecretResolutionException**

`src/OpenClaw.Tests/Security/SecretResolutionExceptionTests.cs`:

```csharp
using OpenClaw.Core.Security;
using Xunit;

namespace OpenClaw.Tests.Security;

public sealed class SecretResolutionExceptionTests
{
    [Fact]
    public void Constructor_SetsMessage()
    {
        var ex = new SecretResolutionException("missing key");
        Assert.Equal("missing key", ex.Message);
    }

    [Fact]
    public void IsException()
    {
        Assert.True(new SecretResolutionException("x") is Exception);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SecretResolutionExceptionTests"
```

Expected: compile error — `SecretResolutionException` 不存在。

- [ ] **Step 3: 实现 SecretResolutionException**

`src/OpenClaw.Core/Security/SecretResolutionException.cs`:

```csharp
namespace OpenClaw.Core.Security;

/// <summary>
/// Thrown when a secret reference cannot be resolved. The legacy sync fallback
/// path may throw this before Vault types load; async paths and the vault
/// backend throw vault-specific exceptions derived from this base.
/// </summary>
public class SecretResolutionException : Exception
{
    public SecretResolutionException(string message) : base(message) { }
    public SecretResolutionException(string message, Exception inner) : base(message, inner) { }
}
```

- [ ] **Step 4: 跑测试确认通过**

Expected: PASS.

- [ ] **Step 5: 写失败测试 — ResolverAccessor**

`src/OpenClaw.Tests/Security/ResolverAccessorTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using OpenClaw.Core.Security;
using Xunit;

namespace OpenClaw.Tests.Security;

public sealed class ResolverAccessorTests : IDisposable
{
    public ResolverAccessorTests()
    {
        // Reset state for test isolation
        ResolverAccessor.Reset();
    }

    public void Dispose() => ResolverAccessor.Reset();

    [Fact]
    public void Current_BeforeUse_IsNull()
    {
        Assert.Null(ResolverAccessor.Current);
    }

    [Fact]
    public void Use_RegistersResolverFromServiceProvider()
    {
        var resolver = new FakeResolver();
        var services = new ServiceCollection();
        services.AddSingleton<ISecretResolver>(resolver);
        using var sp = services.BuildServiceProvider();

        ResolverAccessor.Use(sp);

        Assert.Same(resolver, ResolverAccessor.Current);
    }

    [Fact]
    public void Use_WhenNoResolverRegistered_CurrentStaysNull()
    {
        var services = new ServiceCollection();
        using var sp = services.BuildServiceProvider();

        ResolverAccessor.Use(sp);

        Assert.Null(ResolverAccessor.Current);
    }

    private sealed class FakeResolver : ISecretResolver
    {
        public ValueTask<string?> ResolveAsync(string? secretRef, CancellationToken ct = default)
            => ValueTask.FromResult<string?>("fake");
        public string? Resolve(string? secretRef) => "fake";
        public bool IsRawRef(string? secretRef) => false;
    }
}
```

- [ ] **Step 6: 跑测试确认失败（缺 ISecretResolver / ResolverAccessor）**

Expected: compile errors.

- [ ] **Step 7: 实现 ISecretProvider + ISecretResolver + ResolverAccessor**

`src/OpenClaw.Core/Security/ISecretProvider.cs`:

```csharp
namespace OpenClaw.Core.Security;

/// <summary>
/// Resolves secrets for a single scheme ("env", "raw", "vault", ...).
/// Implementations must be safe to register as singletons.
/// </summary>
public interface ISecretProvider
{
    /// <summary>Prefix this provider claims, without trailing colon (e.g. "vault", "env", "raw").</summary>
    string Scheme { get; }

    /// <summary>True if <paramref name="secretRef"/> matches this provider's scheme.</summary>
    bool CanResolve(string secretRef);

    /// <summary>Resolve the secret reference asynchronously.</summary>
    ValueTask<string?> ResolveAsync(string secretRef, CancellationToken ct);
}
```

`src/OpenClaw.Core/Security/ISecretResolver.cs`:

```csharp
namespace OpenClaw.Core.Security;

/// <summary>
/// Composite secret resolver exposing both a sync facade (cache hit or throw)
/// and an async entry point. Backed by one or more <see cref="ISecretProvider"/>.
/// </summary>
public interface ISecretResolver
{
    ValueTask<string?> ResolveAsync(string? secretRef, CancellationToken ct = default);

    /// <summary>
    /// Sync facade. For <c>env:</c>/<c>raw:</c>/bare refs returns synchronously.
    /// For <c>vault:</c> refs returns the cached value if present, otherwise throws
    /// <see cref="SecretResolutionException"/> (no sync-over-async, no deadlock risk).
    /// </summary>
    string? Resolve(string? secretRef);

    bool IsRawRef(string? secretRef);
}
```

`src/OpenClaw.Core/Security/ResolverAccessor.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace OpenClaw.Core.Security;

/// <summary>
/// Bridges the static <see cref="SecretResolver"/> facade to a DI-registered
/// <see cref="ISecretResolver"/>. Set once during bootstrap via <see cref="Use"/>.
/// </summary>
public static class ResolverAccessor
{
    private static IServiceProvider? _serviceProvider;
    private static ISecretResolver? _resolver;
    private static readonly object _lock = new();

    public static ISecretResolver? Current
    {
        get
        {
            lock (_lock) return _resolver;
        }
    }

    public static void Use(IServiceProvider serviceProvider)
    {
        lock (_lock)
        {
            _serviceProvider = serviceProvider;
            _resolver = serviceProvider.GetService<ISecretResolver>();
        }
    }

    internal static void Reset()
    {
        lock (_lock)
        {
            _serviceProvider = null;
            _resolver = null;
        }
    }
}
```

> 注：`Reset` 是 `internal`；`OpenClaw.Tests` 已通过 `InternalsVisibleTo("OpenClaw.Tests")` 看到它（见 [OpenClaw.Core.csproj:11](src/OpenClaw.Core/OpenClaw.Core.csproj)）。

- [ ] **Step 8: 跑测试确认通过**

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~ResolverAccessorTests|FullyQualifiedName~SecretResolutionExceptionTests"
```

Expected: PASS.

- [ ] **Step 9: 提交**

```bash
git add src/OpenClaw.Core/Security/SecretResolutionException.cs \
        src/OpenClaw.Core/Security/ISecretProvider.cs \
        src/OpenClaw.Core/Security/ISecretResolver.cs \
        src/OpenClaw.Core/Security/ResolverAccessor.cs \
        src/OpenClaw.Tests/Security/SecretResolutionExceptionTests.cs \
        src/OpenClaw.Tests/Security/ResolverAccessorTests.cs
git commit -m "feat(core): add ISecretProvider / ISecretResolver / ResolverAccessor abstractions"
```

---

### Task 2: EnvRawSecretProvider + CompositeSecretResolver

**Files:**
- Create: `src/OpenClaw.Core/Security/EnvRawSecretProvider.cs`
- Create: `src/OpenClaw.Core/Security/CompositeSecretResolver.cs`
- Create: `src/OpenClaw.Tests/Security/EnvRawSecretProviderTests.cs`
- Create: `src/OpenClaw.Tests/Security/CompositeSecretResolverTests.cs`

**Interfaces:**
- Consumes: `ISecretProvider` (Task 1)
- Produces:
  - `EnvRawSecretProvider : ISecretProvider` — `Scheme = "env"`（同时覆盖 `env:`/`raw:`/裸串）
  - `CompositeSecretResolver : ISecretResolver` — 按优先级派发

- [ ] **Step 1: 写失败测试 — EnvRawSecretProvider 行为等价现有 SecretResolver**

`src/OpenClaw.Tests/Security/EnvRawSecretProviderTests.cs`:

```csharp
using OpenClaw.Core.Security;
using Xunit;

namespace OpenClaw.Tests.Security;

public sealed class EnvRawSecretProviderTests
{
    [Fact]
    public void Scheme_IsEnv() => Assert.Equal("env", new EnvRawSecretProvider().Scheme);

    [Fact]
    public void CanResolve_EnvPrefix_True()
        => Assert.True(new EnvRawSecretProvider().CanResolve("env:FOO"));

    [Fact]
    public void CanResolve_RawPrefix_True()
        => Assert.True(new EnvRawSecretProvider().CanResolve("raw:hello"));

    [Fact]
    public void CanResolve_BareString_True()
        => Assert.True(new EnvRawSecretProvider().CanResolve("MY_VAR"));

    [Fact]
    public void CanResolve_VaultPrefix_False()
        => Assert.False(new EnvRawSecretProvider().CanResolve("vault:secret/x"));

    [Fact]
    public async Task ResolveAsync_EnvPrefix_ReadsEnvironment()
    {
        Environment.SetEnvironmentVariable("OPENCLAW_PROVIDER_TEST_1", "v");
        try
        {
            var p = new EnvRawSecretProvider();
            Assert.True(p.CanResolve("env:OPENCLAW_PROVIDER_TEST_1"));
            Assert.Equal("v", await p.ResolveAsync("env:OPENCLAW_PROVIDER_TEST_1", CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_PROVIDER_TEST_1", null);
        }
    }

    [Fact]
    public async Task ResolveAsync_RawPrefix_ReturnsLiteral()
    {
        var p = new EnvRawSecretProvider();
        Assert.Equal("my-secret", await p.ResolveAsync("raw:my-secret", CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAsync_BareString_EnvHit()
    {
        Environment.SetEnvironmentVariable("OPENCLAW_PROVIDER_TEST_2", "env-value");
        try
        {
            var p = new EnvRawSecretProvider();
            Assert.Equal("env-value", await p.ResolveAsync("OPENCLAW_PROVIDER_TEST_2", CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_PROVIDER_TEST_2", null);
        }
    }

    [Fact]
    public async Task ResolveAsync_BareString_EnvMiss_ReturnsLiteral()
    {
        var p = new EnvRawSecretProvider();
        // Use a string unlikely to be set
        Assert.Equal("OPENCLAW_PROVIDER_TEST_DEFINITELY_UNSET", await p.ResolveAsync("OPENCLAW_PROVIDER_TEST_DEFINITELY_UNSET", CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAsync_EmptyRef_ReturnsNull()
        => Assert.Null(await new EnvRawSecretProvider().ResolveAsync("", CancellationToken.None));
}
```

- [ ] **Step 2: 跑测试确认失败**

Expected: compile error — `EnvRawSecretProvider` 不存在。

- [ ] **Step 3: 实现 EnvRawSecretProvider**

`src/OpenClaw.Core/Security/EnvRawSecretProvider.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace OpenClaw.Core.Security;

/// <summary>
/// Resolves <c>env:</c>, <c>raw:</c>, and bare-string (treated as env var name
/// with literal fallback) secret references. Behavior is identical to the
/// legacy <see cref="SecretResolver"/> implementation.
/// </summary>
public sealed class EnvRawSecretProvider : ISecretProvider
{
    private const string EnvPrefix = "env:";
    private const string RawPrefix = "raw:";

    public string Scheme => "env";

    public bool CanResolve(string secretRef) =>
        !string.IsNullOrWhiteSpace(secretRef) && (
            secretRef.StartsWith(EnvPrefix, StringComparison.OrdinalIgnoreCase) ||
            secretRef.StartsWith(RawPrefix, StringComparison.OrdinalIgnoreCase) ||
            LooksLikeEnvVarName(secretRef));

    public ValueTask<string?> ResolveAsync(string secretRef, CancellationToken ct)
        => new(ResolveInternal(secretRef));

    private static string? ResolveInternal(string secretRef)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
            return null;

        if (secretRef.StartsWith(EnvPrefix, StringComparison.OrdinalIgnoreCase))
            return Environment.GetEnvironmentVariable(secretRef[EnvPrefix.Length..]);

        if (secretRef.StartsWith(RawPrefix, StringComparison.OrdinalIgnoreCase))
            return secretRef[RawPrefix.Length..];

        var envValue = Environment.GetEnvironmentVariable(secretRef);
        return envValue ?? secretRef;
    }

    private static bool LooksLikeEnvVarName(string value)
        => value.Length >= 3 && value.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_');
}
```

> 注：本类型不依赖 `ILogger`——旧 `SecretResolver.Resolve(ref, logger)` 的 warning 路径在 composite 层处理（避免每次 resolve 都要传 logger）。如未来需恢复 warning，由 `CompositeSecretResolver` 包裹并注入 logger。

- [ ] **Step 4: 跑测试确认通过**

Expected: PASS.

- [ ] **Step 5: 写失败测试 — CompositeSecretResolver 派发**

`src/OpenClaw.Tests/Security/CompositeSecretResolverTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Security;
using Xunit;

namespace OpenClaw.Tests.Security;

public sealed class CompositeSecretResolverTests
{
    [Fact]
    public void EmptyProviders_ResolveAsync_ReturnsNullForNullRef()
    {
        var resolver = new CompositeSecretResolver(Array.Empty<ISecretProvider>(), NullLogger<CompositeSecretResolver>.Instance);
        Assert.Null(resolver.Resolve(null));
        Assert.Null(resolver.ResolveAsync(null).AsTask().GetAwaiter().GetResult());
    }

    [Fact]
    public async Task ResolveAsync_DispatchesToFirstMatchingProvider()
    {
        var env = new EnvRawSecretProvider();
        var fakeVault = new FakeProvider("vault", "vault:secret/x#k", "vault-value");
        var resolver = new CompositeSecretResolver(new ISecretProvider[] { env, fakeVault }, NullLogger<CompositeSecretResolver>.Instance);

        Assert.Equal("vault-value", await resolver.ResolveAsync("vault:secret/x#k", CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAsync_NoMatch_FallsBackToEnvRawProvider()
    {
        var env = new EnvRawSecretProvider();
        var resolver = new CompositeSecretResolver(new ISecretProvider[] { env }, NullLogger<CompositeSecretResolver>.Instance);

        // Bare string with no env hit — composite defers to env provider which returns literal
        Assert.Equal("plain-text", await resolver.ResolveAsync("plain-text", CancellationToken.None));
    }

    [Fact]
    public void Resolve_Sync_DelegatesToProvider()
    {
        var env = new EnvRawSecretProvider();
        var resolver = new CompositeSecretResolver(new ISecretProvider[] { env }, NullLogger<CompositeSecretResolver>.Instance);

        Assert.Equal("value", resolver.Resolve("raw:value"));
    }

    [Fact]
    public void IsRawRef_True()
        => Assert.True(new CompositeSecretResolver(Array.Empty<ISecretProvider>(), NullLogger<CompositeSecretResolver>.Instance).IsRawRef("raw:x"));

    [Fact]
    public void IsRawRef_False_Null()
        => Assert.False(new CompositeSecretResolver(Array.Empty<ISecretProvider>(), NullLogger<CompositeSecretResolver>.Instance).IsRawRef(null));

    private sealed class FakeProvider : ISecretProvider
    {
        private readonly string _matchRef;
        private readonly string _value;
        public FakeProvider(string scheme, string matchRef, string value)
        {
            Scheme = scheme; _matchRef = matchRef; _value = value;
        }
        public string Scheme { get; }
        public bool CanResolve(string r) => r == _matchRef;
        public ValueTask<string?> ResolveAsync(string r, CancellationToken ct) => new(_value);
    }
}
```

- [ ] **Step 6: 跑测试确认失败**

Expected: compile error — `CompositeSecretResolver` 不存在。

- [ ] **Step 7: 实现 CompositeSecretResolver**

`src/OpenClaw.Core/Security/CompositeSecretResolver.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace OpenClaw.Core.Security;

/// <summary>
/// Routes <see cref="ResolveAsync(string?, CancellationToken)"/> calls to the
/// first <see cref="ISecretProvider"/> whose <see cref="ISecretProvider.CanResolve"/>
/// returns true. Order of registration is the precedence order.
/// </summary>
public sealed class CompositeSecretResolver : ISecretResolver
{
    private readonly IReadOnlyList<ISecretProvider> _providers;
    private readonly ILogger<CompositeSecretResolver> _logger;

    public CompositeSecretResolver(IEnumerable<ISecretProvider> providers, ILogger<CompositeSecretResolver> logger)
    {
        _providers = providers.ToArray();
        _logger = logger;
    }

    public async ValueTask<string?> ResolveAsync(string? secretRef, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
            return null;

        foreach (var provider in _providers)
        {
            if (provider.CanResolve(secretRef))
                return await provider.ResolveAsync(secretRef, ct);
        }

        // No provider claimed it — defer to the first provider that could plausibly handle
        // bare/env/raw, otherwise return the literal as a last-resort fallback (mirrors legacy).
        _logger.LogDebug("No provider claimed ref of length {Length}; returning literal fallback.", secretRef.Length);
        return secretRef;
    }

    public string? Resolve(string? secretRef)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
            return null;

        foreach (var provider in _providers)
        {
            if (provider.CanResolve(secretRef))
            {
                // EnvRawSecretProvider is fully sync; other providers must implement a sync path.
                // VaultSecretProvider throws SecretResolutionException on cache miss.
                if (provider is ISyncSecretProvider sync)
                    return sync.ResolveSync(secretRef);
                throw new SecretResolutionException(
                    $"Provider '{provider.Scheme}' is async-only; call ResolveAsync or pre-warm.");
            }
        }

        return secretRef;
    }

    public bool IsRawRef(string? secretRef) =>
        secretRef is not null && secretRef.StartsWith("raw:", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Optional sync path for providers that can resolve without I/O. EnvRawSecretProvider
/// implements this; vault providers do not.
/// </summary>
public interface ISyncSecretProvider : ISecretProvider
{
    string? ResolveSync(string secretRef);
}
```

> 同步扩展点 `ISyncSecretProvider` 让 env/raw provider 在 `Resolve` 中零开销执行；vault provider 不实现，触发清晰错误。

- [ ] **Step 8: EnvRawSecretProvider 实现 ISyncSecretProvider**

修改 [src/OpenClaw.Core/Security/EnvRawSecretProvider.cs](src/OpenClaw.Core/Security/EnvRawSecretProvider.cs)：类签名改为 `public sealed class EnvRawSecretProvider : ISecretProvider, ISyncSecretProvider`，新增成员：

```csharp
public string? ResolveSync(string secretRef) => ResolveInternal(secretRef);
```

> `ResolveInternal` 当前是 `private static`——改为 `private`（非 static）或加 `internal static`；最简单是改成 `private static` 并新增 `ResolveSync` 直接调用 `Environment.GetEnvironmentVariable(...)` 路径，或者把 `ResolveInternal` 改成实例方法。最干净的方案：

```csharp
public string? ResolveSync(string secretRef) => ResolveInternal(secretRef);
```
并把 `ResolveInternal` 由 `private static` 改为 `private`。

- [ ] **Step 9: 跑测试确认通过**

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~CompositeSecretResolverTests|FullyQualifiedName~EnvRawSecretProviderTests"
```

Expected: PASS.

- [ ] **Step 10: 提交**

```bash
git add src/OpenClaw.Core/Security/EnvRawSecretProvider.cs \
        src/OpenClaw.Core/Security/CompositeSecretResolver.cs \
        src/OpenClaw.Tests/Security/EnvRawSecretProviderTests.cs \
        src/OpenClaw.Tests/Security/CompositeSecretResolverTests.cs
git commit -m "feat(core): add EnvRawSecretProvider and CompositeSecretResolver"
```

---

### Task 3: SecretResolver 改为门面（保持现有 11 个测试零修改通过）

**Files:**
- Modify: `src/OpenClaw.Core/Security/SecretResolver.cs`

**Interfaces:**
- Consumes: `ResolverAccessor` (Task 1), `ISecretResolver`
- Produces: `SecretResolver` 静态门面（行为向后兼容 11 个既有测试）

- [ ] **Step 1: 跑现有测试确认基线**

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SecurityTests"
```

Expected: 11 个 SecretResolver 相关测试 + 其他 SecurityTests 全绿。**这是重构基线**。

- [ ] **Step 2: 重写 SecretResolver 为门面**

替换 [src/OpenClaw.Core/Security/SecretResolver.cs](src/OpenClaw.Core/Security/SecretResolver.cs) 全部内容：

```csharp
using Microsoft.Extensions.Logging;

namespace OpenClaw.Core.Security;

/// <summary>
/// 集中式密钥解析门面。支持：
/// <list type="bullet">
///   <item><c>env:VAR_NAME</c> — 从环境变量读取</item>
///   <item><c>raw:literal</c>  — 字面量（生产环境不推荐）</item>
///   <item><c>裸串</c>          — 按 env 变量名解析，未命中回退为字面量</item>
///   <item><c>vault:path#key</c> — 从 Vault / OpenBao 拉取（需 OpenClaw.Security.Vault 项目启用）</item>
/// </list>
/// 所有 tools 与 gateway 共享这一实现。DI 引导后委托给 <see cref="ISecretResolver"/>；
/// 否则回退到内嵌 legacy 逻辑，保持向后兼容。
/// </summary>
public static class SecretResolver
{
    private const string EnvPrefix = "env:";
    private const string RawPrefix = "raw:";

    /// <summary>
    /// 解析一个 secret 引用为实际值。当 <paramref name="secretRef"/> 为 null/空白，或
    /// 所引用的环境变量未设置时返回 null。
    /// </summary>
    public static string? Resolve(string? secretRef)
        => Resolve(secretRef, logger: null);

    /// <summary>
    /// 带日志的同步解析。DI 未引导时走 legacy 路径；已引导时委托给注册的 <see cref="ISecretResolver"/>。
    /// </summary>
    public static string? Resolve(string? secretRef, ILogger? logger)
    {
        var resolver = ResolverAccessor.Current;
        if (resolver is not null)
            return resolver.Resolve(secretRef);
        return LegacyResolve(secretRef, logger);
    }

    /// <summary>
    /// 异步解析。当注册的 provider 支持异步（如 Vault）时，从 Vault / OpenBao 拉取。
    /// </summary>
    public static ValueTask<string?> ResolveAsync(string? secretRef, CancellationToken ct = default)
    {
        var resolver = ResolverAccessor.Current;
        if (resolver is not null)
            return resolver.ResolveAsync(secretRef, ct);
        return ValueTask.FromResult<string?>(LegacyResolve(secretRef, logger: null));
    }

    /// <summary>
    /// 判断是否为 <c>raw:</c> 前缀引用。供 public-bind 加固检查使用。
    /// </summary>
    public static bool IsRawRef(string? secretRef)
        => secretRef is not null && secretRef.StartsWith(RawPrefix, StringComparison.OrdinalIgnoreCase);

    // ── Legacy fallback (DI 未引导时使用) ──────────────────────────────

    private static string? LegacyResolve(string? secretRef, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
            return null;

        if (secretRef.StartsWith(EnvPrefix, StringComparison.OrdinalIgnoreCase))
            return Environment.GetEnvironmentVariable(secretRef[EnvPrefix.Length..]);

        if (secretRef.StartsWith(RawPrefix, StringComparison.OrdinalIgnoreCase))
            return secretRef[RawPrefix.Length..];

        var envValue = Environment.GetEnvironmentVariable(secretRef);
        if (envValue is not null)
            return envValue;

        if (logger is not null && LooksLikeEnvVarName(secretRef))
            logger.LogWarning(
                "A secret ref with {Length} chars looks like an environment variable name but no such variable is set. " +
                "Falling back to the literal value. Prefix with 'env:' for strict resolution.",
                secretRef.Length);

        return secretRef;
    }

    private static bool LooksLikeEnvVarName(string value)
        => value.Length >= 3 && value.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_');
}
```

- [ ] **Step 3: 跑测试确认现有 11 个测试仍全绿**

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SecurityTests"
```

Expected: 11 个 `SecretResolver.*` 测试 + 其余 SecurityTests 全绿。**关键不变式**：现有 67 个调用点零修改。

- [ ] **Step 4: 新增门面层测试（验证 DI 委托路径）**

`src/OpenClaw.Tests/Security/SecretResolverFacadeTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using OpenClaw.Core.Security;
using Xunit;

namespace OpenClaw.Tests.Security;

public sealed class SecretResolverFacadeTests : IDisposable
{
    public SecretResolverFacadeTests() => ResolverAccessor.Reset();
    public void Dispose() => ResolverAccessor.Reset();

    [Fact]
    public void Resolve_DIBootstrapped_DelegatesToRegisteredResolver()
    {
        var fake = new FakeResolver("from-fake");
        var sp = new ServiceCollection().AddSingleton<ISecretResolver>(fake).BuildServiceProvider();
        ResolverAccessor.Use(sp);

        Assert.Equal("from-fake", SecretResolver.Resolve("anything"));
        Assert.True(SecretResolver.IsRawRef("raw:x"));
    }

    [Fact]
    public void Resolve_DINotBootstrapped_UsesLegacy()
    {
        Assert.Equal("raw-literal", SecretResolver.Resolve("raw:raw-literal"));
        Assert.Null(SecretResolver.Resolve("env:DEFINITELY_NOT_SET_X_123"));
    }

    [Fact]
    public async Task ResolveAsync_DIBootstrapped_DelegatesAsync()
    {
        var fake = new FakeResolver("async-fake");
        var sp = new ServiceCollection().AddSingleton<ISecretResolver>(fake).BuildServiceProvider();
        ResolverAccessor.Use(sp);

        Assert.Equal("async-fake", await SecretResolver.ResolveAsync("anything"));
    }

    [Fact]
    public async Task ResolveAsync_DINotBootstrapped_UsesLegacy()
    {
        Assert.Equal("legacy-literal", await SecretResolver.ResolveAsync("raw:legacy-literal"));
    }

    private sealed class FakeResolver : ISecretResolver
    {
        private readonly string _value;
        public FakeResolver(string value) => _value = value;
        public ValueTask<string?> ResolveAsync(string? secretRef, CancellationToken ct = default)
            => ValueTask.FromResult<string?>(_value);
        public string? Resolve(string? secretRef) => _value;
        public bool IsRawRef(string? secretRef) => false;
    }
}
```

- [ ] **Step 5: 跑测试确认通过**

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SecretResolverFacadeTests"
```

Expected: PASS.

- [ ] **Step 6: 跑完整测试集确认无回归**

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj
```

Expected: 全绿（除非其他测试因静态门面状态污染而失败——若有，定位修复；正常情况应无回归）。

- [ ] **Step 7: 提交**

```bash
git add src/OpenClaw.Core/Security/SecretResolver.cs \
        src/OpenClaw.Tests/Security/SecretResolverFacadeTests.cs
git commit -m "refactor(core): turn SecretResolver into facade delegating to ISecretResolver"
```

---

## Phase 2 — Vault 实现

### Task 4: 新建 OpenClaw.Security.Vault 项目

**Files:**
- Create: `src/OpenClaw.Security.Vault/OpenClaw.Security.Vault.csproj`
- Create: `src/OpenClaw.Security.Vault/README.md`
- Modify: `OpenClaw.Net.slnx`
- Modify: `src/OpenClaw.Gateway/OpenClaw.Gateway.csproj`（添加 ProjectReference，待 Task 12 验证完整接入；此处先加让 build 通过）
- Modify: `src/OpenClaw.Tests/OpenClaw.Tests.csproj`（添加 ProjectReference 以便后续 task 跑测试）

- [ ] **Step 1: 创建 csproj**

`src/OpenClaw.Security.Vault/OpenClaw.Security.Vault.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>OpenClaw.Security.Vault</RootNamespace>
    <IsAotCompatible>false</IsAotCompatible>
    <IsPackable>false</IsPackable>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\OpenClaw.Core\OpenClaw.Core.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="VaultSharp" Version="1.17.2.1" />
    <PackageReference Include="Microsoft.Extensions.Caching.Memory" Version="10.0.9" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.0.9" />
    <PackageReference Include="Microsoft.Extensions.Http" Version="10.0.9" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.9" />
    <PackageReference Include="Microsoft.Extensions.Options.ConfigurationExtensions" Version="10.0.9" />
  </ItemGroup>
</Project>
```

> **版本核对**：实际实现时跑 `dotnet add src/OpenClaw.Security.Vault package VaultSharp` 取当前 latest stable；锁定后写进 csproj。

- [ ] **Step 2: 创建 README 说明 AOT 权衡**

`src/OpenClaw.Security.Vault/README.md`:

```markdown
# OpenClaw.Security.Vault

Vault / OpenBao 后端密钥解析，基于 [VaultSharp](https://github.com/rajanadar/VaultSharp)。

## AOT 兼容性

`IsAotCompatible=false`。VaultSharp 大量依赖反射（auth 方法、KV 响应反序列化），不适配 NativeAOT trim。
引用此项目的 gateway 在做 `dotnet publish -p:PublishAot=true` 时会包含 VaultSharp 全部传递依赖。

如使用 AOT 部署且未启用 Vault（`Security.Vault.Enabled=false`），可通过条件 ProjectReference
或反射剔除减小产物——本仓库暂不实现，按需后续优化。
```

- [ ] **Step 3: 加入 slnx**

修改 [OpenClaw.Net.slnx](OpenClaw.Net.slnx)：在 `/src/` Folder 内追加项目条目（位置：紧邻 `OpenClaw.SemanticKernelAdapter`）：

```xml
<Project Path="src/OpenClaw.Security.Vault/OpenClaw.Security.Vault.csproj" />
```

- [ ] **Step 4: 给 Gateway 与 Tests 加 ProjectReference**

`src/OpenClaw.Gateway/OpenClaw.Gateway.csproj` 的 `<ItemGroup>` 内追加：

```xml
<ProjectReference Include="..\OpenClaw.Security.Vault\OpenClaw.Security.Vault.csproj" />
```

`src/OpenClaw.Tests/OpenClaw.Tests.csproj` 的 `<ItemGroup>` 内追加：

```xml
<ProjectReference Include="..\OpenClaw.Security.Vault\OpenClaw.Security.Vault.csproj" />
```

- [ ] **Step 5: 跑完整 build 确认**

```bash
dotnet build OpenClaw.Net.slnx
```

Expected: BUILD SUCCEEDED. `OpenClaw.Security.Vault` 编译进 solution。

- [ ] **Step 6: 提交**

```bash
git add src/OpenClaw.Security.Vault/ OpenClaw.Net.slnx \
        src/OpenClaw.Gateway/OpenClaw.Gateway.csproj \
        src/OpenClaw.Tests/OpenClaw.Tests.csproj
git commit -m "feat(vault): scaffold OpenClaw.Security.Vault project with VaultSharp"
```

---

### Task 5: VaultExceptions + VaultSecurityOptions

**Files:**
- Create: `src/OpenClaw.Security.Vault/VaultExceptions.cs`
- Create: `src/OpenClaw.Security.Vault/VaultSecurityOptions.cs`
- Modify: `src/OpenClaw.Core/Models/ConfigurationModels.cs`（添加 `VaultSecurityOptions` 到 Core 的 `SecurityOptions`，避免 Vault 项目反向依赖 Core 的具体 model 名）
- Create: `src/OpenClaw.Tests/Security/VaultSecurityOptionsTests.cs`

**Interfaces:**
- Consumes: Core 的 `SecretResolutionException`
- Produces:
  - `VaultRefParseException : SecretResolutionException`
  - `VaultAuthException : SecretResolutionException`
  - `VaultUnavailableException : SecretResolutionException` — 带 `Retryable` 属性
  - `VaultPathNotFoundException : SecretResolutionException`
  - `VaultKeyNotFoundException : SecretResolutionException`
  - `VaultNotConfiguredException : SecretResolutionException`
  - `VaultSecurityOptions`（Core 与 Vault 都可见；放在 Core 因为 ConfigurationModels 是 Core 项目）

- [ ] **Step 1: 在 Core 的 ConfigurationModels 中加 VaultSecurityOptions**

`src/OpenClaw.Core/Models/ConfigurationModels.cs` 找到 `SecurityOptions` 类，追加：

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

并在 `SecurityOptions` 类中追加字段：

```csharp
public VaultSecurityOptions? Vault { get; set; }
```

> 若 `SecurityOptions` 类已有 `using` 或命名空间嵌套需调整；保持与既有风格一致即可。

- [ ] **Step 2: 写失败测试 — ConfigValidator Vault 规则**

`src/OpenClaw.Tests/Security/VaultSecurityOptionsTests.cs`:

```csharp
using OpenClaw.Core.Models;
using OpenClaw.Core.Validation;
using Xunit;

namespace OpenClaw.Tests.Security;

public sealed class VaultSecurityOptionsTests
{
    [Fact]
    public void ConfigValidator_VaultEnabled_MissingAddress_Rejects()
    {
        var cfg = MakeValidConfig();
        cfg.Security.Vault = new VaultSecurityOptions { Enabled = true, TokenRef = "env:X" };
        // Address is null
        var errors = ConfigValidator.Validate(cfg).ToList();
        Assert.Contains(errors, e => e.Contains("Address", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConfigValidator_VaultEnabled_TokenRefStartsWithVault_Rejects()
    {
        var cfg = MakeValidConfig();
        cfg.Security.Vault = new VaultSecurityOptions
        {
            Enabled = true,
            Address = "https://vault.example.com",
            TokenRef = "vault:secret/data/x#k"
        };
        var errors = ConfigValidator.Validate(cfg).ToList();
        Assert.Contains(errors, e => e.Contains("TokenRef", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConfigValidator_CacheTtlTooShort_Rejects()
    {
        var cfg = MakeValidConfig();
        cfg.Security.Vault = new VaultSecurityOptions
        {
            Enabled = true, Address = "https://vault.example.com", TokenRef = "env:X",
            CacheTtl = TimeSpan.FromSeconds(10)
        };
        var errors = ConfigValidator.Validate(cfg).ToList();
        Assert.Contains(errors, e => e.Contains("CacheTtl", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConfigValidator_VaultDisabled_NoError()
    {
        var cfg = MakeValidConfig();
        cfg.Security.Vault = new VaultSecurityOptions { Enabled = false };
        var errors = ConfigValidator.Validate(cfg).ToList();
        Assert.DoesNotContain(errors, e => e.Contains("Vault", StringComparison.OrdinalIgnoreCase));
    }

    private static GatewayConfig MakeValidValidConfig() => MakeValidConfig();

    private static GatewayConfig MakeValidConfig()
    {
        var cfg = new GatewayConfig
        {
            BindAddress = "127.0.0.1",
            AuthToken = "loopback",
            Security = new SecurityOptions { AuthMode = "token" }
        };
        return cfg;
    }
}
```

- [ ] **Step 3: 跑测试确认失败**

Expected: 编译或断言失败。

- [ ] **Step 4: 实现 VaultExceptions**

`src/OpenClaw.Security.Vault/VaultExceptions.cs`:

```csharp
using OpenClaw.Core.Security;

namespace OpenClaw.Security.Vault;

public sealed class VaultRefParseException : SecretResolutionException
{
    public VaultRefParseException(string message) : base(message) { }
}

public sealed class VaultAuthException : SecretResolutionException
{
    public VaultAuthException(string message) : base(message) { }
}

public sealed class VaultUnavailableException : SecretResolutionException
{
    public bool Retryable { get; }
    public VaultUnavailableException(string message, bool retryable = true) : base(message)
    {
        Retryable = retryable;
    }
}

public sealed class VaultPathNotFoundException : SecretResolutionException
{
    public VaultPathNotFoundException(string message) : base(message) { }
}

public sealed class VaultKeyNotFoundException : SecretResolutionException
{
    public VaultKeyNotFoundException(string message) : base(message) { }
}

public sealed class VaultNotConfiguredException : SecretResolutionException
{
    public VaultNotConfiguredException(string message) : base(message) { }
}
```

- [ ] **Step 5: 在 ConfigValidator 中新增 Vault 校验**

打开 [src/OpenClaw.Core/Validation/ConfigValidator.cs](src/OpenClaw.Core/Validation/ConfigValidator.cs)；找到 `Validate` 主方法或合适位置，新增方法：

```csharp
private static IEnumerable<string> ValidateVaultSecurity(VaultSecurityOptions v, SecurityOptions sec)
{
    if (!v.Enabled)
        yield break;

    if (string.IsNullOrWhiteSpace(v.Address))
    {
        yield return "Security.Vault.Address is required when Vault is enabled.";
    }
    else if (!Uri.TryCreate(v.Address, UriKind.Absolute, out var uri) ||
             uri.Scheme != Uri.UriSchemeHttps)
    {
        yield return $"Security.Vault.Address must be an HTTPS URI (got '{v.Address}').";
    }
    else if (sec.PublicBind && IsLoopbackHost(uri.Host))
    {
        yield return $"Security.Vault.Address must not point to loopback when Security.PublicBind=true (got '{uri.Host}').";
    }

    if (string.IsNullOrWhiteSpace(v.TokenRef))
        yield return "Security.Vault.TokenRef is required when Vault is enabled.";
    else if (v.TokenRef.StartsWith("vault:", StringComparison.OrdinalIgnoreCase))
        yield return "Security.Vault.TokenRef must not use the 'vault:' prefix (recursion guard).";

    if (v.CacheTtl < TimeSpan.FromSeconds(30) || v.CacheTtl > TimeSpan.FromHours(24))
        yield return $"Security.Vault.CacheTtl must be between 00:00:30 and 1.00:00:00 (got {v.CacheTtl}).";

    if (v.RequestTimeout < TimeSpan.FromSeconds(1) || v.RequestTimeout > TimeSpan.FromSeconds(60))
        yield return $"Security.Vault.RequestTimeout must be between 00:00:01 and 00:01:00 (got {v.RequestTimeout}).";

    if (v.RateLimit.RequestsPerSecond < 1 || v.RateLimit.RequestsPerSecond > 1000)
        yield return $"Security.Vault.RateLimit.RequestsPerSecond must be between 1 and 1000 (got {v.RateLimit.RequestsPerSecond}).";

    if (v.KvVersion != 2)
        yield return $"Security.Vault.KvVersion must be 2 in v1 (got {v.KvVersion}).";
}

private static bool IsLoopbackHost(string host) =>
    host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
    host == "127.0.0.1" ||
    host == "::1";
```

并在主 `Validate(GatewayConfig)` 调用处（找到迭代其他 Security 子字段的地方）追加：

```csharp
if (config.Security.Vault is not null)
    foreach (var err in ValidateVaultSecurity(config.Security.Vault, config.Security))
        yield return err;
```

> 注意：若 `SecurityOptions.PublicBind` 字段不存在，需先在 `SecurityOptions` 加 `public bool PublicBind { get; set; }`。检查并按需添加。

- [ ] **Step 6: 跑测试确认通过**

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~VaultSecurityOptionsTests"
```

Expected: PASS.

- [ ] **Step 7: 跑现有 ConfigValidator 测试确认无回归**

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~ConfigValidator|FullyQualifiedName~ConfigPathResolver|FullyQualifiedName~ProviderSmokeProbe"
```

Expected: 全绿。

- [ ] **Step 8: 提交**

```bash
git add src/OpenClaw.Core/Models/ConfigurationModels.cs \
        src/OpenClaw.Core/Validation/ConfigValidator.cs \
        src/OpenClaw.Security.Vault/VaultExceptions.cs \
        src/OpenClaw.Tests/Security/VaultSecurityOptionsTests.cs
git commit -m "feat(vault): add VaultSecurityOptions, VaultExceptions, ConfigValidator rules"
```

---

### Task 6: VaultRefParser

**Files:**
- Create: `src/OpenClaw.Security.Vault/VaultRefParser.cs`
- Create: `src/OpenClaw.Tests/Security/VaultRefParserTests.cs`

**Interfaces:**
- Consumes: 无外部依赖
- Produces:
  - `VaultRef(string Path, string Key, int KvVersion, string Mount)` — readonly record struct
  - `VaultRefParser.Parse(string secretRef) → VaultRef` — 抛 `VaultRefParseException`

- [ ] **Step 1: 写失败测试**

`src/OpenClaw.Tests/Security/VaultRefParserTests.cs`:

```csharp
using OpenClaw.Security.Vault;
using Xunit;

namespace OpenClaw.Tests.Security;

public sealed class VaultRefParserTests
{
    [Fact]
    public void Parse_FullRef_ReturnsMountPathKey()
    {
        var v = VaultRefParser.Parse("vault:secret/data/openclaw/openai#api_key");
        Assert.Equal("secret", v.Mount);
        Assert.Equal("openclaw/openai", v.Path);
        Assert.Equal("api_key", v.Key);
        Assert.Equal(2, v.KvVersion);
    }

    [Fact]
    public void Parse_DefaultMount_Applied_WhenMissingDataSegment()
    {
        var v = VaultRefParser.Parse("vault:data/openclaw/openai#api_key", defaultMount: "secret");
        Assert.Equal("secret", v.Mount);
        Assert.Equal("openclaw/openai", v.Path);
    }

    [Fact]
    public void Parse_NestedPath_Preserved()
    {
        var v = VaultRefParser.Parse("vault:secret/data/a/b/c#k");
        Assert.Equal("a/b/c", v.Path);
        Assert.Equal("k", v.Key);
    }

    [Fact]
    public void Parse_MissingHash_Throws()
    {
        Assert.Throws<VaultRefParseException>(() =>
            VaultRefParser.Parse("vault:secret/data/openclaw/openai"));
    }

    [Fact]
    public void Parse_EmptyPath_Throws()
    {
        Assert.Throws<VaultRefParseException>(() =>
            VaultRefParser.Parse("vault:secret/data/#api_key"));
    }

    [Fact]
    public void Parse_EmptyKey_Throws()
    {
        Assert.Throws<VaultRefParseException>(() =>
            VaultRefParser.Parse("vault:secret/data/openclaw/openai#"));
    }

    [Fact]
    public void Parse_NullOrEmpty_Throws()
    {
        Assert.Throws<VaultRefParseException>(() => VaultRefParser.Parse(""));
        Assert.Throws<VaultRefParseException>(() => VaultRefParser.Parse(null!));
    }

    [Fact]
    public void Parse_ExceptionMessage_DoesNotLeakPath()
    {
        // Spec invariant: exception messages must not include the resolved value.
        // Path itself is structural — confirm only path/key/structure are in the message.
        var ex = Assert.Throws<VaultRefParseException>(() =>
            VaultRefParser.Parse("vault:secret/data/SOMETHING#"));
        Assert.Contains("path", ex.Message, StringComparison.OrdinalIgnoreCase);
        // No 'value' or arbitrary payload strings
        Assert.DoesNotContain("value", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Expected: compile error.

- [ ] **Step 3: 实现 VaultRefParser**

`src/OpenClaw.Security.Vault/VaultRefParser.cs`:

```csharp
namespace OpenClaw.Security.Vault;

public readonly record struct VaultRef(string Path, string Key, int KvVersion, string Mount);

public static class VaultRefParser
{
    private const string Prefix = "vault:";

    public static VaultRef Parse(string secretRef, string defaultMount = "secret")
    {
        if (string.IsNullOrWhiteSpace(secretRef))
            throw new VaultRefParseException("Empty vault reference.");

        if (!secretRef.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            throw new VaultRefParseException("Reference must start with 'vault:'.");

        var body = secretRef[Prefix.Length..];
        var hashIdx = body.IndexOf('#');
        if (hashIdx < 0)
            throw new VaultRefParseException("Vault reference must contain '#' separating path and key.");

        var pathPart = body[..hashIdx];
        var key = body[(hashIdx + 1)..];
        if (string.IsNullOrEmpty(key))
            throw new VaultRefParseException("Vault reference key segment is empty (after '#').");

        if (string.IsNullOrEmpty(pathPart))
            throw new VaultRefParseException("Vault reference path segment is empty (before '#').");

        string mount;
        string path;
        var dataIdx = pathPart.IndexOf("/data/", StringComparison.Ordinal);
        if (dataIdx > 0)
        {
            mount = pathPart[..dataIdx];
            path = pathPart[(dataIdx + "/data/".Length)..];
        }
        else
        {
            // No mount segment — treat whole thing as path, apply default mount.
            mount = defaultMount;
            path = pathPart;
        }

        if (string.IsNullOrEmpty(path))
            throw new VaultRefParseException("Vault reference path is empty after mount parsing.");

        return new VaultRef(Path: path, Key: key, KvVersion: 2, Mount: mount);
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~VaultRefParserTests"
```

Expected: PASS.

- [ ] **Step 5: 提交**

```bash
git add src/OpenClaw.Security.Vault/VaultRefParser.cs \
        src/OpenClaw.Tests/Security/VaultRefParserTests.cs
git commit -m "feat(vault): add VaultRefParser for vault:<mount>/data/<path>#<key> grammar"
```

---

### Task 7: VaultRefCache（TTL + 单飞 + refresh-ahead + 失败回退）

**Files:**
- Create: `src/OpenClaw.Security.Vault/VaultRefCache.cs`
- Create: `src/OpenClaw.Tests/Security/VaultRefCacheTests.cs`

**Interfaces:**
- Consumes: `VaultRef`, `VaultSecurityOptions.CacheTtl`, `IMemoryCache`
- Produces:
  - `VaultRefCache(IMemoryCache, ILogger<VaultRefCache>)`
  - `bool TryGet(VaultRef key, out string value)` — 同步读，命中即返回（包含 stale），调用方根据 Age 决定是否触发后台刷新
  - `Task<string> GetOrFetchAsync(VaultRef key, Func<CancellationToken, Task<string>> fetch, CancellationToken ct)` — 单飞 + refresh-ahead + 失败回退旧值
  - `void Invalidate(VaultRef key)`

- [ ] **Step 1: 写失败测试**

`src/OpenClaw.Tests/Security/VaultRefCacheTests.cs`:

```csharp
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Security.Vault;
using Xunit;

namespace OpenClaw.Tests.Security;

public sealed class VaultRefCacheTests
{
    private static VaultRefCache NewCache(TimeSpan? ttl = null)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        return new VaultRefCache(cache, NullLogger<VaultRefCache>.Instance, ttl ?? TimeSpan.FromMinutes(5));
    }

    private static VaultRef Key(string path = "x", string key = "k")
        => new(path, key, KvVersion: 2, Mount: "secret");

    [Fact]
    public async Task GetOrFetchAsync_CacheMiss_CallsFetchOnce_ReturnsAndCaches()
    {
        var cache = NewCache();
        var key = Key();
        var calls = 0;
        string Fetch(CancellationToken _)
        {
            calls++;
            return Task.FromResult("v");
        }

        var v = await cache.GetOrFetchAsync(key, Fetch, CancellationToken.None);
        Assert.Equal("v", v);
        Assert.Equal(1, calls);

        var v2 = await cache.GetOrFetchAsync(key, Fetch, CancellationToken.None);
        Assert.Equal("v", v2);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetOrFetchAsync_ConcurrentMisses_SingleFlight_OneFetch()
    {
        var cache = NewCache();
        var key = Key();
        var calls = 0;
        async Task<string> Fetch(CancellationToken _)
        {
            await Task.Delay(50);
            Interlocked.Increment(ref calls);
            return "v";
        }

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => cache.GetOrFetchAsync(key, Fetch, CancellationToken.None))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal("v", r));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetOrFetchAsync_StaleEntry_RefreshAhead_ReturnsStale()
    {
        // TTL = 100ms; first fetch at t=0, second at t=200ms should trigger refresh-ahead but return previous value.
        var cache = NewCache(TimeSpan.FromMilliseconds(100));
        var key = Key();
        var calls = 0;
        async Task<string> Fetch(CancellationToken _)
        {
            await Task.Yield();
            return Interlocked.Increment(ref calls) switch { 1 => "v1", _ => "v2" };
        }

        var first = await cache.GetOrFetchAsync(key, Fetch, CancellationToken.None);
        Assert.Equal("v1", first);
        await Task.Delay(200);
        var second = await cache.GetOrFetchAsync(key, Fetch, CancellationToken.None);
        Assert.Equal("v1", second); // stale returned
        // Give refresh-ahead a moment to land
        await Task.Delay(100);
        var third = await cache.GetOrFetchAsync(key, Fetch, CancellationToken.None);
        Assert.Equal("v2", third);
        Assert.True(calls >= 2);
    }

    [Fact]
    public async Task GetOrFetchAsync_FetchFailsWithStale_ReturnsStale()
    {
        var cache = NewCache(TimeSpan.FromMilliseconds(50));
        var key = Key();

        await cache.GetOrFetchAsync(key, _ => Task.FromResult("v1"), CancellationToken.None);
        await Task.Delay(100);

        var result = await cache.GetOrFetchAsync(key, _ => throw new InvalidOperationException("boom"), CancellationToken.None);
        Assert.Equal("v1", result);
    }

    [Fact]
    public async Task GetOrFetchAsync_FetchFailsNoStale_Throws()
    {
        var cache = NewCache();
        var key = Key();
        await Assert.ThrowsAsync<VaultUnavailableException>(() =>
            cache.GetOrFetchAsync(key, _ => throw new InvalidOperationException("boom"), CancellationToken.None));
    }

    [Fact]
    public void TryGet_NoEntry_False()
    {
        var cache = NewCache();
        Assert.False(cache.TryGet(Key(), out _));
    }

    [Fact]
    public async Task TryGet_AfterFetch_TrueAndReturnsValue()
    {
        var cache = NewCache();
        var key = Key();
        await cache.GetOrFetchAsync(key, _ => Task.FromResult("v"), CancellationToken.None);
        Assert.True(cache.TryGet(key, out var v));
        Assert.Equal("v", v);
    }

    [Fact]
    public async Task Invalidate_RemovesEntry_NextFetchHitsVault()
    {
        var cache = NewCache();
        var key = Key();
        await cache.GetOrFetchAsync(key, _ => Task.FromResult("v1"), CancellationToken.None);
        cache.Invalidate(key);
        var result = await cache.GetOrFetchAsync(key, _ => Task.FromResult("v2"), CancellationToken.None);
        Assert.Equal("v2", result);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Expected: compile error.

- [ ] **Step 3: 实现 VaultRefCache**

`src/OpenClaw.Security.Vault/VaultRefCache.cs`:

```csharp
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace OpenClaw.Security.Vault;

/// <summary>
/// TTL cache for vault secret values. Backed by <see cref="IMemoryCache"/>.
/// Single-flight per key (SemaphoreSlim), refresh-ahead on TTL expiry,
/// stale-on-failure fallback.
/// </summary>
public sealed class VaultRefCache
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<VaultRefCache> _logger;
    private readonly TimeSpan _ttl;
    private readonly Dictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly object _locksLock = new();

    private sealed record Entry(string Value, DateTimeOffset FetchedAt, bool Refreshing);

    public VaultRefCache(IMemoryCache cache, ILogger<VaultRefCache> logger, TimeSpan ttl)
    {
        _cache = cache;
        _logger = logger;
        _ttl = ttl;
    }

    public bool TryGet(VaultRef key, out string value)
    {
        var ck = CacheKey(key);
        if (_cache.TryGetValue<Entry>(ck, out var entry) && entry is not null)
        {
            value = entry.Value;
            return true;
        }
        value = string.Empty;
        return false;
    }

    public async Task<string> GetOrFetchAsync(VaultRef key, Func<CancellationToken, Task<string>> fetch, CancellationToken ct)
    {
        var ck = CacheKey(key);
        var sem = GetLock(ck);

        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue<Entry>(ck, out var existing) && existing is not null)
            {
                var age = DateTimeOffset.UtcNow - existing.FetchedAt;
                if (age < _ttl)
                    return existing.Value;

                // Stale: kick off refresh-ahead, return stale value now.
                if (!existing.Refreshing)
                {
                    var captured = key;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var v = await fetch(ct).ConfigureAwait(false);
                            Set(captured, v);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Vault refresh-ahead failed for key {Key}; keeping stale value.", ck);
                        }
                    }, CancellationToken.None);
                }

                return existing.Value;
            }

            // Miss: fetch synchronously under lock.
            try
            {
                var v = await fetch(ct).ConfigureAwait(false);
                Set(key, v);
                return v;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Vault fetch failed for key {Key}.", ck);
                throw new VaultUnavailableException($"Vault fetch failed: {ex.Message}", retryable: true);
            }
        }
        finally
        {
            sem.Release();
        }
    }

    public void Invalidate(VaultRef key)
    {
        _cache.Remove(CacheKey(key));
    }

    private void Set(VaultRef key, string value)
    {
        var entry = new Entry(value, DateTimeOffset.UtcNow, Refreshing: false);
        _cache.Set(CacheKey(key), entry, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = _ttl * 2  // keep stale value beyond TTL for fallback
        });
    }

    private SemaphoreSlim GetLock(string cacheKey)
    {
        lock (_locksLock)
        {
            if (!_locks.TryGetValue(cacheKey, out var sem))
            {
                sem = new SemaphoreSlim(1, 1);
                _locks[cacheKey] = sem;
            }
            return sem;
        }
    }

    private static string CacheKey(VaultRef key) =>
        $"vault:{key.Mount}:{key.Path}#{key.Key}";
}
```

- [ ] **Step 4: 跑测试确认通过**

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~VaultRefCacheTests"
```

Expected: PASS. 注意：`StaleEntry_RefreshAhead_ReturnsStale` 是时序敏感的，若 CI 抖动可放宽 200ms 到 300ms。

- [ ] **Step 5: 提交**

```bash
git add src/OpenClaw.Security.Vault/VaultRefCache.cs \
        src/OpenClaw.Tests/Security/VaultRefCacheTests.cs
git commit -m "feat(vault): add VaultRefCache with TTL, single-flight, refresh-ahead"
```

---

### Task 8: IVaultClient + VaultSecretProvider

**Files:**
- Create: `src/OpenClaw.Security.Vault/IVaultClient.cs`
- Create: `src/OpenClaw.Security.Vault/VaultSecretProvider.cs`
- Create: `src/OpenClaw.Tests/Security/VaultSecretProviderTests.cs`

**Interfaces:**
- Consumes: `IVaultClient`, `VaultRefCache`, `VaultSecurityOptions`, `VaultRefParser`, `ISecretProvider`, `Microsoft.Extensions.Logging`
- Produces:
  - `IVaultClient` — 单方法 `Task<Dictionary<string, object>?> ReadSecretV2Async(string mount, string path, CancellationToken ct)`
  - `VaultSharpClient : IVaultClient` — 真实实现（包装 VaultSharp）
  - `VaultSecretProvider : ISecretProvider` — `Scheme = "vault"`，异步解析，缓存优先，错误映射到异常类型

- [ ] **Step 1: 写失败测试**

`src/OpenClaw.Tests/Security/VaultSecretProviderTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OpenClaw.Core.Security;
using OpenClaw.Security.Vault;
using Xunit;

namespace OpenClaw.Tests.Security;

public sealed class VaultSecretProviderTests
{
    private static (VaultSecretProvider provider, IVaultClient client, VaultRefCache cache) Build(
        Action<VaultSecurityOptions>? configure = null,
        TimeSpan? ttl = null)
    {
        var opts = new VaultSecurityOptions();
        configure?.Invoke(opts);
        var memCache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var cache = new VaultRefCache(memCache, NullLogger<VaultRefCache>.Instance,
            ttl ?? TimeSpan.FromMinutes(5));
        var client = Substitute.For<IVaultClient>();
        var provider = new VaultSecretProvider(client, cache, opts, NullLogger<VaultSecretProvider>.Instance);
        return (provider, client, cache);
    }

    [Fact]
    public void Scheme_IsVault()
        => Assert.Equal("vault", Build().provider.Scheme);

    [Fact]
    public void CanResolve_VaultPrefix_True()
        => Assert.True(Build().provider.CanResolve("vault:secret/data/x#k"));

    [Fact]
    public void CanResolve_EnvPrefix_False()
        => Assert.False(Build().provider.CanResolve("env:X"));

    [Fact]
    public async Task ResolveAsync_CacheMiss_FetchesAndCaches()
    {
        var (provider, client, _) = Build();
        client.ReadSecretV2Async("secret", "openclaw/openai", Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, object> { ["api_key"] = "sk-xyz" });

        var v = await provider.ResolveAsync("vault:secret/data/openclaw/openai#api_key", CancellationToken.None);
        Assert.Equal("sk-xyz", v);
        await client.Received(1).ReadSecretV2Async("secret", "openclaw/openai", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_CacheHit_NoHttpCall()
    {
        var (provider, client, _) = Build();
        client.ReadSecretV2Async("secret", "openclaw/openai", Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, object> { ["api_key"] = "sk-xyz" });

        await provider.ResolveAsync("vault:secret/data/openclaw/openai#api_key", CancellationToken.None);
        await provider.ResolveAsync("vault:secret/data/openclaw/openai#api_key", CancellationToken.None);
        await client.Received(1).ReadSecretV2Async("secret", "openclaw/openai", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_VaultReturnsNull_Throws_KeyNotFound()
    {
        var (provider, client, _) = Build();
        client.ReadSecretV2Async(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Dictionary<string, object>?)null);

        await Assert.ThrowsAsync<VaultKeyNotFoundException>(() =>
            provider.ResolveAsync("vault:secret/data/openclaw/openai#api_key", CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAsync_VaultReturnsDictMissingKey_Throws_KeyNotFound()
    {
        var (provider, client, _) = Build();
        client.ReadSecretV2Async(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, object> { ["other"] = "x" });

        await Assert.ThrowsAsync<VaultKeyNotFoundException>(() =>
            provider.ResolveAsync("vault:secret/data/openclaw/openai#api_key", CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAsync_VaultThrowsHttp_PropagatesAsUnavailable()
    {
        var (provider, client, _) = Build();
        client.ReadSecretV2Async(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("network"));

        await Assert.ThrowsAsync<VaultUnavailableException>(() =>
            provider.ResolveAsync("vault:secret/data/x#k", CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAsync_InvalidRef_Throws_VaultRefParseException()
    {
        var (provider, _, _) = Build();
        await Assert.ThrowsAsync<VaultRefParseException>(() =>
            provider.ResolveAsync("vault:secret/data/x", CancellationToken.None)); // missing '#'
    }

    [Fact]
    public async Task ResolveAsync_NoValueInExceptionMessage()
    {
        var (provider, client, _) = Build();
        client.ReadSecretV2Async(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, object> { ["api_key"] = "SUPER-SECRET-VALUE" });

        var ex = await Assert.ThrowsAsync<VaultKeyNotFoundException>(() =>
            provider.ResolveAsync("vault:secret/data/x#missing_key", CancellationToken.None));
        Assert.DoesNotContain("SUPER-SECRET-VALUE", ex.Message);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Expected: compile error.

- [ ] **Step 3: 实现 IVaultClient**

`src/OpenClaw.Security.Vault/IVaultClient.cs`:

```csharp
namespace OpenClaw.Security.Vault;

/// <summary>
/// Thin abstraction over VaultSharp for unit-test substitution. Implementations
/// must return the KV v2 data dictionary for <paramref name="path"/> under
/// <paramref name="mount"/>, or null when the path does not exist.
/// </summary>
public interface IVaultClient
{
    Task<Dictionary<string, object>?> ReadSecretV2Async(
        string mount, string path, CancellationToken ct);
}
```

- [ ] **Step 4: 实现 VaultSecretProvider**

`src/OpenClaw.Security.Vault/VaultSecretProvider.cs`:

```csharp
using System.Net;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Security;
using VaultSharp;
using VaultSharp.Core;
using VaultSharp.V1;

namespace OpenClaw.Security.Vault;

public sealed class VaultSecretProvider : ISecretProvider
{
    private readonly IVaultClient _client;
    private readonly VaultRefCache _cache;
    private readonly VaultSecurityOptions _options;
    private readonly ILogger<VaultSecretProvider> _logger;

    public VaultSecretProvider(IVaultClient client, VaultRefCache cache, VaultSecurityOptions options, ILogger<VaultSecretProvider> logger)
    {
        _client = client;
        _cache = cache;
        _options = options;
        _logger = logger;
    }

    public string Scheme => "vault";

    public bool CanResolve(string secretRef) =>
        secretRef.StartsWith("vault:", StringComparison.OrdinalIgnoreCase);

    public ValueTask<string?> ResolveAsync(string secretRef, CancellationToken ct)
        => new(ResolveInternalAsync(secretRef, ct));

    private async Task<string?> ResolveInternalAsync(string secretRef, CancellationToken ct)
    {
        var parsed = VaultRefParser.Parse(secretRef, defaultMount: _options.KvMount);

        var value = await _cache.GetOrFetchAsync(parsed, async token =>
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(_options.RequestTimeout);

                var data = await _client.ReadSecretV2Async(parsed.Mount, parsed.Path, timeoutCts.Token)
                    .ConfigureAwait(false);

                if (data is null)
                    throw new VaultPathNotFoundException(
                        $"Vault path '{parsed.Mount}/data/{parsed.Path}' not found.");

                if (!data.TryGetValue(parsed.Key, out var raw) || raw is null)
                    throw new VaultKeyNotFoundException(
                        $"Vault key '{parsed.Key}' not found at path '{parsed.Mount}/data/{parsed.Path}'.");

                return raw.ToString() ?? string.Empty;
            }
            catch (VaultPathNotFoundException) { throw; }
            catch (VaultKeyNotFoundException) { throw; }
            catch (VaultAuthException) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                throw new VaultUnavailableException(
                    $"Vault request timed out after {_options.RequestTimeout}.", retryable: true);
            }
            catch (VaultSharpException vex)
            {
                var status = vex.GetType().Name;
                if (status.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
                    status.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
                    throw new VaultAuthException($"Vault auth failed: {status}");
                throw new VaultUnavailableException($"Vault error: {status}", retryable: true);
            }
            catch (HttpRequestException hex)
            {
                throw new VaultUnavailableException($"Vault network error: {hex.Message}", retryable: true);
            }
        }, ct).ConfigureAwait(false);

        return value;
    }
}

/// <summary>
/// Concrete <see cref="IVaultClient"/> backed by VaultSharp. Resolves the
/// configured token at construction time via <see cref="OpenClaw.Core.Security.SecretResolver"/>.
/// </summary>
public sealed class VaultSharpClient : IVaultClient
{
    private readonly IVaultClient _inner;

    public VaultSharpClient(string address, string token, string? ns, VaultTlsOptions tls)
    {
        var settings = new VaultClientSettings(address, token)
        {
            Namespace = ns,
        };
        if (tls.SkipVerify)
            settings.ValidateVaultServerCertificate = false;
        // CA cert path: pass through HttpMessageHandler customization — for v1, document as future work.
        _inner = new VaultSharp.VaultClient(settings);
    }

    public async Task<Dictionary<string, object>?> ReadSecretV2Async(
        string mount, string path, CancellationToken ct)
    {
        var secret = await ((VaultSharp.VaultClient)_inner).V1.Secrets.KeyValue.V2
            .ReadSecretAsync(path, mountVersion: 2, mountPoint: mount)
            .WaitAsync(ct).ConfigureAwait(false);
        return secret?.Data?.ToDictionary(kv => kv.Key, kv => kv.Value);
    }
}
```

> **注意**：以上 VaultSharp API 调用为示意。实际 API 因版本而异——实现时需对照 VaultSharp 当前文档调整：
> - `KeyValue.V2.ReadSecretAsync(path, mountVersion, mountPoint)` 是 KV v2 读取路径
> - `Secret<Dictionary<string, object>>` 或类似泛型包装层
> - `ValidateVaultServerCertificate` 与 CA cert 在不同版本字段名可能不同
>
> 若 VaultSharp 公开 API 与此处不匹配，按实际 API 调整 wrapper，并相应修正测试中的断言。**原则**：wrapper 必须能用 NSubstitute 替换，单元测试不应触达真实 VaultSharp。

- [ ] **Step 5: 跑测试确认通过**

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~VaultSecretProviderTests"
```

Expected: PASS. 若 VaultSharp API 不匹配编译失败，按上一步注释调整 wrapper 代码。

- [ ] **Step 6: 提交**

```bash
git add src/OpenClaw.Security.Vault/IVaultClient.cs \
        src/OpenClaw.Security.Vault/VaultSecretProvider.cs \
        src/OpenClaw.Tests/Security/VaultSecretProviderTests.cs
git commit -m "feat(vault): add VaultSecretProvider and IVaultClient abstraction"
```

---

### Task 9: VaultServiceCollectionExtensions（DI 注册）

**Files:**
- Create: `src/OpenClaw.Security.Vault/VaultServiceCollectionExtensions.cs`

**Interfaces:**
- Consumes: `IServiceCollection`, `IConfiguration`, `VaultSecurityOptions`, `IVaultClient`, `VaultSecretProvider`, `VaultRefCache`
- Produces:
  - `IServiceCollection AddOpenClawVaultSecrets(this IServiceCollection services, IConfiguration config)` — 完整注册 vault 后端到 DI

- [ ] **Step 1: 实现 AddOpenClawVaultSecrets**

`src/OpenClaw.Security.Vault/VaultServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenClaw.Core.Security;

namespace OpenClaw.Security.Vault;

public static class VaultServiceCollectionExtensions
{
    public static IServiceCollection AddOpenClawVaultSecrets(
        this IServiceCollection services, IConfiguration config)
    {
        var opts = new VaultSecurityOptions();
        config.GetSection("Security:Vault").Bind(opts);

        if (!opts.Enabled)
            return services; // No-op when vault disabled

        services.AddSingleton(opts);

        // Core abstractions
        services.AddSingleton<ISecretProvider, EnvRawSecretProvider>();
        services.AddSingleton<ISecretProvider, VaultSecretProvider>();

        // Resolve token via secret resolver (handles env:/raw:) at provider construction.
        services.AddSingleton<IVaultClient>(sp =>
        {
            var token = SecretResolver.Resolve(opts.TokenRef)
                ?? throw new VaultAuthException("Vault token ref resolved to null.");
            var client = new VaultSharpClient(
                opts.Address ?? throw new VaultAuthException("Vault address missing."),
                token,
                string.IsNullOrEmpty(opts.Namespace) ? null : opts.Namespace,
                opts.Tls);
            return client;
        });

        services.AddSingleton<VaultRefCache>(sp =>
            new VaultRefCache(
                Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions
                    .GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(sp),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<VaultRefCache>>(),
                opts.CacheTtl));

        services.AddSingleton<ISecretResolver>(sp =>
            new OpenClaw.Core.Security.CompositeSecretResolver(
                sp.GetServices<ISecretProvider>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<OpenClaw.Core.Security.CompositeSecretResolver>>()));

        services.AddHostedService<VaultRefPrewarmService>();

        return services;
    }
}
```

> **实现注意**：
> 1. `CompositeSecretResolver` 在 Core 项目，引用其完全限定名即可。
> 2. `EnvRawSecretProvider` 必须**第一个**注册——它声明 "env" scheme，vault 声明 "vault" scheme，两者不重叠；但其他 provider 顺序需慎重。
> 3. 注册 `ISecretResolver` 时直接 new `CompositeSecretResolver`，跳过 `CompositeSecretResolver` 的 DI 注册（避免循环）。
> 4. `IMemoryCache` 与 `ILogger<T>` 由调用方在 `AddOpenClawBootstrapAsync` 之前注册（既有代码已经注册 `ILogger` 与可能的 `AddMemoryCache`）。
> 5. 若 `AddMemoryCache()` 尚未注册，需在此调用前补 `services.AddMemoryCache()`。
>
> 编译报错时按实际情况调整；如有 `Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions` 不可见，用 `using Microsoft.Extensions.DependencyInjection;` 并直接调 `sp.GetRequiredService<IMemoryCache>()`。

- [ ] **Step 2: 编译确认通过**

```bash
dotnet build src/OpenClaw.Security.Vault/OpenClaw.Security.Vault.csproj
```

Expected: BUILD SUCCEEDED. 若有 VaultRefPrewarmService 引用错误，提示 Task 10 还没写——暂时注释 `AddHostedService<VaultRefPrewarmService>()` 行。

- [ ] **Step 3: 提交**

```bash
git add src/OpenClaw.Security.Vault/VaultServiceCollectionExtensions.cs
git commit -m "feat(vault): add AddOpenClawVaultSecrets DI registration"
```

---

## Phase 3 — Wiring + Config + 预热

### Task 10: VaultRefPrewarmService（IHostedService）

**Files:**
- Create: `src/OpenClaw.Security.Vault/VaultRefPrewarmService.cs`
- Create: `src/OpenClaw.Tests/Security/VaultRefPrewarmServiceTests.cs`

**Interfaces:**
- Consumes: `ISecretResolver`（拉 vault）, `VaultSecurityOptions.PrewarmRefs`, `IServiceProvider`（扫描 config 用）
- Produces: `VaultRefPrewarmService : IHostedService`

- [ ] **Step 1: 写失败测试**

`src/OpenClaw.Tests/Security/VaultRefPrewarmServiceTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Security.Vault;
using Xunit;

namespace OpenClaw.Tests.Security;

public sealed class VaultRefPrewarmServiceTests
{
    private static (VaultRefPrewarmService svc, ISecretResolver resolver, VaultSecurityOptions opts) Build(
        bool prewarmRequired, params string[] refs)
    {
        var opts = new VaultSecurityOptions
        {
            Enabled = true,
            Address = "https://vault.example",
            TokenRef = "env:X",
            PrewarmRefs = refs.ToList(),
            PrewarmRequired = prewarmRequired,
        };
        var resolver = Substitute.For<ISecretResolver>();
        resolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<string?>("value"));
        var sp = new ServiceCollection().BuildServiceProvider();
        var svc = new VaultRefPrewarmService(opts, resolver, sp, Substitute.For<Microsoft.Extensions.Logging.ILogger<VaultRefPrewarmService>>());
        return (svc, resolver, opts);
    }

    [Fact]
    public async Task StartAsync_AllSuccess_NoThrow()
    {
        var (svc, resolver, _) = Build(prewarmRequired: true, "vault:secret/data/x#k", "vault:secret/data/y#k");
        await svc.StartAsync(CancellationToken.None);
        await resolver.Received(2).ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_OneFail_PrewarmRequired_Throws()
    {
        var opts = new VaultSecurityOptions { Enabled = true, Address = "https://v", TokenRef = "env:X", PrewarmRefs = ["vault:secret/data/x#k"], PrewarmRequired = true };
        var resolver = Substitute.For<ISecretResolver>();
        resolver.ResolveAsync("vault:secret/data/x#k", Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<string?>(null)); // miss
        var svc = new VaultRefPrewarmService(opts, resolver, new ServiceCollection().BuildServiceProvider(),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<VaultRefPrewarmService>>());

        await Assert.ThrowsAsync<HostingStartupException>(() => svc.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_OneFail_PrewarmNotRequired_LogsAndContinues()
    {
        var opts = new VaultSecurityOptions { Enabled = true, Address = "https://v", TokenRef = "env:X", PrewarmRefs = ["vault:secret/data/x#k"], PrewarmRequired = false };
        var resolver = Substitute.For<ISecretResolver>();
        resolver.ResolveAsync("vault:secret/data/x#k", Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<string?>(null));
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<VaultRefPrewarmService>>();
        var svc = new VaultRefPrewarmService(opts, resolver, new ServiceCollection().BuildServiceProvider(), logger);

        await svc.StartAsync(CancellationToken.None); // no throw
        logger.Received().Log(
            Microsoft.Extensions.Logging.LogLevel.Error,
            Arg.Any<Microsoft.Extensions.Logging.EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}

public sealed class HostingStartupException : Exception
{
    public HostingStartupException(string message) : base(message) { }
}
```

> `HostingStartupException` 是测试本地 stub——若 host builder 已有同名类型，可删掉本地定义用其类型。

- [ ] **Step 2: 跑测试确认失败**

Expected: compile error.

- [ ] **Step 3: 实现 VaultRefPrewarmService**

`src/OpenClaw.Security.Vault/VaultRefPrewarmService.cs`:

```csharp
using System.Threading.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Security;

namespace OpenClaw.Security.Vault;

public sealed class VaultRefPrewarmService : IHostedService
{
    private readonly VaultSecurityOptions _options;
    private readonly ISecretResolver _resolver;
    private readonly IServiceProvider _services;
    private readonly ILogger<VaultRefPrewarmService> _logger;

    public VaultRefPrewarmService(
        VaultSecurityOptions options,
        ISecretResolver resolver,
        IServiceProvider services,
        ILogger<VaultRefPrewarmService> logger)
    {
        _options = options;
        _resolver = resolver;
        _services = services;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            return;

        var refs = new HashSet<string>(_options.PrewarmRefs, StringComparer.Ordinal);
        // Scan gateway config for vault:* refs
        var scanned = ScanConfigForVaultRefs();
        foreach (var s in scanned) refs.Add(s);

        if (refs.Count == 0)
        {
            _logger.LogInformation("Vault pre-warm: no refs to resolve.");
            return;
        }

        var failures = new List<(string Ref, string Reason)>();

        var limiter = new RateLimiter(_options.RateLimit.RequestsPerSecond);
        var tasks = refs.Select(async r =>
        {
            using var lease = await limiter.AcquireAsync(cancellationToken).ConfigureAwait(false);
            if (!lease.IsAcquired) return;
            try
            {
                var v = await _resolver.ResolveAsync(r, cancellationToken).ConfigureAwait(false);
                if (v is null)
                {
                    lock (failures) failures.Add((r, "resolve returned null"));
                    _logger.LogError("Vault pre-warm: ref {Ref} resolved to null.", r);
                }
            }
            catch (Exception ex)
            {
                lock (failures) failures.Add((r, ex.GetType().Name));
                _logger.LogError(ex, "Vault pre-warm: ref {Ref} failed: {Kind}", r, ex.GetType().Name);
            }
        }).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);

        if (failures.Count == 0)
        {
            _logger.LogInformation("Vault pre-warm: {Count} refs OK.", refs.Count);
            return;
        }

        if (_options.PrewarmRequired)
        {
            throw new HostingStartupException(
                $"Vault pre-warm failed for {failures.Count} of {refs.Count} refs: " +
                string.Join(", ", failures.Select(f => $"{f.Ref} ({f.Reason})")));
        }

        _logger.LogWarning("Vault pre-warm completed with {Failures} of {Total} failures; continuing in soft-fail mode.",
            failures.Count, refs.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private IEnumerable<string> ScanConfigForVaultRefs()
    {
        // Resolve GatewayConfig from DI; reflection-walk all *Ref string properties with "vault:" prefix.
        var config = _services.GetService<OpenClaw.Core.Models.GatewayConfig>();
        if (config is null) yield break;

        foreach (var (path, value) in WalkStrings(config, "GatewayConfig"))
        {
            if (value.StartsWith("vault:", StringComparison.OrdinalIgnoreCase))
                yield return value;
        }
    }

    private static IEnumerable<(string Path, string Value)> WalkStrings(object root, string path)
    {
        if (root is null) yield break;
        var t = root.GetType();
        foreach (var prop in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0) continue;
            object? val;
            try { val = prop.GetValue(root); } catch { continue; }
            if (val is null) continue;
            var p = $"{path}.{prop.Name}";

            if (val is string s)
            {
                yield return (p, s);
            }
            else if (val is System.Collections.IEnumerable e && val is not string)
            {
                int i = 0;
                foreach (var item in e)
                {
                    foreach (var inner in WalkStrings(item, $"{p}[{i}]"))
                        yield return inner;
                    i++;
                }
            }
            else if (prop.PropertyType.IsClass || (prop.PropertyType.IsValueType && !prop.PropertyType.IsPrimitive && !prop.PropertyType.IsEnum))
            {
                foreach (var inner in WalkStrings(val, p))
                    yield return inner;
            }
        }
    }

    private sealed class RateLimiter
    {
        private readonly System.Threading.SemaphoreSlim _sem;
        private readonly Task _refillTask;
        private readonly int _perSecond;
        public RateLimiter(int perSecond)
        {
            _perSecond = Math.Max(1, perSecond);
            _sem = new System.Threading.SemaphoreSlim(_perSecond, _perSecond);
            _refillTask = Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    try
                    {
                        while (_sem.CurrentCount < _perSecond)
                            _sem.Release();
                    }
                    catch { }
                }
            });
        }
        public async Task<Lease> AcquireAsync(CancellationToken ct)
        {
            var ok = await _sem.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            return new Lease(ok);
        }
        public sealed class Lease : IDisposable
        {
            public bool IsAcquired { get; }
            public Lease(bool acquired) => IsAcquired = acquired;
            public void Dispose() { }
        }
    }
}

public sealed class HostingStartupException : Exception
{
    public HostingStartupException(string message) : base(message) { }
}
```

> **简化点**：`RateLimiter` 用 SemaphoreSlim + 1Hz refill；复杂场景可用 `System.Threading.RateLimiting.RateLimitPartition` 但增加依赖。v1 此实现足够。

- [ ] **Step 4: 跑测试确认通过**

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~VaultRefPrewarmServiceTests"
```

Expected: PASS.

- [ ] **Step 5: 提交**

```bash
git add src/OpenClaw.Security.Vault/VaultRefPrewarmService.cs \
        src/OpenClaw.Tests/Security/VaultRefPrewarmServiceTests.cs
git commit -m "feat(vault): add VaultRefPrewarmService for startup pre-warm"
```

---

### Task 11: GatewayBootstrapExtensions 接线

**Files:**
- Modify: `src/OpenClaw.Gateway/Bootstrap/GatewayBootstrapExtensions.cs`
- Modify: `src/OpenClaw.Gateway/Composition/ChannelServicesExtensions.cs`（或其他合适位置）

**Interfaces:**
- Consumes: `AddOpenClawVaultSecrets` (Task 9), `SecretResolverAccessor` (Task 1)
- Produces: 在 `AddOpenClawBootstrapAsync` 内条件注册 vault 后端；调用 `ResolverAccessor.Use(sp)` 让静态门面路由到 DI 实例

- [ ] **Step 1: 找到 AddOpenClawBootstrapAsync 末尾位置**

打开 [src/OpenClaw.Gateway/Bootstrap/GatewayBootstrapExtensions.cs](src/OpenClaw.Gateway/Bootstrap/GatewayBootstrapExtensions.cs)；找到 `var sp = builder.Services.BuildServiceProvider()` 之前或合适位置（构建 `IServiceProvider` 之前**不**能调 `Use`——必须在 `builder.Build()` 之后）。

最常见模式：在 `AddOpenClawBootstrapAsync` 的最后阶段，构造完 `WebApplication` 后立刻调 `ResolverAccessor.Use(app.Services)`。

- [ ] **Step 2: 加 vault 注册调用**

在 `AddOpenClawBootstrapAsync` 内、`builder.Services` 配置完成后，构造 `WebApplication` 之前，添加：

```csharp
builder.Services.AddOpenClawVaultSecrets(builder.Configuration);
```

- [ ] **Step 3: 在 WebApplication 构造完成后立刻 Use 静态门面**

在创建 `var app = builder.Build();` 之后立刻：

```csharp
SecretResolver.Use(app.Services);
```

（命名空间：`OpenClaw.Core.Security.SecretResolver` / `ResolverAccessor`——按需 using。）

> 注意：`SecretResolver` 是静态类；`ResolverAccessor.Use(sp)` 是静态方法调用。

- [ ] **Step 4: 编译 Gateway**

```bash
dotnet build src/OpenClaw.Gateway/OpenClaw.Gateway.csproj
```

Expected: BUILD SUCCEEDED. 若 `SecretResolver.Use` 不可见（因为是 `ResolverAccessor.Use`），用 `ResolverAccessor.Use(...)`。

- [ ] **Step 5: 跑现有 Gateway 测试确认无回归**

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~Gateway|FullyQualifiedName~Channel|FullyQualifiedName~Security|FullyQualifiedName~SecretResolver"
```

Expected: 全绿。

- [ ] **Step 6: 提交**

```bash
git add src/OpenClaw.Gateway/Bootstrap/GatewayBootstrapExtensions.cs
git commit -m "feat(gateway): wire AddOpenClawVaultSecrets and ResolverAccessor.Use"
```

---

## Phase 4 — 部署、文档、CI

### Task 12: deploy/docker-compose/openbao.yml

**Files:**
- Create: `deploy/docker-compose/openbao.yml`

**Interfaces:**
- Consumes: 无
- Produces: 单服务 `openbao` compose 文件（含健康检查、root token 输出到 `eng/` 外不安全的 `.env`，以及可选 `bootstrap.sh`）

- [ ] **Step 1: 创建目录**

```bash
mkdir -p deploy/docker-compose
```

- [ ] **Step 2: 创建 openbao.yml**

`deploy/docker-compose/openbao.yml`:

```yaml
services:
  openbao:
    image: openbao/openbao:2.0.0
    container_name: openclaw-openbao
    command: server -dev -dev-root-token-id=root -dev-listen-address=0.0.0.0:8200
    ports:
      - "8200:8200"
    environment:
      BAO_DEV_ROOT_TOKEN_ID: root
      BAO_DEV_LISTEN_ADDRESS: 0.0.0.0:8200
    healthcheck:
      test: ["CMD", "bao", "status"]
      interval: 5s
      timeout: 3s
      retries: 10
    restart: unless-stopped
```

> **安全提醒**：`-dev` 模式仅用于集成测试；**禁止**在任何生产或共享环境使用 `-dev` token。

- [ ] **Step 3: 在 README 中加使用说明**

追加到 `deploy/docker-compose/README.md`（新建）：

```markdown
# deploy/docker-compose/

容器化工具配置目录。

## openbao.yml

集成测试用 OpenBao 服务器（仅 dev 模式）。

启动：

```bash
docker compose -f deploy/docker-compose/openbao.yml up -d
```

环境变量：

- `OPENBAO_ADDR=http://127.0.0.1:8200`
- `OPENBAO_TOKEN=root`

跑 OpenClaw 集成测试：

```bash
OPENBAO_ADDR=http://127.0.0.1:8200 OPENBAO_TOKEN=root \
  dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj \
    --filter "Category=Integration"
```
```

- [ ] **Step 4: 提交**

```bash
git add deploy/docker-compose/
git commit -m "feat(deploy): add OpenBao integration test compose file"
```

---

### Task 13: 用户文档（vault.md + zh-CN）

**Files:**
- Create: `docs/security/vault.md`
- Create: `docs/zh-CN/security/vault.md`

**Interfaces:**
- Consumes: spec
- Produces: 用户可读的配置 / 引用语法 / 运维手册

- [ ] **Step 1: 创建 docs/security/vault.md**

`docs/security/vault.md` 至少包含：

- 概述（一段话说明 vault 后端的目的）
- 配置 schema（复制 spec 的 json 示例 + 字段含义表）
- 引用语法：`vault:<mount>/data/<path>#<key>` + 示例
- 启动预热：`Security.Vault.PrewarmRefs` 显式列出 / `PrewarmRequired` 行为 / 限流
- 同步 vs 异步：`SecretResolver.Resolve` vs `ResolveAsync`，缓存未命中行为
- TLS 配置：`SkipVerify`、`CaCertPath`
- Token 递归防护
- 错误码表（链接到 spec 的 error matrix）
- 升级 / 回滚步骤

参考风格见 `docs/security/payments.md`（既有）。

- [ ] **Step 2: 创建中文版 docs/zh-CN/security/vault.md**

中文翻译，技术字面量保留英文。结构与英文版对应。

- [ ] **Step 3: 提交**

```bash
git add docs/security/vault.md docs/zh-CN/security/vault.md
git commit -m "docs: add user-facing vault configuration and usage docs (en + zh-CN)"
```

---

### Task 14: 集成测试文档 + ci.yml job

**Files:**
- Create: `docs/security/vault-integration-tests.md`
- Create: `docs/zh-CN/security/vault-integration-tests.md`
- Modify: `.github/workflows/ci.yml`

- [ ] **Step 1: 创建 docs/security/vault-integration-tests.md**

内容：

- 如何启动 OpenBao（指向 `deploy/docker-compose/openbao.yml`）
- 环境变量设置
- `dotnet test` filter 用法
- 本地跳过默认行为说明

- [ ] **Step 2: 创建中文版 docs/zh-CN/security/vault-integration-tests.md**

中文翻译。

- [ ] **Step 3: 在 .github/workflows/ci.yml 中新增可选 job**

打开 `.github/workflows/ci.yml`；在文件末尾（主 build/test job 之外）追加：

```yaml
  vault-integration:
    name: Vault integration tests (opt-in)
    runs-on: ubuntu-latest
    if: github.event.repository.fork == false && vars.RUN_VAULT_INTEGRATION == 'true'
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - run: docker compose -f deploy/docker-compose/openbao.yml up -d
      - name: Wait for OpenBao
        run: |
          for i in {1..30}; do
            if curl -sf http://127.0.0.1:8200/v1/sys/health >/dev/null; then break; fi
            sleep 1
          done
      - name: Run integration tests
        env:
          OPENBAO_ADDR: http://127.0.0.1:8200
          OPENBAO_TOKEN: root
        run: dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "Category=Integration"
      - run: docker compose -f deploy/docker-compose/openbao.yml down -v
```

> `vars.RUN_VAULT_INTEGRATION` 在仓库 Settings → Variables 配置。

- [ ] **Step 4: 提交**

```bash
git add docs/security/vault-integration-tests.md \
        docs/zh-CN/security/vault-integration-tests.md \
        .github/workflows/ci.yml
git commit -m "docs+ci: integration test docs (en+zh-CN) and optional ci job"
```

---

### Task 15: 集成测试 stub + 收尾

**Files:**
- Create: `src/OpenClaw.Tests/Security/VaultIntegrationTests.cs`
- Modify: `docs/security/payments.md`
- Modify: `CHANGELOG.md`

- [ ] **Step 1: 创建集成测试 stub（默认跳过）**

`src/OpenClaw.Tests/Security/VaultIntegrationTests.cs`:

```csharp
using OpenClaw.Security.Vault;
using Xunit;

namespace OpenClaw.Tests.Security;

[Trait("Category", "Integration")]
public sealed class VaultIntegrationTests
{
    private static bool ShouldRun()
    {
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENBAO_ADDR")) &&
               !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENBAO_TOKEN"));
    }

    [SkippableFact]
    public void Smoke_PingOpenBao()
    {
        Skip.IfNot(ShouldRun(), "OPENBAO_ADDR and OPENBAO_TOKEN not set; skipping.");
        var addr = Environment.GetEnvironmentVariable("OPENBAO_ADDR")!;
        using var http = new HttpClient { BaseAddress = new Uri(addr) };
        var resp = http.GetAsync("/v1/sys/health").GetAwaiter().GetResult();
        Assert.True(resp.IsSuccessStatusCode);
    }
}
```

> 需 `Xunit.SkippableFact` 包；若未引用则用 `if (!ShouldRun()) return;` 简单跳过。

- [ ] **Step 2: 更新 docs/security/payments.md**

找到 "Production vault adapters are intentionally extension points..." 这段（spec 中引用 line 49），更新为：

```markdown
Production secret backends are implemented as follows:
- HashiCorp Vault / OpenBao (KV v2 read): see `docs/security/vault.md`. Implementation in `OpenClaw.Security.Vault` using VaultSharp.
- Future: AWS Secrets Manager, Azure Key Vault, DPAPI (planned v2).
```

- [ ] **Step 3: 更新 CHANGELOG.md**

在文件顶部 Unreleased 节追加：

```markdown
### Added
- Vault / OpenBao secret resolver backend (`OpenClaw.Security.Vault`). KV v2 read with Token auth, TTL cache, refresh-ahead, IHostedService pre-warm. See `docs/security/vault.md`.
```

并翻译为中文版（如果 CHANGELOG.md 是中英双语布局则两份都更新；如单语，按既有语言）。

- [ ] **Step 4: 跑全量测试确认无回归**

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "Category!=Integration"
```

Expected: 全绿。集成测试默认跳过。

- [ ] **Step 5: 跑 build 确认 slnx 全绿**

```bash
dotnet build OpenClaw.Net.slnx
```

Expected: BUILD SUCCEEDED.

- [ ] **Step 6: 提交**

```bash
git add src/OpenClaw.Tests/Security/VaultIntegrationTests.cs \
        docs/security/payments.md \
        CHANGELOG.md
git commit -m "feat: integrate vault secret resolver; update payments.md and CHANGELOG"
```

---

## Self-Review Checklist

实施前请执行：

- [ ] **Spec 覆盖**：每个 spec 节都有对应 task：
  - 抽象 + 门面 → Task 1, 2, 3
  - Vault 实现 → Task 4, 5, 6, 7, 8, 9
  - 配置 + 预热 + 接线 → Task 10, 11
  - 部署 + 文档 + CI → Task 12, 13, 14, 15
- [ ] **占位符扫描**：检查所有 task——无 "TBD"/"TODO"/"类似 Task N"。
- [ ] **类型一致性**：
  - `ISecretProvider.Scheme` 在所有 task 中一致（"env"、"vault"）
  - `VaultRefParser.Parse` 签名 `(string, string defaultMount = "secret")` 在 Task 6, 8 中一致
  - `VaultRefCache.GetOrFetchAsync(VaultRef, Func<CancellationToken, Task<string>>, CancellationToken)` 签名在 Task 7, 8 中一致
  - `ISyncSecretProvider` 接口在 Task 2 中定义，EnvRaw 在 Task 2 Step 8 中实现
- [ ] **AOT 约束**：`OpenClaw.Security.Vault.csproj` `IsAotCompatible=false` ✓
- [ ] **不泄漏 value**：所有异常 / 日志路径在测试中验证（Task 6 "Parse_ExceptionMessage_DoesNotLeakPath", Task 8 "ResolveAsync_NoValueInExceptionMessage", Task 7 失败回退）

---

## 实施说明

执行本 plan 推荐：

1. **Subagent-Driven (推荐)**：每个 task 派一个 fresh subagent，task 间 review
2. **Inline Execution**：本 session 内 batch 执行，checkpoints 暂停 review

执行选项二选一。选好后我会按对应 skill（`subagent-driven-development` 或 `executing-plans`）开始执行 task 1。

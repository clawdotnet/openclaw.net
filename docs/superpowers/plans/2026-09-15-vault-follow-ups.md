# Vault 后续三项修复 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复 Vault/OpenBao 密钥解析器的三个遗留问题：补齐 4 个真实后端 End2End 集成场景、接线 `Tls.CaCertPath` 自定义 CA 证书包、让 `vault:` 引用在 vault 禁用时 fail-closed（抛 `VaultNotConfiguredException` 而非字面量回退）。

**Architecture:** 三个任务相互独立、各自可测：Task 1 仅测试代码（用 VaultSharp 直连真实 OpenBao 写入、用计数装饰器观测 HTTP 调用）；Task 2 在 Vault 项目新增公开的 CA 证书包加载器，经既有 `PostProcessHttpClientHandlerAction` 钩子挂接自定义根信任回调，并在 `ConfigValidator` 增加 SkipVerify/CaCertPath 互斥规则；Task 3 把 `VaultNotConfiguredException` 从 Vault 项目下移到 Core（避免循环引用），`CompositeSecretResolver` 对无 provider 认领的 `vault:` 前缀引用抛该异常。

**Tech Stack:** .NET 10 / C# 14, xUnit v3, NSubstitute, VaultSharp 1.17.5.1, OpenBao 2.0.0 (dev mode), Microsoft.Extensions.Caching.Memory.

**Spec:** [docs/superpowers/specs/2026-09-15-vault-secret-resolver-design.md](../specs/2026-09-15-vault-secret-resolver-design.md)（错误矩阵「Vault 节未启用但使用了 `vault:` 前缀 → 抛 `VaultNotConfiguredException`」、集成测试矩阵 4 场景、TLS 配置节）。执行基线：上一计划（2026-09-15-vault-secret-resolver.md）已全部落地，当前 HEAD 为分支 `secrectprovider` 上 `322ac1c`。

## Global Constraints

- **TargetFramework**: `net10.0`；**LangVersion**: 14；**Nullable**: enable；**TreatWarningsAsErrors**: true（xUnit 分析器错误即构建失败，测试内禁止阻塞式等待，如 xUnit1031 禁 `.GetAwaiter().GetResult()`）；**TrimMode**: link；**InvariantGlobalization**: true。
- **OpenClaw.Core**：`IsAotCompatible=true`、`IsPackable=true`。Core 不能引用 VaultSharp 或 Vault 项目。
- **OpenClaw.Security.Vault**：`IsAotCompatible=false`、`IsPackable=false`。无 InternalsVisibleTo——需要被 OpenClaw.Tests 直接测试的类型必须为 `public`。
- **测试框架**：xUnit v3 + NSubstitute。`OpenClaw.Tests` 已 ProjectReference OpenClaw.Security.Vault（VaultSharp 作为传递包依赖可见）。
- **不泄漏密钥 value**：异常消息、日志、堆栈中绝不出现解析后的 value。仅 path / key / 状态码 / 错误类型名。
- **集成测试默认跳过**：除非 `OPENBAO_ADDR` 与 `OPENBAO_TOKEN` 环境变量同时设置（既有 stub 的 `ShouldRun()` 模式沿用）。
- **既有行为零破坏**：`env:`/`raw:`/裸串的解析、legacy 回退、67 个调用点保持零修改。Task 3 的语义变更仅影响「vault 禁用 + `vault:` 前缀」这一条此前即与规范不符的路径。
- **提交格式**：每任务一个提交，消息以 `feat(...)`/`fix(...)`/`test(...)` 开头，结尾加 `Co-Authored-By: Claude Code <noreply@anthropic.com>`。

---

## File Map

| 文件 | 操作 | 职责 |
|---|---|---|
| `src/OpenClaw.Tests/Security/VaultIntegrationTests.cs` | 修改 | 4 个 End2End 场景 + CountingClient 装饰器 + VaultSharp 写辅助 |
| `src/OpenClaw.Security.Vault/VaultCaCertLoader.cs` | 新增 | 从文件/目录加载 PEM CA 证书包；构建自定义根信任校验回调（public，可单测） |
| `src/OpenClaw.Security.Vault/VaultSecretProvider.cs` | 修改 | `VaultSharpClient` 构造函数接线 `CaCertPath`（替换 future-work 注释） |
| `src/OpenClaw.Core/Validation/ConfigValidator.cs` | 修改 | `ValidateVaultSecurity` 增加 SkipVerify 与 CaCertPath 互斥规则 |
| `src/OpenClaw.Core/Models/GatewayConfig.cs` | 修改 | 更新 `VaultTlsOptions.CaCertPath` 的 XML 注释（不再是 future work） |
| `src/OpenClaw.Core/Security/VaultNotConfiguredException.cs` | 新增 | 异常下移到 Core（命名空间 `OpenClaw.Core.Security`） |
| `src/OpenClaw.Security.Vault/VaultExceptions.cs` | 修改 | 移除 VaultNotConfiguredException（下移后不再重复定义） |
| `src/OpenClaw.Core/Security/CompositeSecretResolver.cs` | 修改 | 同步/异步路径对无 provider 认领的 `vault:` 引用抛 `VaultNotConfiguredException` |
| `src/OpenClaw.Tests/Security/VaultTlsCaCertTests.cs` | 新增 | 加载器 + 校验回调 + 客户端接线单测（自签证书，无真实服务器） |
| `src/OpenClaw.Tests/Security/VaultSecurityOptionsTests.cs` | 修改 | 互斥规则校验测试 |
| `src/OpenClaw.Tests/Security/CompositeSecretResolverTests.cs` | 修改 | 禁用时 `vault:` 引用抛异常（同步+异步）+ 非 ref 字面量回退不变 |
| `docs/security/vault.md` + `docs/zh-CN/security/vault.md` | 修改 | TLS 节、CaCertPath 字段行、错误矩阵行按实际行为更新 |
| `CHANGELOG.md` | 修改 | Security 节追加两条（CaCertPath、禁用 fail-closed） |

---

### Task 1: End2End 集成测试（4 个真实 OpenBao 场景）

**Files:**
- Modify: `src/OpenClaw.Tests/Security/VaultIntegrationTests.cs`

**Interfaces:**
- Consumes: `VaultSharpClient`（public ctor `(string address, string token, string? ns, VaultTlsOptions tls)`）、`IVaultClient.ReadSecretV2Async`、`VaultRefCache`（ctor `(IMemoryCache, ILogger<VaultRefCache>, TimeSpan ttl)`）、`VaultSecretProvider`（ctor `(IVaultClient, VaultRefCache, VaultSecurityOptions, ILogger<VaultSecretProvider>)`）、VaultSharp 直连写 API（反射核实 1.17.5.1）：`IVaultClient.V1.Secrets.KeyValue.V2.WriteSecretAsync<T>(string path, T data, int? cas, string mountPoint)` 与 `DeleteSecretAsync(string path, string mountPoint)`
- Produces: 4 个 `[Fact]` + `[Trait("Category","Integration")]` 测试；本地私有辅助 `CreateResolverAsync(ttl)`、`WriteSecretAsync(path, data)`、`DeleteSecretAsync(path)`、`CountingClient` 装饰器

- [ ] **Step 1: 写 4 个失败测试（无 OPENBAO_ADDR 时编译通过、运行跳过）**

打开 `src/OpenClaw.Tests/Security/VaultIntegrationTests.cs`，替换现有 stub 内容为：

```csharp
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Security.Vault;
using VaultSharp;
using VaultSharp.V1.AuthMethods.Token;
using Xunit;

namespace OpenClaw.Tests.Security;

[Trait("Category", "Integration")]
public sealed class VaultIntegrationTests
{
    private const string DefaultMount = "secret";

    private static bool ShouldRun()
    {
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENBAO_ADDR")) &&
               !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENBAO_TOKEN"));
    }

    private static string Addr => Environment.GetEnvironmentVariable("OPENBAO_ADDR")!;
    private static string Token => Environment.GetEnvironmentVariable("OPENBAO_TOKEN")!;

    [Fact]
    public async Task Smoke_PingOpenBao()
    {
        if (!ShouldRun())
            return;

        using var http = new HttpClient { BaseAddress = new Uri(Addr) };
        using var resp = await http.GetAsync("/v1/sys/health");
        Assert.True(resp.IsSuccessStatusCode);
    }

    [Fact]
    public async Task End2End_PutAndResolve_KvV2()
    {
        if (!ShouldRun())
            return;

        var path = $"openclaw-e2e/put-{Guid.NewGuid():n}";
        try
        {
            await WriteSecretAsync(path, new Dictionary<string, object> { ["api_key"] = "sk-e2e" });
            var (resolver, _) = CreateResolver(TimeSpan.FromMinutes(5));

            var value = await resolver.ResolveAsync($"vault:{DefaultMount}/data/{path}#api_key", CancellationToken.None);
            Assert.Equal("sk-e2e", value);
        }
        finally
        {
            await DeleteSecretAsync(path);
        }
    }

    [Fact]
    public async Task End2End_TtlExpiry_FetchesAgain()
    {
        if (!ShouldRun())
            return;

        var path = $"openclaw-e2e/ttl-{Guid.NewGuid():n}";
        try
        {
            await WriteSecretAsync(path, new Dictionary<string, object> { ["api_key"] = "v1" });
            var (resolver, client) = CreateResolver(TimeSpan.FromMilliseconds(500));

            Assert.Equal("v1", await resolver.ResolveAsync($"vault:{DefaultMount}/data/{path}#api_key", CancellationToken.None));
            Assert.Equal(1, client.Calls);
            Assert.Equal("v1", await resolver.ResolveAsync($"vault:{DefaultMount}/data/{path}#api_key", CancellationToken.None));
            Assert.Equal(1, client.Calls); // cache hit, no HTTP

            await Task.Delay(700); // pass TTL (500ms) but stay inside the stale window (2xTTL)
            Assert.Equal("v1", await resolver.ResolveAsync($"vault:{DefaultMount}/data/{path}#api_key", CancellationToken.None));
            // refresh-ahead kicks off in the background; poll for it instead of racing.
            for (var i = 0; i < 30 && client.Calls < 2; i++)
                await Task.Delay(100);
            Assert.Equal(2, client.Calls);
        }
        finally
        {
            await DeleteSecretAsync(path);
        }
    }

    [Fact]
    public async Task End2End_TokenUnauth_Throws_VaultAuthException()
    {
        if (!ShouldRun())
            return;

        var client = new VaultSharpClient(Addr, "definitely-invalid-token", ns: null, new VaultTlsOptions());
        var memCache = new MemoryCache(new MemoryCacheOptions());
        var cache = new VaultRefCache(memCache, NullLogger<VaultRefCache>.Instance, TimeSpan.FromMinutes(5));
        var provider = new VaultSecretProvider(client, cache, new VaultSecurityOptions(), NullLogger<VaultSecretProvider>.Instance);

        await Assert.ThrowsAsync<VaultAuthException>(() =>
            provider.ResolveAsync($"vault:{DefaultMount}/data/does/not/matter#k", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task End2End_RotatedValue_PickedUpAfterTtl()
    {
        if (!ShouldRun())
            return;

        var path = $"openclaw-e2e/rotate-{Guid.NewGuid():n}";
        try
        {
            await WriteSecretAsync(path, new Dictionary<string, object> { ["api_key"] = "v1" });
            var (resolver, _) = CreateResolver(TimeSpan.FromMilliseconds(500));
            var secretRef = $"vault:{DefaultMount}/data/{path}#api_key";

            Assert.Equal("v1", await resolver.ResolveAsync(secretRef, CancellationToken.None));

            await WriteSecretAsync(path, new Dictionary<string, object> { ["api_key"] = "v2" });
            await Task.Delay(700); // pass TTL so the next resolve serves stale + refresh-ahead

            string? value = null;
            for (var i = 0; i < 30 && value != "v2"; i++)
            {
                value = await resolver.ResolveAsync(secretRef, CancellationToken.None);
                await Task.Delay(100);
            }
            Assert.Equal("v2", value);
        }
        finally
        {
            await DeleteSecretAsync(path);
        }
    }

    private static (ISecretResolver resolver, CountingClient client) CreateResolver(TimeSpan ttl)
    {
        var client = new CountingClient(new VaultSharpClient(Addr, Token, ns: null, new VaultTlsOptions()));
        var memCache = new MemoryCache(new MemoryCacheOptions());
        var cache = new VaultRefCache(memCache, NullLogger<VaultRefCache>.Instance, ttl);
        var provider = new VaultSecretProvider(client, cache, new VaultSecurityOptions(), NullLogger<VaultSecretProvider>.Instance);
        return (new CompositeSecretResolver([provider], NullLogger<CompositeSecretResolver>.Instance), client);
    }

    private static async Task WriteSecretAsync(string path, IDictionary<string, object> data)
    {
        var client = new VaultSharp.VaultClient(new VaultClientSettings(Addr, new TokenAuthMethodInfo(Token)));
        await client.V1.Secrets.KeyValue.V2.WriteSecretAsync(path, data, cas: null, mountPoint: DefaultMount);
    }

    private static async Task DeleteSecretAsync(string path)
    {
        var client = new VaultSharp.VaultClient(new VaultClientSettings(Addr, new TokenAuthMethodInfo(Token)));
        await client.V1.Secrets.KeyValue.V2.DeleteSecretAsync(path, DefaultMount);
    }

    private sealed class CountingClient : IVaultClient
    {
        private readonly IVaultClient _inner;
        public int Calls { get; private set; }

        public CountingClient(IVaultClient inner) => _inner = inner;

        public async Task<Dictionary<string, object>?> ReadSecretV2Async(string mount, string path, CancellationToken ct)
        {
            Calls++;
            return await _inner.ReadSecretV2Async(mount, path, ct);
        }
    }
}
```

> 注意：`CompositeSecretResolver` 与 `ISecretResolver` 在 `OpenClaw.Core.Security`——需要 `using OpenClaw.Core.Security;`（若上面 using 列表遗漏则编译期补上）。`AsTask()` 用于 `Assert.ThrowsAsync` 的 ValueTask 适配（项目内既有模式）。

- [ ] **Step 2: 编译确认失败方式符合预期**

Run: `dotnet build src/OpenClaw.Tests/OpenClaw.Tests.csproj`
Expected: BUILD SUCCEEDED（本任务为纯测试新增，无 TDD 红灯；运行期由 `ShouldRun()` 跳过）。

- [ ] **Step 3: 有 OpenBao 时跑集成测试**

```bash
docker compose -f deploy/docker-compose/openbao.yml up -d
# 等待健康后：
OPENBAO_ADDR=http://127.0.0.1:8200 OPENBAO_TOKEN=root \
  dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "Category=Integration"
```

Expected: 5 个测试（含 smoke）全绿。若本机无 Docker，先跑 `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~VaultIntegrationTests"` 确认 5/5 跳过不报错，并在完成报告中注明「集成测试未对真实 OpenBao 验证」。

- [ ] **Step 4: 提交**

```bash
git add src/OpenClaw.Tests/Security/VaultIntegrationTests.cs
git commit -m "test(vault): add end-to-end integration scenarios against real OpenBao

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 2: `Tls.CaCertPath` 接线（自定义 CA 证书包）

**Files:**
- Create: `src/OpenClaw.Security.Vault/VaultCaCertLoader.cs`
- Modify: `src/OpenClaw.Security.Vault/VaultSecretProvider.cs:91-112`
- Modify: `src/OpenClaw.Core/Validation/ConfigValidator.cs`（`ValidateVaultSecurity` 内）
- Modify: `src/OpenClaw.Core/Models/GatewayConfig.cs`（`VaultTlsOptions.CaCertPath` XML 注释）
- Test: `src/OpenClaw.Tests/Security/VaultTlsCaCertTests.cs`
- Modify: `src/OpenClaw.Tests/Security/VaultSecurityOptionsTests.cs`

**Interfaces:**
- Consumes: `VaultTlsOptions { SkipVerify, CaCertPath }`、`SecretResolutionException`（Core）、`VaultClientSettings.PostProcessHttpClientHandlerAction`
- Produces: `public static class VaultCaCertLoader`：`X509Certificate2Collection Load(string? path)`、`RemoteCertificateValidationCallback BuildServerCertificateValidator(X509Certificate2Collection roots)`；`VaultSharpClient` 构造函数在 `CaCertPath` 非空时挂接该回调

- [ ] **Step 1: 写失败测试**

新建 `src/OpenClaw.Tests/Security/VaultTlsCaCertTests.cs`：

```csharp
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Security.Vault;
using Xunit;

namespace OpenClaw.Tests.Security;

public sealed class VaultTlsCaCertTests : IDisposable
{
    private readonly string _tempDir;

    public VaultTlsCaCertTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("openclaw-cacert-").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Load_ValidPemFile_ReturnsCert()
    {
        var cert = CreateSelfSigned();
        var pemPath = Path.Combine(_tempDir, "ca.pem");
        File.WriteAllText(pemPath, cert.ExportCertificatePem());

        var bundle = VaultCaCertLoader.Load(pemPath);

        Assert.Single(bundle);
        Assert.Equal(cert.Thumbprint, bundle[0].Thumbprint);
    }

    [Fact]
    public void Load_MissingFile_Throws()
    {
        Assert.Throws<SecretResolutionException>(() =>
            VaultCaCertLoader.Load(Path.Combine(_tempDir, "nope.pem")));
    }

    [Fact]
    public void Load_EmptyDirectory_Throws()
    {
        Assert.Throws<SecretResolutionException>(() => VaultCaCertLoader.Load(_tempDir));
    }

    [Fact]
    public void Load_Directory_LoadsAllPems()
    {
        var c1 = CreateSelfSigned();
        var c2 = CreateSelfSigned();
        File.WriteAllText(Path.Combine(_tempDir, "a.pem"), c1.ExportCertificatePem());
        File.WriteAllText(Path.Combine(_tempDir, "b.pem"), c2.ExportCertificatePem());

        var bundle = VaultCaCertLoader.Load(_tempDir);

        Assert.Equal(2, bundle.Count);
    }

    [Fact]
    public void Load_MalformedPem_Throws()
    {
        var bad = Path.Combine(_tempDir, "bad.pem");
        File.WriteAllText(bad, "this is not a certificate");

        Assert.Throws<SecretResolutionException>(() => VaultCaCertLoader.Load(bad));
    }

    [Fact]
    public void Validator_TrustsCertChainingToBundleRoot()
    {
        var root = CreateSelfSigned();
        var roots = new X509Certificate2Collection(root);

        var validator = VaultCaCertLoader.BuildServerCertificateValidator(roots);

        var ok = validator(null!, root, null, SslPolicyErrors.RemoteCertificateChainErrors);
        Assert.True(ok);
    }

    [Fact]
    public void Validator_RejectsCertOutsideBundle()
    {
        var root = CreateSelfSigned();
        var stranger = CreateSelfSigned();
        var roots = new X509Certificate2Collection(root);

        var validator = VaultCaCertLoader.BuildServerCertificateValidator(roots);

        Assert.False(validator(null!, stranger, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void Validator_RejectsNameMismatch()
    {
        var root = CreateSelfSigned();
        var validator = VaultCaCertLoader.BuildServerCertificateValidator(new X509Certificate2Collection(root));

        Assert.False(validator(null!, root, null, SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    private X509Certificate2 CreateSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=openclaw-test-ca", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return new X509Certificate2(cert.Export(X509ContentType.Pfx));
    }
}
```

新建/追加到 `src/OpenClaw.Tests/Security/VaultSecurityOptionsTests.cs`：

```csharp
    [Fact]
    public void ConfigValidator_SkipVerifyAndCaCertPath_Rejects()
    {
        var cfg = MakeValidConfig();
        cfg.Security.Vault = new VaultSecurityOptions
        {
            Enabled = true,
            Address = "https://vault.example.com",
            TokenRef = "env:X",
            Tls = new VaultTlsOptions { SkipVerify = true, CaCertPath = "/etc/ssl/ca.pem" }
        };
        var errors = ConfigValidator.Validate(cfg).ToList();
        Assert.Contains(errors, e => e.Contains("SkipVerify", StringComparison.OrdinalIgnoreCase) &&
                                    e.Contains("CaCertPath", StringComparison.OrdinalIgnoreCase));
    }
```

- [ ] **Step 2: 运行确认失败（编译错误）**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~VaultTlsCaCertTests|FullyQualifiedName~VaultSecurityOptionsTests"`
Expected: FAIL，`CS0246: VaultCaCertLoader 不存在`（TDD 红灯）。

- [ ] **Step 3: 实现 VaultCaCertLoader**

新建 `src/OpenClaw.Security.Vault/VaultCaCertLoader.cs`：

```csharp
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using OpenClaw.Core.Security;

namespace OpenClaw.Security.Vault;

/// <summary>
/// Loads a custom CA certificate bundle (single PEM file, or a directory of
/// .pem/.crt/.cer files) and builds a server-certificate validator that trusts
/// exactly those roots (X509Chain custom root trust, revocation check off).
/// </summary>
public static class VaultCaCertLoader
{
    private static readonly string[] CertificateExtensions = [".pem", ".crt", ".cer"];

    public static X509Certificate2Collection Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return [];

        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        string[] files;
        if (Directory.Exists(fullPath))
        {
            files = Directory.EnumerateFiles(fullPath)
                .Where(f => CertificateExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .ToArray();
            if (files.Length == 0)
                throw new SecretResolutionException("Vault TLS CA bundle directory contains no certificate files.");
        }
        else
        {
            files = [fullPath];
        }

        var bundle = new X509Certificate2Collection();
        foreach (var file in files)
        {
            if (!File.Exists(file))
                throw new SecretResolutionException($"Vault TLS CA bundle file not found: '{file}'.");
            try
            {
                bundle.ImportFromPemFile(file);
            }
            catch (CryptographicException ex)
            {
                throw new SecretResolutionException($"Vault TLS CA bundle file is not a valid PEM certificate: '{file}'.", ex);
            }
        }
        return bundle;
    }

    public static RemoteCertificateValidationCallback BuildServerCertificateValidator(X509Certificate2Collection roots)
    {
        return (_, serverCertificate, _, errors) =>
        {
            // Hostname and availability stay delegated to the platform's own checks.
            if ((errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
                return false;
            if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
                return false;

            if (serverCertificate is not X509Certificate2 cert)
                return false;

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(roots);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(cert);
        };
    }
}
```

- [ ] **Step 4: 接线 VaultSharpClient**

修改 `src/OpenClaw.Security.Vault/VaultSecretProvider.cs` 的 `VaultSharpClient` 构造函数（91-112 行）：

```csharp
    public VaultSharpClient(string address, string token, string? ns, VaultTlsOptions tls)
    {
        var settings = new VaultClientSettings(
            address,
            new VaultSharp.V1.AuthMethods.Token.TokenAuthMethodInfo(token))
        {
            Namespace = ns,
        };
        var caCerts = VaultCaCertLoader.Load(tls.CaCertPath);
        if (tls.SkipVerify)
        {
            // VaultSharp 1.x exposes TLS customization via the handler post-processing hook.
            settings.PostProcessHttpClientHandlerAction = handler =>
            {
                if (handler is HttpClientHandler clientHandler)
                    clientHandler.ServerCertificateCustomValidationCallback =
                        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            };
        }
        else if (caCerts.Count > 0)
        {
            // Custom CA bundle: trust exactly the loaded roots, keep hostname checks.
            var validator = VaultCaCertLoader.BuildServerCertificateValidator(caCerts);
            settings.PostProcessHttpClientHandlerAction = handler =>
            {
                if (handler is HttpClientHandler clientHandler)
                    clientHandler.ServerCertificateCustomValidationCallback = validator;
            };
        }
        _client = new VaultSharp.VaultClient(settings);
    }
```

- [ ] **Step 5: ConfigValidator 互斥规则**

修改 `src/OpenClaw.Core/Validation/ConfigValidator.cs` 的 `ValidateVaultSecurity`，在 `if (!v.Enabled) return;` 之后追加：

```csharp
        if (v.Tls.SkipVerify && !string.IsNullOrWhiteSpace(v.Tls.CaCertPath))
            errors.Add("Security.Vault.Tls.SkipVerify and Security.Vault.Tls.CaCertPath are mutually exclusive; set at most one.");
```

修改 `src/OpenClaw.Core/Models/GatewayConfig.cs` 中 `VaultTlsOptions` 的 XML 注释（若原注释含 "reserved"/"not yet wired" 字样则删除该说明）。

- [ ] **Step 6: 运行确认全绿**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~VaultTlsCaCertTests|FullyQualifiedName~VaultSecurityOptionsTests"`
Expected: PASS（8 个加载器/校验器测试 + 4 个既有 options 测试 + 1 个新互斥测试）。

- [ ] **Step 7: 更新文档 + CHANGELOG 并提交**

`docs/security/vault.md` 与 `docs/zh-CN/security/vault.md`：
- 字段表中 `Tls.CaCertPath` 行：从「预留给自定义 CA 证书包（尚未接入客户端）」改为「自定义 CA 证书包路径（PEM 文件或含 .pem/.crt/.cer 的目录）；与 `SkipVerify` 互斥」。
- TLS 节补充：`CaCertPath` 加载的根证书作为自定义根信任（`CustomRootTrust`），主机名校验保留。

`CHANGELOG.md` `### Security` 节追加：

```markdown
- Added `Security.Vault.Tls.CaCertPath` support: load a custom CA bundle (PEM file or directory) for Vault TLS validation; mutually exclusive with `Tls.SkipVerify`.
```

```bash
git add src/OpenClaw.Security.Vault/VaultCaCertLoader.cs src/OpenClaw.Security.Vault/VaultSecretProvider.cs src/OpenClaw.Core/Validation/ConfigValidator.cs src/OpenClaw.Core/Models/GatewayConfig.cs src/OpenClaw.Tests/Security/VaultTlsCaCertTests.cs src/OpenClaw.Tests/Security/VaultSecurityOptionsTests.cs docs/security/vault.md docs/zh-CN/security/vault.md CHANGELOG.md
git commit -m "feat(vault): wire Tls.CaCertPath custom CA bundle validation

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 3: VaultNotConfiguredException 语义化（禁用时 fail-closed）

**Files:**
- Create: `src/OpenClaw.Core/Security/VaultNotConfiguredException.cs`
- Modify: `src/OpenClaw.Security.Vault/VaultExceptions.cs`（删除同类定义）
- Modify: `src/OpenClaw.Core/Security/CompositeSecretResolver.cs`
- Test: `src/OpenClaw.Tests/Security/CompositeSecretResolverTests.cs`
- Modify: `docs/security/vault.md` + `docs/zh-CN/security/vault.md` + `CHANGELOG.md`

**Interfaces:**
- Consumes: `SecretResolutionException`（Core）、`CompositeSecretResolver`（Core）
- Produces: `public sealed class VaultNotConfiguredException : SecretResolutionException`（命名空间 `OpenClaw.Core.Security`，Vault 项目经 Core 引用可见）；`CompositeSecretResolver` 同步/异步路径对「`vault:` 前缀且无 provider 认领」的引用抛该异常

- [ ] **Step 1: 写失败测试**

在 `src/OpenClaw.Tests/Security/CompositeSecretResolverTests.cs` 追加（需 `using OpenClaw.Core.Security;` 已有；无额外 using）：

```csharp
    [Fact]
    public async Task ResolveAsync_VaultPrefix_NoProvider_ThrowsVaultNotConfigured()
    {
        var resolver = new CompositeSecretResolver(Array.Empty<ISecretProvider>(), NullLogger<CompositeSecretResolver>.Instance);

        await Assert.ThrowsAsync<VaultNotConfiguredException>(() =>
            resolver.ResolveAsync("vault:secret/data/x#k", CancellationToken.None).AsTask());
    }

    [Fact]
    public void Resolve_VaultPrefix_NoProvider_ThrowsVaultNotConfigured()
    {
        var resolver = new CompositeSecretResolver(Array.Empty<ISecretProvider>(), NullLogger<CompositeSecretResolver>.Instance);

        Assert.Throws<VaultNotConfiguredException>(() => resolver.Resolve("vault:secret/data/x#k"));
    }

    [Fact]
    public async Task ResolveAsync_NonRef_LiteralFallbackUnchanged()
    {
        var resolver = new CompositeSecretResolver(Array.Empty<ISecretProvider>(), NullLogger<CompositeSecretResolver>.Instance);

        Assert.Equal("sk-plaintext-key", await resolver.ResolveAsync("sk-plaintext-key"));
        Assert.Equal("https://api.example.com/v1", await resolver.ResolveAsync("https://api.example.com/v1"));
    }
```

> 若既有测试中存在「`vault:` 引用期望字面量回退」的断言（本计划审查时未见，执行时以实际文件为准），将其改为期望 `VaultNotConfiguredException`——这是本任务的目标语义变更。

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~CompositeSecretResolverTests"`
Expected: FAIL，`CS0246: VaultNotConfiguredException 不存在`（或断言失败，若类型解析顺序不同）。

- [ ] **Step 3: 异常下移到 Core**

新建 `src/OpenClaw.Core/Security/VaultNotConfiguredException.cs`：

```csharp
namespace OpenClaw.Core.Security;

/// <summary>
/// Thrown when a reference uses the <c>vault:</c> scheme but no provider is
/// registered to resolve it (the Vault backend is disabled or not wired).
/// </summary>
public sealed class VaultNotConfiguredException : SecretResolutionException
{
    public VaultNotConfiguredException(string message) : base(message) { }
}
```

从 `src/OpenClaw.Security.Vault/VaultExceptions.cs` 删除第 34-37 行的同类定义。

- [ ] **Step 4: CompositeSecretResolver fail-closed**

修改 `src/OpenClaw.Core/Security/CompositeSecretResolver.cs`。异步路径的兜底处（`return secretRef;` 前）与同步路径兜底处（第 56 行 `return secretRef;` 前）分别改为：

```csharp
        if (secretRef.StartsWith("vault:", StringComparison.OrdinalIgnoreCase))
            throw new VaultNotConfiguredException(
                "Vault secret reference was used but no vault provider is configured (Security.Vault.Enabled).");

        // No provider claimed it — defer to the first provider that could plausibly handle
        // bare/env/raw, otherwise return the literal as a last-resort fallback (mirrors legacy).
        _logger.LogDebug("No provider claimed ref of length {Length}; returning literal fallback.", secretRef.Length);
        return secretRef;
```

（同步路径无 `_logger`，只需前置 `if` 抛异常，保留原 `return secretRef;`。）

- [ ] **Step 5: 运行确认全绿**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~CompositeSecretResolverTests|FullyQualifiedName~VaultSecretProviderTests|FullyQualifiedName~SecretResolverFacadeTests|FullyQualifiedName~EnvRawSecretProviderTests"`
Expected: PASS。若其余测试（Security 全量）有回归，逐个检查是否为上述既有断言需要更新。

- [ ] **Step 6: 更新文档 + CHANGELOG 并提交**

`docs/security/vault.md` 与 `docs/zh-CN/security/vault.md` 错误矩阵最后一行：

| 之前 | 之后 |
|---|---|
| `Vault disabled, `vault:` ref used` → treated as literal string | → throws `VaultNotConfiguredException` |
| `Vault 未启用却使用 `vault:` 引用` → 视为字面量字符串 | → 抛 `VaultNotConfiguredException` |

配置节字段表中 `Enabled` 行的说明同步（「禁用时，`vault:` 引用…视为字面量字符串」→「…解析时报 `VaultNotConfiguredException`（fail-closed）」）。

`CHANGELOG.md` `### Security` 节追加：

```markdown
- Made `vault:` references fail closed: resolving a `vault:` ref while the Vault backend is disabled now throws `VaultNotConfiguredException` instead of silently falling back to the literal string.
```

```bash
git add src/OpenClaw.Core/Security/VaultNotConfiguredException.cs src/OpenClaw.Security.Vault/VaultExceptions.cs src/OpenClaw.Core/Security/CompositeSecretResolver.cs src/OpenClaw.Tests/Security/CompositeSecretResolverTests.cs docs/security/vault.md docs/zh-CN/security/vault.md CHANGELOG.md
git commit -m "fix(security): fail closed on vault: refs when vault backend is disabled

Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### 收尾验证（三个任务全部提交后）

- [ ] **全量测试**

```bash
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "Category!=Integration"
```

Expected: 除 2 个既有基线失败（`PluginCommandsTests.InstallPreparedDirectoryAsync_NativePluginDoesNotRunNpmLifecycleScripts`、`CompanionCanvasUiTests.Keyboard_PaletteAndComposer_PreserveDraftUntilSend`，在基线 `3fecb6d` 同样失败）与 1 个既有 skip 外全绿；新增 vault 相关测试全部通过。

- [ ] **全解决方案构建**

```bash
dotnet build OpenClaw.Net.slnx
```

Expected: BUILD SUCCEEDED，0 警告 0 错误。

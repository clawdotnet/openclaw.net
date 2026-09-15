# Vault / OpenBao 密钥解析

OpenClaw.NET 通过 `SecretResolver`（静态门面）解析密钥，其背后是可插拔的 `ISecretProvider` 链。Vault 后端（`OpenClaw.Security.Vault`，基于 VaultSharp）从 HashiCorp Vault 或 OpenBao 读取 KV v2 密钥，并以 TTL 缓存、单飞（single-flight）、提前刷新（refresh-ahead）方式管理。默认态势是故障关闭（fail-closed）：

- 除非设置 `OpenClaw:Security:Vault:Enabled=true`，否则 vault 禁用
- 解析后的 value 绝不出现在异常消息、日志、追踪或堆栈中
- token 通过既有的 `env:`/`raw:` 间接引用，绝不作为明文配置写入

## 引用语法

```
vault:<mount>/data/<path>#<key>
```

| 组件 | 含义 | 示例 |
|---|---|---|
| `<mount>` | KV v2 挂载点（省略时默认 `secret`） | `secret` |
| `<path>` | `data/` 之后的密钥路径 | `openclaw/openai` |
| `<key>` | 密钥内的字段名 | `api_key` |

示例：

- `vault:secret/data/openclaw/openai#api_key`
- `vault:openclaw/data/payments/stripe#sk_live`（自定义挂载点 `openclaw`）
- `vault:data/config#nested_key`（默认挂载点 `secret`）

引用可用于任何配置密钥值的位置：`env:`/`raw:` 与 `vault:` 引用是等价的配置值形式（频道凭据、LLM API key、插件配置……）。

## 配置

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

| 字段 | 默认值 | 含义 |
|---|---|---|
| `Enabled` | `false` | 启用 Vault 后端。禁用时，`vault:` 引用在解析链中被视为字面量字符串。 |
| `Address` | — | Vault 服务器 URL。启用时必填；必须是 HTTPS 且为合法 URI。 |
| `TokenRef` | — | Vault token 的密钥引用（`env:`/`raw:`）。启用时必填。**禁止**以 `vault:` 开头（递归防护）。 |
| `Namespace` | — | Vault Enterprise 命名空间（可选）。 |
| `KvMount` | `secret` | KV v2 挂载点，作为默认 `<mount>`。 |
| `KvVersion` | `2` | KV 版本。仅支持 `2`。 |
| `RequestTimeout` | 10s | 单次拉取的 HTTP 超时（1s–60s）。 |
| `CacheTtl` | 5min | 缓存 TTL（30s–24h）。 |
| `RateLimit.RequestsPerSecond` | `20` | 启动预热的并发上限（1–1000）。 |
| `PrewarmRequired` | `true` | 预热引用解析失败时阻止启动。`false` 则记录日志并继续。 |
| `PrewarmRefs` | `[]` | 启动时解析的引用。网关配置树中所有 `vault:` 值也会被自动扫描。 |
| `Tls.SkipVerify` | `false` | 接受任意服务器证书。仅限集成/开发环境。 |
| `Tls.CaCertPath` | — | 预留给自定义 CA 证书包（尚未接入客户端）。 |

配置读取自既有 `IConfiguration` 来源：`appsettings.json`、环境变量（`OpenClaw__Security__Vault__Address`）、命令行，以及经 `SecurityPostureBuilder` 加密的文件。不新增独立配置提供程序。

同一节由 `ConfigValidator` 校验：`Enabled=true` 时 `Address` 与 `TokenRef` 必填，`Address` 必须为 HTTPS，`CacheTtl`/`RequestTimeout`/`RequestsPerSecond` 有范围校验；当网关绑定公网（非回环）地址时，拒绝回环地址的 `Address`。

## 同步 vs 异步解析

`SecretResolver.Resolve`（同步）供既有调用点使用：

- `env:`/`raw:` 引用与以往一样经环境 provider 解析。
- `vault:` 引用在缓存命中时返回缓存值。
- `vault:` 引用在缓存未命中时快速失败，抛出 `SecretResolutionException`——同步路径永不阻塞等待 HTTP。冷缓存请使用 `ResolveAsync`（异步路径）或依赖启动预热。

`SecretResolver.ResolveAsync`（异步）在缓存未命中时总是拉取：每个 key 单飞（并发未命中只产生一次 HTTP 调用）、TTL 缓存、提前刷新（过期条目先返回旧值，后台刷新；刷新失败保留旧值并记 warning）。

## 启动预热

`VaultRefPrewarmService`（IHostedService）在网关开始服务前解析 `PrewarmRefs` 以及扫描网关配置树发现的所有 `vault:` 引用。失败会被收集：

- `PrewarmRequired=true`（默认）：启动失败（`HostingStartupException`）——故障关闭。
- `PrewarmRequired=false`：启动继续并输出警告；单个失败分别记录日志。

预热并发由 `RateLimit.RequestsPerSecond` 限制。

## TLS

- `Tls.SkipVerify=true` 接受任意服务器证书（仅限集成/开发环境；除非 `Security.AllowInsecureTls=true`，否则产生校验警告）。
- `Tls.CaCertPath` 预留给自定义 CA 证书包；尚未接入 Vault 客户端。

## Token 递归防护

`TokenRef` 不得以 `vault:` 开头——token 的解析绝不能依赖 vault 后端本身。`ConfigValidator` 会拒绝此类配置。

## 错误处理

| 场景 | 同步路径 | 异步路径 |
|---|---|---|
| `vault:` 缓存命中 | 返回 value | 返回 value |
| `vault:` 缓存未命中 | 抛 `SecretResolutionException` | 拉取 → 缓存 → 返回 |
| Vault 401/403 | 抛 `VaultAuthException` | 抛 `VaultAuthException` |
| Vault 5xx / 网络 / 超时 | 抛 `VaultUnavailableException` | 抛出；存在旧值时返回旧值 + warning |
| Vault path 级 404 | 抛 `VaultPathNotFoundException` | 抛 `VaultPathNotFoundException` |
| Vault key 级 404 | 抛 `VaultKeyNotFoundException` | 抛 `VaultKeyNotFoundException` |
| 引用格式错误 | 抛 `VaultRefParseException` | 抛 `VaultRefParseException` |
| Vault 未启用却使用 `vault:` 引用 | 视为字面量字符串 | 视为字面量字符串 |

异常消息仅包含 path、key、HTTP 状态码和错误类型名——绝不包含解析后的 value。日志输出另经 `RedactionPipeline` 二次防护。

## 本地集成测试

```bash
docker compose -f deploy/docker-compose/openbao.yml up -d
```

然后配置网关：

```jsonc
"OpenClaw": { "Security": { "Vault": {
  "Enabled": true,
  "Address": "http://127.0.0.1:8200",
  "TokenRef": "raw:root"
} } }
```

集成测试套件的运行方式见 `docs/zh-CN/security/vault-integration-tests.md`。

## 升级 / 回滚

- **升级**：启用后端（`Enabled=true`），先设 `PrewarmRequired=false` 观察解析失败日志而不阻塞启动，待引用解析稳定后再改为 `true`。
- **回滚**：设 `Enabled=false`；解析链对 `vault:` 引用回退为字面量处理，`env:`/`raw:` 行为不变。
- **轮换**：在 Vault 中轮换 value；缓存经 `CacheTtl` 过期后后台刷新（refresh-ahead），无需重启网关即可生效。

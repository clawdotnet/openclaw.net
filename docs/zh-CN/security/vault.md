# Vault / OpenBao 密钥解析

OpenClaw.NET 通过 `SecretResolver`（静态门面）解析密钥，其背后是可插拔的 `ISecretProvider` 链。Vault 后端（`OpenClaw.Security.Vault`，基于 VaultSharp）从 HashiCorp Vault 或 OpenBao 读取 KV v2 密钥，并以 TTL 缓存、单飞（single-flight）、提前刷新（refresh-ahead）方式管理。默认态势是故障关闭（fail-closed）：

- 除非设置 `OpenClaw:Security:Vault:Enabled=true`，否则 vault 禁用
- NativeAOT 网关构建不包含 Vault 后端（VaultSharp 不适配 trim），该构建中 `vault:` 引用故障关闭；JIT 构建包含后端
- 解析后的 value 绝不出现在异常消息、日志、追踪或堆栈中
- token 通过既有的 `env:`/`raw:` 间接引用，绝不作为明文配置写入

## 凭据覆盖范围与优先级

以下配置路径均相对于 `OpenClaw` 配置根节点。某个配置字段或 `*Ref` 属性存在，并不代表它支持 `vault:`；是否受保护取决于实际消费者是否调用 `SecretResolver`。以下清单依据当前解析调用点。

| 优先级 | 配置字段 | 当前覆盖状态与建议 |
|---|---|---|
| P0 | `OpenClaw:AuthToken` / `OpenClaw:AuthTokenRef` | 网关在 Vault 预热后解析 `AuthTokenRef`；显式引用优先于 `OPENCLAW_AUTH_TOKEN` 环境变量回退。公网绑定必须配置 `AuthTokenRef` 或 OIDC，并拒绝内嵌 `AuthToken`。 |
| P1 | `OpenClaw:Llm:ApiKey`、`OpenClaw:Models:Profiles[*]:ApiKey`、`OpenClaw:Multimodal:ElevenLabs:ApiKey`、图像/搜索工具消费者使用的 provider key，以及 `OpenClaw:DynamicTurnRouting:Jev:ApiKeyRef` | 现有消费者会解析这些值。生产环境应使用引用；请确认所选 provider/tool 确实消费对应字段。 |
| P1 | `OpenClaw:Channels:Telegram:BotTokenRef`、`WebhookSecretTokenRef`；`Slack:BotTokenRef`、`SigningSecretRef`；`Discord:BotTokenRef`、`PublicKeyRef`；`WhatsApp:WebhookVerifyTokenRef`、`WebhookAppSecretRef`、`CloudApiTokenRef`、`BridgeTokenRef`；`Teams:AppPasswordRef`；`Sms:Twilio:AuthTokenRef`；`Feishu:AppSecretRef`；`DingTalk:AppKeyRef`、`AppSecretRef`；`WeCom:BotSecretRef`、`CorpSecretRef` | 这些凭据引用由 channel/webhook 解析路径消费。对应的直接值仍属于旧式配置；只有配置了引用时才由 OpenBao 保护。Channel ID、App ID、Tenant ID、Robot Code、Account ID 和电话号码默认属于标识符，而非凭据。 |
| P1 | `OpenClaw:Plugins:Database:ConnectionString`、`Plugins:HomeAssistant:TokenRef`、`Plugins:Notion:ApiKeyRef`、`Plugins:Email:PasswordRef`、`Plugins:Mqtt:UsernameRef` / `PasswordRef`，以及 Nacos adapter 用户名/密码 | 现有工具与 adapter 消费者会调用 `SecretResolver`；在支持的字段中使用 `env:`/`vault:` 引用。 |
| P1 | `OpenClaw:Workflows:Backends[*]:ApiTokenSecret`、`Authentication:ClientSecretRef`；MCP 委派凭据 `ClientSecretRef` / `SigningKeyRef`；`OpenClaw:Webhooks:Endpoints[*]:Secret`；`GmailPubSub:WebhookSecretRef` | 这些消费路径会解析引用。OAuth client ID、audience、endpoint、scope 和 webhook 元数据不是秘密。 |
| P1 | `OpenClaw:Payments:StripeLink:EnvironmentVariables[*]` | 按环境变量名分类敏感凭据，并在启动 CLI 前于独立环境副本中解析其 `env:`/`vault:`（或显式允许的 `raw:`）引用；非敏感标识符保持不变。 |
| P1 | `OpenClaw:Channels:WhatsApp:FirstPartyWorker:Proxy` 和 `Accounts[*]:Proxy` | Worker 边界解析完整 URL 的 `env:`/`vault:`（或显式允许的 `raw:`）引用。URI userinfo 中嵌入的凭据视为内嵌凭据，公网绑定会拒绝。 |
| P2 | `OpenClaw:CodingBackends:*:SecretRef` 与 connected account 的 `SecretRef` | 现有凭据消费者会解析这些引用。Token 文件和受保护 blob 模式继续支持；OAuth 签发与刷新不在本设计范围。 |
| 缺口 | `OpenClaw:GmailPubSub:CredentialsPath` / `CredentialsPathRef` | 已声明路径，但没有 Gmail service-account 凭据加载器消费它。不得声称文件内容已由 OpenBao 保护。 |
| 迁移兼容 | 旧版 `admin-settings.json` 快照及设置/导出投影中的 WhatsApp 值 | 本地部署仍可加载旧快照，并只输出字段路径、不包含值的警告；设置写入前旧文件保持不变。新快照写入、设置/setup 响应、agent bundle/support 导出及审计记录均不包含 WhatsApp webhook、Cloud API、bridge 和代理的内嵌凭据；公网 hardening 会拒绝旧式内嵌凭据。 |
| 不在范围内 | 对话、记忆、附件、个人数据、WhatsApp 协议 session 文件、一次性支付秘密、TLS 终止私钥，以及应用/业务数据 | 本轮凭据改造不应将这些内容存入普通 OpenBao KV；如有需要，应采用独立的数据保护和密钥管理策略。 |

KV v2 路径使用 `secret/data/openclaw/<environment>/<service>#<key>`，例如 `vault:secret/data/openclaw/prod/gateway#auth_token` 或 `vault:secret/data/openclaw/prod/channels/slack#bot_token`。名称使用稳定的小写格式；路径或 key 名不得包含秘密值或个人数据。OpenBao token 应由外部 bootstrap 来源（例如 `env:VAULT_TOKEN`）提供，不得把 Vault token 存入同一 KV 后端。

公网绑定会拒绝内嵌凭据并接受 `env:`/`vault:` 引用。`raw:` 兼容开关（`OpenClaw:Security:AllowRawSecretRefsOnPublicBind=true`）默认关闭。本地部署在迁移期间继续兼容旧字面值。OpenBao 后端仅支持 JIT 发布；NativeAOT 发布不包含该后端。

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

引用可用于其消费者调用 `SecretResolver` 的配置项。尚未迁移到该解析器的密钥配置仍使用各自文档中说明的格式。

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
| `Enabled` | `false` | 启用 Vault 后端。禁用时，`vault:` 引用故障关闭：解析时抛 `VaultNotConfiguredException`，而不是回退为字面量字符串。 |
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
| `Tls.SkipVerify` | `false` | 接受任意服务器证书。仅限集成/开发环境；未设置全局 opt-in `Security.AllowInsecureTls=true` 时校验会拒绝。 |
| `Tls.CaCertPath` | — | 自定义 CA 证书包（单个 PEM 文件，或含 `.pem`/`.crt`/`.cer` 文件的目录）。加载的根证书作为自定义根信任，主机名校验保留。与 `SkipVerify` 互斥。 |

配置读取自既有 `IConfiguration` 来源：`appsettings.json`、环境变量（`OpenClaw__Security__Vault__Address`）、命令行，以及经 `SecurityPostureBuilder` 加密的文件。不新增独立配置提供程序。

同一节由 `ConfigValidator` 校验：`Enabled=true` 时 `Address` 与 `TokenRef` 必填，`Address` 必须为 HTTPS，`CacheTtl`/`RequestTimeout`/`RequestsPerSecond` 有范围校验；未设置全局 opt-in `Security.AllowInsecureTls` 时拒绝 `Tls.SkipVerify`；当网关绑定公网（非回环）地址时，拒绝回环地址的 `Address`。

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

- `Tls.SkipVerify=true` 接受任意服务器证书，仅适用于隔离的集成测试环境。校验会在未设置全局 opt-in `Security.AllowInsecureTls=true` 时拒绝它，该显式开关在生产环境也会禁用证书校验；隔离开发环境之外应保持两个选项为 false。
- `Tls.CaCertPath` 加载自定义 CA 证书包，作为 Vault TLS 校验的自定义根信任（`CustomRootTrust`）；主机名校验仍然生效。文件缺失或无效时启动失败。

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
| Vault 未启用却使用 `vault:` 引用 | 抛 `VaultNotConfiguredException` | 抛 `VaultNotConfiguredException` |

异常消息仅包含 path、key、HTTP 状态码和错误类型名——绝不包含解析后的 value。日志输出另经 `RedactionPipeline` 二次防护。

## 本地集成测试

```bash
docker compose -f deploy/docker-compose/openbao.yml up -d
```

直接解析器集成测试会绕过网关配置校验来构造 Vault 客户端，因此可以使用下面仅限回环地址的 HTTP 设置。不要将此配置块复制到网关配置中：启用 Vault 的网关要求 HTTPS 地址。生产或共享环境必须使用 HTTPS。

```bash
OPENBAO_ADDR=http://127.0.0.1:8200 OPENBAO_TOKEN=root \
  dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj \
  --filter "Category=Integration"
```

集成测试套件的运行方式见 `docs/zh-CN/security/vault-integration-tests.md`。

## 升级 / 回滚

- **升级**：启用后端（`Enabled=true`），先设 `PrewarmRequired=false` 观察解析失败日志而不阻塞启动，待引用解析稳定后再改为 `true`。
- **回滚**：设 `Enabled=false`；`vault:` 引用故障关闭（抛 `VaultNotConfiguredException`），`env:`/`raw:` 行为不变。
- **轮换**：在 Vault 中轮换 value；缓存经 `CacheTtl` 过期后后台刷新（refresh-ahead），无需重启网关即可生效。

Vault 预热会在运行时初始化之前执行。托管服务会每隔半个缓存 TTL 刷新一次已配置的引用，供同步使用者使用。上次成功获取的值最多可在两个 TTL 的时间内作为过期值提供；失败不会延长此期限。构造时已捕获凭据的现有客户端仍需重新创建，才能使用轮换后的值。

JIT 发布版本可使用 Vault（`-p:PublishAot=false`）。NativeAOT 发布版本不包含此集成，并会拒绝 `vault:` 引用。

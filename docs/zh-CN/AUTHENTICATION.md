# OpenClaw.NET 认证系统技术文档

## 概述

OpenClaw.NET Gateway 支持多层认证体系，涵盖静态令牌、OIDC/JWT Bearer、操作员账户令牌、浏览器会话等多种认证方式。本文档详细说明认证架构、配置选项、请求处理流程以及客户端集成方案。

---

## 一、配置模型

认证配置位于 `appsettings.json` 的 `OpenClaw.Security` 节点下。

### 1.1 SecurityConfig

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `AuthToken` | `string?` | `null` | 静态 Bootstrap 令牌。`null` 时禁用 Bootstrap 认证 |
| `AlwaysRequireAuth` | `bool` | `false` | `true` 时，即使是 loopback 绑定也需要认证 |
| `AllowViewerAgentExecution` | `bool` | `false` | **临时设置，将在下一个版本移除。** `true` 时，低于 `operator` 的身份仍可执行智能体（见 3.3 节）。每个此类请求都会记录日志，`admin posture` 也会报告该风险 |
| `AuthMode` | `string` | `"token"` | 认证模式：`"token"` 或 `"oidc"` |
| `AllowQueryStringToken` | `bool` | `false` | 是否允许从查询字符串 `?token=` 读取令牌 |
| `BrowserSessionIdleMinutes` | `int` | `60` | 浏览器会话空闲超时（分钟） |
| `BrowserRememberDays` | `int` | `30` | "记住我"会话有效期（天） |
| `Oidc` | `OidcConfig` | — | OIDC/JWT 配置 |

### 1.2 OidcConfig

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Authority` | `string?` | `null` | OIDC 签发者 URL（如 Keycloak Realm） |
| `Audience` | `string?` | `null` | 期望的 `aud` 声明值 |
| `RequireHttpsMetadata` | `bool` | `true` | OIDC 元数据发现是否要求 HTTPS |
| `RoleClaim` | `string` | `"roles"` | JWT 中提取操作员角色的声明名 |

### 1.3 配置示例

```json
{
  "OpenClaw": {
    "Security": {
      "AuthToken": null,
      "AlwaysRequireAuth": true,
      "AuthMode": "token",
      "AllowQueryStringToken": true,
      "BrowserSessionIdleMinutes": 60,
      "BrowserRememberDays": 30,
      "Oidc": {
        "Authority": "https://passport.ai4c.cn/realms/ai4c-saas",
        "Audience": "account",
        "RequireHttpsMetadata": true,
        "RoleClaim": "roles"
      }
    }
  }
}
```

---

## 二、认证方式

系统支持五种认证方式，按优先级从高到低排列：

### 2.1 Loopback 免认证

- **触发条件**：绑定地址为 loopback（`127.0.0.1` / `localhost`），且 `AlwaysRequireAuth` 为 `false`，且 `AuthMode` 不是 `"oidc"`
- **结果**：直接授权，角色为 `admin`，身份标识为 `"Loopback operator"`
- **适用场景**：本地开发和调试

### 2.2 OIDC / JWT Bearer

- **触发条件**：ASP.NET Core 认证中间件成功验证了 JWT 令牌，`ctx.User.Identity.IsAuthenticated == true`
- **中间件激活条件**：`AuthMode == "oidc"` 或 `Oidc.Authority` 非空
- **认证流程**：
  1. 客户端通过 OIDC 流程获取 JWT（如 Keycloak 登录页）
  2. 客户端在请求中携带 `Authorization: Bearer <jwt>` 头，或通过查询字符串 `?token=<jwt>` 传递
  3. 中间件（`Program.cs` 第 89-99 行）将 `?token=` 自动转换为 `Authorization: Bearer` 头
  4. `UseAuthentication()` 中间件验证 JWT 签名、签发者、受众、过期时间
  5. 验证通过后，`ctx.User` 被填充，`EndpointHelpers` 从中提取角色和身份信息
- **角色提取**：从 JWT 的 `RoleClaim` 对应声明中提取，默认为 `"roles"` 声明
- **身份信息**：`sub` → AccountId，`preferred_username` → Username，`name` → DisplayName

### 2.3 Bootstrap 令牌

- **触发条件**：`AuthToken` 已配置，请求携带的令牌与之匹配
- **验证方式**：恒定时间字符串比较（`CryptographicOperations.FixedTimeEquals`）
- **令牌提取顺序**：
  1. `Authorization: Bearer <token>` 请求头
  2. `?token=<token>` 查询字符串（需启用 `AllowQueryStringToken`）
- **结果**：角色为 `admin`，标记为 `IsBootstrapAdmin`

### 2.4 操作员账户令牌

- **触发条件**：请求令牌匹配 `OperatorAccountService` 中存储的操作员令牌
- **存储**：PBKDF2 哈希存储（120,000 次迭代），文件路径 `{storagePath}/admin/operator-accounts.json`
- **令牌格式**：`{12字符前缀}.{随机密钥}`，前缀用于 UI 显示，完整令牌仅在创建时返回一次
- **结果**：使用该账户配置的角色和身份信息

### 2.5 浏览器会话（Cookie）

- **触发条件**：请求携带有效的浏览器会话 Cookie
- **管理服务**：`BrowserSessionAuthService`
- **特性**：
  - 空闲超时：默认 60 分钟（可配置）
  - "记住我"：默认 30 天持久会话
  - CSRF 保护：API 端点要求 CSRF 令牌，WebSocket 端点豁免

---

## 三、请求处理流程

### 3.1 HTTP API 认证流程

HTTP API 端点使用 `AuthorizeOperatorRequest` 方法（[EndpointHelpers.cs](../src/OpenClaw.Gateway/Endpoints/EndpointHelpers.cs#L83)）：

```
请求进入
  │
  ├─ Loopback 且无需强制认证？ ── 是 ──→ 返回 loopback-open（admin 角色）
  │
  ├─ ctx.User 已认证（JWT）？ ── 是 ──→ 提取 JWT Claims，返回 oidc_jwt
  │
  ├─ 令牌匹配 AuthToken？ ── 是 ──→ 返回 bearer（Bootstrap admin）
  │
  ├─ 令牌匹配操作员账户？ ── 是 ──→ 返回 account_token
  │
  ├─ 浏览器会话有效？ ── 是 ──→ 返回 browser-session
  │
  └─ 全部失败 ──→ 返回 unauthorized（401）
```

### 3.2 WebSocket 认证流程

WebSocket 端点 (`/ws`, `/ws/live`) 在第一步完成认证，随后执行角色检查（第二步）；`/ws` 还会解析用户 ID（第三步）：

**第一步：`TryValidateWebSocketRequest` → `IsAuthorizedRequest`**

```
WebSocket 请求 (/ws)
  │
  ├─ 非 WebSocket 请求？ ── 是 ──→ 400 Bad Request
  │
  ├─ Origin 不允许？ ── 是 ──→ 403 Forbidden
  │
  ├─ IsAuthorizedRequest() → 与 API 相同的认证链
  │
  ├─ 超出速率限制？ ── 是 ──→ 429 Too Many Requests
  │
  └─ 通过 ──→ 接受 WebSocket 连接
```

**第二步：`EndpointHelpers.CanExecuteAgent`**

每个 `/ws` 帧都会成为智能体输入，因此连接需要与 `POST /api/integration/messages` 相同的 `operator` 角色。角色通过 `AuthorizeOperatorRequest` 解析，与 HTTP API 使用同一认证链：

```
WebSocket 已接受 (/ws)
  │
  ├─ 角色低于 operator，或身份不被组织策略允许？
  │     ── 是 ──→ 关闭 1008 (PolicyViolation) "Chat requires the operator role."
  │
  └─ 通过 ──→ 第三步
```

连接会先被接受再关闭，而不是在握手阶段返回 403。浏览器无法读取握手失败的状态码，而 Web Chat 会将关闭码 1008 视为授权失败并停止重连。

OIDC 身份同样适用。缺少所配置 `RoleClaim` 的 JWT 会解析为 `viewer`，无法聊天；请通过该声明为需要使用 Web Chat 的用户授予 `operator` 角色。

**第三步：`TryResolveAuthorizedUserIdForWebSocket`**

```
WebSocket 已连接
  │
  ├─ Loopback 绑定？ ── 是 ──→ userId = null，授权通过
  │
  ├─ ctx.User 已认证（JWT）？ ── 是 ──→ 提取 sub 声明作为 userId
  │
  └─ 调用 AuthorizeOperatorRequest() ──→ 提取 AccountId 作为 userId
```

### 3.3 智能体执行所需角色

`IsAuthorizedRequest` 只确认调用方已通过认证。会把请求变为智能体输入或其他变更的入口，还需要 `operator` 角色（与 `POST /api/integration/messages` 相同），由 `EndpointHelpers.CanExecuteAgent` 检查：

| 入口 | 角色低于 operator 时 |
|------|----------------------|
| `/ws` | 先接受，再以 1008 (PolicyViolation) 关闭 |
| `/ws/live` | 先接受，再以 1008 关闭。实时桥接不运行工具，但会消耗提供商凭据额度 |
| `POST /v1/chat/completions`、`POST /v1/responses` | 403，返回 OpenAI 风格的 `permission_error` 响应体 |
| A2A 执行路径（发现端点仍然公开） | 403 |
| `POST /apps/chat` | 403 |
| `/apps/mcp/{appId}` 的 `tools/call` | 返回工具错误结果；列出和读取 App 工具与资源仍可用 |
| MCP `openclaw.send_message`、`openclaw.run_workflow`、`openclaw.respond_workflow` | 返回工具错误结果；只读 MCP 工具对 viewer 仍可用 |

引导令牌和开放回环会解析为 `admin`，不受影响。新建的操作员账户默认为 `viewer`，因此用于 Companion、CLI/TUI 聊天或 API 客户端的账户需要 `operator` 角色。

每次拒绝都会在 `OpenClaw.Gateway.Authorization` 类别下记录一条警告日志，包含入口、认证方式、账户和角色（绝不包含凭据），便于管理员找出需要提升角色的账户。如需无中断迁移，可设置 `OpenClaw:Security:AllowViewerAgentExecution=true`，从日志中找出被放行的账户，为其授予 `operator` 角色，然后关闭该设置。该设置是临时的，将在下一个版本移除。

`GET /auth/session` 会以 `canExecuteAgent` 字段报告这项检查的结果（包括 `AllowViewerAgentExecution` 的影响），客户端可以在连接前说明拒绝原因。Companion 会使用该字段；早于此字段的网关不会返回它。

### 3.4 `IsAuthorizedRequest` 详细逻辑

```csharp
// 第 1 步：Loopback 豁免
if (!isNonLoopbackBind && !config.Security.AlwaysRequireAuth && !config.Security.IsOidcMode)
    return true;

// 第 2 步：JWT 认证（由 UseAuthentication() 中间件预先验证）
if (ctx.User.Identity?.IsAuthenticated == true)
    return true;

// 第 3 步：静态 AuthToken 匹配
if (!string.IsNullOrWhiteSpace(config.AuthToken))
{
    var token = GatewaySecurity.GetToken(ctx, config.Security.AllowQueryStringToken);
    if (GatewaySecurity.IsTokenValid(token, config.AuthToken))
        return true;
}

// 第 4 步：操作员账户令牌
if (IsAllowedAuthMode(policy, OrganizationAuthModeNames.AccountToken))
{
    var operatorAccounts = ctx.RequestServices.GetService<OperatorAccountService>();
    if (operatorAccounts?.TryAuthenticateToken(token, out _) == true)
        return true;
}

// 第 5 步：浏览器会话
if (IsAllowedAuthMode(policy, OrganizationAuthModeNames.BrowserSession))
{
    var browserSessions = ctx.RequestServices.GetService<BrowserSessionAuthService>();
    if (browserSessions?.TryAuthorize(ctx, requireCsrf: false, out _) == true)
        return true;
}

return false;  // 401 Unauthorized
```

---

### 3.5 轮次身份

`Session.AuthenticatedUserId` 是一个轮次运行时所用的身份。它划分按用户的能力绑定范围，并以 `_meta.userId` 传给 MCP 服务器。为空时，二者回退到会话的 `SenderId`。

运行轮次的入口会根据已登录账户设置它（`EndpointHelpers.ResolveAuthenticatedAccountId`），绝不采用调用方提供的发送者 ID：

| 入口 | 身份来源 |
|------|----------|
| `POST /api/integration/messages`、MCP `openclaw.send_message` | 请求的账户；请求体中的 `senderId` 只用于路由和显示 |
| `/ws` | 连接时解析出的账户 |
| `POST /v1/chat/completions`、`POST /v1/responses`、`POST /apps/chat` | 请求的账户 |
| A2A 执行 | 请求的账户；A2A 的 `contextId` 只作为发送者 ID |

开放回环和引导令牌调用方没有账户，因此它们的轮次不带账户身份运行。

来自外部发送者且不带账户的管道轮次同样不带账户身份运行，不会继承此前写入该会话的账户。例如，Telegram 用户的轮次绝不会以曾向该 Telegram 会话发消息的操作员身份运行。系统、定时、自动化和后台续跑轮次代表会话执行，保留会话原有身份。

### 3.6 会话归属

由已登录账户创建的会话会把该账户记录为 `Session.OwnerAccountId`。归属只在创建时设置一次，之后不会改变。

| 会话 | 谁可以向其发送消息 |
|------|--------------------|
| 有归属 | 所有者和管理员。其他账户在所有入口都会被拒绝：REST 返回 403，MCP 返回工具错误，`/apps/chat` 返回 403，`/v1/*` 稳定会话返回 403（错误码 `session_forbidden`），A2A 返回错误事件，管道轮次（包括 `/ws`）会收到“该会话属于另一个账户”的回复 |
| 无归属（由渠道、定时任务、引导令牌或回环调用方创建，或创建于引入归属之前） | 任何被允许发送消息的调用方。向无归属会话写入永远不会占有它 |

没有账户的调用方（引导令牌、开放回环、渠道和系统轮次）不受归属限制。它们等同于管理员，或者按各自的键访问会话。

同一规则也适用于会话管理：删除会话（`DELETE /admin/sessions/{id}`）、修改元数据、中止运行、恢复分支以及引导式恢复，对其他非管理员账户返回 403。将会话提升为自动化只读取会话，不受影响。

读取对所有能读取会话的角色保持开放，仪表盘和审计不受影响。

`GET /api/integration/sessions?owner=me` 只列出调用方自己的会话（活动和持久化的）。`SessionSummary.ownerAccountId` 给出所有者。没有账户的调用方不拥有任何会话。

`/v1/*` 稳定会话（`X-OpenClaw-Session-Id`）按 Bearer 令牌的哈希或（浏览器会话时）客户端地址派生，因此同一地址后的两个已登录账户可能得到同一个会话；归属检查将它们区分开。`/ws` 与其他入口使用相同的调用方解析，因此仍要求认证的回环绑定网关（`AlwaysRequireAuth` 或 OIDC）也会记录并执行归属。

## 四、中间件管道

认证相关的中间件在 `Program.cs` 中按以下顺序注册：

### 4.1 查询字符串令牌桥接

```csharp
// Program.cs 第 89-99 行
app.Use(async (ctx, next) =>
{
    if (startup.Config.Security.AllowQueryStringToken
        && ctx.Request.Path.StartsWithSegments("/ws", StringComparison.OrdinalIgnoreCase)
        && !ctx.Request.Headers.ContainsKey("Authorization"))
    {
        var queryToken = ctx.Request.Query["token"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(queryToken))
            ctx.Request.Headers.Authorization = $"Bearer {queryToken}";
    }
    await next(ctx);
});
```

**目的**：浏览器 WebSocket API 不支持自定义请求头。当 `AllowQueryStringToken` 启用时，前端可以通过 `?token=<jwt>` 传递令牌，此中间件将其转换为标准的 `Authorization: Bearer` 头，使 JWT 认证中间件能够正确处理。当 `AllowQueryStringToken` 为 `false`（默认值）时，查询字符串令牌将被拒绝，仅接受 `Authorization` 头。

### 4.2 认证中间件

```csharp
// Program.cs 第 104-107 行
if (startup.Config.Security.IsOidcMode
    || !string.IsNullOrWhiteSpace(startup.Config.Security.Oidc.Authority))
    app.UseAuthentication();
```

**注册条件**：`AuthMode == "oidc"` 或 `Oidc.Authority` 已配置。这使得即使 `AuthMode` 为 `"token"`，只要配置了 OIDC Authority，JWT 令牌也能被验证。

### 4.3 JWT Bearer 配置

```csharp
// SecurityServicesExtensions.cs 第 15-26 行
var hasOidcAuthority = !string.IsNullOrWhiteSpace(startup.Config.Security.Oidc.Authority);
if (startup.Config.Security.IsOidcMode || hasOidcAuthority)
{
    services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = startup.Config.Security.Oidc.Authority;
            options.Audience = startup.Config.Security.Oidc.Audience;
            options.RequireHttpsMetadata = startup.Config.Security.Oidc.RequireHttpsMetadata;
        });
    services.AddAuthorization();
}
```

---

## 五、令牌提取

`GatewaySecurity.GetToken()` 按以下优先级提取令牌：

1. **Authorization 头**：`Authorization: Bearer <token>`（`GetBearerToken`）
2. **查询字符串**：`?token=<token>`（仅当 `AllowQueryStringToken` 为 `true` 时）

```csharp
public static string? GetToken(HttpContext ctx, bool allowQueryStringToken)
{
    var token = GetBearerToken(ctx);
    if (!string.IsNullOrEmpty(token))
        return token;

    if (!allowQueryStringToken)
        return null;

    return ctx.Request.Query["token"].FirstOrDefault();
}
```

---

## 六、前端认证集成

Web Chat UI（`webchat.js`）支持两种客户端认证模式：

### 6.1 Token 模式

- 用户在设置面板输入静态令牌
- 令牌存储在 `localStorage`（"记住我"）或 `sessionStorage`
- WebSocket 连接时将令牌作为 `?token=` 参数传递

### 6.2 OIDC 模式

- 用户点击"OIDC 登录"按钮
- 浏览器重定向到 OIDC 提供者（如 Keycloak）完成登录
- 回调时通过 PKCE 流程换取 JWT
- JWT 存储在 `sessionStorage` 中
- 所有后续请求携带 JWT

### 6.3 认证状态反馈

前端通过 `GET /auth/session` 获取认证状态：

```javascript
const resp = await fetch('/auth/session', { method: 'GET', headers });
if (!resp.ok) {
    renderChatState({
        authMode: resp.status === 401 ? 'unauthorized' : 'unknown',
        // ...
    });
}
```

当 `authMode === 'unauthorized'` 时，前端显示认证横幅，提示用户输入令牌或登录。

---

## 七、关键文件索引

| 文件 | 说明 |
|------|------|
| [GatewayConfig.cs](../src/OpenClaw.Core/Models/GatewayConfig.cs) | 认证配置模型（`SecurityConfig`, `OidcConfig`） |
| [SecurityServicesExtensions.cs](../src/OpenClaw.Gateway/Composition/SecurityServicesExtensions.cs) | JWT Bearer 认证注册 |
| [Program.cs](../src/OpenClaw.Gateway/Program.cs) | 中间件管道配置 |
| [EndpointHelpers.cs](../src/OpenClaw.Gateway/Endpoints/EndpointHelpers.cs) | `IsAuthorizedRequest`, `AuthorizeOperatorRequest` |
| [WebSocketEndpoints.cs](../src/OpenClaw.Gateway/Endpoints/WebSocketEndpoints.cs) | WebSocket 认证与用户解析 |
| [GatewaySecurity.cs](../src/OpenClaw.Gateway/GatewaySecurity.cs) | 令牌提取与验证工具 |
| [BrowserSessionAuthService.cs](../src/OpenClaw.Gateway/BrowserSessionAuthService.cs) | 浏览器会话管理 |
| [OperatorAccountService.cs](../src/OpenClaw.Gateway/OperatorAccountService.cs) | 操作员账户与令牌管理 |
| [OrganizationPolicyService.cs](../src/OpenClaw.Gateway/OrganizationPolicyService.cs) | 认证方式白名单策略 |
| [webchat.js](../src/OpenClaw.Gateway/wwwroot/webchat.js) | 前端认证逻辑 |

---

## 八、常见配置场景

### 场景 1：本地开发（无认证）

```json
{
  "Security": {
    "AuthToken": null,
    "AlwaysRequireAuth": false,
    "AuthMode": "token"
  }
}
```

### 场景 2：Bootstrap 令牌认证

```json
{
  "Security": {
    "AuthToken": "my-secure-token",
    "AlwaysRequireAuth": true,
    "AuthMode": "token"
  }
}
```

### 场景 3：Keycloak OIDC 认证

```json
{
  "Security": {
    "AuthToken": null,
    "AlwaysRequireAuth": true,
    "AuthMode": "oidc",
    "Oidc": {
      "Authority": "https://passport.example.com/realms/my-realm",
      "Audience": "account",
      "RequireHttpsMetadata": true,
      "RoleClaim": "roles"
    }
  }
}
```

### 场景 4：混合模式（Token + JWT 共存）

```json
{
  "Security": {
    "AuthToken": "fallback-bootstrap-token",
    "AlwaysRequireAuth": true,
    "AuthMode": "token",
    "Oidc": {
      "Authority": "https://passport.example.com/realms/my-realm",
      "Audience": "account"
    }
  }
}
```

在此模式下，`AuthMode` 为 `"token"` 但 `Oidc.Authority` 已配置，JWT 认证中间件会自动激活。请求可以携带 JWT 令牌或静态 Bootstrap 令牌，系统按优先级依次验证。

---

## 九、安全考量

1. **恒定时间比较**：`AuthToken` 使用 `CryptographicOperations.FixedTimeEquals` 进行恒定时间比较，防止时序攻击
2. **PBKDF2 哈希**：操作员令牌使用 PBKDF2（120,000 次迭代）进行哈希存储，防止令牌泄露后的明文暴露
3. **CSRF 保护**：浏览器会话在 API 端点上要求 CSRF 令牌验证；WebSocket 端点在握手阶段浏览器会携带 Cookie，因此依赖严格的 `Origin` 头校验（参见第 6 条）来防止跨域攻击，而非基于 Cookie 的 CSRF 令牌
4. **JWT 验证**：由 ASP.NET Core JWT Bearer 中间件提供完整的签名、签发者、受众和过期时间验证
5. **速率限制**：所有认证端点均受速率限制保护，以 IP、操作员账户和浏览器会话为维度
6. **Origin 检查**：WebSocket 端点验证 `Origin` 头，防止跨域 WebSocket 攻击
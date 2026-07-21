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

WebSocket 端点 (`/ws`, `/ws/live`) 使用两步认证流程：

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

**第二步：`TryResolveAuthorizedUserIdForWebSocket`**

```
WebSocket 已连接
  │
  ├─ Loopback 绑定？ ── 是 ──→ userId = null，授权通过
  │
  ├─ ctx.User 已认证（JWT）？ ── 是 ──→ 提取 sub 声明作为 userId
  │
  └─ 调用 AuthorizeOperatorRequest() ──→ 提取 AccountId 作为 userId
```

### 3.3 `IsAuthorizedRequest` 详细逻辑

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
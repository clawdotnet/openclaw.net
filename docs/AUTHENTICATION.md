# OpenClaw.NET Authentication System — Technical Reference

## Overview

The OpenClaw.NET Gateway provides a multi-layered authentication system supporting static tokens, OIDC/JWT Bearer, operator account tokens, and browser sessions. This document covers the authentication architecture, configuration options, request processing flows, and client integration patterns.

---

## 1. Configuration Model

Authentication configuration lives under the `OpenClaw.Security` node in `appsettings.json`.

### 1.1 SecurityConfig

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `AuthToken` | `string?` | `null` | Static bootstrap token. When `null`, bootstrap auth is disabled |
| `AlwaysRequireAuth` | `bool` | `false` | When `true`, even loopback-bound requests must carry valid credentials |
| `AuthMode` | `string` | `"token"` | Authentication mode: `"token"` or `"oidc"` |
| `AllowQueryStringToken` | `bool` | `false` | Whether to accept tokens from the `?token=` query string parameter |
| `BrowserSessionIdleMinutes` | `int` | `60` | Idle timeout for browser admin sessions (minutes) |
| `BrowserRememberDays` | `int` | `30` | Lifetime for persistent "Remember me" browser sessions (days) |
| `Oidc` | `OidcConfig` | — | OIDC / JWT Bearer configuration |

### 1.2 OidcConfig

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Authority` | `string?` | `null` | OIDC issuer URL (e.g. Keycloak realm URL) |
| `Audience` | `string?` | `null` | Expected `aud` claim value. Leave empty to skip audience validation |
| `RequireHttpsMetadata` | `bool` | `true` | Whether to require HTTPS for OIDC metadata discovery |
| `RoleClaim` | `string` | `"roles"` | JWT claim name from which the operator role is extracted |

### 1.3 Configuration Example

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
        "RequireHttpsMetadata": false,
        "RoleClaim": "roles"
      }
    }
  }
}
```

---

## 2. Authentication Methods

The system supports five authentication methods, listed in priority order (highest first):

### 2.1 Loopback Bypass

- **Condition**: Bind address is loopback (`127.0.0.1` / `localhost`), `AlwaysRequireAuth` is `false`, and `AuthMode` is not `"oidc"`
- **Result**: Authorized immediately with role `admin`, identity `"Loopback operator"`
- **Use case**: Local development and debugging

### 2.2 OIDC / JWT Bearer

- **Condition**: ASP.NET Core authentication middleware successfully validated a JWT, populating `ctx.User.Identity.IsAuthenticated == true`
- **Middleware activation**: `AuthMode == "oidc"` **or** `Oidc.Authority` is configured (non-empty)
- **Flow**:
  1. Client obtains a JWT via OIDC flow (e.g. Keycloak login page)
  2. Client includes `Authorization: Bearer <jwt>` header, or passes `?token=<jwt>` in the query string
  3. The middleware bridge (`Program.cs` lines 89–99) copies `?token=` into the `Authorization: Bearer` header
  4. `UseAuthentication()` middleware validates the JWT signature, issuer, audience, and expiration
  5. On success, `ctx.User` is populated and `EndpointHelpers` extracts claims
- **Role extraction**: Reads the claim named by `RoleClaim` (default: `"roles"`) from the JWT
- **Identity mapping**: `sub` → AccountId, `preferred_username` → Username, `name` → DisplayName

### 2.3 Bootstrap Token

- **Condition**: `AuthToken` is configured and the request token matches it
- **Validation**: Constant-time string comparison via `CryptographicOperations.FixedTimeEquals`
- **Token extraction order**:
  1. `Authorization: Bearer <token>` header
  2. `?token=<token>` query string (requires `AllowQueryStringToken: true`)
- **Result**: Role `admin`, flagged `IsBootstrapAdmin`

### 2.4 Operator Account Token

- **Condition**: Request token matches a stored operator account token in `OperatorAccountService`
- **Storage**: PBKDF2 hashed (120,000 iterations), file at `{storagePath}/admin/operator-accounts.json`
- **Token format**: `{12-char prefix}.{random secret}` — the prefix is shown in the UI; the full token is returned only once at creation
- **Result**: Role and identity from the matched account

### 2.5 Browser Session (Cookie)

- **Condition**: Request carries a valid browser session cookie
- **Service**: `BrowserSessionAuthService`
- **Characteristics**:
  - Idle timeout: 60 minutes by default (configurable)
  - "Remember me": 30-day persistent sessions
  - CSRF protection: required for API endpoints, exempted for WebSocket endpoints

---

## 3. Request Processing Flows

### 3.1 HTTP API Authentication Flow

HTTP API endpoints use `AuthorizeOperatorRequest` ([EndpointHelpers.cs](../src/OpenClaw.Gateway/Endpoints/EndpointHelpers.cs#L83)):

```
Request enters
  │
  ├─ Loopback & auth not required? ── yes ──→ return loopback-open (admin role)
  │
  ├─ ctx.User authenticated (JWT)? ── yes ──→ extract JWT claims, return oidc_jwt
  │
  ├─ Token matches AuthToken? ── yes ──→ return bearer (Bootstrap admin)
  │
  ├─ Token matches operator account? ── yes ──→ return account_token
  │
  ├─ Browser session valid? ── yes ──→ return browser-session
  │
  └─ All failed ──→ return unauthorized (401)
```

### 3.2 WebSocket Authentication Flow

WebSocket endpoints (`/ws`, `/ws/live`) use a two-phase authentication flow:

**Phase 1: `TryValidateWebSocketRequest` → `IsAuthorizedRequest`**

```
WebSocket request (/ws)
  │
  ├─ Not a WebSocket request? ── yes ──→ 400 Bad Request
  │
  ├─ Origin not allowed? ── yes ──→ 403 Forbidden
  │
  ├─ IsAuthorizedRequest() → same auth chain as API
  │
  ├─ Rate limit exceeded? ── yes ──→ 429 Too Many Requests
  │
  └─ Passed ──→ Accept WebSocket connection
```

**Phase 2: `TryResolveAuthorizedUserIdForWebSocket`**

```
WebSocket connected
  │
  ├─ Loopback bind? ── yes ──→ userId = null, authorized
  │
  ├─ ctx.User authenticated (JWT)? ── yes ──→ extract sub claim as userId
  │
  └─ Call AuthorizeOperatorRequest() ──→ extract AccountId as userId
```

### 3.3 `IsAuthorizedRequest` — Detailed Logic

```csharp
// Step 1: Loopback exemption
if (!isNonLoopbackBind && !config.Security.AlwaysRequireAuth && !config.Security.IsOidcMode)
    return true;

// Step 2: JWT authentication (pre-validated by UseAuthentication() middleware)
if (ctx.User.Identity?.IsAuthenticated == true)
    return true;

// Step 3: Static AuthToken match
if (!string.IsNullOrWhiteSpace(config.AuthToken))
{
    var token = GatewaySecurity.GetToken(ctx, config.Security.AllowQueryStringToken);
    if (GatewaySecurity.IsTokenValid(token, config.AuthToken))
        return true;
}

// Step 4: Operator account token
if (IsAllowedAuthMode(policy, OrganizationAuthModeNames.AccountToken))
{
    var operatorAccounts = ctx.RequestServices.GetService<OperatorAccountService>();
    if (operatorAccounts?.TryAuthenticateToken(token, out _) == true)
        return true;
}

// Step 5: Browser session cookie
if (IsAllowedAuthMode(policy, OrganizationAuthModeNames.BrowserSession))
{
    var browserSessions = ctx.RequestServices.GetService<BrowserSessionAuthService>();
    if (browserSessions?.TryAuthorize(ctx, requireCsrf: false, out _) == true)
        return true;
}

return false;  // 401 Unauthorized
```

---

## 4. Middleware Pipeline

Authentication middleware is registered in `Program.cs` in the following order:

### 4.1 Query-String Token Bridge

```csharp
// Program.cs lines 89–99
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/ws", StringComparison.OrdinalIgnoreCase)
        && !ctx.Request.Headers.ContainsKey("Authorization"))
    {
        var queryToken = ctx.Request.Query["token"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(queryToken))
            ctx.Request.Headers.Authorization = $"Bearer {queryToken}";
    }
    await next(ctx);
});
```

**Purpose**: The browser WebSocket API does not support custom headers. The frontend passes tokens via `?token=<jwt>`. This middleware copies the query string token into the standard `Authorization: Bearer` header so the JWT authentication middleware can process it.

### 4.2 Authentication Middleware

```csharp
// Program.cs lines 104–107
if (startup.Config.Security.IsOidcMode
    || !string.IsNullOrWhiteSpace(startup.Config.Security.Oidc.Authority))
    app.UseAuthentication();
```

**Activation condition**: `AuthMode == "oidc"` **or** `Oidc.Authority` is configured. This means JWT tokens are validated even when `AuthMode` is `"token"`, as long as an OIDC Authority is set.

### 4.3 JWT Bearer Configuration

```csharp
// SecurityServicesExtensions.cs lines 15–26
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

## 5. Token Extraction

`GatewaySecurity.GetToken()` extracts tokens in the following order:

1. **Authorization header**: `Authorization: Bearer <token>` (via `GetBearerToken`)
2. **Query string**: `?token=<token>` (only when `AllowQueryStringToken` is `true`)

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

## 6. Frontend Authentication Integration

The Web Chat UI (`webchat.js`) supports two client-side authentication modes:

### 6.1 Token Mode

- User enters a static token in the settings drawer
- Token is stored in `localStorage` ("Remember me") or `sessionStorage`
- On WebSocket connect, the token is passed as `?token=` query parameter

### 6.2 OIDC Mode

- User clicks "OIDC Login" button
- Browser redirects to the OIDC provider (e.g. Keycloak)
- On callback, the PKCE flow exchanges the authorization code for a JWT
- The JWT is stored in `sessionStorage`
- All subsequent requests carry the JWT

### 6.3 Auth State Feedback

The frontend polls `GET /auth/session` to determine its auth state:

```javascript
const resp = await fetch('/auth/session', { method: 'GET', headers });
if (!resp.ok) {
    renderChatState({
        authMode: resp.status === 401 ? 'unauthorized' : 'unknown',
        // ...
    });
}
```

When `authMode === 'unauthorized'`, the frontend displays an auth banner prompting the user for a token or OIDC login.

---

## 7. Key File Index

| File | Purpose |
|------|---------|
| [GatewayConfig.cs](../src/OpenClaw.Core/Models/GatewayConfig.cs) | Auth configuration models (`SecurityConfig`, `OidcConfig`) |
| [SecurityServicesExtensions.cs](../src/OpenClaw.Gateway/Composition/SecurityServicesExtensions.cs) | JWT Bearer authentication registration |
| [Program.cs](../src/OpenClaw.Gateway/Program.cs) | Middleware pipeline configuration |
| [EndpointHelpers.cs](../src/OpenClaw.Gateway/Endpoints/EndpointHelpers.cs) | `IsAuthorizedRequest`, `AuthorizeOperatorRequest` |
| [WebSocketEndpoints.cs](../src/OpenClaw.Gateway/Endpoints/WebSocketEndpoints.cs) | WebSocket auth and user resolution |
| [GatewaySecurity.cs](../src/OpenClaw.Gateway/GatewaySecurity.cs) | Token extraction and validation utilities |
| [BrowserSessionAuthService.cs](../src/OpenClaw.Gateway/BrowserSessionAuthService.cs) | Browser session lifecycle management |
| [OperatorAccountService.cs](../src/OpenClaw.Gateway/OperatorAccountService.cs) | Operator accounts and token management |
| [OrganizationPolicyService.cs](../src/OpenClaw.Gateway/OrganizationPolicyService.cs) | Auth mode allowlist policy |
| [webchat.js](../src/OpenClaw.Gateway/wwwroot/webchat.js) | Frontend auth logic |

---

## 8. Common Configuration Scenarios

### Scenario 1: Local Development (No Auth)

```json
{
  "Security": {
    "AuthToken": null,
    "AlwaysRequireAuth": false,
    "AuthMode": "token"
  }
}
```

### Scenario 2: Bootstrap Token Auth

```json
{
  "Security": {
    "AuthToken": "my-secure-token",
    "AlwaysRequireAuth": true,
    "AuthMode": "token"
  }
}
```

### Scenario 3: Keycloak OIDC Auth

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

### Scenario 4: Hybrid Mode (Token + JWT)

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

In this mode, `AuthMode` is `"token"` but `Oidc.Authority` is set, so the JWT authentication middleware is activated automatically. Requests may carry either a JWT or the static bootstrap token; the system validates against each method in priority order.

---

## 9. Security Considerations

1. **Constant-time comparison**: `AuthToken` validation uses `CryptographicOperations.FixedTimeEquals` to prevent timing side-channel attacks
2. **PBKDF2 hashing**: Operator account tokens are stored as PBKDF2 hashes (120,000 iterations), protecting against plaintext exposure if the storage file is compromised
3. **CSRF protection**: Browser sessions require CSRF token validation on API endpoints; WebSocket endpoints are exempt because cookies are not automatically attached to WebSocket upgrades
4. **JWT validation**: Full signature, issuer, audience, and expiration validation is provided by the ASP.NET Core JWT Bearer middleware
5. **Rate limiting**: All authenticated endpoints are subject to rate limiting, keyed by IP, operator account, and browser session
6. **Origin checking**: WebSocket endpoints validate the `Origin` header to prevent cross-site WebSocket hijacking
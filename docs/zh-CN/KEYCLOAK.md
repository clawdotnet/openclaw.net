# Keycloak 接入指南（OpenClaw.NET 作为 OIDC 客户端）

OpenClaw.NET 自带 OIDC/JWT Bearer 支持，接 Keycloak **不需要改代码**，只需配置 +
在 Keycloak 侧产出正确的 claim。本文记录实测通过的完整路径，以及三个必然踩到的坑。

配套的认证架构总览见 [认证系统技术文档](AUTHENTICATION.md)。

**可直接运行的示例**：[`deploy/keycloak/`](../../deploy/keycloak/) —— Keycloak 编排、
realm 导入文件、三个种子用户、一键启动与端到端验证脚本。

```bash
cd deploy/keycloak
./scripts/up.sh        # 起 Keycloak、导 realm、修补导入缺口
./scripts/verify.sh    # 验证 issuer / JWKS / claim / 角色
```

本文其余部分解释这些配置**为什么**必须是这样。

---

## 1. 最小可用配置

```jsonc
{
  "OpenClaw": {
    "Security": {
      "AuthMode": "oidc",
      "AlwaysRequireAuth": true,
      "Oidc": {
        "Authority": "https://kc.example.com/realms/openclaw",
        "Audience": "openclaw",
        "RequireHttpsMetadata": true,
        "RoleClaim": "operator_role"
      }
    }
  }
}
```

或用环境变量（容器部署更方便）：

```bash
OpenClaw__Security__AuthMode=oidc
OpenClaw__Security__AlwaysRequireAuth=true
OpenClaw__Security__Oidc__Authority=https://kc.example.com/realms/openclaw
OpenClaw__Security__Oidc__Audience=openclaw
OpenClaw__Security__Oidc__RequireHttpsMetadata=true
OpenClaw__Security__Oidc__RoleClaim=operator_role
```

本地 HTTP 自测才把 `RequireHttpsMetadata` 设为 `false`；**生产必须 `true`**。

---

## 2. Keycloak 侧要配置什么

### 2.1 Client

| 项 | 值 |
|---|---|
| Client type | OpenID Connect |
| Client ID | `openclaw`（或任意） |
| Client authentication | 浏览器登录 → Public + PKCE；后端调用 → Confidential |
| Standard flow | 开 |
| Valid redirect URIs | `https://<openclaw-host>/*` |

### 2.2 必须产出的三个 claim

| claim | 来源 | 缺失后果 |
|---|---|---|
| `sub` | Keycloak 内置 | `AccountId` 为空，审计无法归属 |
| `preferred_username` | Keycloak 内置 | 显示名退化成 `sub` |
| **`operator_role`** | **需自己加** | **恒为 viewer，连聊天都进不去** |

前两个 Keycloak 默认就有。第三个是本文的重点 —— 见下一节。

### 2.3 多角色：现在会取最高档，但仍建议单值 claim

**已修复**：`EndpointHelpers` 过去用 `FindFirstValue(RoleClaim)`，只读**第一个** claim。
Keycloak 的 `realm_roles` 是 JSON 数组，落到 `ClaimsPrincipal` 会变成一个值一条 claim：

```
realm_roles claims = [viewer, admin], FindFirstValue = viewer
                                          ↑ 只拿到 viewer，admin 丢了
```

而 `Normalize` 对未知值返回 `viewer`，所以一个 admin 会被静默降级成 viewer。

现在改为读取**全部** claim 交给 `OperatorRoleNames.FromRoles`，取最高档位，
顺序无关：

```
realm_roles claims = [viewer, admin] → FromRoles → admin
realm_roles claims = [admin, viewer] → FromRoles → admin   ← 与顺序无关
```

四条行为边界（都由 `OidcRoleClaimResolutionTests` 钉住）：

| 输入 | 结果 | 理由 |
|---|---|---|
| `[viewer, admin]` | `admin` | 取最高 |
| `[app_user, some_role]` | `viewer` | 未知名忽略；授权不能因未知值放开 |
| `[app_user, admin]` | `admin` | 未知名不干扰已知角色 |
| `[]` / `null` | `viewer` | 缺 claim 时 fail closed |

**仍未改变的一点**：角色值必须是 `viewer` / `operator` / `admin` 三者之一。
`app_admin` 这种名字**不会被猜成** `admin` —— 模糊匹配会误伤
`app_administrator`、`not_admin` 之类的名字。所以在 Keycloak 侧仍需把角色
映射成这三个值。

**推荐做法**（比依赖数组更可控）：用一个单值的 user attribute 承载角色。

Client scope → Mappers → 新增：

| 项 | 值 |
|---|---|
| Mapper type | User Attribute |
| User Attribute | `operator_role` |
| Token Claim Name | `operator_role` |
| Claim JSON Type | String |
| Add to access token | 开 |

这样 `RoleClaim=operator_role` 读到的就是唯一确定的值，不依赖 claim 顺序。
`deploy/keycloak/realm/openclaw-realm.json` 已按此配置。

### 2.4 ⚠️ Keycloak 26 会静默丢弃未声明的属性

这是最难查的一个。Keycloak 26 默认 `unmanagedAttributePolicy=None`，此时
通过 Admin API 写入一个没在 User Profile 里声明的 attribute：

```
PUT /admin/realms/{realm}/users/{id}   →  204 No Content   ← 看起来成功
GET /admin/realms/{realm}/users/{id}   →  "attributes": null  ← 实际没存
```

token 里自然也没有该 claim，OpenClaw 侧恒为 viewer，**两边日志都没有任何报错**。

`operator_role` 会被复制到 access token 并决定授权结果。绝不能让终端用户编辑它，
否则用户可以自行提升权限。

两种安全解法，选一个：

**A. 声明该属性并仅允许管理员编辑（首选，严格）**

```bash
# 用 realm 的 user profile 配置声明 operator_role，并将 edit 权限设为 ["admin"]，
# 不要设为 ["user"]。
curl -X PUT "$KC/admin/realms/$REALM/users/profile" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  --data-binary @profile-with-operator-role.json
```

**B. 允许管理员编辑未声明属性**

```bash
cd deploy/keycloak && ./scripts/allow-unmanaged-attributes.sh
```

脚本把 `unmanagedAttributePolicy` 设为 `ADMIN_EDIT`，并复核所有用户的
`operator_role` 是否真的存下来了。与 `ENABLED` 不同，`ADMIN_EDIT` 不允许终端用户
编辑未声明属性。脚本幂等，可重复执行。

注意字段名：Keycloak 的 user profile 配置里是 `validations`（复数），写成
`validation` 会得到 `Unrecognized field` 的 400。

---

## 3. Audience：配置并校验 `aud`

Keycloak 默认不会为 client 产出 `aud`，除非配置 audience mapper。本示例 realm 为
`openclaw`（浏览器客户端）和 `openclaw-verify`（仅供本地验证脚本使用的
password-grant 客户端）都配置了 mapper，发出 `aud=openclaw`。因此 OpenClaw 应配置：

```text
OpenClaw__Security__Oidc__Audience=openclaw
```

`openclaw-verify` 仅用于本地验证，生产环境不要保留或使用这个 password-grant client。
其他 client 需配置 mapper 发出 `openclaw`，或将 OpenClaw audience 设置为对应值。

留空 `OidcConfig.Audience` 仍可显式跳过校验。历史版本里留空会直接失败：

```
SecurityTokenInvalidAudienceException: IDX10208:
Unable to validate audience. validationParameters.ValidAudience is null or whitespace
```

原因是对 `JwtBearerOptions.Audience` 赋 null 会让验证器强制校验 audience。
该问题已修复（留空时显式 `ValidateAudience = false`）。

如果确实要校验 audience，需要在 Keycloak 的 client 上加 **Audience mapper**，
把 `aud` 写成固定值，然后在 `Oidc.Audience` 里填同一个值。

---

## 4. 角色映射验证

配好后用真实 token 验证，不要只看到「登录成功」就认为通了。

```bash
# 1) 取 token（用开了 direct access grants 的 client，或走浏览器登录后从 cookie 拿）
TOKEN=$(curl -s -X POST "$KC/realms/$REALM/protocol/openid-connect/token" \
  --data-urlencode grant_type=password --data-urlencode client_id=openclaw-verify \
  --data-urlencode username=demo --data-urlencode password=*** --data-urlencode scope=openid \
  | python3 -c 'import json,sys;print(json.load(sys.stdin)["access_token"])')

# 2) 确认 claim 真的在
python3 -c "
import base64,json
t='$TOKEN'; seg=t.split('.')[1]; seg+='='*(-len(seg)%4)
p=json.loads(base64.urlsafe_b64decode(seg))
print('operator_role =', p.get('operator_role'))
print('sub =', p.get('sub'))
"

# 3) 确认角色真的影响权限（用 admin 专属端点）
curl -s -o /dev/null -w '%{http_code}\n' -X POST \
  -H "Authorization: Bearer $TOKEN" \
  "http://127.0.0.1:18789/tools/approve?approvalId=x&approved=true"
# 期望：admin → 404（业务错误，权限已过）；operator → 403 requires role 'admin'
# 如果 RequireRequesterMatchForHttpToolApproval=true 且未提供 requester identity，
# 端点会在角色探测完成前返回 400。
```

实测结果（`operator_role` 从 operator 改成 admin，同一个用户）：

| operator_role | HTTP | 响应 |
|---|---|---|
| `admin` | 404 | `No pending approval found for that id.` |
| `operator` | 403 | `Endpoint 'admin.approvals.override' requires role 'admin'.` |

当 `RequireRequesterMatchForHttpToolApproval=true` 时，这些探测未提供 requester identity，
端点会在角色专属的审批处理前返回 400。验证脚本会将结果报告为无法判定并以状态码 2
退出，因为此模式下无法比较角色。

### 4.1 多角色不降级的真机验证

`deploy/keycloak/scripts/verify-multi-role.sh` 用管理 API 给用户动态挂上
`viewer` + `admin` 两个 realm role，构造真实的多值 claim：

```bash
cd deploy/keycloak
./scripts/verify-multi-role.sh http://localhost:18789   # 需网关以 RoleClaim=realm_roles 启动
```

实测输出：

```
realm_roles = [viewer, admin, openclaw_admin]
老逻辑（FindFirstValue 取首个）→ viewer     ← 被静默降级
新逻辑（FromRoles 取最高）    → admin
```

同一份 token 打真实网关，`/tools/approve` 的响应：

| 代码版本 | 角色解析 | HTTP | 含义 |
|---|---|---|---|
| `FromRoles`（当前） | `admin` | 404 | 通过权限判定 |
| `FindFirstValue`（变异） | `viewer` | **403** | `requires role 'operator'` |
| 只有 viewer 的用户 | `viewer` | 403 | 反向对照，证明 404 不是校验被跳过 |

最后一行是关键：没有它，404 也可能只是权限校验整体失效。

---

## 5. 其它必须知道的差异

- **`supabase.auth.*` 那套 API 不适用**：OpenClaw.NET 没有 Supabase SDK 依赖。
  内置 Web Chat 使用带 PKCE 的 OIDC authorization-code 流程，后端只验证取得的 token。
- **WebSocket 需要 `AllowQueryStringToken`**：浏览器 WebSocket 不能带自定义头。
  开了之后可以 `wss://host/ws?token=<jwt>`；生产上要评估 URL 泄漏风险。
- **登出不吊销 token**：Keycloak 删用户或登出后，已签发的 token 在 `exp` 前仍然有效
  （`accessTokenLifespan` 默认 300 秒）。要立刻生效需调短该值或加状态校验。
- **`AuthMode=oidc` 但 `Authority` 为空会 fail fast**：报
  `Security.AuthMode is "oidc" but Security.Oidc.Authority is empty`。
  这是修复前会崩溃在中间件管线里的问题（`UnableToResolveService`）。

---

## 6. 相关代码位置

| 关注点 | 文件 |
|---|---|
| 认证服务注册 | `src/OpenClaw.Gateway/Composition/SecurityServicesExtensions.cs` |
| 中间件启用 | `src/OpenClaw.Gateway/Program.cs`（`UseAuthentication`） |
| claim → 身份映射 | `src/OpenClaw.Gateway/Endpoints/EndpointHelpers.cs` |
| 配置模型 | `src/OpenClaw.Core/Models/GatewayConfig.cs`（`OidcConfig`） |
| 角色语义 | `src/OpenClaw.Core/Models/OperatorGovernanceModels.cs` |
| 回归判据 | `src/OpenClaw.Tests/OidcServiceRegistrationTests.cs`、`OidcRoleClaimResolutionTests.cs` |

---

English version: [docs/KEYCLOAK.md](../KEYCLOAK.md)

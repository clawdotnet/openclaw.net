# deploy/keycloak/

Keycloak 作为 openclaw.net 的**用户与角色唯一来源**。可运行的参考实现，不是生产模板。

```bash
cd deploy/keycloak
./scripts/up.sh                    # 生成 .env → 起 Keycloak → 等就绪 → 修补 realm
./scripts/verify.sh                # 验证 issuer / JWKS / claim
```

使用本节下面的 OIDC 配置先启动网关（初始 `RoleClaim=operator_role`），再验证通常
角色映射：

```bash
OpenClaw__Security__AuthMode=oidc \
OpenClaw__Security__AlwaysRequireAuth=true \
OpenClaw__Security__Oidc__Authority=http://localhost:8081/realms/openclaw \
OpenClaw__Security__Oidc__Audience=openclaw \
OpenClaw__Security__Oidc__RequireHttpsMetadata=false \
OpenClaw__Security__Oidc__RoleClaim=operator_role \
OpenClaw__Security__AllowQueryStringToken=true \
dotnet run --project ../../src/OpenClaw.Gateway
```

另开终端，从仓库根目录进入 `deploy/keycloak` 后运行常规验证，然后停止网关：

```bash
cd deploy/keycloak
./scripts/verify.sh http://localhost:18789
```

重启网关进行多角色验证。这里保留完整 OIDC 配置，只将 `RoleClaim` 改为
`realm_roles`：

```bash
OpenClaw__Security__AuthMode=oidc \
OpenClaw__Security__AlwaysRequireAuth=true \
OpenClaw__Security__Oidc__Authority=http://localhost:8081/realms/openclaw \
OpenClaw__Security__Oidc__Audience=openclaw \
OpenClaw__Security__Oidc__RequireHttpsMetadata=false \
OpenClaw__Security__Oidc__RoleClaim=realm_roles \
OpenClaw__Security__AllowQueryStringToken=true \
dotnet run --project ../../src/OpenClaw.Gateway
```

网关启动后，在另一个终端从仓库根目录进入 `deploy/keycloak`，再运行多角色验证。
脚本会尝试撤销本次新增的角色映射。如果 Keycloak 的 DELETE 请求失败，映射仍会保留，
脚本会保留 `cleanup.json` 并打印 realm、用户和手动恢复步骤。按提示完成恢复并确认映射
已移除后，再重启网关：

```bash
cd deploy/keycloak
./scripts/verify-multi-role.sh http://localhost:18789   # 多角色不降级（真机）
```

验证完成并确认角色映射已清理后，停止网关并使用上面的常规启动命令重启
（`RoleClaim=operator_role`），恢复种子管理员的正常角色解析。

控制台 <http://localhost:8081>，管理员账号在 `deploy/keycloak/.env`。

## 目录

| 文件 | 作用 |
|---|---|
| `docker-compose.yml` | Keycloak 26 + 它自己的 Postgres |
| `docker-compose.openclaw.yml` | 可选：把 openclaw 也放进容器（**仅 Linux**，见下文） |
| `realm/openclaw-realm.json` | realm 定义：3 个用户、3 个角色、claim mappers |
| `scripts/init-env.sh` | 生成 `.env`（随机口令） |
| `scripts/up.sh` | 一键启动 |
| `scripts/verify.sh` | 端到端验证 |
| `scripts/verify-multi-role.sh` | 真机验证多角色不被降级（动态授予多个 realm role）|
| `scripts/allow-unmanaged-attributes.sh` | 修补 realm 导入后必须做的一步 |
| `scripts/wait-keycloak.sh` | 探 `/health/ready`（在 9000，不在 8081） |

## 种子数据

| 用户名 | `operator_role` | realm role |
|---|---|---|
| `demo-viewer` | `viewer` | `openclaw_viewer` |
| `demo-operator` | `operator` | `openclaw_operator` |
| `demo-admin` | `admin` | `openclaw_admin` |

初始口令写在 realm 文件里（`change-me-*`），**上线前必须改**。

## openclaw 侧配置

```bash
OpenClaw__Security__AuthMode=oidc
OpenClaw__Security__AlwaysRequireAuth=true
OpenClaw__Security__Oidc__Authority=http://localhost:8081/realms/openclaw
OpenClaw__Security__Oidc__Audience=openclaw
OpenClaw__Security__Oidc__RequireHttpsMetadata=false
OpenClaw__Security__Oidc__RoleClaim=operator_role
OpenClaw__Security__AllowQueryStringToken=true
```

本示例为 `openclaw` 客户端配置了 `aud=openclaw`，所以此处应设为 `openclaw`。
`openclaw-verify` 是仅供本地脚本使用的 password-grant 客户端，也带该 audience mapper
以支持本地网关探测；不要在生产中保留或使用它。

## 为什么 openclaw 不默认放在这个 compose 里

OIDC 有一条硬约束：

```
openclaw 的 Oidc.Authority  ≡  token 里的 iss  ≡  浏览器访问地址
```

三者必须**逐字相同**，否则 JwtBearer 以 issuer 不匹配拒掉所有 token。而容器里的
`localhost` 指向容器自己，解析不到宿主机端口。实测过这些组合都不成立：

| 尝试 | 结果 |
|---|---|
| `--hostname=http://keycloak:8080` | 浏览器解析不到 `keycloak`，登录跳转失败 |
| `extra_hosts` 覆盖 `localhost` | `/etc/hosts` 里 `127.0.0.1 localhost` 优先级更高，覆盖无效 |
| `--hostname=keycloak`（只给 host）| issuer 变 `keycloak:8081`，浏览器仍失败 |
| `--hostname` + `backchannel-dynamic` | iss 正确、jwks 动态，但 openclaw 容器内 `localhost` 指向自己 |
| `network_mode: host` | 可用，但 Docker Desktop 上容器与外层 VM 共享栈，不可靠 |

所以默认形态与本站 `deploy/docker-compose/openbao.yml` 一致：**工具容器在 compose 里，
openclaw 跑在宿主机**。此时三处天然都是 `http://localhost:8081`。

Linux 上确实想全容器化，加 `docker-compose.openclaw.yml` 覆盖文件（走 host 网络）。

## 三个必踩的坑

### 1. 多角色已支持取最高档，但仍建议单值 claim

**曾经的问题**：`EndpointHelpers` 用 `FindFirstValue(RoleClaim)` 只读第一个 claim。
Keycloak 的 `realm_roles` 是 JSON 数组，会被拆成多条 claim：

```
realm_roles = [openclaw_viewer, openclaw_admin]  →  FindFirstValue = openclaw_viewer
                                                 →  Normalize(未知值) = viewer
```

一个 admin 就这样被静默降级成 viewer。

**现在**：读取全部 claim 交给 `OperatorRoleNames.FromRoles`，取最高档位、顺序无关。
`[viewer, admin]` 和 `[admin, viewer]` 都得 `admin`。

**但角色值仍必须是** `viewer` / `operator` / `admin`。`openclaw_admin` 这种名字不会
被猜成 `admin` —— 模糊匹配会误伤 `app_administrator` 之类。所以 realm 里仍用独立的
单值 user attribute `operator_role` 承载角色，`realm_roles` 只作参考
（`openclaw_roles_hint`）。单值 claim 的好处是结果唯一确定，不依赖 token 里数组的顺序。

**真机验证**（`./scripts/verify-multi-role.sh`）：脚本用管理 API 给用户动态挂上
`viewer` + `admin` 两个 realm role，取回真实 token：

```
realm_roles = [viewer, admin, openclaw_admin]
老逻辑（FindFirstValue 取首个）→ viewer   ← 被静默降级
新逻辑（FromRoles 取最高）    → admin
```

同一份 token 打真实网关：修复后 `/tools/approve` 得到 404（通过权限判定），
把实现改回 `FindFirstValue` 后变成 **403 `requires role 'operator'`** ——
真机复现了这个 bug，并证明修复有效。反向对照（只有 viewer 的用户）始终 403，
说明 404 确实来自 admin 角色而非权限校验被跳过。

注意：验证退出时会撤销本次新增的 `viewer`/`admin` 角色映射，并保留原有授权。
若脚本自行创建了同名 realm role，角色定义仍会保留。要彻底重置测试 realm：

```bash
docker compose down -v && ./scripts/up.sh
```

### 2. Keycloak 26 会静默丢弃未声明的 attribute

默认 `unmanagedAttributePolicy=None`。此时通过 Admin API 写一个没在 User Profile
里声明的 attribute：

```
PUT /admin/realms/{realm}/users/{id}   →  204   ← 看起来成功
GET .../users/{id}                     →  attributes: null  ← 实际没存
```

token 里自然没有该 claim，openclaw 侧恒为 viewer，**两边日志都没有任何报错**。

不要设为 `ENABLED`：它允许终端用户编辑未声明属性，而 `operator_role` 会进入 access token，
用户便可能自行提升角色。首选在 User Profile 中将 `operator_role` 声明为仅管理员可编辑的
managed attribute；本示例则将策略设为 `ADMIN_EDIT`，仅允许管理员编辑未声明属性。
`up.sh` 会自动应用并核对该策略。脚本幂等。

### 3. `/health/ready` 在 9000，不在 8081

`--health-enabled=true` 只让**管理端口**多出 `/health` 系列路由。在 8081 探
`/health/ready` 永远 404 —— 很容易被误读成"Keycloak 没起来"或"realm 导入失败"。

另外 Keycloak 26 基于 ubi-micro，镜像里没有 curl/wget/bash，**不能写容器内
healthcheck**，只能用 `wait-keycloak.sh` 从宿主机探。

## realm 导入的已知限制

`--import-realm` 在 realm 已存在时会跳过，不会覆盖线上改过的配置。要重新导入：

```bash
docker compose down -v      # 会删掉 Keycloak 数据库
./scripts/up.sh
```

**注意**：`down -v` 重建 Keycloak 会重新生成 RSA 签名密钥。openclaw 启动时从
`Authority` 拉一次 JWKS 并缓存（默认 5 分钟自动刷新），在这之前所有 token 都会
401。重建 Keycloak 后重启 openclaw，或等缓存过期：

```bash
# 症状：verify.sh 里 admin/operator 都返回 401（不是 403），
#       openclaw 日志出现 "IDX10500: Signature validation failed" 或 kid 不匹配
```

`unmanagedAttributePolicy` 无法通过 realm JSON 表达（Keycloak 把它放在 user profile
配置里，不在 realm 表示中），所以必须靠 `allow-unmanaged-attributes.sh` 补。

## 上线前必改

- 三个种子用户的口令
- `KEYCLOAK_PUBLIC_URL` / `Authority` 改成真实域名，且**必须 HTTPS**
  （非 localhost 的 http 会被 Keycloak 拒绝）
- `RequireHttpsMetadata` 改回 `true`
- 删掉 `openclaw-verify` client（它开了 password grant，只为验证脚本存在）
- `AllowQueryStringToken` 评估：token 会进 URL，可能落到访问日志与 Referer

更多背景见 [`docs/zh-CN/KEYCLOAK.md`](../../docs/zh-CN/KEYCLOAK.md)
（English: [`docs/KEYCLOAK.md`](../../docs/KEYCLOAK.md)）。

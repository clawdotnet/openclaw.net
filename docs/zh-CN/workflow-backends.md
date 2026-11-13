# 工作流后端

OpenClaw.NET 可以正常运行 Agent 回合，并将长时间运行的工作委派给配置的工作流后端。工作流委派用于持久计划、审批关卡、扇出/扇入审查和面向审计的企业自动化。它不是为每个快速 Agent 回合使用的。

## 配置

工作流后端位于 `OpenClaw:Workflows` 下：

```json
{
  "OpenClaw": {
    "Workflows": {
      "Enabled": true,
      "Backends": {
        "durable-review": {
          "Kind": "maf-durable-http",
          "DisplayName": "Durable Agent Review",
          "BaseUrl": "http://127.0.0.1:5095",
          "WorkflowName": "DurableAgentReview",
          "PollIntervalSeconds": 2,
          "TimeoutSeconds": 120
        }
      }
    }
  }
}
```

`maf-durable-http` 是第一个受支持的后端类型。网关通过 HTTP 调用持久的 MAF/Azure Functions 主机；Durable Task 和 Azure Functions 依赖保留在该主机或示例中，不在网关中。

### 认证模式

`Authentication.Mode` 默认为 `static`，兼容原有部署：后端可选的
`ApiTokenSecret`（例如 `env:WORKFLOW_API_TOKEN`）提供共享 Bearer Token；
未配置时不发送认证头。静态模式保留调用方的 `SenderId`、`ActorId`，
且不能同时设置委托专属字段。

逐用户委托支持按后端选择 `entra-obo` 或 RFC 8693 `token-exchange`。
两者都要求 `BaseUrl` 和 `TokenEndpoint` 使用 HTTPS，且不含 userinfo、
查询串或 fragment；网关必须明确配置 `Security.Oidc.Authority` 和
`Security.Oidc.Audience`，后端不得同时配置 `ApiTokenSecret`。
生产环境的 `ClientSecretRef` 应使用 `env:` 等受支持的密钥引用，不应写入明文密钥。

先在 `OpenClaw` 下配置网关 OIDC：

```json
{
  "Security": {
    "Oidc": {
      "Authority": "https://login.microsoftonline.com/TENANT_ID/v2.0",
      "Audience": "api://GATEWAY_APP_ID",
      "RequireHttpsMetadata": true
    }
  }
}
```

在 `OpenClaw.Workflows.Backends` 下添加 Entra 后端：

```json
{
  "review": {
    "Kind": "maf-durable-http",
    "BaseUrl": "https://workflow.example.com",
    "WorkflowName": "DurableAgentReview",
    "Authentication": {
      "Mode": "entra-obo",
      "TokenEndpoint": "https://login.microsoftonline.com/TENANT_ID/oauth2/v2.0/token",
      "ClientId": "GATEWAY_APP_ID",
      "ClientSecretRef": "env:WORKFLOW_OBO_CLIENT_SECRET",
      "Scopes": ["api://workflow/access_as_user"],
      "UserAssertionProfile": "entra-delegated"
    }
  }
}
```

客户端必须提供**受众为网关的访问令牌**，不能使用 ID Token 或目标工作流 API
的令牌。需要将网关注册为 Entra 机密中间层应用，配置下游委托权限，并完成所需的
用户或管理员 consent。`entra-delegated` 要求原始 `scp` 非空，并拒绝
`idtyp=app`；仅有角色不能证明逐用户委托身份。

对于已正确配置 RFC 8693 的提供方（如 Keycloak realm），将网关 authority/audience
改为该提供方，并配置如下后端：

```json
{
  "review": {
    "Kind": "maf-durable-http",
    "BaseUrl": "https://workflow.example.com",
    "Authentication": {
      "Mode": "token-exchange",
      "TokenEndpoint": "https://idp.example.com/realms/workflows/protocol/openid-connect/token",
      "ClientId": "openclaw-gateway",
      "ClientSecretRef": "env:WORKFLOW_EXCHANGE_CLIENT_SECRET",
      "Scopes": ["workflow.read"],
      "Audience": "workflow-api",
      "Resource": "https://workflow.example.com",
      "UserAssertionProfile": "required-claims",
      "AccessTokenClaims": { "typ": "Bearer" },
      "UserDelegationClaims": { "openclaw_actor_kind": "user" }
    }
  }
}
```

RFC 换票使用 `client_secret_basic`；`Scopes`、`Audience`、`Resource` 至少配置一项，
仅使用提供方支持的参数。Entra 必须配置非空 `Scopes`，不接受 `Audience` 或
`Resource`。用户断言准入策略与换票协议独立。
`required-claims` 对两个非空映射中的**所有原始声明名和值精确匹配**，不依赖
入站声明名称映射。签发方必须保证只有**交互用户访问令牌**能获得用户标记。
不能仅开启通用 mapper 就认定安全：部署前必须负向验证 ID Token、client credentials、
服务账号及错误受众均不能通过。仅有 `typ=Bearer` 不能区分用户与应用。

### 信任边界、容量与错误

首期支持通过 OIDC 认证的 HTTP 集成 API 和 HTTP MCP 工作流调用。每次调用仅使用
当前已验证的 JWT，不从请求体、元数据、MCP initialize 身份或历史会话读取凭据。
三个 MCP 工作流工具均同步执行；legacy 有状态会话拒绝跨用户复用。
Cookie、本地账号令牌、引导令牌、无认证回环访问、stdio、聊天和后台任务均不能
提供委托用户断言。现有角色、scope、CSRF 和限流检查保持不变。
缺失凭据、准入拒绝或换票失败绝不回退静态凭据，也不会重放 POST。

委托模式下出站 `SenderId`、`ActorId` 统一为已验证 subject；Payload、Metadata、
SessionId 和调用方原对象不变。**工作流宿主必须对启动、读取、响应逐次授权**，
验证 issuer、audience、scope、run 归属及审批权限。网关的 subject 规范化不是业务授权。
现有示例宿主没有实现上述生产授权要求。

换票预算为 30 秒，实际响应最多读取 65,536 字节，`expires_in` 必须为 1 至
86,400 秒的整数，Bearer Token 必须符合 RFC 6750 语法，不跟随重定向。
无效令牌及密钥解析失败在缓存或调用后端前返回安全换票错误。
进程内缓存最多 1,024 条目和 1,024 个总等待者，
按 issuer、subject、断言哈希、后端及换票参数隔离。有效到期时间取断言到期与
“换票开始时间 + expires_in”的较早者；刷新时点固定为
“有效到期 - min(60 秒, 初始可用寿命 / 2)”，刷新失败不复用旧令牌。
单调用取消不取消其他用户正在等待的共享获取任务。
网关不持久化令牌或断言，不将其附加到事件、session 或日志；
宿主也必须避免在业务响应中回显凭据。

| HTTP 状态 | 错误码 | 含义 |
| --- | --- | --- |
| 401 | `workflow_user_auth_required` | 没有当前合格的用户访问令牌。 |
| 403 | `workflow_delegation_denied` | 提供方明确拒绝用户委托。 |
| 502 | `workflow_token_exchange_failed` | 凭据、协议、网络、容量或提供方失败。 |
| 401 / 403 | `workflow_backend_auth_denied` | 下游认证或授权拒绝，保留原状态。 |

HTTP 错误码位于 JSON 的 `error` 字段；MCP 返回 `IsError=true`，
在 SDK 工具错误文本中包含安全错误码，而不是成功的 JSON 字符串。
不暴露供应方描述或凭据。主动取消保持取消语义。

实现未向 Core 引入身份 SDK 或反射序列化。CI 对 Linux JIT 与 NativeAOT
真实发布程序运行相同的 HTTPS/OIDC、REST/MCP 双协议矩阵，要求恰好两个通过且零跳过。
普通测试未设置 `OPENCLAW_WORKFLOW_GATEWAY_EXE` 时显式跳过该矩阵；
设置了无效路径则必须失败，不能当作跳过或通过。

## HTTP 合约

网关期望相对于 `BaseUrl` 的以下后端端点：

| 操作 | 方法和路径 | 网关端点 |
| --- | --- | --- |
| 启动运行 | `POST /api/workflows/{workflowName}/run` | `POST /api/integration/workflows/{backendId}/runs` |
| 获取状态 | `GET /api/workflows/{workflowName}/status/{runId}` | `GET /api/integration/workflows/{backendId}/runs/{runId}` |
| 响应输入 | `POST /api/workflows/{workflowName}/respond/{runId}` | `POST /api/integration/workflows/{backendId}/runs/{runId}/responses` |

MCP 工具暴露相同的表面：

| 工具 | 用途 |
| --- | --- |
| `openclaw.list_workflows` | 列出配置的工作流后端。 |
| `openclaw.run_workflow` | 启动工作流运行，需要 `operator` 角色。 |
| `openclaw.get_workflow_run` | 读取当前状态、事件、待处理的输入和输出。 |
| `openclaw.respond_workflow` | 向待处理的输入端口发送人工或系统响应，需要 `operator` 角色。 |

## 状态模型

工作流运行使用以下状态：

| 状态 | 含义 |
| --- | --- |
| `queued` | 后端接受运行但尚未开始工作。 |
| `running` | 后端正在主动处理。 |
| `waiting_for_input` | 后端在人工审批或外部响应端口上阻塞。 |
| `completed` | 运行成功完成。 |
| `failed` | 运行失败。 |
| `cancelled` | 运行被拒绝或取消。 |

事件和待处理的输入是来自 `OpenClaw.Core` 的 AOT 安全 JSON DTO。负载字段使用 `JsonElement`，以便工作流主机可以附加结构化审查摘要、审计追踪或审批上下文，而不将网关耦合到特定的持久运行时包。

## DurableAgentReview 示例

`samples/OpenClaw.DurableAgentReview` 暴露了一个示例 `maf-durable-http` 主机：

```bash
dotnet run --project samples/OpenClaw.DurableAgentReview
```

然后使用匹配的后端配置网关，通过集成 API 或 MCP 启动工作流。示例建模了以下流程：

```text
用户请求
  -> 计划执行器
  -> 安全、架构和成本审查者
  -> 聚合器
  -> 人工审批 RequestPort
  -> 执行批准的操作
  -> 审计追踪输出
```

该示例将编排代码保持在网关外部。生产主机可以用 Microsoft Agent Framework Durable Workflows、Azure Functions 和 Durable Task 存储替换内存中的示例状态，同时保留相同的网关合约。

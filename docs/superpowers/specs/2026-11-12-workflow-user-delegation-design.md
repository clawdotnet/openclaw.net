# 工作流 HTTP Runner 逐用户身份委托设计

## 状态与目标

用户已于 2026-11-12 确认设计方向，并允许编写本规格文档。进入实施计划阶段前，
仍需用户审阅并批准本文。本文描述计划实现的行为，不代表功能已经可用。

当前工作流 HTTP Runner 解析后端统一配置的 `ApiTokenSecret`，并在所有操作中
使用该 Bearer Token。本次目标是让工作流后端以经过认证的实际调用用户身份，
对操作执行授权和审计。

已确认的首期范围：

- 支持 Microsoft Entra ID 的 On-Behalf-Of（OBO）协议。
- 支持 RFC 8693 Token Exchange，包括满足协议和策略要求的 Keycloak 部署。
- 每个工作流后端显式选择换票协议。
- 覆盖经过 OIDC 认证的集成 HTTP API 和 HTTP MCP 调用。
- 覆盖启动、状态查询、输入响应以及 Runner 内部轮询。
- 既有配置继续保持静态 Token 行为。

委托模式后端拒绝没有经过验证的用户 OAuth 凭据的调用。首期不为本地账号 Cookie、
网关引导 Token 或账号 Token、stdio MCP、聊天渠道以及脱离请求运行的 Agent 任务
增加委托能力。不新增登录流程、持久化刷新令牌存储或后台用户凭据续期能力。

## 现有实现

- [工作流模型](../../../src/OpenClaw.Core/Models/WorkflowModels.cs)
  定义后端配置以及可序列化的请求和响应数据。
- [Runner 接口](../../../src/OpenClaw.Core/Abstractions/IAgentWorkflowRunner.cs)
  尚无可信调用者上下文。
- [HTTP Runner](../../../src/OpenClaw.Gateway/Workflows/MafDurableHttpWorkflowRunner.cs)
  在构造时获取静态 Token，并逐请求设置认证头；流式操作通过 `GetAsync` 轮询。
- [工作流注册表](../../../src/OpenClaw.Gateway/Workflows/AgentWorkflowRegistry.cs)
  在不同调用之间共享后端 Runner。
- [集成门面](../../../src/OpenClaw.Gateway/Composition/IntegrationApiFacade.cs)、
  [HTTP 端点](../../../src/OpenClaw.Gateway/Endpoints/IntegrationEndpoints.cs)和
  [MCP 工具](../../../src/OpenClaw.Gateway/Mcp/OpenClawMcpTools.cs)暴露工作流操作。
- [安全服务注册](../../../src/OpenClaw.Gateway/Composition/SecurityServicesExtensions.cs)
  在配置 OIDC Authority 后启用 JWT Bearer 验证。

`SenderId`、`ActorId`、`SessionId` 和元数据均属于客户端可控制的业务数据，
不能作为用户身份证明。

## 技术方案与备选方案

在 Gateway 中实现轻量 HTTP 协议适配器，共用令牌提供服务，并显式传递不可序列化
的执行上下文。Core 仅保留 Runner 抽象需要的小型配置和上下文契约。
网关认证、令牌获取和工作流执行保持职责分离。

备选方案包括厂商 SDK 和外置令牌代理。前者增加依赖以及裁剪验证负担，后者增加
部署组件和信任边界，均不是已确认首期范围的必要条件。

## 配置与校验

为每个后端增加 `Authentication` 配置节：

| 字段 | 含义 |
| --- | --- |
| `Mode` | `static`（默认）、`entra-obo` 或 `token-exchange` |
| `TokenEndpoint` | 显式配置且固定的 HTTPS 授权服务器令牌端点 |
| `ClientId` | 机密客户端注册标识 |
| `ClientSecretRef` | 通过现有密钥基础设施解析的密钥引用 |
| `Scopes` | Entra 必填的非空权限范围列表；RFC 8693 中可选 |
| `Audience` | RFC 8693 可选的目标受众 |
| `Resource` | RFC 8693 可选的目标资源 |

委托模式要求后端 `BaseUrl` 和令牌端点均为绝对 HTTPS 地址，并配置客户端凭据、
OIDC Authority 和明确的网关 OIDC Audience。端点地址不得包含用户信息、片段或
查询参数；OAuth 参数通过表单请求体发送。Entra 模式必须配置权限范围，且拒绝
RFC 专属的 audience/resource 配置。RFC 模式至少指定 scopes、audience、
resource 中的一项作为目标选择条件。

未知模式、非法值及不兼容的模式专属配置必须在启动时拒绝。委托模式下不允许同时
配置非空 `ApiTokenSecret`，避免保留含义不明确的回退凭据。静态模式保留现有
`ApiTokenSecret`、可选 Token 和后端地址行为，并拒绝仅适用于委托的配置项。

复用现有密钥解析器，不新增明文凭据配置字段。首期客户端认证仅支持客户端密钥：
Entra 通过表单体传递 `client_secret`，RFC 8693 使用 `client_secret_basic`。
不声明支持证书断言、公共客户端或其他客户端认证方式。

端点、目标权限范围、受众、资源和客户端凭据均来自运维配置，不得由请求或模型
指定。令牌端点和委托模式后端均禁止自动跟随 HTTP 重定向，防止凭据被转发到
其他地址。

## 可信调用者上下文与传递

每次 HTTP 调用均从配置的 JWT Bearer 认证方案获取成功认证结果。不能仅凭任意
已认证主体或原始 `Authorization` 请求头建立委托上下文。必须使用本次成功认证
所对应的 Bearer Token，以及经过验证的 issuer、subject 和过期时间。
subject 必须非空，且必须有明确的过期时间。遵循认证处理器的验证结果，
不得自行信任仅解码、未验证的 JWT 内容。

保留原有端点授权、权限范围、CSRF 和限流检查。通过回环地址免认证、本地 Cookie
或静态 Token 获得端点访问资格，并不意味着具备 OAuth 委托资格。

构造不可序列化的调用者上下文，显式经过门面、注册表传递到 Runner。
保留现有无上下文重载以维持源码兼容；无上下文调用委托模式后端时明确失败。
在 Runner 抽象中增加支持上下文的重载，不删除现有签名。默认接口实现可以在
上下文为空时转发给原方法；但外部 Runner 未实现委托时，必须拒绝非空上下文，
不得静默丢弃调用方提供的上下文。

HTTP MCP 从当前经过认证的传输请求获取身份，而非工具参数、连接初始化元数据
或同一 MCP 会话中的历史请求。上下文注入不得出现在模型可见的工具参数 Schema 中。
stdio MCP 没有此类上下文，访问委托模式后端时明确拒绝。
单例 Runner、会话、事件和 DTO 均不得存储调用者上下文。

启动、查询、响应和流式操作均接收并传递上下文。每次轮询重新检查上下文有效性，
并获取适用的令牌；入口用户断言过期后终止轮询，不能借此延长用户授权。
本次不新增 HTTP 流式端点。

委托模式中，发送给后端的 `SenderId` 和响应中的 `ActorId` 使用经过验证的
subject，不使用客户端提供的值。身份权威依据仍是换取令牌中带有 issuer 边界的
身份。其他业务元数据不能作为授权来源。静态模式保留现有数据语义。

## 令牌获取协议

使用表单编码的 HTTPS POST 请求，并限制超时和响应大小。
JSON 反序列化使用源生成元数据。

### Entra OBO

发送以下参数：

- `grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer`
- `requested_token_use=on_behalf_of`
- `assertion`：本次请求经过验证的入口访问令牌。
- 配置的 `client_id`、解析后的 `client_secret`，以及空格分隔的 `scope`。

配置的 Entra 令牌端点及应用注册必须允许网关向目标工作流 API 执行 OBO。
入口断言必须是访问令牌，不能使用 ID Token。

### RFC 8693

执行客户端认证，并发送以下参数：

- `grant_type=urn:ietf:params:oauth:grant-type:token-exchange`
- `subject_token`：本次请求经过验证的入口访问令牌。
- `subject_token_type=urn:ietf:params:oauth:token-type:access_token`
- `requested_token_type=urn:ietf:params:oauth:token-type:access_token`
- 配置中存在的 `scope`、`audience` 和 `resource`。

响应中的 `issued_token_type` 必须标识访问令牌。
Keycloak 的具体部署、版本和策略必须显式允许所配置的交换流程；支持 RFC 协议
不等同于声明兼容所有 Keycloak 部署。

### 响应处理

两种适配器均要求访问令牌非空、令牌类型为 Bearer，且 `expires_in` 为正数并处于
允许范围内。格式错误的成功响应也必须视为失败。响应若包含 scope，必须覆盖配置
要求的权限范围。身份提供方和后端负责执行目标 audience 与委托权限约束；
网关不得将不透明的后端访问令牌擅自当作经过验证的 JWT。

## 令牌生命周期、缓存与并发

令牌仅缓存在内存中。缓存键包含经过验证的 issuer 和 subject、入口断言的密码学
指纹、后端 ID、协议、客户端 ID、令牌端点，以及目标 scopes/audience/resource。
不得仅使用 subject 作为键，也不得将入口 Token 原文保留为字典键。

每个令牌提供服务实例最多保留 1,024 个缓存或正在获取的条目。达到上限后，淘汰
过期或已完成条目，或明确拒绝新增不同键的获取请求；不得创建无界的等待者或键集合。
同一键的并发获取合并为一次请求。失败条目及时移除，允许后续调用重试。
单个等待者取消不得取消其他调用者的获取操作；共享任务拥有独立的有限超时，
并响应应用关闭取消信号。

令牌可用期不得超过入口断言和返回令牌两者中更早的过期时间。
提前刷新余量取 60 秒与剩余可用期一半中的较小值。入口断言过期后不得隐式续期；
命中缓存也不能绕过调用者有效性检查。

Runner 仅在本次 `HttpRequestMessage` 上附加换取的 Bearer Token，
不得修改 `DefaultRequestHeaders.Authorization`。
认证失败后不得自动重放工作流 POST 请求。

## 失败语义与授权边界

使用明确类型的委托失败，以及稳定、脱敏的错误码：

| 情况 | HTTP 行为 |
| --- | --- |
| 缺少可信用户断言或断言已过期 | 401，`workflow_user_auth_required` |
| 令牌端点明确拒绝委托或要求用户交互 | 403，`workflow_delegation_denied` |
| 工作流后端拒绝换取的用户令牌 | 保留 401/403，并返回脱敏的工作流授权错误 |
| 令牌端点通信异常、超时、响应格式错误，或服务/客户端配置失败 | 502，`workflow_token_exchange_failed` |

MCP 返回对应的工具错误，不得返回伪装成成功的工作流结果。
调用者取消继续保持取消语义，不转换为普通后端失败。
配置校验失败必须阻止启动。换票失败后不得向后端发送请求，
也不得改用 `ApiTokenSecret` 重试。

遵循现有日志规范，记录后端 ID、操作、状态及安全错误码。
不得记录 OAuth 请求体或响应体、认证头、包含敏感值的密钥引用或提供方原始错误。
不得向客户端暴露提供方错误描述或令牌。

工作流宿主仍负责每次操作的授权，包括运行归属、跨用户访问和审批权限。
网关不能通过 `runId`、`SenderId` 或 `ActorId` 推断这些业务授权策略。
部署文档必须明确要求宿主执行委托授权；无差别接受所有换取令牌的宿主不满足本次
安全目标。

## 影响范围与文档

实施时需覆盖配置与源生成 JSON 契约、Runner 接口及实现、注册表、门面、
集成端点、HTTP MCP 上下文适配、依赖注入和启动校验。协议逻辑保留在 Gateway。
不修改与本次需求无关的工具执行或 Agent 推理代码。

更新[英文工作流文档](../../workflow-backends.md)和
[中文工作流文档](../../zh-CN/workflow-backends.md)，补充两种协议的配置、
身份提供方应用注册前提、后端授权要求、支持的入口和失败时拒绝访问的行为。
保留现有配置示例。示例宿主若未增加委托授权实现及测试，不得声明它已经具备该能力。

JIT 和 NativeAOT 使用相同的委托实现。不得向 Core 引入依赖大量反射的身份 SDK。
新增 OAuth DTO 使用源生成序列化器；裁剪和 AOT 验证必须覆盖新路径，
不能仅以普通编译通过作为依据。

## 验证与验收

使用可确定性执行的模拟令牌端点和后端 HTTP 处理器。
自动化测试不依赖真实用户凭据或外部身份租户。

1. 精确断言 Entra 与 RFC 表单字段及客户端认证方式，覆盖转义、权限范围、
   目标选择条件和固定配置端点。
2. 拒绝非法配置、缺少身份、请求体或元数据中的伪造身份、非 Bearer 认证、
   过期断言以及不支持的传输入口。
3. 通过认证链路端到端测试集成 HTTP API 和 HTTP MCP。检查注入上下文不出现在
   MCP 工具 Schema 中，且共用 MCP 服务或会话的两个用户不会共享身份。
4. 验证启动、查询、响应及每次轮询请求均使用正确用户的换取令牌，
   并正确规范化委托请求体中的用户身份。
5. 交错执行双用户、双后端请求，验证 issuer、断言、目标及后端隔离；覆盖同键
   单次并发获取、过期、容量上限、失败条目移除及等待者独立取消。
6. 覆盖拒绝委托、要求用户交互、令牌字段缺失或格式错误、超时、重定向、
   提供方不可用、取消以及后端 401/403。断言没有静态凭据回退或不安全的 POST 重放。
7. 断言日志、错误、工作流负载、事件和持久化会话均不包含令牌或 OAuth 错误响应体。
8. 保留静态模式和无上下文 API 的兼容性测试，明确记录委托模式有意改变的
   请求体身份规范化行为。
9. 先运行定向 Release 测试，再运行仓库规定的解决方案 Release 构建和测试，
   确认零警告且实际执行测试数非零。因涉及配置和依赖注入，还需运行网关
   `--doctor` 冒烟检查，并执行仓库适用的 NativeAOT 验证流程。

用户审阅并批准本规格文档后，方可进入实施计划阶段。
本次仅编写规格文档，不包含运行代码变更或实施验证。

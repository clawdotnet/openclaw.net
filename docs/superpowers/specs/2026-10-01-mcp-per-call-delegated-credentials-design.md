# MCP 逐调用委托凭证设计

## 状态

设计内容已在对话中确认；实现计划见 [2026-10-02-mcp-per-call-delegated-credentials.md](../plans/2026-10-02-mcp-per-call-delegated-credentials.md)。

## 背景

OpenClaw 当前的 MCP HTTP transport 在 client 创建时从静态配置加载 headers。MCP client 会被工具复用，而 MetaSkill 可并行执行多个工具调用，因此不能通过修改共享 `HttpClient` 的默认 Authorization header 来传递用户身份，否则并发调用可能串用凭证。

需要覆盖的远端 MCP 调用入口包括：

- `McpNativeTool` 和 `McpServerToolRegistry` 注册的普通 MCP 工具。
- `McpAppNativeTool` 和 `McpAppServer` 管理的 MCP App 工具。
- `AgentRuntime` 与 `MafAgentRuntime` 执行的 MetaSkill 工具调用，包括并行波次和 fan-out。MAF 有独立的 MetaSkill 调度路径，但其工具调用最终进入公共 `OpenClawToolExecutor`。

相关现状见 [McpServerToolRegistry](../../../src/OpenClaw.Agent/Plugins/McpServerToolRegistry.cs)、[McpNativeTool](../../../src/OpenClaw.Agent/Tools/McpNativeTool.cs)、[McpAppNativeTool](../../../src/mcpapp/OpenClaw.McpApp/McpAppToolProvider.cs)、[McpAppServer](../../../src/mcpapp/OpenClaw.McpApp/McpAppServer.cs)、[AgentRuntime](../../../src/OpenClaw.Agent/AgentRuntime.cs)、[MafAgentRuntime](../../../src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs) 和 [ToolExecutionContext](../../../src/OpenClaw.Core/Abstractions/IToolWithContext.cs)。

## 目标

- 让受支持的工具调用以调用者身份向指定 Strategos MCP endpoint 提供短期、受 audience 与 scope 限制的凭证。
- 并发调用之间隔离凭证，不修改共享 `HttpClient` 或 transport 的可变默认认证状态。
- 保持普通 MCP 与 MCP App 共用一致的凭证获取和逐请求传递语义。
- 保持现有静态 MCP headers 对未启用委托认证的目标兼容。
- 保持 Gateway NativeAOT 友好；新增组件必须纳入 AOT 验证。

## 非目标

- 不实现 Strategos 的令牌验证、`ClaimsPrincipal` 构造、到 `ActionPrincipal` 的映射或授权决策。上述能力由 Strategos 实现和测试。
- 首版不覆盖 Telegram、Slack 等渠道身份，也不将 bot / channel credential 伪装成用户凭证。
- 不为每个用户建立独立 MCP client，也不增加单独的凭证代理服务。
- 不把调用者令牌写入 Session、持久化执行记录、工具参数、MCP `_meta` 或日志。
- 不在同一调用中因 token exchange 失败而自动改用网关签名令牌，也不在上游授权失败后回退到静态 Authorization。
- 首版不缓存 exchange / delegation 结果；每个 MCP 工具调用单独获取凭证。

## 设计决策

### 支持入口

首版只支持：

1. 带已验证 OIDC 身份的 Gateway HTTP 请求。
2. 在连接建立时通过 OIDC 验证的 WebSocket 请求。

HTTP 请求凭证从当前已认证请求上下文取得。WebSocket 凭证绑定到已认证连接的作用域，不放入 Session；凭证过期后要求重新认证连接，不使用过期凭证执行新的 MCP 调用。

### 组件边界

- **调用者凭证上下文**：以不可变、按请求或连接隔离的值，从入口贯穿到每个 `ToolExecutionContext`。不得通过共享可变字段或跨调用 `AsyncLocal` 身份覆盖来传递用户凭证。
- **凭证 provider**：按目标 MCP endpoint 的显式配置，使用已验证 OIDC 凭证执行 OAuth token exchange，或在配置为网关委托模式时签发短期委托凭证。凭证限定 Strategos audience 和最小所需 scopes。
- **逐请求认证适配层**：将 provider 返回的凭证附加到单个 MCP HTTP 请求。优先使用 MCP SDK 的每请求认证能力；若 SDK 不提供可安全并发使用的能力，则在 MCP 调用边界增加请求级 transport / auth 适配层。不得改写共享 `HttpClient.DefaultRequestHeaders`、共享 `HttpClientTransportOptions.AdditionalHeaders` 或其他共享默认认证状态。
- **MCP 工具入口**：普通 MCP 与 MCP App 工具都从当前 `ToolExecutionContext` 获取同一类调用者上下文，并经逐请求认证适配层发送请求。
- **Runtime 派发**：`AgentRuntime` 与 `MafAgentRuntime` 的普通、MetaSkill 并行及 fan-out 调用，都经公共工具派发边界传递各自的调用者上下文。并行任务只共享无用户身份状态的 MCP client / transport。

委托认证按上游 endpoint 显式启用。仅匹配该 Strategos 目标的调用可以获取和发送委托凭证；其他 MCP server / MCP App 保持现有静态认证行为。

对启用委托认证的 endpoint，本次调用的 Authorization 凭证取代该 endpoint 的静态 Authorization；其他非认证 headers 仍可按配置发送。静态 Authorization 不得与委托凭证同时发送。

### 凭证签发策略

1. **OAuth token exchange（首选）**：当身份提供方支持且已配置时，Gateway 使用已验证的用户凭证换取面向 Strategos、短时有效且 scope 受限的令牌。
2. **Gateway 签名委托凭证（显式后备模式）**：身份提供方不支持 token exchange 时，可由 Gateway 作为受信任身份代理签发短期、限定 audience 和权限的凭证。Gateway 管理签名密钥和签发配置；Strategos 配置相应信任并负责验证。

部署配置明确选择凭证模式。不能以运行时错误作为自动切换策略，以免身份、权限或信任边界静默变化。凭证生成和请求传递只属于 OpenClaw 的职责；令牌验证和 principal 映射只属于 Strategos 的职责。

### 请求数据流

1. Gateway 在 HTTP 请求或 WebSocket 建连时验证 OIDC 身份。
2. HTTP 请求作用域或 WebSocket 连接作用域保存执行所需的凭证上下文；Session 仍只保存既有用户标识等业务状态，不保存令牌。
3. 工具派发为当前调用创建带有该凭证上下文的 `ToolExecutionContext`。MetaSkill 并行步骤分别保留各自上下文。
4. 普通 MCP 或 MCP App 工具请求凭证 provider 为目标 endpoint 取得短期凭证。
5. transport 适配层只将凭证附加到本次 MCP HTTP 请求的 Authorization header，随后发送请求。
6. Strategos 验证凭证、构造 `ClaimsPrincipal` 并映射到 `ActionPrincipal`；这些步骤不在 OpenClaw 中实现。

凭证不通过 MCP `_meta`、工具参数或其他业务 payload 传递。

### 失败处理与安全

- 无调用者凭证、凭证已过期、token exchange / 签名失败或请求级认证适配失败时，OpenClaw 不发送该 MCP 请求，并返回不含凭证的稳定失败类别。
- Strategos 返回 401 / 403 时，按上游授权失败处理；不得使用静态 Authorization 重试。
- token exchange 与 Gateway delegation 是互斥的显式运行模式；失败不触发自动降级。
- 只有显式启用委托认证的 endpoint 才能使用此凭证。其他 MCP endpoint 不得收到用户凭证。
- 日志、trace、审计记录和错误文本不得包含原始令牌、签名材料或可恢复的凭证内容；只记录失败类别、目标标识及关联 ID。
- WebSocket 连接凭证过期后，不尝试用已过期凭证执行新调用；调用失败并要求重新认证连接。

## 测试与验证

OpenClaw 测试验证凭证获取和传递，不模拟 Strategos 内部的 token validation 或 principal mapping：

- 使用并发调用与可控测试 MCP endpoint，验证不同调用者同时通过共享 client 发起请求时，每个请求收到正确且互不串用的 Authorization；覆盖普通 MCP 与 MCP App。
- 覆盖 `AgentRuntime` 和 `MafAgentRuntime` 的 MetaSkill 并行波次及 fan-out，确认各子调用保留入口身份。
- 覆盖 HTTP OIDC 请求、WebSocket 连接作用域与凭证过期行为。
- 验证凭证生成失败时上游请求计数为零；验证上游 401 / 403 不会触发静态 Authorization 重试。
- 验证未启用委托认证的 MCP endpoint 保持原静态 headers 行为，且不会接收其他调用者的凭证。
- 验证 Session、工具参数、MCP `_meta`、持久化执行记录、日志和 trace 不含令牌或签名材料。
- 对凭证组件和 Gateway 执行 NativeAOT 构建 / 兼容性验证；任何新增依赖不得未经评估地引入 trim-unsafe 或 reflection-heavy 路径。

Strategos 侧需独立测试签名、issuer、audience、有效期、scope / 权限校验、`ClaimsPrincipal` 构造及 `ActionPrincipal` 映射；这些测试不属于 OpenClaw 仓库实现范围。

## 兼容与 rollout

委托认证默认关闭，按 MCP endpoint 显式启用。未启用的普通 MCP 和 MCP App 继续使用现有静态 header 配置。启用的 endpoint 只有在对应 provider、audience、scope、密钥或 IdP 配置完整后才能启动或接受调用；不通过隐式静态凭证回退掩盖缺失配置。

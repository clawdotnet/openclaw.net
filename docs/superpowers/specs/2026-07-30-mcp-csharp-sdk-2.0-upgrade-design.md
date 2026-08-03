# OpenClaw.NET MCP 升级到 csharp-sdk v2.0.0 设计说明

- 日期：2026-07-30
- 状态：已评审（方案 2：分层迁移）
- 语言：中文
- 目标分支：当前工作分支

## 1. 背景与目标

本设计用于将 OpenClaw.NET 的 MCP 相关依赖从 `1.4.0` 升级到 `2.0.0`，并完成完整迁移（服务端、客户端、治理与文档）。

本次采用“分层迁移”策略：

1. 依赖与编译层
2. 协议与行为层
3. 能力与治理层

目标是默认对齐 MCP 2026-07-28 规范与 csharp-sdk v2.0.0，同时保留最小必要的受控回退能力。

## 2. 范围

### 2.1 In Scope

- MCP 相关包升级到 `2.0.0`
- 网关 `/mcp` 与 `/apps/mcp/{serverId}` 路由行为统一
- discover-first 协商路径对齐
- `Tool.inputSchema` 严格化与结果处理兼容
- `structuredContent` 非对象返回语义兼容
- 关键错误传播与可观测性增强
- Tasks 扩展接入与回归覆盖
- OAuth/PKCE 严格策略与最小回退开关
- 迁移文档、运维排障说明、退场计划

### 2.2 Out of Scope

- 与 MCP 升级无关的大规模重构
- 非 MCP 业务能力新增
- 超出最小必要范围的双轨长期并行架构

## 3. 当前基线

当前代码中 MCP 相关 NuGet 版本为 `1.4.0`，主要位置：

- `src/OpenClaw.Agent/OpenClaw.Agent.csproj`
- `src/OpenClaw.Gateway/OpenClaw.Gateway.csproj`
- `src/mcpapp/OpenClaw.McpApp/OpenClaw.McpApp.csproj`

网关已显式配置 HTTP 传输为 Stateless：

- `src/OpenClaw.Gateway/Mcp/McpServiceExtensions.cs`

这与 v2 的默认方向一致，但仍需完成协议行为与兼容测试收敛。

## 4. 方案对比与选择

### 4.1 备选方案

1. 一次性大迁移（Big Bang）
2. 分层迁移（推荐）
3. 双轨兼容迁移（长期并行）

### 4.2 选择结论

选择方案 2（分层迁移）：在同一升级主线内分阶段落地并逐层验收。对于高风险行为仅提供最小必要回退开关，不引入全量双轨复杂度。

## 5. 目标架构与边界

### 5.1 三层迁移模型

1. 依赖与编译层：包升级、API 变更修复、编译与告警收敛。
2. 协议与行为层：discover-first、schema 严格化、structuredContent 语义、错误模型对齐。
3. 能力与治理层：Tasks 扩展、OAuth/PKCE、观测、回退开关、文档发布。

### 5.2 架构边界

- 不改变 Gateway 与 Runtime 的职责边界。
- 不将可选扩展变成强依赖。
- 保持 NativeAOT 友好，避免引入反射重路径。
- 回退开关为临时治理手段，必须有退场计划。

## 6. 组件改造清单

### 6.1 网关 MCP 服务注册层

- 文件：`src/OpenClaw.Gateway/Mcp/McpServiceExtensions.cs`
- 目标：显式化 v2 协商/兼容策略，保留 stateless 默认，补齐可观测埋点。

### 6.2 网关路由与代理层

- 文件：`src/OpenClaw.Gateway/Program.cs`、`src/OpenClaw.Gateway/Endpoints/AppsMcpProxyEndpoint.cs`
- 目标：`/mcp` 与 `/apps/mcp/{appId}` 在协商、Header、错误语义上保持一致。

### 6.3 MCP App 托管层

- 文件：`src/mcpapp/OpenClaw.McpApp/McpAppServer.cs`、`src/mcpapp/OpenClaw.McpApp/McpAppToolProvider.cs`
- 目标：对齐 tool schema 与 structuredContent 新语义，保证工具桥接类型信息完整。

### 6.4 Agent 外部 MCP 工具注册层

- 文件：`src/OpenClaw.Agent/Plugins/McpServerToolRegistry.cs`
- 目标：客户端协商与异常处理更新，避免旧异常包装假设导致误判。

### 6.5 客户端模型与调用层

- 文件：`src/OpenClaw.Client/McpModels.cs`、`src/OpenClaw.Client/OpenClawHttpClient.cs`
- 目标：兼容非对象 structuredContent 与 inputSchema 严格校验路径。

### 6.6 测试层

- 目录：`src/OpenClaw.Tests`
- 目标：覆盖 discover-first、schema 严格性、代理一致性、错误传播、Tasks 扩展。

## 7. 关键数据流（目标态）

1. 客户端 -> `/mcp` -> discover-first -> runtime tool dispatch
2. 浏览器/UI -> `/apps/mcp/{serverId}` -> 已连接 `McpClient` -> 同一治理与观测链路

要求：主路由与代理路由在协议与错误语义上可验证一致。

## 8. 错误处理与兼容策略

### 8.1 错误分类

- 协商失败：discover/initialize 协议不匹配
- 传输失败：保留底层 HTTP/SSE 异常类型
- 数据失败：schema 缺失、结构不符、反序列化失败

### 8.2 兼容策略

- 默认走 v2 discover-first
- 对下游旧服务保留受控回退握手
- 对弃用能力仅保留兼容透传与观测告警，不新增依赖扩展

### 8.3 回退开关（最小必要）

- 强制 legacy initialize（紧急兼容）
- schema 严格校验降级开关（短期）
- OAuth/PKCE 严格度降级开关（默认严格）

要求：所有回退都必须有日志、指标、审计记录与退场期限。

## 9. 测试与验收标准

### 9.1 构建与静态检查

- MCP 相关项目、网关主项目、测试项目可编译通过
- 不新增不可接受的 AOT/Trim 风险

### 9.2 协议行为

- `/mcp` 与 `/apps/mcp/{serverId}` discover-first 路径可用
- 旧握手回退在开关开启时可用
- 缺失 `inputSchema` 场景明确失败并可定位
- 非对象 `structuredContent` 按 v2 语义正确处理

### 9.3 安全与认证

- OAuth issuer 与 PKCE S256 默认严格
- 降级触发有告警和审计

### 9.4 回归稳定

- MCP 关键测试集通过
- 新增 v2 破坏点回归用例通过

### 9.5 文档与运维

- 发布迁移说明、回退策略与排障手册

## 10. 实施顺序与里程碑

### M1：依赖与编译层

- 升级 MCP 包到 `2.0.0`
- 修复编译 API 变更
- 验证 AOT/Trim 边界

### M2：协议与行为层

- 对齐 discover-first、schema 严格化、structuredContent、错误传播
- 收敛 `/mcp` 与 `/apps/mcp/{serverId}` 一致性

### M3：能力与治理层

- 接入 Tasks 扩展
- 完成 OAuth/PKCE 严格策略与最小回退
- 增强观测（协商路径、回退触发、失败类型）

### M4：回归封板与发布文档

- 执行 MCP 回归集并固化证据
- 更新升级与运维文档

## 11. 风险与缓解

1. 生态对旧握手路径依赖：通过短期回退开关与观测缓冲。
2. 代理与主路由行为分叉：以契约测试强制对齐。
3. AOT/Trim 回归：维持现有边界，不新增反射重路径。
4. 安全降级被长期遗留：设置退场期限并纳入发布门禁。

## 12. 发布与退场原则

- 默认值以安全和规范一致性优先。
- 回退开关仅用于应急，必须记录启用原因和到期时间。
- 至少一个稳定发布周期后评估移除回退开关。

## 13. 产出物

- 本设计文档（中文）
- 后续实现计划文档（由 writing-plans 阶段生成）
- 迁移说明与运维排障文档更新

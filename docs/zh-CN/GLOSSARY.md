# 术语表

OpenClaw.NET 文档中出现的术语的统一参考。如果其他文档中的术语不熟悉，先在这里查找。

## 运行时形态

**Gateway（网关）** — `src/OpenClaw.Gateway` 中的 ASP.NET 宿主。终止 HTTP 和 WebSocket 流量，提供 `/chat`、`/admin`、`/mcp`、webhook 和诊断服务，应用认证和策略，将请求交给运行时。这是长期运行的服务器进程。

**Runtime（运行时）** — `src/OpenClaw.Agent` 中的 Agent 运行时。运行一个推理轮次：提示组装、模型调用、工具选择、工具执行、重试、委托、审批和最终响应。网关是宿主；运行时是其中的 Agent 循环。

**Core（核心）** — `src/OpenClaw.Core` 中的共享基础设施：配置绑定、会话、记忆、安全、可观测性、插件元数据、验证。被网关和运行时共同消费。

**Companion** — 桌面运维者应用 `src/OpenClaw.Companion`。面向网关的本地客户端。

**TUI** — 终端 UI `src/OpenClaw.Tui`。

**CLI** — `src/OpenClaw.Cli` 中的 `openclaw` 命令。`setup`、`launch`、`status`、`admin posture`、`chat`、`run`、`plugins`、`skills` 等入口点。

## 执行通道

**`aot`** — 预编译、裁剪安全的运行时通道。较窄的插件接口（仅原生工具和主流桥接能力）。适用于需要低内存、小二进制和零动态代码的场景。

**`jit`** — 即时编译运行时通道。完整插件接口，包括 `registerChannel()`、`registerCommand()`、`registerProvider()`、`api.on(...)` 和动态进程内 .NET 插件。

**`auto`** — 运行时动态代码可用时选择 `jit`，否则 `aot`。

## 工具、插件、技能

**Tool（工具）** — 运行时在推理轮次中可调用的函数（文件操作、shell、网页搜索、记忆、通道等）。原生工具位于 `src/OpenClaw.Agent`。

**Plugin（插件）** — 加载到运行时的扩展。两种类型：原生动态 .NET 插件（仅 jit）和来自上游 OpenClaw 生态的 TS/JS 桥接插件。

**Skill（技能）** — 打包的 `SKILL.md` 能力。可通过 `openclaw skills install` 安装。

**Bridge（桥接）** — 进程内适配器层，使 TS/JS 上游插件能在 .NET 运行时上运行。

## 身份与访问

**Bootstrap token（启动令牌）** — `OPENCLAW_AUTH_TOKEN` 的值。在非 loopback 部署中用于创建第一个运维者账户，并作为紧急访问凭证保留。

**Breakglass credential（紧急凭证）** — 同样的令牌，不同角色：账户认证不可用时的回退路径。

**Operator account（运维者账户）** — Admin UI 的常规命名登录。推荐用于浏览器会话和签发运维者账户令牌。

**Operator account token（运维者账户令牌）** — 从运维者账户签发的令牌，供 Companion、CLI 自动化、API 客户端和 WebSocket 集成使用。

## 部署态势

**Posture（态势）** — 运行中网关的综合安全和部署状态：绑定地址类别、转发头部信任、审批策略、工具限制、插件信任、通道验证等。

**`--doctor`** — 网关进程参数。在不下线的情况下对配置文件运行引导诊断。

**`openclaw admin posture`** — 针对运行中网关验证实时安全态势。

**`openclaw setup status`** — 针对配置文件总结生成的产物和下一步操作。

## 会话、记忆、配置、自动化

**Session（会话）** — 一个持久化对话，拥有自己的历史、待办状态和记忆。

**Memory（记忆）** — 项目范围的笔记和事实，Agent 可读写。与会话历史分离。

**User profile（用户配置）** — 关于特定用户的稳定事实、偏好、项目、语气和近期意图。

**Model profile（模型配置）** — 与 Provider 无关的命名模型配置。

**Automation（自动化）** — 按需或按 cron 计划运行的已保存任务。

**Learning proposal（学习提案）** — 运行时在观察到成功会话后建议的待处理变更。运维者审批、拒绝或回滚。

## 通道与接口

**Channel（通道）** — 将外部消息接口转为网关可路由请求的入站适配器。

**Route（路由）** — 从 Actor 和通道到特定模型配置、提示、工具预设和工具允许列表的映射。

**MCP** — 在 `/mcp` 暴露的 Model Context Protocol 端点。

**Integration API** — `/api/integration/*` 下的类型化 HTTP 接口。

**OIDC / OIDC JWT** — 组织认证模式 (`oidc_jwt`)，用于 OIDC/Keycloak JWT bearer 流程。与 `bootstrap_token`、`browser_session`、`account_token` 认证模式并列使用。

## 企业频道

**飞书频道** — 飞书/Lark IM 适配器，支持热重载运行时配置、消息去重、媒体附件处理和网关组合注册。可通过管理频道配置端点进行配置。

**钉钉频道** — 钉钉 IM 适配器，支持热重载运行时配置。注册于网关组合中，支持管理可配置覆盖。

**企业微信频道** — 企业微信 IM 适配器，支持热重载运行时配置。注册于网关组合中，支持管理可配置覆盖。

**频道配置存储（Channel Config Store）** — 持久化频道级别配置覆盖的后端存储，确保每频道设置在网关重启后存活。

## 工作区管理

**工作区 MCP** — `/admin/workspace/mcp` 管理接口，用于持久化普通 `Plugins:Mcp` 服务器定义并热重载标准 MCP 插件工具面，无需重启网关。与 MCP Apps 分开（参见 [MCPAPP.md](MCPAPP.md)）。

**工作区管理 API** — `/admin/workspace/*` 下的管理端点，提供工作区文件浏览、目录树、上传、下载及 MCP 配置管理。

**数字员工端点** — 面向企业数字员工场景的管理端点，位于 `/admin/digital-employee/*`。

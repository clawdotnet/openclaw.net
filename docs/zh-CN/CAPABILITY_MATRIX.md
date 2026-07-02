# 能力矩阵

本矩阵汇总了当前 OpenClaw.NET 的能力通道。它与 [COMPATIBILITY.md](COMPATIBILITY.md) 互补，后者是权威的详细兼容性指南。

## 状态图例

- `Core`：默认运行时路径中可用
- `Optional`：启用相关配置、包、provider 或宿主时可用
- `Experimental`：可用于评估，但非承诺的稳定接口
- `JIT-only`：需要 `jit` 运行时通道，在 `aot` 模式下快速失败

## 运行时与宿主能力

| 能力 | 通道 | 备注 |
| --- | --- | --- |
| Agent 运行时循环 | Core | 工具调用、流式、取消、重试、会话、记忆和钩子 |
| Gateway HTTP 宿主 | Core | 本地/自托管宿主，提供聊天、管理、健康检查、诊断、OpenAI 兼容 API、MCP 和 WebSocket |
| 后台会话执行 | Core | 有活跃 Goal 的会话在所有 Channel 断开后通过 `MessagePipeline` 自动续跑；有限批次、native/MAF 一致、启动恢复 |
| CLI 安装与启动 | Core | 源码检出、托管本地配置、诊断、模型/配置文件工具、插件/技能命令 |
| NativeAOT 发布 | Core | 运行时和 gateway 专为严格 AOT 通道设计 |
| 桌面 Companion | Optional | 包含在桌面包和解决方案构建中 |
| TUI | Optional | 面向操作员工作流的终端 UI 界面 |
| OpenAI 兼容端点 | Core | 由 gateway 托管 |
| MCP 端点 | Core | 由 gateway 托管 |
| 公网绑定加固 | Core | Gateway 在所需设置加固前拒绝不安全的公网绑定配置 |

## 工具与扩展

| 能力 | 通道 | 备注 |
| --- | --- | --- |
| 第一方和可选工具界面（80+） | Core / Optional | 文件、shell、记忆、会话、网页、数据库、邮件、日历、浏览器、MQTT、Canvas/A2UI、分形记忆、元技能、Goal、Loop、MCP App、Semantic Kernel 及相关工具。实际启用集合取决于配置。 |
| 浏览器工具 | Optional | 在 `OpenClaw.Protocols.Browser` 中实现；保守默认值 |
| MQTT 工具与事件桥接 | Optional | 在 `OpenClaw.Protocols.Mqtt` 中实现 |
| Home Assistant 工具与事件桥接 | Optional | 原生家庭自动化接口 |
| 插件桥接工具/服务 | Optional | 主流 JS/TS 插件工具和服务受支持 |
| 动态插件频道 | JIT-only | JIT 通道外快速失败 |
| 动态插件命令 | JIT-only | 在 JIT 模式下注册为动态聊天命令 |
| 动态插件钩子 | JIT-only | 带超时保护的 `tool:before`/`tool:after` 钩子 |
| 动态插件 provider | JIT-only | 通过动态 provider 接口的插件提供模型 provider |
| 原生动态 .NET 插件 | JIT-only | 通过 `OpenClaw:Plugins:DynamicNative` 加载 |
| 支付工具 | Optional | 原生支付运行时持有实时密钥 |

## Provider、模型与工作流

| 能力 | 通道 | 备注 |
| --- | --- | --- |
| OpenAI provider | Optional | 通过 provider 配置和密钥引用启用 |
| Claude provider | Optional | 通过 provider 配置和密钥引用启用 |
| Gemini provider | Optional | 通过 provider 配置和密钥引用启用 |
| Azure OpenAI provider | Optional | 通过 provider 配置和密钥引用启用 |
| Ollama provider | Optional | 本地 provider 路径，带模型配置文件诊断 |
| OpenAI 兼容 provider | Optional | 兼容端点的 provider 中立路径 |
| 嵌入式本地模型 sidecar | Experimental | 受监督的本地模型包 |
| Microsoft Agent Framework 适配器 | Optional | 通过 `Runtime.Orchestrator=maf` 选择 |
| 动态轮次路由 | Experimental | 稳定运行时接线，保守阈值 |
| 持久化工作流后端 | Optional | 将长时间运行委托给受支持的工作流宿主 |
| 分形记忆 MCP 集成 | Optional | MCP 优先的结构化记忆集成 |
| Mempalace 记忆 provider | Optional | 可选时序知识图谱记忆 provider |

## 频道与客户端

| 能力 | 通道 | 备注 |
| --- | --- | --- |
| Web 聊天 | Core | Gateway 托管的本地 UI |
| 管理 UI | Core | Gateway 托管的诊断和操作员界面 |
| 类型化 .NET 客户端 | Optional | 集成 API 和 MCP facade 客户端 |
| Telegram 频道 | Optional | 操作员 DM 策略、允许列表、诊断 |
| SMS 频道 | Optional | Twilio 支持的频道接口 |
| WhatsApp 频道 | Optional | Worker 支持的集成路径 |
| Teams 频道 | Optional | Microsoft Teams 设置路径 |
| Slack 频道 | Optional | 共享操作员模型的频道适配器 |
| Discord 频道 | Optional | 包含斜杠命令入口对等 |
| Signal 频道 | Optional | 共享操作员模型的频道适配器 |
| 邮件频道 | Optional | 与聊天频道允许列表分开的操作接口 |
| 通用 Webhook | Optional | 认证的入站触发器 |

## 贡献者规则

当某项能力增加了 provider SDK 权重、动态加载、厂商特定默认值或协议特定行为时，优先选择可选项目或扩展边界，而非添加到 `OpenClaw.Core` 或默认 agent 路径。

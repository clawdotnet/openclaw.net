<div align="center">
  <img src="src/OpenClaw.Gateway/wwwroot/image.png" alt="OpenClaw.NET Logo" width="180" />
</div>

# OpenClaw.NET

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)
![NativeAOT-friendly](https://img.shields.io/badge/NativeAOT-friendly-blue)
![Plugin compatibility](https://img.shields.io/badge/plugin%20compatibility-evolving-green)
![Tools](https://img.shields.io/badge/native%20tools-75-green)
![Channels](https://img.shields.io/badge/channels-9-green)
[![Ask DeepWiki](https://deepwiki.com/badge.svg)](https://deepwiki.com/clawdotnet/openclaw.net)

> **声明**: 本项目与 [OpenClaw](https://github.com/openclaw/openclaw) 无关联、未获其背书或隶属关系。这是一个受其工作启发的独立 .NET 实现。

OpenClaw.NET 是一个面向 .NET 的 NativeAOT 友好型 AI Agent 运行时和网关，具有实用的 OpenClaw 生态兼容性。

面向希望拥有本地或自托管 Agent 网关的 .NET 开发者和运维人员，提供明确的诊断信息、第一方 .NET 工具、OpenAI 兼容的 HTTP 接口，以及从源码检出到 NativeAOT 发布产物的完整路径。

> **文档:** [AgentQi.dev](https://agentqi.dev) 是 OpenClaw.NET 的文档和生态主页。OpenClaw.NET 是当前的运行时和仓库标识。

## AgentQi 文档

AgentQi 是 OpenClaw.NET 背后更广泛的开发者基础设施方向：为 .NET 开发者提供实用、可观测、自托管的 AI Agent 系统。

OpenClaw.NET 是你今天就可以使用的运行时和仓库。AgentQiX 是未来的运行时标识。

快速入口：

- [快速入门](https://agentqi.dev/docs/quickstart)
- [入门指南](https://agentqi.dev/docs/getting-started)
- [架构](https://agentqi.dev/docs/start-here)
- [安全](https://agentqi.dev/docs/security)
- [路线图](https://agentqi.dev/docs/roadmap)

## 当前可用功能

- **NativeAOT 友好**的运行时和网关，适用于 .NET Agent 工作负载
- **Agent 运行时**，支持工具执行、流式输出、取消、重试、记忆和会话
- **Gateway**，提供聊天 UI、管理 UI、OpenAI 兼容端点、MCP、WebSocket、健康检查和诊断
- **被动 Harness Contracts**，可检查的 Agent 工作计划，不改变默认聊天或审批行为
- **被动 Evidence Bundles**，可检查的运行证据、检查项、风险项和人工审查，不拦截默认运行时
- **可选的 Plan-Execute-Verify 模式**，对高风险工具执行进行受控治理，包含契约、证据和验证
- **被动 Governance Ledger**，持久的审批和监管决策记录，不会自动批准未来操作
- **Harness Regression Suite**，通过 `openclaw harness test` 在信任 harness/运行时变更前进行离线检查
- **Harness Evolution Proposals**，审查优先的改进建议，涵盖 harness 策略、路由、记忆检索、验证、pulse 行为和工具治理
- **可选的 Fractal Memory MCP 集成**，紧凑的结构化项目记忆和 Runtime Pulse 上下文，不替代 OpenClaw 记忆/会话存储
- **Shared Harness State**，跨参与者、操作、读写集、假设、验证义务、证据链接和冲突的被动委托工作协调
- **Codebase Harness Map**，通过 `openclaw harness map` 生成项目、模块、端点、工具、provider、频道、配置和测试的被动静态仓库地图
- **OpenClaw SkillKit**，通过 `openclaw skill` 进行本地优先的技能编写、验证、评审、打包和演练执行规划
- **一流的可选 Microsoft Agent Framework 适配器**，通过 `Runtime.Orchestrator=maf` 使用，无需特殊构建
- **持久化工作流委托**，通过受支持的工作流后端（如 `maf-durable-http`）进行
- **CLI 和 Companion** 安装流程，适用于源码检出和桌面包
- **75 个原生工具**，涵盖文件操作、会话、记忆、网页、消息、家庭自动化、数据库、邮件等
- **9 个频道适配器**（Telegram、SMS、WhatsApp、Teams、Slack、Discord、Signal、邮件、Webhook），支持 DM 策略、允许列表和签名验证
- **原生 LLM provider**，支持 OpenAI、Claude、Gemini、Azure OpenAI、Ollama 和 OpenAI 兼容端点
- **可选的嵌入式本地模型**，支持 Gemma 4 GGUF 包、包安装/验证 CLI 命令、受监督的 sidecar 推理和基于帧的视频理解
- **实用复用**现有的 OpenClaw TS/JS 插件和 `SKILL.md` 包

从 [docs/START_HERE.md](docs/START_HERE.md) 开始了解评估概览，[docs/QUICKSTART.md](docs/QUICKSTART.md) 了解受支持的本地安装路径，或 [docs/RELEASES.md](docs/RELEASES.md) 了解桌面下载。

关于 Microsoft Agent Framework、A2A 和持久化工作流设置，参见 [docs/integrations/microsoft-agent-framework.md](docs/integrations/microsoft-agent-framework.md)、[docs/a2a.md](docs/a2a.md) 和 [docs/workflow-backends.md](docs/workflow-backends.md)。

## 下载并运行桌面版

最低门槛的桌面起步方式，下载适用于你平台的最新桌面包：

| 平台 | 下载 |
|----------|----------|
| Windows x64 | [openclaw-desktop-win-x64.zip](https://github.com/clawdotnet/openclaw.net/releases/latest/download/openclaw-desktop-win-x64.zip) |
| Apple Silicon macOS | [openclaw-desktop-osx-arm64.zip](https://github.com/clawdotnet/openclaw.net/releases/latest/download/openclaw-desktop-osx-arm64.zip) |
| Linux x64 | [openclaw-desktop-linux-x64.zip](https://github.com/clawdotnet/openclaw.net/releases/latest/download/openclaw-desktop-linux-x64.zip) |

每个桌面包包含 Companion、NativeAOT gateway 和 NativeAOT CLI。

1. 解压归档文件。
2. 从 `companion` 文件夹启动 Companion。
3. 打开 **Setup**（设置）标签页。
4. 选择 provider/model 并输入 provider 密钥，选择 Ollama 使用本地模型服务器，或选择 Embedded 使用 OpenClaw 管理的本地模型（如 Gemma 4）。
5. 点击 **Set Up and Start**（设置并启动）。

Companion 会写入本地配置，在 `127.0.0.1` 上启动捆绑的 gateway 并连接到它。当前的 Windows 和 macOS 发布归档未经签名，首次运行时的操作系统警告属于正常现象。参见 [docs/RELEASES.md](docs/RELEASES.md) 了解校验和、独立 CLI/gateway 归档、签名状态和维护者发布流程。

## 快速入门

从源码启动真实的本地 gateway：

```bash
export MODEL_PROVIDER_KEY="sk-..."
dotnet run --project src/OpenClaw.Cli -c Release -- start
```

Gateway 完成启动后会打印明确的阶段标记、最终的 `OpenClaw gateway ready.` 块、localhost URL、`Ctrl-C to stop`，以及 `Started with notices:` 下的非致命启动通知。然后打开：

| 界面 | URL |
|---------|-----|
| Web UI / 实时聊天 | `http://127.0.0.1:18789/chat` |
| 管理 UI | `http://127.0.0.1:18789/admin` |
| 集成 API | `http://127.0.0.1:18789/api/integration/status` |
| MCP 端点 | `http://127.0.0.1:18789/mcp` |

根 URL 重定向到 `/chat`。完整的首次运行指南（包括"前 10 分钟"操作手册和调试流程），参见 [docs/QUICKSTART.md](docs/QUICKSTART.md)。在修改代码前了解项目结构和仓库地图，参见 [docs/GETTING_STARTED.md](docs/GETTING_STARTED.md)。

如果需要直接的 gateway 后备方案而非完整的 CLI 引导流程，运行：

```bash
dotnet run --project src/OpenClaw.Gateway -c Release -- --quickstart
```

`--quickstart` 仅限交互式使用。它为当前进程应用最小化的 loopback-local 配置文件，提示缺失的 provider 输入，在常见首次运行失败时重试，成功启动后可将工作配置保存到 `~/.openclaw/config/openclaw.settings.json`。

如果 CLI 已在你的 `PATH` 中，相同的引导入口为：

```bash
openclaw start
openclaw setup
openclaw setup launch --config ~/.openclaw/config/openclaw.settings.json
openclaw setup service --config ~/.openclaw/config/openclaw.settings.json --platform all
openclaw setup status --config ~/.openclaw/config/openclaw.settings.json
openclaw upgrade check --config ~/.openclaw/config/openclaw.settings.json
openclaw upgrade rollback --config ~/.openclaw/config/openclaw.settings.json --offline
```

常用后续命令和界面：

```bash
openclaw models presets
openclaw models packages
openclaw models install gemma-4-e4b --accept-license --path ~/Downloads/gemma-4-E4B-it-Q4_K_M.gguf --mmproj-path ~/Downloads/mmproj-gemma-4-E4B-it-Q8_0.gguf
openclaw models doctor
openclaw maintenance scan --config ~/.openclaw/config/openclaw.settings.json
openclaw maintenance fix --config ~/.openclaw/config/openclaw.settings.json --dry-run
openclaw skill new "社区研究洞察提取器" --category research
openclaw skill validate community.research_insight
openclaw skills inspect ./skills/my-skill
openclaw compatibility catalog
openclaw insights
openclaw admin trajectory export --anonymize --output ./trajectory.jsonl
openclaw upgrade check --config ~/.openclaw/config/openclaw.settings.json --offline
openclaw upgrade rollback --config ~/.openclaw/config/openclaw.settings.json --offline
openclaw migrate upstream --source ./upstream-agent --target-config ~/.openclaw/config/openclaw.settings.json
```

- 技能清单：`/admin/skills`
- 维护报告：`/admin/maintenance`
- 可观测性摘要：`/admin/observability/summary`
- 运维洞察：`/admin/insights`
- 审计导出：`/admin/audit/export`
- 轨迹导出：`/admin/trajectory/export`
- 兼容性矩阵：[docs/COMPATIBILITY.md](docs/COMPATIBILITY.md)

对于本地 Ollama 设置，推荐使用原生根端点和显式预设：

```bash
openclaw setup --non-interactive --profile local --workspace ./workspace --provider ollama --model llama3.2 --model-preset ollama-general
```

OpenClaw.NET 现在将 Ollama 作为一等原生 provider 对待，使用 `http://127.0.0.1:11434`。旧的 `/v1` 端点在一个兼容周期内仍然可用，但 `openclaw models doctor` 会标记它们以便你顺利迁移。

对于 OpenClaw 管理的本地推理，使用 provider `embedded` 配合可安装包。Gemma 4 现在是主要的嵌入式本地模型路径：

```bash
openclaw models packages
openclaw models install gemma-4-e4b \
  --accept-license \
  --path ~/Downloads/gemma-4-E4B-it-Q4_K_M.gguf \
  --mmproj-path ~/Downloads/mmproj-gemma-4-E4B-it-Q8_0.gguf
openclaw setup --provider embedded --model-preset embedded-gemma-4-e4b --model gemma-4-e4b
openclaw models status gemma-4-e4b
```

包目录包含 Gemma 4 E2B、E4B、31B 和 26B-A4B GGUF 条目，以及用于适配器开发的实验性 Gemma 4 E2B LiteRT-LM 包。旧的 `gemma-local-small-q4` Gemma 3 包仍然适用于较小机器。

嵌入式视频支持基于帧：OpenClaw 在调用本地 sidecar 前将本地 `video/*` 输入采样为有序图像帧，Gemma 4 GGUF 包包含图像帧输入所需的多模态投影文件。LiteRT-LM 包是实验性的，需要 OpenClaw 兼容的适配器二进制文件；参见 [docs/LOCAL_MODELS.md](docs/LOCAL_MODELS.md)。

> **破坏性变更**: 浏览器管理使用采用账户/会话优先。使用命名的操作员账户访问 `/admin`，使用操作员账户令牌访问 Companion、CLI、API 和 WebSocket 客户端。

## 通过 Tailscale Serve 进行私有访问

OpenClaw.NET 可以通过 Tailscale Serve 在 tailnet 内部私有暴露，同时保持 gateway 绑定在 `127.0.0.1`。

这对于私有访问 `/chat`、`/admin`、`/mcp`、`/api/integration/*` 和 `/ws` 非常有用，无需将 gateway 绑定到公网。

使用引导助手获取说明：

```bash
openclaw setup tailscale serve
```

参见 [docs/deployment/TAILSCALE.md](docs/deployment/TAILSCALE.md)。

## 安全

当绑定到非 loopback 地址时，gateway **拒绝启动**，除非显式加固了危险设置（需要认证令牌、限制工具根目录、强制签名验证、拒绝 `raw:` 密钥引用）。在将 gateway 暴露到公网之前，请参阅 [SECURITY.md](SECURITY.md)。

出站网页抓取和浏览器导航默认通过 `OpenClaw:Tooling:UrlSafety` 运行。安全默认值阻止 loopback、私有/本地链路、多播和元数据主机；操作员可以有意禁用该策略，或添加 `BlockedHostGlobs` 和 `BlockedCidrs` 用于环境特定的拒绝列表。

## 文档

公开文档站点为 **[AgentQi.dev](https://agentqi.dev)**。源文档地图位于 **[docs/README.md](docs/README.md)**。入门文档：

| 文档 | 何时阅读 |
|-----|--------------|
| [docs/GETTING_STARTED.md](docs/GETTING_STARTED.md) | 项目结构、仓库地图和首次运行调试流程 |
| [docs/QUICKSTART.md](docs/QUICKSTART.md) | 运行本地实例的最短受支持路径 |
| [docs/USER_GUIDE.md](docs/USER_GUIDE.md) | Provider、工具、技能、记忆、频道和日常操作 |
| [docs/RELEASES.md](docs/RELEASES.md) | 桌面下载、发布资产和签名状态 |
| [docs/ARCHITECTURE_BOUNDARIES.md](docs/ARCHITECTURE_BOUNDARIES.md) | Core、gateway、扩展、AOT/JIT 和 Industrial Pack 边界 |
| [docs/CAPABILITY_MATRIX.md](docs/CAPABILITY_MATRIX.md) | Core、可选、实验性和 JIT-only 能力通道 |
| [docs/TOOLS_GUIDE.md](docs/TOOLS_GUIDE.md) | 原生工具目录与配置（75 个工具） |
| [docs/LOCAL_MODELS.md](docs/LOCAL_MODELS.md) | 嵌入式本地模型、基于帧的视频和实验性 LiteRT-LM 适配器说明 |
| [docs/zh-CN/START_HERE.md](docs/zh-CN/START_HERE.md) | 简体中文首次运行指南 |
| [docs/zh-CN/TOOLS_GUIDE.md](docs/zh-CN/TOOLS_GUIDE.md) | 原生工具目录与配置（简体中文） |
| [docs/zh-CN/CAPABILITY_MATRIX.md](docs/zh-CN/CAPABILITY_MATRIX.md) | 能力矩阵（简体中文） |
| [docs/zh-CN/LOOP_TECHNICAL_ARCHITECTURE.md](docs/zh-CN/LOOP_TECHNICAL_ARCHITECTURE.md) | /loop 定时循环命令技术架构（简体中文） |
| [docs/zh-CN/GOAL_TECHNICAL_ARCHITECTURE.md](docs/zh-CN/GOAL_TECHNICAL_ARCHITECTURE.md) | Goal 目标机制技术架构（简体中文） |
| [SECURITY.md](SECURITY.md) | 公网部署加固指南 |

能力通道一览：

| 通道 | 示例 |
|-----|----------|
| Core | 运行时循环、gateway、CLI、NativeAOT 友好宿主路径、OpenAI 兼容 API |
| Optional | 浏览器和 MQTT 协议包、频道、模型 provider、工作流后端 |
| Experimental | 嵌入式本地模型 sidecar 和面向适配器的包路径 |
| JIT-only | 动态插件频道、命令、钩子、provider 和原生动态 .NET 插件 |

# 入门指南

本指南面向"我克隆了仓库，但仍不知道主要部分是什么"的问题。

如需最短路径获得运行实例，使用 [QUICKSTART.md](QUICKSTART.md)。如需先了解整体心智模型，请阅读本页一次，然后运行 quickstart。

## OpenClaw.NET 是什么

OpenClaw.NET 是一个自托管的 .NET Agent 平台，由几个不同层次组成：

1. 网关进程，暴露 HTTP、WebSocket、Web UI、Admin UI 和 Webhook 端点
2. Agent 运行时，运行模型循环、选择工具、协调会话、记忆、审批和路由
3. 工具后端和集成，使 Agent 能够读取文件、运行 shell 命令、搜索网页、与通道通信
4. 可选的编排后端，如 Microsoft Agent Framework
5. 可选的客户端界面，如 CLI、桌面 Companion、TUI

最简单的理解方式：

`用户/通道 -> 网关 -> 会话/运行时 -> 模型 -> 工具/集成 -> 响应`

## 仓库主要部分

| 路径 | 内容 |
| --- | --- |
| `src/OpenClaw.Gateway` | 主 ASP.NET 宿主，启动服务器、映射端点 |
| `src/OpenClaw.Core` | 共享模型和基础设施：配置、记忆、会话、安全、可观测性 |
| `src/OpenClaw.Agent` | Agent 运行时、工具执行、插件桥接、委托 |
| `src/OpenClaw.Cli` | `openclaw` 命令行入口：setup、launch、status、admin、chat |
| `src/OpenClaw.Companion` | 桌面 Companion 应用 |
| `src/OpenClaw.Tests` | 单元和集成测试 |

## 前置条件

- .NET 10 SDK
- Git
- 可选：Node.js 20+（TS/JS 插件支持）
- 可选：Docker（容器部署）

## 推荐的首次本地运行

```bash
git clone https://github.com/clawdotnet/openclaw.net
cd openclaw.net
export MODEL_PROVIDER_KEY="sk-..."
dotnet restore && dotnet build
dotnet run --project src/OpenClaw.Cli -c Release -- setup
```

`setup` 提供：外部配置文件、env 示例、启动网关的命令、后续诊断命令。

然后启动支持的本地开发流程：

```bash
dotnet run --project src/OpenClaw.Cli -c Release -- setup launch --config ~/.openclaw/config/openclaw.settings.json
```

直接从仓库启动 Gateway 的回退方式：

```bash
dotnet run --project src/OpenClaw.Gateway -c Release -- --quickstart
```

## 启动后访问

- 浏览器 Chat UI：`http://127.0.0.1:18789/chat`
- Admin UI：`http://127.0.0.1:18789/admin`
- MCP 端点：`http://127.0.0.1:18789/mcp`
- 集成 API 状态：`http://127.0.0.1:18789/api/integration/status`

## A2A 任务快速视图

如果启用了 A2A，暴露：HTTP+JSON `/a2a`、JSON-RPC `/a2a/rpc`、Agent Card `/.well-known/agent-card.json`。

## 典型设置问题解答

### 配置在哪里？

支持路径是由 `openclaw setup` 生成的外部 JSON 配置。无需从编辑 `appsettings.json` 开始。

### Gateway 启动失败怎么办？

在交互式本地终端中，Gateway 会分类常见启动失败并提供引导式恢复路径。最快的直接恢复路径：`dotnet run --project src/OpenClaw.Gateway -c Release -- --quickstart`

### 本地开始需要沙箱吗？

不需要。支持的新手路径使用 `openclaw setup`（默认不需要沙箱后端），或设置 `OpenClaw:Sandbox:Provider=None`。

### 什么时候需要 aot vs jit？

- `aot`：裁剪安全、低复杂度运行时通道
- `jit`：更广的插件兼容性
- `auto`：让运行时根据环境选择

刚开始时不需过早优化，使用生成的默认值即可。

### 需要每个子项目吗？

大多数贡献者只需要：`OpenClaw.Gateway`、`OpenClaw.Cli`、`OpenClaw.Core`、`OpenClaw.Agent`、`OpenClaw.Tests`。

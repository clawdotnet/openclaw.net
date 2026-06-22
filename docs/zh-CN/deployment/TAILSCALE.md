# Tailscale 部署

OpenClaw.NET 可通过 Tailscale Serve 在 tailnet 内部私有暴露。

在此方案中，OpenClaw.NET 依然是本地 Agent 运行时和网关。Tailscale 提供私有网络访问和设备间的身份感知连接。

```text
Tailnet 用户/设备
    |
Tailscale Serve
    |
http://127.0.0.1:18789
    |
OpenClaw.NET Gateway
    |-- /chat
    |-- /admin
    |-- /mcp
    |-- /api/integration/*
    |-- /ws
    |-- /health
    `-- /doctor/text
```

## 推荐模式：Loopback + Serve

本地运行 OpenClaw.NET：

```bash
dotnet run --project src/OpenClaw.Cli -c Release -- start
```

或：

```bash
openclaw start
```

OpenClaw.NET 在本地监听，通常位于：

```text
http://127.0.0.1:18789
```

然后私有暴露：

```bash
tailscale serve --bg http://127.0.0.1:18789
```

从 tailnet 内的设备打开 Tailscale 提供的 HTTPS URL。

不要仅为使用 Tailscale Serve 而公开绑定 OpenClaw.NET。保持网关绑定在 loopback 地址，让 Tailscale 处理私有 tailnet 可达性。

## 常用路径

- `/chat`
- `/admin`
- `/mcp`
- `/api/integration/status`
- `/ws`
- `/health/ready`
- `/doctor/text`

## 为什么使用 Tailscale Serve？

- 避免直接暴露在公网。
- 保持 OpenClaw.NET 绑定在 `127.0.0.1`。
- 适合私有管理访问。
- 支持 tailnet ACL。
- 方便团队和多设备开发，无需更改 provider、工具、记忆、审批或通道设置。

## Serve vs Funnel

Tailscale Serve 用于私有 tailnet 访问。

Tailscale Funnel 用于公网暴露。

仅在以下场景使用 Funnel：

- 短期演示
- Webhook 测试
- 需要公网可达的公共集成

不要通过 Funnel 暴露 `/admin`，除非满足以下条件：

- 运维者认证已启用
- 已审查公开绑定的安全加固
- 不安全工具已禁用或设置了审批门槛
- 已配置 Webhook 签名
- 管理状态和 doctor 检查结果干净

将 Funnel 视为与其他公开部署路径一样对待。

## 推荐的设置命令

使用引导式设置助手打印推荐的 Serve 命令和验证步骤：

```bash
openclaw setup tailscale serve
```

使用外部配置：

```bash
openclaw setup tailscale serve --config ~/.openclaw/config/openclaw.settings.json
```

该命令不会自动启用 Tailscale，也不会更改 provider 配置。它会打印私有访问说明，在本地 Tailscale CLI 可用时进行检查，并提醒你保持 OpenClaw.NET 绑定在 loopback。

生成记录部署配置但保留本地默认值的配置：

```bash
openclaw setup --profile tailscale-serve --workspace ./workspace --provider openai --model gpt-4o --api-key env:MODEL_PROVIDER_KEY
```

`tailscale-serve` 配置的行为与本地设置配置一致，并添加了部署元数据以便 status/doctor 可见。它不会设置 `OpenClaw:Tailscale:Enabled`。

## 安全清单

- 使用 Serve 时保持 OpenClaw.NET 绑定在 `127.0.0.1`。
- 保持运维者账户启用。
- 对 CLI、API 和 WebSocket 客户端使用运维者令牌。
- 不要在 URL 中携带 provider 密钥。
- 不要对高风险工具禁用审批。
- 在任何公开暴露之前运行 `openclaw setup status`、`/doctor/text` 和 `openclaw admin posture`。
- 将 Funnel 视为公开暴露。
- 不要信任 Tailscale 身份头部用于 OpenClaw.NET 运维者认证，除非经过审查的认证集成显式启用了该行为。

## 排查指南

- `tailscale` 命令未找到：安装 Tailscale 或从 Tailscale 应用中手动配置 Serve。
- Tailnet 设备无法连接：运行 `tailscale status`，确认两台设备在同一 tailnet 中，并检查 ACL。
- 网关未运行：启动 OpenClaw.NET 并检查 `http://127.0.0.1:18789/health/ready`。
- 本地端口错误：向 `openclaw setup tailscale serve` 传递 `--local-url http://127.0.0.1:<port>` 并更新 Serve 命令。
- `/admin` 认证失败：确认你要使用的浏览器会话、启动令牌、账户令牌或 `OPENCLAW_AUTH_TOKEN` 方式。
- `/ws` 连接失败：使用 Tailscale HTTPS URL，确认认证头部/令牌，并验证网关配置允许 WebSocket 客户端。
- Funnel/Serve 混淆：运行 `tailscale serve status`，确认你没有无意间对管理界面使用 Funnel。
- 身份头部缺失：大多数设置中这是预期行为。OpenClaw.NET 仅在可见性层面报告它们，不会在此功能中将其用于认证。

## Aperture 说明

Tailscale Aperture 是一个独立的模型网关集成。当你需要集中化模型访问、Provider 路由、用量遥测或支出控制时使用 Aperture。当你需要私有访问 OpenClaw.NET 运行时本身时使用 Tailscale Serve。

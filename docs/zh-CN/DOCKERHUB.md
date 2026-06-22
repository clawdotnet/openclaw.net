# tellikoroma/openclaw.net

自托管的 OpenClaw.NET 网关 + Agent 运行时，基于 .NET（NativeAOT 友好）。

同一发布镜像的注册表镜像：

- `ghcr.io/clawdotnet/openclaw.net:latest`
- `tellikoroma/openclaw.net:latest`
- `public.ecr.aws/u6i5b9b7/openclaw.net:latest`

该镜像在端口 `18789` 上运行网关，将记忆持久化在 `/app/memory` 下。

> **破坏性变更**：`OPENCLAW_AUTH_TOKEN` 现在是容器的启动和紧急访问凭证，而非推荐的日常运维者登录方式。首次启动后请创建命名运维者账户。

## 快速开始

```bash
docker run -d --name openclaw \
  -p 18789:18789 \
  -e MODEL_PROVIDER_KEY="sk-..." \
  -e OPENCLAW_AUTH_TOKEN="$(openssl rand -hex 32)" \
  -v openclaw-memory:/app/memory \
  -v "$(pwd)/workspace:/app/workspace" \
  tellikoroma/openclaw.net:latest
```

打开 `http://127.0.0.1:18789/admin`，使用启动令牌创建第一个运维者账户，之后使用该账户登录。

## 必需环境变量

- `MODEL_PROVIDER_KEY`：LLM Provider API 密钥
- `OPENCLAW_AUTH_TOKEN`：非 loopback 容器绑定的启动/紧急访问令牌

## 常用可选环境变量

- `MODEL_PROVIDER_MODEL`（默认 `gpt-4o`）
- `MODEL_PROVIDER_ENDPOINT`（默认空）

## 容器中的默认安全加固

- Shell 工具禁用（`OpenClaw__Tooling__AllowShell=false`）
- 文件工具根目录限制在 `/app/workspace`
- JS 插件桥接默认禁用（`OpenClaw__Plugins__Enabled=false`）

## 卷

- `/app/memory`：持久化会话/分支/笔记
- `/app/workspace`：可选的读/写文件工具工作区挂载

## 健康检查

镜像包含健康检查：`/app/OpenClaw.Gateway --health-check`

## Docker Compose

```yaml
services:
  openclaw:
    image: tellikoroma/openclaw.net:latest
    restart: unless-stopped
    ports:
      - "18789:18789"
    environment:
      - MODEL_PROVIDER_KEY=${MODEL_PROVIDER_KEY}
      - OPENCLAW_AUTH_TOKEN=${OPENCLAW_AUTH_TOKEN}
      - OpenClaw__BindAddress=0.0.0.0
      - OpenClaw__Port=18789
    volumes:
      - openclaw-memory:/app/memory
      - ./workspace:/app/workspace
```

## WebChat 说明

非 loopback 绑定时需启用查询令牌：`OpenClaw__Security__AllowQueryStringToken=true`

## 运维者认证说明

- 浏览器 Admin UI 以账户/会话为主
- Companion、CLI、API 客户端和 WebSocket 客户端使用运维者账户令牌
- 网关暴露 `POST /auth/operator-token` 用于凭证换令牌
- 变更操作按 `viewer`、`operator`、`admin` 角色分权限

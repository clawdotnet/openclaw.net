# 外部编程后端

## 概述

OpenClaw.NET 现在支持 OSS 优先的外部编程后端通道，用于支持会话的编程 CLI。将 Codex CLI、Gemini CLI 和 GitHub Copilot CLI 视为 OpenClaw 可以探测、启动、流式传输和停止的外部后端。

## 架构

- `ConnectedAccountService`：创建、列出、加载和删除已连接账户，使用 ASP.NET Data Protection 保护存储的密钥 blob
- `BackendCredentialResolver`：从 env 引用、原始密钥、令牌文件或已存储的已连接账户中解析凭证
- `CodingAgentBackendRegistry`：暴露已注册的后端及其定义
- `BackendSessionCoordinator`：启动/停止后端会话，持久化会话记录和有序事件
- `CodingBackendProcessHost`：拥有 `System.Diagnostics.Process`，异步 stdout/stderr 流式传输，stdin 写入，超时/取消和退出码处理

## 配置

```json
{
  "CodingBackends": {
    "Codex": {
      "Enabled": true,
      "BackendId": "codex",
      "Provider": "codex",
      "ExecutablePath": "codex",
      "Args": ["exec"],
      "DefaultModel": "gpt-5-codex",
      "RequireWorkspace": true,
      "ReadOnlyByDefault": false,
      "WriteEnabled": true,
      "Credentials": {
        "SecretRef": "env:OPENAI_API_KEY"
      }
    }
  }
}
```

## HTTP 端点

集成端点：`GET/POST/DELETE /api/integration/accounts`、`GET /api/integration/backends`、`POST /api/integration/backends/{id}/probe`、`POST/DELETE /api/integration/backends/{id}/sessions`、SSE 流事件端点。

Admin 端点：`POST /admin/accounts/test-resolution`、`GET /admin/backends`、`POST /admin/backends/{id}/probe`。

## CLI 使用

```bash
openclaw accounts list
openclaw accounts add codex --display-name "Local Codex" --secret-ref env:OPENAI_API_KEY
openclaw backends list
openclaw backends probe codex --workspace /absolute/workspace
openclaw backends run codex --workspace /absolute/workspace --prompt "Review the current diff"
```

## 事件模型

后端发出规范化的 `BackendEvent` 记录：assistant message、stdout/stderr 输出、工具调用、shell 命令、补丁、文件读写、错误、会话完成等。每个事件可携带 `RawLine` 保留原始 CLI 输出。

## 限制

- 后端适配器是围绕供应商 CLI 的最佳努力封装
- 结构化事件解析仅当底层 CLI 暴露可用的结构化模式时有效
- 纯文本回退是启发式的
- 认证仅支持 BYO；不添加计费或 SaaS 概念

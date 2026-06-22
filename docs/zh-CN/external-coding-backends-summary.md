# OSS 外部编程后端摘要

## 新增功能

- 已连接账户存储和 CRUD，用于 OSS 凭证处理
- 后端凭证解析（密钥引用、原始密钥、令牌文件、已存储的已连接账户）
- 后端抽象、注册表、协调器和假后端
- 用于支持会话的 CLI 后端的子进程宿主
- Codex CLI、Gemini CLI 和 GitHub Copilot CLI 后端适配器
- 集成和管理 HTTP 端点
- SSE 流式传输后端会话事件
- 当提供 `OwnerSessionId` 时同步 Owner 会话历史
- CLI 命令：`openclaw accounts` 和 `openclaw backends`

## 支持的账户模式

- 直接 env 或原始密钥引用
- 直接原始密钥提交
- 本地令牌文件路径
- 已存储的已连接账户（受保护密钥 blob、存储的密钥引用、存储的令牌文件路径）

## 支持的后端

- Codex CLI
- Gemini CLI
- GitHub Copilot CLI
- 假后端（测试用）

## 限制

### Codex CLI
- 结构化事件规范化基于最佳努力，取决于 CLI 输出模式
- 当后端配置为兼容模式且探测确认 `--json` 支持时优先使用结构化输出

### Gemini CLI
- 独立于原生 OpenClaw Gemini Provider 集成
- 当 CLI 不发出结构化行时，事件规范化回退到文本解析

### GitHub Copilot CLI
- 仅通过 BYO 凭证处理认证
- 后端认证独立于 GitHub 仓库访问

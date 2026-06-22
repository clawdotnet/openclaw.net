# 外部 CLI 连接器

外部 CLI 连接器使 OpenClaw.NET 能够通过一个受治理的原生工具 `external_cli` 封装官方平台 CLI。该功能默认禁用，且有意不作为通用 shell。

官方 CLI 提供平台深度。OpenClaw.NET 提供运行时控制：命名命令允许列表、风险评分、预览、审批、脱敏、超时、审计记录和运行时事件。

## 安全模型

连接器不接受任意命令字符串。Agent 使用命名参数调用配置的连接器和命令：

```json
{
  "action": "execute",
  "connector": "gh",
  "command": "issue_list",
  "parameters": {
    "repo": "clawdotnet/openclaw.net"
  }
}
```

运行时将配置的参数模板直接展开到 `ProcessStartInfo.ArgumentList`，不经过 shell，拒绝缺失或未知参数。

## 配置

```json
{
  "OpenClaw": {
    "ExternalCli": {
      "Enabled": true,
      "DefaultTimeoutSeconds": 60,
      "MaxStdoutBytes": 262144,
      "MaxStderrBytes": 65536,
      "RedactSecrets": true,
      "AllowFreeformCommands": false,
      "RequireApprovalForMutatingCommands": true,
      "Presets": [],
      "Connectors": { }
    }
  }
}
```

重要默认值：`Enabled=false`、`AllowFreeformCommands=false`、`RequireApprovalForMutatingCommands=true`。

## 预览与审批

```bash
openclaw external preview gh repo_view --param repo=clawdotnet/openclaw.net
```

预览返回：已解析的可执行文件和参数列表、脱敏命令行、风险级别、只读/变更分类、是否需要审批、审批指纹。

审批所需的管理员执行必须包含匹配的预览指纹。

## 审计与脱敏

外部 CLI 执行写入追加式审计记录，包含连接器和命令、可执行文件、脱敏参数预览、执行者/会话/通道、审批指纹、退出码、耗时等。原始密钥不存储在审计记录中。

## 内置预设

内置预设是常见 CLI 的保守模板，默认禁用：

- `gh`：GitHub CLI（仓库查看、Issue 列表、PR 列表等）
- `az`：Azure CLI（账户、资源组只读命令）
- `kubectl`：kubectl（只读命令 + 高风险变更命令）
- `stripe`：Stripe CLI（版本 + 审批要求的客户数据命令）
- `lark`：飞书 CLI（认证状态、文档搜索/读取、消息发送）
- `github-copilot`：GitHub Copilot CLI
- `codex`：Codex CLI（只读 exec + 工作区写入 exec）
- `gemini`：Gemini CLI

## Admin API

- `GET /admin/external-cli/connectors`
- `POST /admin/external-cli/preview`
- `POST /admin/external-cli/execute`

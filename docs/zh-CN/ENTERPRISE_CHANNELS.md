# 企业 IM 频道

OpenClaw.NET 为飞书（Lark）、钉钉和企业微信提供一等企业 IM 频道适配器。每个适配器注册于网关组合中，并支持通过管理频道配置端点进行热重载运行时配置。

## 概览

| 频道 | 适配器类 | 消息去重 | 媒体支持 |
|---------|-------------|-------|-------|
| 飞书/Lark | `FeishuChannel` | ✅ 消息去重存储 | ✅ |
| 钉钉 | `DingTalkChannel` | — | — |
| 企业微信 | `WeComChannel` | — | — |

所有企业频道共享相同的操作员模型：DM 策略、允许列表和签名验证。频道特定的配置覆盖通过 `ChannelConfigStore` 持久化，网关重启后仍然存活。

## 快速开始

### 飞书（Lark）

```jsonc
// 在 appsettings.json 或等效配置中
{
  "Channels": {
    "Feishu": {
      "Enabled": true,
      "AppId": "<你的飞书应用 ID>",
      "AppSecret": "<你的飞书应用 Secret>",
      "VerificationToken": "<验证令牌>",
      "EncryptKey": "<加密密钥（如使用）>"
    }
  }
}
```

环境变量：
- `OPENCLAW_FEISHU_APP_ID`
- `OPENCLAW_FEISHU_APP_SECRET`
- `OPENCLAW_FEISHU_VERIFICATION_TOKEN`

### 钉钉

```jsonc
{
  "Channels": {
    "DingTalk": {
      "Enabled": true,
      "AppKey": "<你的钉钉应用 Key>",
      "AppSecret": "<你的钉钉应用 Secret>",
      "CorpId": "<企业 ID>"
    }
  }
}
```

### 企业微信

```jsonc
{
  "Channels": {
    "WeCom": {
      "Enabled": true,
      "CorpId": "<企业 ID>",
      "AgentId": "<应用 AgentId>",
      "Secret": "<应用 Secret>",
      "Token": "<回调 Token>",
      "EncodingAesKey": "<EncodingAESKey>"
    }
  }
}
```

## 管理频道配置

频道配置可在运行时通过管理端点管理，无需直接编辑配置文件：

| 方法 | 端点 | 用途 |
|--------|----------|---------|
| `GET` | `/admin/channels` | 列出所有已注册频道 |
| `GET` | `/admin/channels/{channelId}` | 获取频道配置 |
| `PUT` | `/admin/channels/{channelId}` | 更新频道配置 |
| `DELETE` | `/admin/channels/{channelId}` | 移除频道配置覆盖 |

通过管理 API 写入的配置覆盖持久化在 `ChannelConfigStore` 中，优先级高于文件配置，并在网关重启后保留。

## 飞书消息去重

飞书频道内置消息去重存储（`FeishuMessageDedup`），防止飞书回调重试机制导致的重复消息处理。去重条目以消息 ID 为键，具有可配置的 TTL。

## 安全

所有企业频道遵循与现有频道相同的安全模型：
- DM 策略控制谁可与 Agent 交互
- 允许列表限制发送者访问
- 签名验证确保入站 webhook 的真实性
- 令牌和密钥通过标准 `SecretResolver` 流水线解析

## 相关文档

- [能力矩阵](CAPABILITY_MATRIX.md) — 频道能力通道
- [术语表](GLOSSARY.md) — 企业频道定义
- [安全](../SECURITY.md) — 整体安全态势
- [Teams 设置](TEAMS_SETUP.md) — Microsoft Teams 频道
- [WhatsApp 设置](WHATSAPP_SETUP.md) — WhatsApp 频道

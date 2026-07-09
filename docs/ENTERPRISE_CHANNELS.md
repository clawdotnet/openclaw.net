# Enterprise IM Channels

OpenClaw.NET supports first-class enterprise IM channel adapters for Feishu (Lark), DingTalk, and WeCom (WeChat Work). Each adapter is registered in gateway composition and supports hot-reloadable runtime configuration through admin channel config endpoints.

## Overview

| Channel | Adapter Class | Dedup | Media |
|---------|-------------|-------|-------|
| Feishu (飞书/Lark) | `FeishuChannel` | ✅ message dedup store | ✅ |
| DingTalk (钉钉) | `DingTalkChannel` | — | — |
| WeCom (企业微信) | `WeComChannel` | — | — |

All enterprise channels share the same operator model: DM policy, allowlists, and signature validation. Channel-specific configuration overrides are persisted via `ChannelConfigStore` and survive gateway restarts.

## Quick Start

### Feishu (Lark)

```jsonc
// In appsettings.json or equivalent config
{
  "Channels": {
    "Feishu": {
      "Enabled": true,
      "AppId": "<your-feishu-app-id>",
      "AppSecret": "<your-feishu-app-secret>",
      "VerificationToken": "<verification-token>",
      "EncryptKey": "<encrypt-key-if-used>"
    }
  }
}
```

Environment variables:
- `OPENCLAW_FEISHU_APP_ID`
- `OPENCLAW_FEISHU_APP_SECRET`
- `OPENCLAW_FEISHU_VERIFICATION_TOKEN`

### DingTalk

```jsonc
{
  "Channels": {
    "DingTalk": {
      "Enabled": true,
      "AppKey": "<your-dingtalk-app-key>",
      "AppSecret": "<your-dingtalk-app-secret>",
      "CorpId": "<corp-id>"
    }
  }
}
```

### WeCom

```jsonc
{
  "Channels": {
    "WeCom": {
      "Enabled": true,
      "CorpId": "<corp-id>",
      "AgentId": "<agent-id>",
      "Secret": "<agent-secret>",
      "Token": "<callback-token>",
      "EncodingAesKey": "<encoding-aes-key>"
    }
  }
}
```

## Admin Channel Config

Channel configuration can be managed at runtime through admin endpoints without editing config files directly:

| Method | Endpoint | Purpose |
|--------|----------|---------|
| `GET` | `/admin/channels/{channelId}` | Get channel config |
| `POST` | `/admin/channels/{channelId}/update` | Update channel config |
| `DELETE` | `/admin/channels/{channelId}/override` | Remove channel config override |

Overrides written through the admin API are persisted in `ChannelConfigStore` and take precedence over file-based configuration. They survive gateway restarts.

## Feishu Message Deduplication

The Feishu channel includes a built-in message deduplication store (`FeishuMessageDedup`) to prevent duplicate message processing caused by Feishu's callback retry mechanism. Dedup entries are keyed by message ID and have a configurable TTL.

## Security

All enterprise channels follow the same security model as existing channels:
- DM policy controls who can interact with the agent
- Allowlists restrict sender access
- Signature validation verifies inbound webhook authenticity
- Tokens and secrets are resolved through the standard `SecretResolver` pipeline

## Related Docs

- [Capability Matrix](CAPABILITY_MATRIX.md) — channel capability lanes
- [Glossary](GLOSSARY.md) — enterprise channel definitions
- [Security](../SECURITY.md) — overall security posture
- [Teams Setup](TEAMS_SETUP.md) — Microsoft Teams channel
- [WhatsApp Setup](WHATSAPP_SETUP.md) — WhatsApp channel

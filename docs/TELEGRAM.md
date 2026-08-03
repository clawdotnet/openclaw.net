# Telegram channel

OpenClaw.NET supports two mutually exclusive inbound delivery modes for Telegram:

- `webhook` (default): Telegram sends updates to the gateway through a public HTTPS endpoint.
- `long-polling`: the gateway fetches updates from Telegram with `getUpdates`; only outbound HTTPS access is required.

Both modes use the same Telegram update handler, allowlist, recent-sender store, message pipeline, and session/memory model.

## Upgrade compatibility

Webhook remains the default. Existing Telegram configurations that do not contain `UpdateMode` continue to register and use the same webhook endpoint, path, signature validation, and secret settings after upgrading.

Long polling is opt-in only: set `UpdateMode` to `long-polling` explicitly. The gateway never calls `deleteWebhook` for an existing configuration unless long polling has been selected.

## Long polling

Long polling is a convenient choice for a single gateway running behind NAT, on a workstation, or on a private home server:

```json
{
  "OpenClaw": {
    "Channels": {
      "Telegram": {
        "Enabled": true,
        "BotTokenRef": "env:TELEGRAM_BOT_TOKEN",
        "UpdateMode": "long-polling",
        "PollingTimeoutSeconds": 30,
        "PollingRetryDelaySeconds": 5,
        "DropPendingUpdatesOnStart": false
      }
    }
  }
}
```
The gateway calls `deleteWebhook` before polling because Telegram does not allow `getUpdates` while a webhook is configured. Pending updates are preserved by default. Set `DropPendingUpdatesOnStart` to `true` only when intentionally discarding the existing queue.

Only one active long-poll consumer should use a bot token. The adapter processes updates sequentially and advances the `offset` after each handled update.

Temporary failures use bounded exponential backoff. Telegram `retry_after` values are honored for rate limits, conflicts report that another poller may be active, and permanent authentication or request errors stop the channel with an actionable error instead of retrying indefinitely.

The CLI can configure this mode without a public URL or webhook secret:

```bash
openclaw setup channel telegram \
  --non-interactive \
  --bot-token-ref env:TELEGRAM_BOT_TOKEN \
  --update-mode long-polling
```

## Webhook

Webhook mode requires a public HTTPS URL reachable by Telegram. The gateway itself does not need to own a public IP when a reverse proxy or tunnel supplies that endpoint.

```json
{
  "OpenClaw": {
    "Channels": {
      "Telegram": {
        "Enabled": true,
        "BotTokenRef": "env:TELEGRAM_BOT_TOKEN",
        "UpdateMode": "webhook",
        "WebhookPublicBaseUrl": "https://bot.example.com",
        "WebhookPath": "/telegram/inbound",
        "ValidateSignature": true,
        "WebhookSecretTokenRef": "env:TELEGRAM_WEBHOOK_SECRET"
      }
    }
  }
}
```

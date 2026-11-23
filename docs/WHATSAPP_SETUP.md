# WhatsApp First-Party Integration Setup

OpenClaw supports direct WhatsApp connectivity via two driver engines: **Baileys** (Node.js) and **whatsmeow** (Go). Both provide the same features — text and media messaging, groups, reactions, read receipts, typing indicators, and QR/pairing code authentication — through different underlying libraries.

## Prerequisites

| Driver | Runtime Required | Notes |
|--------|-----------------|-------|
| `baileys` | Node.js 18+ | Uses [@whiskeysockets/baileys](https://github.com/WhiskeySockets/Baileys), an unofficial WhatsApp Web library |
| `whatsmeow` | Pre-built binary or Go 1.21+ | Uses [whatsmeow](https://github.com/tulir/whatsmeow), a Go WhatsApp Web library |
| `simulated` | .NET 10 | Test-only driver — records operations without connecting to WhatsApp |

## Quick Start

```bash
# Automated setup — detects runtimes, installs dependencies, builds binaries
scripts/setup-whatsapp.sh
```

Or manually:

```bash
# Baileys (Node.js)
cd src/whatsapp-baileys-worker
npm install

# whatsmeow (Go)
cd src/whatsapp-whatsmeow-worker
go build -o whatsapp-whatsmeow-worker .
```

## Configuration

Add to `appsettings.json`:

```json
{
  "Channels": {
    "WhatsApp": {
      "Enabled": true,
      "Type": "first_party_worker",
      "FirstPartyWorker": {
        "Driver": "baileys",
        "StoragePath": "./memory/whatsapp-worker",
        "Accounts": [
          {
            "AccountId": "default",
            "SessionPath": "./session/default",
            "PairingMode": "qr",
            "DeviceName": "OpenClaw"
          }
        ]
      }
    }
  }
}
```

### Configuration Reference

#### `FirstPartyWorker`

| Field | Default | Description |
|-------|---------|-------------|
| `Driver` | `"baileys_csharp"` | Engine: `"baileys"`, `"whatsmeow"`, `"simulated"`, or `"baileys_csharp"` |
| `ExecutablePath` | auto-detected | Explicit path to the worker script/binary |
| `WorkingDirectory` | auto | Working directory for the worker process |
| `StoragePath` | `"./memory/whatsapp-worker"` | Root for session, media, and cache files |
| `MediaCachePath` | `{StoragePath}/media-cache` | Where downloaded media files are cached |
| `HistorySync` | `true` | Enable WhatsApp message history sync on first connect |
| `Proxy` | none | HTTP proxy URL for the WhatsApp connection |
| `Accounts` | `[]` | List of WhatsApp account configurations |

#### `Accounts[]`

| Field | Default | Description |
|-------|---------|-------------|
| `AccountId` | `"default"` | Unique identifier for this account |
| `SessionPath` | `"./session/default"` | Where session credentials are stored |
| `DeviceName` | `"OpenClaw"` | Device name shown in WhatsApp linked devices |
| `PairingMode` | `"qr"` | `"qr"` for QR code scan, `"pairing_code"` for 8-digit code |
| `PhoneNumber` | none | Required for `pairing_code` mode (E.164 format, e.g. `"15551234567"`) |
| `SendReadReceipts` | `true` | Automatically mark inbound messages as read |
| `AckReaction` | `false` | Send an emoji reaction when a message is accepted |
| `MediaCachePath` | inherited | Per-account media cache override |
| `HistorySync` | `true` | Per-account history sync override |
| `Proxy` | inherited | Per-account proxy override |

### Proxy credentials

Avoid putting proxy usernames/passwords directly in the URI. Store the complete proxy URL in OpenBao or an environment variable, then set `Proxy` to its reference:

```json
{
  "Proxy": "vault:secret/data/openclaw/prod/channels/whatsapp#proxy_url"
}
```

Use the same form for `FirstPartyWorker.Proxy` or an account's `Accounts[].Proxy`; the referenced value must be the full proxy URL. References are resolved at the worker boundary after secret pre-warming. On public binds, proxy URI userinfo and other inline credentials are rejected; `raw:` references require the explicit public-bind compatibility opt-in.

Legacy local `admin-settings.json` files with inline WhatsApp credentials remain loadable and produce a warning containing field paths only. New persisted snapshots and settings/setup responses, agent bundle exports, and audit records omit inline WhatsApp credentials. In the WhatsApp setup update API, a `null` proxy keeps the current value for that account, while an empty string clears it.

## Authentication Flow

### QR Code (default)

1. Start the gateway with `"PairingMode": "qr"`
2. The worker emits a `qr_code` auth event with the QR data
3. View the QR code:
   - **Admin UI**: Navigate to the WhatsApp setup page
   - **Companion app**: The QR is displayed automatically
   - **API**: `GET /admin/channels/whatsapp/auth-status`
   - **SSE stream**: `GET /admin/channels/{channelId}/auth/stream`
4. Scan the QR code with WhatsApp on your phone (Settings > Linked Devices > Link a Device)
5. The worker emits a `connected` event with the account's JID

### Pairing Code (headless)

For headless servers without a way to display QR codes:

1. Set `"PairingMode": "pairing_code"` and `"PhoneNumber": "15551234567"`
2. Start the gateway
3. The worker requests a pairing code from WhatsApp and emits a `pairing_code` auth event
4. On your phone, go to Settings > Linked Devices > Link a Device > Link with phone number
5. Enter the 8-digit code shown in the admin UI or API response

### Session Persistence

Credentials are stored at `SessionPath` and persist across restarts. You only need to pair once per account. If the session expires or you log out from your phone, re-pairing is required.

## Multi-Account

Configure multiple entries in the `Accounts` array:

```json
{
  "Accounts": [
    {
      "AccountId": "personal",
      "SessionPath": "./session/personal",
      "PairingMode": "qr"
    },
    {
      "AccountId": "business",
      "SessionPath": "./session/business",
      "PairingMode": "pairing_code",
      "PhoneNumber": "15559876543"
    }
  ]
}
```

Each account connects independently and has its own QR/pairing flow. Inbound messages are routed to the gateway pipeline from all accounts. Outbound messages are routed to the appropriate account based on recipient.

## Media Handling

Both drivers support inbound and outbound media:

**Inbound**: Images, videos, audio (including voice notes), documents, and stickers are downloaded to `MediaCachePath` and passed to the gateway as `[IMAGE_URL:file://...]` markers in the message text.

**Outbound**: The gateway sends a `BridgeMediaAttachment` array with each outbound message. The worker downloads media from the provided URL and sends it via WhatsApp. Supported types: image, video, audio (sent as PTT voice note), document, sticker.

## Driver Comparison

| Feature | Baileys (Node.js) | whatsmeow (Go) |
|---------|-------------------|----------------|
| Language | JavaScript/TypeScript | Go |
| Library | @whiskeysockets/baileys | go.mau.fi/whatsmeow |
| Session storage | Multi-file auth state (JSON files) | SQLite database |
| Maturity | Widely used, community-maintained | Production-grade, used by Matrix bridge |
| Protocol stability | Can break on WhatsApp updates | Generally more stable |
| Memory usage | Higher (Node.js overhead) | Lower (native binary) |
| Build step | `npm install` | `go build` |

**Recommendation**: Use **whatsmeow** for production deployments (more stable, lower resource usage). Use **Baileys** if you're already running Node.js or prefer the JavaScript ecosystem.

## Troubleshooting

### Worker not found

```
First-party WhatsApp worker executable was not found.
```

Run `scripts/setup-whatsapp.sh` or set `ExecutablePath` explicitly in the config.

### Node.js not found (Baileys)

```
Node.js is required for the Baileys WhatsApp driver but was not found.
```

Install Node.js 18+ from https://nodejs.org/ or use the whatsmeow driver instead.

### Dependencies not installed (Baileys)

```
Baileys worker dependencies not installed.
```

Run `npm install` in `src/whatsapp-baileys-worker/`.

### Connection keeps dropping

WhatsApp may disconnect for various reasons:
- Check your internet connection
- Ensure your phone has WhatsApp open and connected
- If using a proxy, verify the proxy is reachable
- Check the gateway logs for disconnect reason codes

### Session corruption

If authentication fails after a previous successful connection:
1. Stop the gateway
2. Delete the session directory (`SessionPath`)
3. Restart and re-pair

### Read receipts / reactions not working

Verify that your configuration has `SendReadReceipts: true` (the default). The bridge protocol sends the full message key (messageId + remoteJid + participant) required by both Baileys and whatsmeow. If receipts still fail, check the worker logs for JID parsing errors.

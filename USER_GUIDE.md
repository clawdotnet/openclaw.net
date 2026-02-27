# OpenClaw.NET User Guide

Welcome to the **OpenClaw.NET** User Guide! This document will walk you through the core concepts, configuring your preferred AI provider via API keys, and deploying your first agent.

## Core Concepts

OpenClaw is split into three main logical layers:
1. **The Gateway**: Handles WebSocket, HTTP, and Webhook connectivity (e.g. Telegram/Twilio). It performs authentication and passes messages.
2. **The Agent Runtime**: The cognitive loop of the framework. It handles the "ReAct" (Reasoning and Acting) loop, executing tools like Shell, Browser, or File I/O until the goal is completed.
3. **The Tools**: A set of native capabilities (15 included by default) that the Agent can invoke to interact with the world, such as Web Fetching, File Writing, or Git Operations.

---

## API Key Setup & LLM Providers

OpenClaw.NET relies on `Microsoft.Extensions.AI` to abstract away provider complexity. You can configure which provider to use via `appsettings.json` or environment variables.

### External config file (recommended for desktop app / installers)
You can point the Gateway at an additional JSON config file (merged on top of defaults):
- `--config /path/to/openclaw.json`
- or `OPENCLAW_CONFIG_PATH=/path/to/openclaw.json`

This is useful when you want to keep configuration under your OS app-data folder rather than editing `appsettings.json` in the install directory.

### Environment Variable Defaults
For the quickest start, set your API key as an environment variable before running the gateway.

**Bash / Zsh (Linux/macOS):**
```bash
export MODEL_PROVIDER_KEY="sk-..."
```

**PowerShell (Windows/macOS/Linux):**
```powershell
$env:MODEL_PROVIDER_KEY = "sk-..."
```

If you need to change the endpoint (e.g., for Azure or local models), set `MODEL_PROVIDER_ENDPOINT` similarly.

### Advanced Provider Configuration (`appsettings.json`)

To explicitly define your LLM configuration, edit `src/OpenClaw.Gateway/appsettings.json` under the `Llm` block:

```json
{
  "OpenClaw": {
    "Llm": {
      "Provider": "openai",
      "Model": "gpt-4o",
      "ApiKey": "env:MODEL_PROVIDER_KEY",
      "Temperature": 0.7,
      "MaxTokens": 4096
    }
  }
}
```

### Supported Providers

OpenClaw supports native routing for several providers out-of-the-box. Change the `Provider` field in your config to utilize them:

#### 1. OpenAI (Default)
- **Provider**: `"openai"`
- **Required**: `ApiKey`
- **Optional**: `Endpoint` (if routing through a proxy).

#### 2. Azure OpenAI
- **Provider**: `"azure-openai"`
- **Required**: `ApiKey` and `Endpoint`
- **Notes**: The `Endpoint` must be your Azure resource URL (e.g. `https://myresource.openai.azure.com/`).

#### 3. Ollama (Local AI)
- **Provider**: `"ollama"`
- **Required**: `Model` (e.g., `"llama3"` or `"mistral"`)
- **Default Endpoint**: `http://localhost:11434/v1`
- **Notes**: OpenClaw connects to Ollama's OpenAI-compatible endpoint automatically.

#### 4. Anthropic / Google / Groq / Together AI
- **Provider**: `"anthropic"`, `"google"`, `"groq"`, `"together"`
- **Required**: `ApiKey`, `Model`, and `Endpoint`
- **Notes**: These providers are accessed via the OpenAI-compatible REST abstractions. Ensure that you provide the proper base API URL as the `Endpoint`.

---

## Tooling & Sandbox

OpenClaw gives the AI extreme power. By default, it can run bash commands (`ShellTool`), navigate dynamic websites (`BrowserTool`), and read/write to your local machine.

### Security Configurations
You can lock down the agent via the `Tooling` config block:
```json
{
  "OpenClaw": {
    "Tooling": {
      "AllowShell": false,
      "AllowedReadRoots": ["/Users/telli/safe-dir"],
      "AllowedWriteRoots": ["/Users/telli/safe-dir"],
      "RequireToolApproval": true,
      "ApprovalRequiredTools": ["shell", "write_file"],
      "EnableBrowserTool": true
    }
  }
}
```

If you expose OpenClaw to the internet (a non-loopback bind address like `0.0.0.0`), the Gateway will **refuse to start** unless you explicitly harden these settings or opt-out of the safety checks.

For a complete list of all available tools and their configuration details, see the **[Tool Guide](TOOLS_GUIDE.md)**.

---

## Skills (Built-In + Custom)

OpenClaw.NET supports “skills” — reusable instruction packs loaded from `SKILL.md` files and injected into the system prompt.

Skill locations (precedence order):
1. Workspace: `$OPENCLAW_WORKSPACE/skills/<skill>/SKILL.md`
2. Managed: `~/.openclaw/skills/<skill>/SKILL.md`
3. Bundled: `skills/<skill>/SKILL.md` (shipped with the gateway)
4. Extra dirs: `OpenClaw:Skills:Load:ExtraDirs`

This repo ships a small bundled set (news digest, email triage, Home Assistant + MQTT operation). You can disable any skill via:
```json
{
  "OpenClaw": {
    "Skills": {
      "Entries": {
        "daily-news-digest": { "Enabled": false }
      }
    }
  }
}
```

---

## Interacting With Your Agent

### WebChat UI (Built-In)
The easiest way to interact with OpenClaw locally is via the embedded frontend:
1. Start the Gateway: `dotnet run --project src/OpenClaw.Gateway`
2. Open your browser to `http://127.0.0.1:18789/chat`
3. Enter your `OPENCLAW_AUTH_TOKEN` value into the **Auth Token** field at the top of the page.

WebChat token details:
- The browser client authenticates WebSocket using `?token=<value>` on the `/ws` URL.
- For non-loopback/public binds, enable `OpenClaw:Security:AllowQueryStringToken=true` if you use the built-in WebChat.
- Entered token values are stored as `openclaw_token` in browser `localStorage`.
WebChat includes a **Doctor** button which fetches `GET /doctor/text` and prints a diagnostics report (helpful for onboarding and debugging).

### Avalonia Desktop Companion
You can also interact via the C# desktop interface:
1. Start the Gateway: `dotnet run --project src/OpenClaw.Gateway`
2. Start the UI: `dotnet run --project src/OpenClaw.Companion`
The app will connect to `ws://127.0.0.1:18789/ws` automatically.

### Webhook Channels
You can configure OpenClaw to listen to messages in the background natively.
Enable them under the `Channels` block in your config.

- **Telegram**: Basic bot API support.
- **Twilio SMS**: SMS support via Twilio.
- **WhatsApp**: Official Cloud API or custom bridge support.
- Setup walkthroughs: `README.md#telegram-webhook-channel` and `README.md#twilio-sms-channel`.

### Recipient IDs (Telegram / SMS / Email)
Scheduled jobs (Cron) and outbound delivery require a `RecipientId` that is specific to each channel:
- **Email** (`ChannelId="email"`): the destination email address (e.g. `you@example.com`)
- **SMS** (`ChannelId="sms"`): an E.164 number (e.g. `+15551234567`)
- **Telegram** (`ChannelId="telegram"`): a numeric Telegram `chat.id` (not `from.id`)

To discover a Telegram `chat.id`:
1. Enable the Telegram channel and temporarily set `DmPolicy="open"` (or approve the pairing).
2. Temporarily allow inbound messages:
   - If `OpenClaw:Channels:AllowlistSemantics="legacy"`: you can leave `AllowedFromUserIds` empty.
   - If `OpenClaw:Channels:AllowlistSemantics="strict"` (recommended): set `AllowedFromUserIds=["*"]` (or use `POST /allowlists/telegram/add_latest` after you send a test message).
3. Send your bot a message from Telegram so a session is created.
4. In the WebChat UI, ask: “Use the `sessions` tool to list active sessions.”
5. Find the `telegram:<chatId>` session and use that numeric `<chatId>` in `AllowedFromUserIds` and Cron `RecipientId`.

If you keep `DmPolicy="pairing"` (recommended for internet-facing deployments), new senders will receive a 6-digit code and their messages will be ignored until approved. Approve via the gateway API:
```bash
curl -X POST "http://127.0.0.1:18789/pairing/approve?channelId=telegram&senderId=<chatId>&code=<code>"
```
If your gateway is bound to a non-loopback address and `OpenClaw:AuthToken` is set, include `-H "Authorization: Bearer $OPENCLAW_AUTH_TOKEN"`.

Once you’ve verified the right senders, you can tighten allowlists:
- `POST /allowlists/{channelId}/tighten` (replaces wildcard with paired senders for that channel)

### Tool Approvals (Supervised Mode)
If `OpenClaw:Tooling:AutonomyMode="supervised"`, the gateway will request approval before running write-capable tools (shell, write_file, etc.).
- WebChat prompts via a confirmation dialog.
- Fallbacks:
  - Reply: `/approve <approvalId> yes|no`
  - Admin API: `POST /tools/approve?approvalId=...&approved=true|false`

Webhook request size controls:
- `OpenClaw:Channels:Sms:Twilio:MaxRequestBytes` (default `65536`)
- `OpenClaw:Channels:Telegram:MaxRequestBytes` (default `65536`)
- `OpenClaw:Channels:WhatsApp:MaxRequestBytes` (default `65536`)
- `OpenClaw:Webhooks:Endpoints:<name>:MaxRequestBytes` (default `131072`)

For custom `/webhooks/{name}` routes, `MaxBodyLength` still controls prompt truncation after size validation.

---

## WhatsApp Setup

OpenClaw.NET supports WhatsApp via two methods: the **Official Meta Cloud API** and a **Bridge** (for `whatsmeow` or similar proxies).

### 1. Official Meta Cloud API
1. Create a Meta Developer App and set up "WhatsApp Business API".
2. Get your **Phone Number ID** and **Cloud API Access Token**.
3. Set your **Webhook URL** to `https://your-public-url.com/whatsapp/inbound`.
4. Set the **Verify Token** (default: `openclaw-verify`).

```json
"WhatsApp": {
  "Enabled": true,
  "Type": "official",
  "PhoneNumberId": "YOUR_PHONE_ID",
  "CloudApiTokenRef": "env:WHATSAPP_CLOUD_API_TOKEN"
}
```

### 2. WhatsApp Bridge
If you are using a proxy that handles the WhatsApp protocol (like a `whatsmeow` wrapper), use the bridge mode.

```json
"WhatsApp": {
  "Enabled": true,
  "Type": "bridge",
  "BridgeUrl": "http://your-bridge:3000/send",
  "BridgeTokenRef": "env:WHATSAPP_BRIDGE_TOKEN"
}
```

---

## Email Features

OpenClaw.NET includes a built-in **Email Tool** that allows your agent to interact with the world via email. Unlike Telegram or SMS which act as "Channels" to talking to the agent, the Email Tool is a capability the agent uses to perform tasks like sending reports or reading your inbox.

### Configuring the Email Tool

To enable the email tool, update the `OpenClaw:Plugins:Native` section in your `appsettings.json` or use environment variables.

#### Example `appsettings.json` Configuration:

```json
{
  "OpenClaw": {
    "Plugins": {
      "Native": {
        "Email": {
          "Enabled": true,
          "SmtpHost": "smtp.gmail.com",
          "SmtpPort": 587,
          "SmtpUseTls": true,
          "ImapHost": "imap.gmail.com",
          "ImapPort": 993,
          "Username": "your-email@gmail.com",
          "PasswordRef": "env:EMAIL_PASSWORD",
          "FromAddress": "your-email@gmail.com",
          "MaxResults": 10
        }
      }
    }
  }
}
```

### Authentication Security

We strongly recommend using `env:VARIABLE_NAME` for the `PasswordRef` field. 

**For PowerShell:**
```powershell
$env:EMAIL_PASSWORD = "your-app-password"
```

**For Bash/Zsh:**
```bash
export EMAIL_PASSWORD="your-app-password"
```

> [!TIP]
> If using Gmail, you **must** use an "App Password" rather than your primary password if Two-Factor Authentication is enabled.

### Using Email via the Agent

Once enabled, you can naturally ask the agent to handle emails:
- *"Send an email to boss@example.com with the subject 'Weekly Report' and a summary of my recent work."*
- *"Check my inbox for any emails from 'Support' in the last hour and summarize them."*
- *"Search my email for a receipt from Amazon and tell me the total amount."*

### Scheduled Delivery (Email Channel)

If `OpenClaw:Plugins:Native:Email:Enabled=true`, the gateway also enables an `email` **channel adapter** for scheduled jobs. This is separate from the `email` tool:
- **Email tool**: the agent decides when to send/read email as part of a conversation.
- **Email channel**: cron jobs can deliver their final response directly via SMTP, using `ChannelId="email"` and `RecipientId="<address>"`.

---

## Scheduled Tasks (Cron)

OpenClaw.NET supports scheduled prompts via `OpenClaw:Cron`. Each cron job enqueues an internal system message; the agent runs it and sends the response back through the specified channel.

Recommended fields per job:
- `SessionId`: stable session for that job (e.g. `cron:daily-news`)
- `ChannelId`: `email`, `telegram`, `sms`, etc.
- `RecipientId`: channel recipient (email address, Telegram chat id, E.164 number)
- `Subject`: used by the `email` channel (optional)

### Cron time + syntax notes
- Cron expressions are currently evaluated in **UTC**.
- Supported cron format is **5 fields**: minute hour day-of-month month day-of-week.
- Supported forms per field: `*`, `*/n`, `a,b,c`, `a-b`, or a single integer.

### Automation Recipes
**Daily news (delivered to email)**
- Use the example job in `src/OpenClaw.Gateway/appsettings.json` as a starting point.
- Prompt idea: “Summarize today’s top AI + security news. Include links and 5 bullet takeaways.”

**Inbox triage (daily digest)**
- Enable `OpenClaw:Plugins:Native:Email` and `OpenClaw:Plugins:Native:InboxZero`.
- Cron prompt idea: “Run inbox triage on the last 50 emails (dry-run). Summarize what you would archive, what needs replies, and any urgent items. Then email me the report.”

**Home status report**
- Enable `OpenClaw:Plugins:Native:HomeAssistant`.
- Cron prompt idea: “Check if any doors/windows are open, list any lights left on, and summarize any energy-usage sensors. Email me the results.”

---

## Home Automation (Home Assistant + MQTT)

OpenClaw.NET supports native (C#) smart-home control via:
- **Home Assistant** tools: `home_assistant` (read) and `home_assistant_write` (write)
- **MQTT** tools: `mqtt` (read) and `mqtt_publish` (write)

Matter support:
- OpenClaw.NET does not commission Matter devices directly; the recommended approach is to commission devices into **Home Assistant** and control them through Home Assistant’s entity/service model.

Safety model:
- Keep writes gated via tool approval by adding `home_assistant_write` and `mqtt_publish` to `OpenClaw:Tooling:ApprovalRequiredTools`.
- Use allow/deny policies (`Policy.Allow*Globs` / `Policy.Deny*Globs`) to restrict entities, services, and MQTT topics.

## Plugin Bridge (Ecosystem Compatibility)

OpenClaw.NET is designed to be compatible with the original [OpenClaw](https://github.com/openclaw/openclaw) TypeScript/JavaScript plugin ecosystem. This allows you to leverage hundreds of community plugins without rewriting them.

For a detailed breakdown of supported features and implementation details, see the **[Plugin Compatibility Guide](COMPATIBILITY.md)**.

### How it works

When you enable the plugin system, OpenClaw.NET spawns a optimized Node.js "Bridge" process for each plugin. This bridge loads the TypeScript or JavaScript files, registers the exported tools, and communicates with the .NET Gateway via a high-performance JSON-RPC protocol over local pipes.

### Requirements

- **Node.js 18+**: The bridge requires a modern Node.js runtime.
- **Enabled Config**: Set `OpenClaw:Plugins:Enabled=true` in `appsettings.json`.

### Compatibility Levels

| Feature | Support | Note |
| --- | --- | --- |
| **Tools** | ✅ Full | Bridged tools appear natively to the AI. |
| **Background Services** | ✅ Full | Lifecycle methods `start()` and `stop()` are supported. |
| **Logging** | ✅ Full | Plugin console output is captured and routed to .NET logs. |
| **Channels** | ⚠️ Partial | Registered but not yet active in the .NET gateway. |
| **Model Providers** | ❌ No | Auth flows for third-party providers must be native. |

### Installing Plugins

You can install plugins by placing them in:
1. Your workspace: `.openclaw/extensions/`
2. Your home directory: `~/.openclaw/extensions/`
3. Custom paths: configure them in `Plugins:Load:Paths`.

The agent will automatically choose the `email` tool and perform the requested actions!

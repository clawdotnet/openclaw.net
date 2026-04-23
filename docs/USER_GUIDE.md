# OpenClaw.NET User Guide

Welcome to the **OpenClaw.NET** User Guide! This document will walk you through the core concepts, configuring your preferred AI provider via API keys, and deploying your first agent.

> Upgrading from an earlier release? See [Breaking Changes](#breaking-changes) at the end of this guide.

## Recommended First Run

Start with the guided setup path:

```bash
openclaw start
```

From a source checkout, use:

```bash
dotnet run --project src/OpenClaw.Cli -c Release -- start
```

Use `--profile public` when you are preparing a reverse-proxy or internet-facing deployment. If `openclaw start` finds an existing config, it reuses it; if it needs to run setup, the flow writes an external config file, a matching env example, and prints the exact gateway launch, `--doctor`, and `openclaw admin posture` commands for that config.

Continue the supported bootstrap flow with:

```bash
openclaw start
openclaw setup launch --config ~/.openclaw/config/openclaw.settings.json
openclaw setup service --config ~/.openclaw/config/openclaw.settings.json --platform all
openclaw setup status --config ~/.openclaw/config/openclaw.settings.json
openclaw upgrade check --config ~/.openclaw/config/openclaw.settings.json
```

If you start the gateway directly from a local terminal instead of using `setup launch`, the direct fallback is:

```bash
dotnet run --project src/OpenClaw.Gateway -c Release -- --quickstart
```

That flow is interactive-only. It applies a minimal local loopback profile, prompts for missing provider inputs, retries on the common startup failures, and after a successful start can save the working config to `~/.openclaw/config/openclaw.settings.json`.

If you want raw starter files instead of the guided flow, use `openclaw init`. For the supported upstream skill, plugin, and channel compatibility surface, treat the [Compatibility Guide](COMPATIBILITY.md) as the source of truth.

After the base config exists, use the channel-specific setup wizard for the common chat integrations:

```bash
openclaw setup channel telegram --config ~/.openclaw/config/openclaw.settings.json
openclaw setup channel slack --config ~/.openclaw/config/openclaw.settings.json
openclaw setup channel discord --config ~/.openclaw/config/openclaw.settings.json
openclaw setup channel teams --config ~/.openclaw/config/openclaw.settings.json
openclaw setup channel whatsapp --config ~/.openclaw/config/openclaw.settings.json
```

These wizards update the existing external config and keep the readiness and admin surfaces aligned with what the CLI generated.

Important distinction:

- `openclaw start` is the primary one-command local entrypoint
- `openclaw setup` and `openclaw init` generate the supported onboarding configs
- directly editing `src/OpenClaw.Gateway/appsettings.json` is a lower-level path and can expose optional features that are not part of the easiest first run
- direct gateway startup now prints explicit startup phases and a ready banner with `/chat`, `/admin`, `/doctor/text`, `/health`, `/mcp`, and `/ws`

## Operator Auth Model

OpenClaw.NET now has three fixed operator roles:

- `viewer`: read-only dashboard, audit, setup status, observability, and export access
- `operator`: viewer permissions plus approvals, memory/profile/learning changes, automation execution, session promotion, and webhook replay
- `admin`: operator permissions plus settings, plugins, provider policies, accounts, and organization policy

Recommended auth flow:

1. Use `OPENCLAW_AUTH_TOKEN` once on a non-loopback deployment to bootstrap the first operator account.
2. Sign into `/admin` with the operator account username and password.
3. Exchange credentials for an operator account token when setting up Companion, API clients, CLI automation, or websocket integrations.

Operator token exchange is available at `POST /auth/operator-token`.

## Core Concepts

OpenClaw is split into three main logical layers:
1. **The Gateway**: Handles WebSocket, HTTP, and Webhook connectivity (e.g. Telegram/Twilio). It performs authentication and passes messages.
2. **The Agent Runtime**: The cognitive loop of the framework. It handles the "ReAct" (Reasoning and Acting) loop, executing tools like Shell, Browser, or File I/O until the goal is completed.
3. **The Tools**: A set of native capabilities (48 built-in at the time of writing) that the Agent can invoke to interact with the world, such as Web Fetching, File Writing, or Git Operations.

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

> **Note on Resilience & Streaming**: Configured properties like `FallbackModels` and agent constraints like the `SessionTokenBudget` are enforced uniformly across both standard HTTP API requests and real-time WebSocket streaming sessions (`RunStreamingAsync`). If a primary provider drops mid-stream, the gateway will flawlessly failover and resume generation using your fallback model.

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

#### 4. Claude / Anthropic
- **Provider**: `"anthropic"` or `"claude"`
- **Required**: `ApiKey` and `Model`
- **Optional**: `Endpoint`
- **Notes**: This uses the native Anthropic client. You only need `Endpoint` when routing through a proxy or compatible gateway.

#### 5. Gemini / Google
- **Provider**: `"gemini"` or `"google"`
- **Required**: `ApiKey` and `Model`
- **Optional**: `Endpoint`
- **Notes**: This uses the native Gemini client for chat and embeddings. You only need `Endpoint` when routing through a proxy or compatible gateway.

#### 6. Groq / Together AI / LM Studio / OpenAI-compatible
- **Provider**: `"groq"`, `"together"`, `"lmstudio"`, or `"openai-compatible"`
- **Required**: `ApiKey`, `Model`, and usually `Endpoint`
- **Notes**: These providers are accessed via the OpenAI-compatible REST abstractions. Ensure that you provide the proper base API URL as the `Endpoint` when required by the target service.

---

## Tooling & Sandbox

OpenClaw gives the AI extreme power. By default, it can run bash commands (`ShellTool`), navigate dynamic websites (`BrowserTool`), and read/write to your local machine.

### What most local users should do first

If you are just trying to get the project running locally from source:

- use the config generated by `openclaw setup`
- open `http://127.0.0.1:18789/chat` for chat
- open `http://127.0.0.1:18789/admin` for operator/admin work
- or visit `http://127.0.0.1:18789/`, which redirects to `/chat`
- ignore sandboxing unless you explicitly want isolated execution

If you are running the gateway directly from Visual Studio and want the simplest behavior, set:

```json
{
  "OpenClaw": {
    "Sandbox": {
      "Provider": "None"
    }
  }
}
```

This disables optional sandbox routing for sandbox-capable native tools and keeps execution local.

### Why the sandbox story is confusing

The current codebase supports OpenSandbox, but it is optional:

- the checked-in gateway config files now default to `OpenClaw:Sandbox:Provider=None`
- the default gateway build does not include the OpenSandbox integration unless you compile with `OpenClawEnableOpenSandbox=true`
- the supported onboarding flow does not require OpenSandbox

So if you are new to the project, the right interpretation is:

- sandboxing is an advanced deployment/runtime option
- it is not required for a normal local first run

For the full optional path, see [sandboxing.md](sandboxing.md).

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

### Installing skills from ClawHub

OpenClaw.NET skill folders are compatible with the upstream OpenClaw skill format (a folder containing `SKILL.md`).

Prerequisite: install the ClawHub CLI:
- `npm i -g clawhub` (or `pnpm add -g clawhub`)

Install into your workspace skills (recommended):
- Ensure `OPENCLAW_WORKSPACE` is set
- `openclaw clawhub install <skill-slug>`

Install into managed skills (shared across workspaces):
- `openclaw clawhub --managed install <skill-slug>`

Note: start a new Gateway session (or restart the Gateway) to pick up newly installed skills.

### Inspecting and installing local skill packages

When you already have a local upstream-style skill folder or tarball, use the built-in skill installer:

- `openclaw skills inspect ./path/to/skill`
- `openclaw skills install ./path/to/skill --dry-run`
- `openclaw skills install ./path/to/skill`
- `openclaw skills install ./path/to/skill.tgz --managed`
- `openclaw skills list`

`inspect` and `install --dry-run` report trust classification, install slug, requirements, command dispatch metadata, and warnings before any files are copied.

### Compatibility catalog for upstream packages

OpenClaw.NET also ships the pinned compatibility catalog that backs the public smoke lane:

- `openclaw compatibility catalog`
- `openclaw compatibility catalog --status compatible --kind npm-plugin`
- `openclaw compat catalog --json`

This is useful when you want a concrete list of tested upstream scenarios, including install commands, config examples, and expected failure diagnostics for known-bad cases.

This repo ships a bundled set of powerful personas and capabilities out-of-the-box (Software Developer, Deep Researcher, Data Analyst, daily news digest, email triage, Home Assistant + MQTT operations). You can disable any skill via:
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

> For operator account management (`openclaw accounts`) and external coding backend configuration (`openclaw backends`), see [External Coding Backends](external-coding-backends.md).

### WebChat UI (Built-In)
The easiest way to interact with OpenClaw locally is via the embedded frontend:
1. Start the Gateway: `dotnet run --project src/OpenClaw.Gateway`
2. Open your browser to `http://127.0.0.1:18789/chat`
3. For chat-only local usage, load the token the UI asks for. For operator/admin workflows, use `/admin` and sign in with an operator account.

The root URL (`http://127.0.0.1:18789/`) is not the main browser chat entrypoint. Use `/chat`.

WebChat token details:
- The browser client authenticates WebSocket using `?token=<value>` on the `/ws` URL.
- For non-loopback/public binds, enable `OpenClaw:Security:AllowQueryStringToken=true` if you use the built-in WebChat.
- Tokens are stored in `sessionStorage` by default.
- Enable the **Remember** checkbox to also store `openclaw_token` in `localStorage`.
WebChat includes a **Doctor** button which fetches `GET /doctor/text` and prints a diagnostics report (helpful for onboarding and debugging).

### Admin operator surfaces

The built-in admin UI at `/admin` is now the primary browser operator surface. It supports:

- username/password browser-session login
- operator account token login
- bootstrap token fallback for first-account setup and emergency access
- setup/deploy status, operator accounts, organization policy, observability, audit export, and local migration report review

For operator workflows outside the chat UI, the gateway also exposes:

- `GET /admin/posture`
- `GET /admin/setup/status`
- `GET /admin/observability/summary`
- `GET /admin/observability/series`
- `GET /admin/audit/export`
- `POST /admin/approvals/simulate`
- `GET /admin/incident/export`
- `GET /admin/operator-accounts`
- `POST /admin/operator-accounts`
- `POST /admin/operator-accounts/{id}/tokens`
- `GET /admin/organization-policy`
- `PUT /admin/organization-policy`
- `GET /admin/memory/notes`
- `GET /admin/memory/search`
- `GET /admin/memory/notes/{key}`
- `POST /admin/memory/notes`
- `DELETE /admin/memory/notes/{key}`
- `GET /admin/memory/export`
- `POST /admin/memory/import`
- `GET /admin/profiles`
- `GET /admin/profiles/{actorId}`
- `GET /admin/profiles/export`
- `POST /admin/profiles/import`
- `GET /admin/plugins`
- `GET /admin/plugins/{id}`
- `POST /admin/plugins/{id}/review`
- `POST /admin/plugins/{id}/unreview`
- `GET /admin/skills`
- `GET /admin/compatibility/catalog`
- `GET /admin/learning/proposals/{id}`
- `POST /admin/learning/proposals/{id}/rollback`

CLI mirrors:

- `openclaw admin posture`
- `openclaw admin approvals simulate`
- `openclaw admin incident export`
- `openclaw compatibility catalog`

Companion and other non-browser clients should authenticate with operator account tokens. The Companion now has an **Admin** tab that exchanges account credentials for a token and persists it through the OS-backed secret store.

These are useful for validating public-bind posture, approval-policy behavior, and exporting a redacted incident bundle during support/debugging.

For memory and learning operations, the admin API now also supports:

- searching durable note memory and inspecting project-scoped memory entries
- editing and deleting memory notes from the operator surface
- exporting/importing memory bundles that include notes, profiles, learning proposals, and automations
- browsing stored user profiles directly
- exporting/importing profile bundles for portability between deployments
- inspecting a learning proposal with provenance and computed profile diffs before approval
- rolling back an approved profile-update proposal when a learned preference should be reverted
- reviewing bridge or dynamic plugins after operator validation and clearing that review state later
- browsing the currently loaded skills with trust level, required env/config/bin dependencies, and command dispatch metadata
- browsing the pinned public compatibility catalog with pass/fail scenarios, install guidance, config examples, and expected diagnostics
- using the built-in operator dashboard to inspect session volume, approval pressure, automation health, memory activity, delegation usage, channel readiness, and plugin trust in one view
- inspecting a session's delegated child agents, delegated tool usage, and proposed changes directly from the session detail pane
- promoting a successful session into a disabled automation draft, a scoped provider policy, or a pending skill draft proposal without leaving the admin UI
- using the built-in automation center to apply reusable templates such as inbox triage, daily summary, incident follow-up, channel moderation, and repo hygiene
- reviewing learning proposals directly in the admin UI, including profile diffs, provenance, rollback, and one-click loading of automation drafts into the automation editor

Plugin trust levels shown in the admin UI and CLI are:

- `first-party`
- `upstream-compatible`
- `third-party-reviewed`
- `untrusted`

### Memory Retention Sweeper (Sessions + Branches)
Retention is opt-in and targets persisted sessions/branches only (not notes).

Key defaults:
- `OpenClaw:Memory:Retention:Enabled=false`
- `SessionTtlDays=30`
- `BranchTtlDays=14`
- `ArchiveEnabled=true` with archive-before-delete
- `ArchiveRetentionDays=30`

Recommended enablement flow:
1. Configure retention in `appsettings.json` under `OpenClaw:Memory:Retention`.
2. Run a dry-run first: `POST /memory/retention/sweep?dryRun=true`
3. Inspect status: `GET /memory/retention/status`
4. Validate `/doctor/text` warnings and retained-count trends after enabling.

The runtime also performs proactive in-memory active-session expiry sweeps, so expired sessions are evicted over time even without max-capacity pressure.

### Avalonia Desktop Companion
You can also interact via the C# desktop interface:
1. Start the Gateway: `dotnet run --project src/OpenClaw.Gateway`
2. Start the UI: `dotnet run --project src/OpenClaw.Companion`
The app will connect to `ws://127.0.0.1:18789/ws` automatically.

> **Breaking change**: Companion should now use an operator account token as its primary credential. Use the **Admin** tab to exchange username/password for a token instead of reusing the shared bootstrap token.

### Typed integration API and MCP facade
The gateway also exposes two typed automation surfaces alongside the browser UI, WebSocket endpoint, and OpenAI-compatible routes:

- `/api/integration/*` for typed operational reads and inbound message enqueueing
- `/mcp` for a gateway-hosted MCP JSON-RPC facade over the same runtime/integration data

Current integration API coverage includes:

- status and dashboard snapshots
- pending approvals and approval history
- provider and plugin health snapshots
- operator audit events
- session lists, session detail, and session timelines
- runtime event queries
- message enqueueing

Current MCP coverage includes:

- `initialize`
- `tools/list` and `tools/call`
- `resources/list`, `resources/templates/list`, and `resources/read`
- `prompts/list` and `prompts/get`

If you are building a .NET client, use `OpenClaw.Client` for typed access to both `/api/integration/*` and `/mcp`.

Example:

```csharp
using System.Text.Json;
using OpenClaw.Client;
using OpenClaw.Core.Models;

using var client = new OpenClawHttpClient("http://127.0.0.1:18789", authToken: null);

var sessions = await client.ListSessionsAsync(page: 1, pageSize: 25, query: null, CancellationToken.None);
var mcp = await client.InitializeMcpAsync(new McpInitializeRequest { ProtocolVersion = "2025-03-26" }, CancellationToken.None);

using var emptyArguments = JsonDocument.Parse("{}");
var status = await client.CallMcpToolAsync("openclaw.get_status", emptyArguments.RootElement.Clone(), CancellationToken.None);
```

On non-loopback/public binds, authenticate these surfaces with `Authorization: Bearer <operator-account-token>` for normal automation, or the bootstrap token only for first-run recovery flows.

## Upstream Migration

Use the upstream migration flow to translate an upstream-style OpenClaw tree into an external OpenClaw.NET config:

```bash
openclaw migrate upstream \
  --source ./upstream-agent \
  --target-config ~/.openclaw/config/openclaw.settings.json \
  --report ./migration-report.json
```

Dry-run is the default. `--apply` writes the translated config, imports managed `SKILL.md` packages, and writes the plugin review plan next to the target config.

### Webhook Channels
You can configure OpenClaw to listen to messages in the background natively.
Enable them under the `Channels` block in your config.

- **Telegram**: Basic bot API support.
- **Twilio SMS**: SMS support via Twilio.
- **WhatsApp**: Official Cloud API or custom bridge support.
- Setup walkthroughs: `../README.md#telegram-webhook-channel` and `../README.md#twilio-sms-channel`.

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
If your gateway is bound to a non-loopback address, include `-H "Authorization: Bearer $OPENCLAW_OPERATOR_TOKEN"` for normal operator automation, or use the bootstrap token only when you are still bootstrapping accounts.

Once you’ve verified the right senders, you can tighten allowlists:
- `POST /allowlists/{channelId}/tighten` (replaces wildcard with paired senders for that channel)

### Tool Approvals (Supervised Mode)
If `OpenClaw:Tooling:AutonomyMode="supervised"`, the gateway will request approval before running write-capable tools (shell, write_file, etc.).
- WebChat prompts via a confirmation dialog.
- On non-loopback/public binds, requester-bound HTTP approval depends on `OpenClaw:Security:RequireRequesterMatchForHttpToolApproval`.
  - `true`: the approver must match the original requester.
  - `false`: any authenticated admin/operator can approve the pending request by id.
- Fallbacks:
  - Reply: `/approve <approvalId> yes|no`
  - Admin API: `POST /tools/approve?approvalId=...&approved=true|false`

Use `POST /admin/approvals/simulate` or `openclaw admin approvals simulate` to inspect the effective result for a tool/action without mutating the live approval queue.

Webhook request size controls:
- `OpenClaw:Channels:Sms:Twilio:MaxRequestBytes` (default `65536`)
- `OpenClaw:Channels:Telegram:MaxRequestBytes` (default `65536`)
- `OpenClaw:Channels:WhatsApp:MaxRequestBytes` (default `65536`)
- `OpenClaw:Webhooks:Endpoints:<name>:MaxRequestBytes` (default `131072`)

For custom `/webhooks/{name}` routes, `MaxBodyLength` still controls prompt truncation after size validation.
If `ValidateHmac=true`, `Secret` is mandatory and validated at startup.

Compaction note:
- History compaction remains off by default.
- If you enable `OpenClaw:Memory:EnableCompaction=true`, `CompactionThreshold` must be greater than `MaxHistoryTurns`.

### Estimated token admission control

OpenClaw can optionally reject a turn before the provider call when the next turn estimate would already exceed the session budget.

Config:

```json
{
  "OpenClaw": {
    "EnableEstimatedTokenAdmissionControl": true
  }
}
```

This is off by default for compatibility with the existing post-admission budget behavior.

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
  "ValidateSignature": true,
  "WebhookAppSecretRef": "env:WHATSAPP_APP_SECRET",
  "PhoneNumberId": "YOUR_PHONE_ID",
  "CloudApiTokenRef": "env:WHATSAPP_CLOUD_API_TOKEN"
}
```

For non-loopback/public binds, official mode requires `ValidateSignature=true` and a valid app secret.

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

Bridge mode validates inbound webhook auth using `Authorization: Bearer <BridgeToken>` or `X-Bridge-Token`.
For non-loopback/public binds, `BridgeTokenRef`/`BridgeToken` is required.

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

### Cron time + syntax notes
- Cron expressions are currently evaluated in **UTC**.
- Supported cron format is **5 fields**: minute hour day-of-month month day-of-week.
- Supported forms per field: `*`, `*/n`, `a,b,c`, `a-b`, or a single integer.

### Example recipe
**Daily news (delivered to email)**
- Use the example job in `src/OpenClaw.Gateway/appsettings.json` as a starting point.
- Prompt idea: “Summarize today’s top AI + security news. Include links and 5 bullet takeaways.”

For the full per-job field reference (`SessionId`, `ChannelId`, `RecipientId`, `Subject`) and additional recipes, see the **Scheduled Tasks** section of the [Tool Guide](TOOLS_GUIDE.md).

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

## Shared Scratchpads (Notion)

OpenClaw.NET supports an optional native Notion integration for shared scratchpads and note databases:
- `notion`: read/search/list operations
- `notion_write`: append/create/update operations

Recommended use:
- shared project scratchpads
- operator-visible runbooks
- handoff notes across sessions or team members

Design constraints:
- Notion is not used for session memory, branch storage, or core retention.
- Access is bounded by `AllowedPageIds` / `AllowedDatabaseIds` plus any configured defaults.
- `DefaultPageId` is used for scratchpad-style reads/appends.
- `DefaultDatabaseId` is used for list/search/create workflows.

Recommended safety posture:
- Keep `RequireApprovalForWrites=true` unless you intentionally want autonomous writes.
- Set `ReadOnly=true` if the agent should only search/read Notion.
- Share only the specific pages/databases the integration needs. The token may have broader workspace reach than the local allowlist, so the allowlist is part of the tool boundary.

Minimal config example:

```json
"OpenClaw": {
  "Plugins": {
    "Native": {
      "Notion": {
        "Enabled": true,
        "ApiKeyRef": "env:NOTION_API_KEY",
        "DefaultPageId": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
        "DefaultDatabaseId": "yyyyyyyy-yyyy-yyyy-yyyy-yyyyyyyyyyyy",
        "AllowedPageIds": [
          "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
        ],
        "AllowedDatabaseIds": [
          "yyyyyyyy-yyyy-yyyy-yyyy-yyyyyyyyyyyy"
        ],
        "ReadOnly": false,
        "RequireApprovalForWrites": true
      }
    }
  }
}
```

## Plugin Bridge (Ecosystem Compatibility)

OpenClaw.NET is designed to be compatible with the original [OpenClaw](https://github.com/openclaw/openclaw) TypeScript/JavaScript plugin ecosystem. This allows you to leverage hundreds of community plugins without rewriting them.

OpenClaw.NET spawns a Node.js bridge process to run upstream plugins over JSON-RPC. For runtime requirements, the compatibility matrix, and install paths, see the **Bridged Tools** section of the [Tool Guide](TOOLS_GUIDE.md). For the full supported-feature breakdown, see the [Compatibility Guide](COMPATIBILITY.md).

---

## Breaking Changes

- `OPENCLAW_AUTH_TOKEN` is now the bootstrap and breakglass credential, not the recommended day-to-day operator login.
- Browser admin usage is now account/session-first.
- Companion, CLI, API, and websocket clients should use operator account tokens instead of the shared bootstrap token.
- Mutation access is role-gated. A read-only `viewer` account can no longer rely on the old "any authenticated operator can mutate" assumption.
- Bare `openclaw migrate` remains the legacy automation migration alias in this release. Upstream translation now lives under `openclaw migrate upstream`.

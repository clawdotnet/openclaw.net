# OpenClaw.NET Tool Guide

This guide provides a comprehensive overview of the native tools available in OpenClaw.NET and how to configure them securely.

---

## 🚀 How to Use / Install Tools

### How the Agent Uses Tools
You don't need to manually invoke tools! OpenClaw's cognitive architecture (the "ReAct" loop) analyzes your prompt, looks at the list of enabled tools, and decides which ones to use to accomplish your goal. 

For example, if you say *"Email my weekly report to my boss,"* the agent will automatically formulate the `email` tool call, execute it, and tell you when it's done.

### Tool Names (Important)
Tool **names** are the stable identifiers used by the agent and tool-approval system (e.g. `home_assistant_write`). Some parts of the codebase refer to “plugin ids” (often hyphenated, like `home-assistant`) — those are **not** tool names.

### How to Install New Tools

There are two primary ways to add new capabilities to your agent:

1. **Native C# Tools**
   Configure them in `src/OpenClaw.Gateway/appsettings.json`. Native tools (like `email`, `browser`, or `shell`) are built into the robust .NET runtime and offer the highest performance and AOT compatibility. See the Core and Native Plugin tool lists below.

2. **Community Node.js Plugins (The Bridge)**
   OpenClaw.NET is fully compatible with the massive ecosystem of original [OpenClaw](https://github.com/openclaw/openclaw) Node.js plugins!
   - Ensure Node.js 18+ is installed on your machine.
   - Download or clone a community plugin into your `.openclaw/extensions/` folder.
   - Run `npm install` inside that plugin's folder.
   - Restart the OpenClaw.NET gateway. The gateway will automatically detect, load, and bridge the plugin!
   - *For in-depth details on bridging, see the [Compatibility Guide](COMPATIBILITY.md).*

---

## 🏗 Core Tools
These tools are enabled by default but can be restricted via `Security` and `Tooling` configurations.

### 1. Shell Tool (`shell`)
Allows the agent to execute terminal commands.
- **Config**: `OpenClaw:Tooling:AllowShell` (bool)
- **Autonomy** (recommended): restrict what can run via `OpenClaw:Tooling:AllowedShellCommandGlobs` and block sensitive paths via `OpenClaw:Tooling:ForbiddenPathGlobs`.
- **Security**: Can be restricted by setting `RequireToolApproval: true` (or by running in `AutonomyMode=supervised`).

### 2. File System Tools (`read_file`, `write_file`)
Allows basic file operations.
- **Config**:
  - `OpenClaw:Tooling:AllowedReadRoots`: Array of paths (use `["*"]` for everything, or specify directories).
  - `OpenClaw:Tooling:AllowedWriteRoots`: Array of paths.
- **Safety**: Put `write_file` in `OpenClaw:Tooling:ApprovalRequiredTools`.
- **Autonomy** (recommended): set `OpenClaw:Tooling:WorkspaceOnly=true` and provide `OpenClaw:Tooling:WorkspaceRoot` to deny reads/writes outside your workspace; use `ForbiddenPathGlobs` for additional deny patterns.

### 3. Browser Tool (`browser`)
Allows the agent to navigate and interact with websites using Playwright.
- **Config**: `OpenClaw:Tooling:EnableBrowserTool` (bool)
- **Options**: `BrowserHeadless` (default: true), `BrowserTimeoutSeconds` (default: 30).

### 4. Memory Note Tool (`memory`)
Stores and retrieves lightweight notes in the configured memory store.
- Typical usage: “remember this”, “save a note”, “what did I note about X?”

### 4b. Memory Search Tool (`memory_search`)
Keyword search over saved memory notes (backed by SQLite FTS5 when enabled).
- Typical usage: “search memory for X”, “what did I write about Y?”
- **Config**:
  - `OpenClaw:Memory:Provider = "file" | "sqlite"`
  - `OpenClaw:Memory:Sqlite:EnableFts = true` for fast keyword search (recommended)
  - `OpenClaw:Memory:Recall:Enabled = true` to inject “Relevant memory” into context automatically

### 5. Project Memory Tool (`project_memory`)
Writes to and reads from project-scoped memory (helpful for long-running projects).
- Project scope: `OpenClaw:Memory:ProjectId` (or `OPENCLAW_PROJECT`)

### 6. Sessions Tool (`sessions`)
Admin/ops tool to list active sessions, inspect recent history, or send a cross-session message.
- Useful for discovering channel IDs / sender IDs (e.g., your Telegram chat id) and for operator workflows.

### 7. Delegate Agent Tool (`delegate_agent`)
Spawns a “sub-agent” for multi-agent delegation (only present when `OpenClaw:Delegation:Enabled=true`).

---

## 🔌 Native Plugin Tools
These must be enabled in the `OpenClaw:Plugins:Native` section of your `appsettings.json`.

### 8. Email Tool (`email`)
Send (SMTP) and Read (IMAP) emails.
- **Required Config**:
  - `OpenClaw:Plugins:Native:Email:SmtpHost`, `SmtpPort`, `SmtpUseTls`
  - `OpenClaw:Plugins:Native:Email:ImapHost`, `ImapPort`
  - `OpenClaw:Plugins:Native:Email:Username`, `PasswordRef` (recommended: `env:VARIABLE`)
  - `OpenClaw:Plugins:Native:Email:FromAddress`
- **Tip**: The `email` tool is separate from the `email` *channel adapter* used by cron delivery (see “Cron” below).

### 9. Git Tool (`git`)
Perform git operations (Clone, Pull, Commit, Push).
- **Config**: `OpenClaw:Plugins:Native:GitTools:Enabled=true`
- **Safety**: Keep push disabled unless you really want it (see `AllowPush`).

### 10. Web Search (`web_search`)
Search the web using Tavily, Brave, or SearXNG.
- **Config**: `OpenClaw:Plugins:Native:WebSearch:Enabled=true`
- **Providers**: `tavily` (default), `brave`, `searxng`
- **Required**: `ApiKey`.

### 11. Web Fetch (`web_fetch`)
Fetch and extract content from URLs (useful for summarization pipelines).
- **Config**: `OpenClaw:Plugins:Native:WebFetch:Enabled=true`

### 12. Code Execution (`code_exec`)
Execute Python, JavaScript, or Bash code in a isolated environment.
- **Backends**: `process` (local), `docker` (isolated).
- **Options**: `DockerImage`, `AllowedLanguages`.

### 13. PDF Reader (`pdf_read`)
Extract text from PDF documents.
- **Options**: `MaxPages`, `MaxOutputChars`.

### 14. Image Generation (`image_gen`)
Generate images using DALL-E.
- **Provider**: `openai`.
- **Required**: `ApiKey`.

### 15. Calendar Tool (`calendar`)
Manage Google Calendar events via the Google Calendar REST API (service account).
- **Config**: `OpenClaw:Plugins:Native:Calendar:Enabled=true`
- **Required**: `CredentialsPath` (service account JSON key file) and `CalendarId` (default: `primary`)

### 16. Database Tool (`database`)
Query SQLite, PostgreSQL, or MySQL databases.
- **Required**: `Provider`, `ConnectionString`.
- **Options**: `AllowWrite` (default: false).

### 17. Inbox Zero (`inbox_zero`)
AI-powered email triage inspired by [paperMoose/inbox-zero](https://github.com/paperMoose/inbox-zero). Works with **any IMAP email provider**.
- **Actions**: `analyze` (categorize + report), `cleanup` (archive newsletters/promos), `trash-sender` (trash all from one sender), `spam-rescue` (find false positives in spam), `categorize` (alias for analyze).
- **Config**: Set `OpenClaw:Plugins:Native:InboxZero:Enabled=true`. Requires IMAP credentials in `OpenClaw:Plugins:Native:Email`.
- **Safety**: `DryRun=true` by default — the agent reports what it *would* do without making changes.
- **Customizable**: Set `VipSenders`, `ProtectedSenders`, and `ProtectedKeywords` arrays.
- **Built-in protection**: Emails from banks (Chase, PayPal, etc.), healthcare, government, and major tech (Google, GitHub, Apple) are never auto-archived.

### 18. Home Assistant (`home_assistant`, `home_assistant_write`)
Control your smart home through a Home Assistant instance (covers Matter/Zigbee/Z-Wave via HA’s entity model).
- **Required Config**: `OpenClaw:Plugins:Native:HomeAssistant:Enabled=true`, `BaseUrl`, `TokenRef` (recommended: `env:HOME_ASSISTANT_TOKEN`)
- **Read Tool**: `home_assistant` — `list_entities`, `get_state`, `list_services`, `resolve_targets`, `describe_entity`
- **Write Tool**: `home_assistant_write` — `call_service`, `call_services`
- **Security**:
  - Use `Policy.AllowEntityIdGlobs`/`DenyEntityIdGlobs` to restrict which entities can be controlled.
  - Use `Policy.AllowServiceGlobs`/`DenyServiceGlobs` to restrict which services can be called.
  - Recommended: add `home_assistant_write` to `OpenClaw:Tooling:ApprovalRequiredTools` when `RequireToolApproval=true`.
- **Events (optional)**: `OpenClaw:Plugins:Native:HomeAssistant:Events:Enabled=true` to enqueue HA events as inbound messages (with cooldown + filters).

**Home Assistant setup tips**
- Token: Home Assistant → Profile → “Long-Lived Access Tokens” → create token → set `HOME_ASSISTANT_TOKEN`.
- Entity ids: ask the agent: “List my Home Assistant entities” (uses `home_assistant.list_entities`).
- Areas: use `home_assistant.resolve_targets(area="Living Room", domain="light")`.

### 19. MQTT (`mqtt`, `mqtt_publish`)
Integrate with MQTT brokers for DIY automation stacks (Zigbee2MQTT, ESPHome, custom sensors).
- **Required Config**: `OpenClaw:Plugins:Native:Mqtt:Enabled=true`, `Host`, `Port` (optional `UsernameRef`/`PasswordRef`)
- **Read Tool**: `mqtt` — `subscribe_once`, `get_last` (last-message cache requires `OpenClaw:Plugins:Native:Mqtt:Events:Enabled=true`)
- **Write Tool**: `mqtt_publish` — `publish`
- **Security**:
  - Publish allow/deny via `Policy.AllowPublishTopicGlobs`/`DenyPublishTopicGlobs`
  - Subscribe allow/deny via `Policy.AllowSubscribeTopicGlobs`/`DenySubscribeTopicGlobs`
  - Recommended: add `mqtt_publish` to `OpenClaw:Tooling:ApprovalRequiredTools` when `RequireToolApproval=true`.

**MQTT setup tips**
- Topic discovery (manual): use your broker’s tooling (`mosquitto_sub -v -t '#'`) in a controlled environment, then lock down policies.
- Topic discovery (agent): `mqtt.subscribe_once` for a known topic/prefix.

---

## 🛡 Security Best Practices
1. **Approval Mode**: Enable `RequireToolApproval: true` to review dangerous commands before they run.
2. **Environment Variables**: Always use `env:SECRET_NAME` for API keys and passwords instead of plain text in `appsettings.json`.
3. **Path Restricting**: Limit `AllowedReadRoots` and `AllowedWriteRoots` to your project directory.

Common approval list (example): `["shell","write_file","home_assistant_write","mqtt_publish","code_exec","git"]`

### Autonomy Modes (recommended)
`OpenClaw:Tooling:AutonomyMode` adds a hard “deny layer” across all tools:
- `readonly`: denies write-capable tools (shell, write_file, git, etc.) outright.
- `supervised` (default): enables tool approvals by default and prompts for approval on write-capable tools.
- `full`: no approval prompts by default (still respects allowlists/policies/forbidden paths).

Helpful knobs:
- `WorkspaceOnly` + `WorkspaceRoot` (workspace-only file access)
- `AllowedShellCommandGlobs` (shell allowlist)
- `ForbiddenPathGlobs` (deny sensitive paths even if other rules would allow)

### Tool Approvals (Supervised Mode)
When a tool requires approval, the gateway emits a `tool_approval_required` event to WebSocket envelope clients.
- WebChat supports approvals via a confirmation dialog.
- Fallbacks:
  - Reply in chat: `/approve <approvalId> yes|no`
  - HTTP: `POST /tools/approve?approvalId=...&approved=true|false` (Bearer-protected on non-loopback binds)

### Strict Allowlists + Onboarding Helpers
To make allowlists consistent across channels, set:
- `OpenClaw:Channels:AllowlistSemantics = "strict"`

Strict semantics:
- `[]` → deny all
- `["*"]` → allow all

Helpers (admin endpoints, Bearer-protected on non-loopback binds):
- `POST /allowlists/{channelId}/add_latest` (adds the latest seen sender to the dynamic allowlist)
- `POST /allowlists/{channelId}/tighten` (replaces wildcard with the currently paired/approved senders)

### Doctor Diagnostics
The gateway exposes:
- `GET /doctor` (JSON)
- `GET /doctor/text` (human-readable)

These reports summarize autonomy posture, allowlists, pairing, memory backend status, cron jobs, and skills.

### Media Marker Protocol (Telegram + WebChat)
The gateway/channels support portable attachment markers embedded in text (one per line):
- `[IMAGE_URL:https://...]` (Telegram sends a real photo; WebChat renders inline)
- `[FILE_URL:https://...]` (WebChat renders as a link)
- `[IMAGE:telegram:file_id=<id>]` (Telegram inbound photos are represented this way; the agent can reference it)

## ⏰ Scheduled Tasks (Cron)
OpenClaw.NET can run scheduled prompts via `OpenClaw:Cron`. For delivery, set a `ChannelId` and `RecipientId` on the job so the agent’s response is sent through that channel adapter.

Cron job delivery fields:
- `SessionId`: stable session key for the job (recommended, e.g. `cron:daily-news`)
- `ChannelId`: delivery channel (e.g. `telegram`, `sms`, `email`, `websocket`)
- `RecipientId`: channel-specific recipient (Telegram chat id, SMS E.164 number, email address, WebSocket connection id)
- `Subject`: used by the `email` channel (optional; defaults to `OpenClaw Cron: <JobName>`)
- `RunOnStartup`: when true, runs once immediately on gateway start (in addition to the schedule)

**Cron expression support (current)**
- 5 fields only: minute hour day-of-month month day-of-week
- Supported forms per field: `*`, `*/n`, `a,b,c`, `a-b`, or a single integer
- Evaluated in **UTC** (so `0 9 * * *` runs at 09:00 UTC)

**RecipientId quick reference**
- `ChannelId="email"` → `RecipientId="you@example.com"`
- `ChannelId="sms"` → `RecipientId="+15551234567"` (must be in `AllowedToNumbers`)
- `ChannelId="telegram"` → `RecipientId="<numeric chat id>"` (see “Telegram Webhook channel” in `README.md`)
- `ChannelId="whatsapp"` → `RecipientId="<phone number>"` (Meta Cloud API “to”; format depends on your WhatsApp setup)
- `ChannelId="websocket"` → `RecipientId="<connection id>"` (only works while that client is connected)

**Default cron delivery**
- If a job doesn’t set `ChannelId`, it uses `ChannelId="cron"`.
- The built-in `cron` channel writes outputs to `OpenClaw:Memory:StoragePath/cron/*.log` and logs the file path.

---

## 🌉 Bridged Tools (TypeScript/JS)

OpenClaw.NET can run original OpenClaw plugins via the **Plugin Bridge**. These tools are loaded dynamically from the `.openclaw/extensions` folder or custom paths.

### Third-Party Plugin Tools
Any tool provided by a TypeScript or JavaScript plugin (e.g., `notion-search`, `spotify-control`) is automatically exposed as a bridged tool.

- **Requirement**: Node.js 18+ installed on your system.
- **Config**: Ensure `OpenClaw:Plugins:Enabled` is set to `true`.
- **Note**: Bridged tools may have slightly higher latency than native (C#) tools due to Inter-Process Communication (IPC).

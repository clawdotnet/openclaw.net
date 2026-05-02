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
   OpenClaw.NET supports the upstream [OpenClaw](https://github.com/openclaw/openclaw) plugin format for the bridge surfaces that are currently implemented and tested.
   - Ensure Node.js 18+ is installed on your machine.
   - Download or clone a community plugin into your `.openclaw/extensions/` folder.
   - Run `npm install` inside that plugin's folder.
   - For TypeScript plugins, also ensure `jiti` is present in the plugin dependency tree.
   - Restart the OpenClaw.NET gateway. The gateway will automatically detect, load, and bridge the plugin.
   - `Runtime:Mode=aot`: supports `registerTool`, tool execution, `registerService`, plugin-packaged skills, `.js` / `.mjs` / `.ts` discovery, and the documented config-schema subset.
   - `Runtime:Mode=jit`: additionally supports `registerChannel`, `registerCommand`, `registerProvider`, and `api.on(...)`.
   - Unsupported extension-host APIs, or JIT-only capabilities in AOT mode, fail fast with explicit diagnostics instead of partially loading.
   - *For the exact matrix and current proof points, see the [Compatibility Guide](COMPATIBILITY.md).*

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
- **Runtime note**: source/setup-generated local profiles disable this tool by default because the NativeAOT-friendly gateway does not run local Playwright execution unless dynamic code or a configured non-local execution backend is available. Enable it only after configuring an execution backend or sandbox for browser automation.

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

### 7b. Canvas and A2UI Tools (`canvas_present`, `canvas_hide`, `canvas_navigate`, `canvas_snapshot`, `a2ui_push`, `a2ui_reset`, `a2ui_eval`)
Control the current websocket session's Canvas visual workspace.
- **Config**: `OpenClaw:Canvas:*`
- **Scope**: websocket sessions only; commands are routed to the active client sender for the current session.
- **A2UI**: `a2ui_push` accepts A2UI v0.8 JSONL frames for text, markdown, card, button, input, select, checklist, table, image, progress, and simple chart components.
- **Navigation**: `canvas_navigate` supports inline local HTML and `about:blank`; remote `http:` / `https:` Canvas navigation is rejected. Use the `browser` tool for remote webpages.
- **Eval**: `a2ui_eval` is capability-gated. First-party v1 clients do not advertise `a2ui.eval`, so browser-side script execution remains disabled by default.
- **Snapshots**: `canvas_snapshot` returns lightweight JSON state, not a remote browser screenshot.
- **Safety**: non-loopback deployments must explicitly opt in with `OpenClaw:Canvas:AllowOnPublicBind=true`.

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

### 20. Notion (`notion`, `notion_write`)
Use Notion as an optional shared scratchpad or note database.
- **Required Config**: `OpenClaw:Plugins:Native:Notion:Enabled=true`, `ApiKeyRef`, and at least one of `DefaultPageId`, `DefaultDatabaseId`, `AllowedPageIds`, or `AllowedDatabaseIds`
- **Read Tool**: `notion` — `read_page`, `get_note`, `list_notes`, `search`
- **Write Tool**: `notion_write` — `append_page`, `create_note`, `update_note`
- **Defaults**:
  - `DefaultPageId` is used when `read_page` or `append_page` omit `page_id`
  - `DefaultDatabaseId` is used when `list_notes`, `search`, or `create_note` omit `database_id`
- **Security**:
  - Restrict access with `AllowedPageIds` and `AllowedDatabaseIds`
  - `ReadOnly=true` omits `notion_write`
  - `RequireApprovalForWrites=true` forces `notion_write` into the effective approval-required tool set even if global approvals are otherwise off
  - The Notion token may have broader workspace access than the configured allowlists, so share only the pages/databases you intend to expose and keep the allowlist narrow

**Notion setup tips**
- Create a dedicated internal integration in Notion and store the token in `NOTION_API_KEY`.
- Share the target page/database explicitly with that integration.
- Start with one scratchpad page and one notes database rather than a workspace-wide token + broad allowlist.

---

## 🛡 Security Best Practices
1. **Approval Mode**: Enable `RequireToolApproval: true` to review dangerous commands before they run.
2. **Environment Variables**: Always use `env:SECRET_NAME` for API keys and passwords instead of plain text in `appsettings.json`.
3. **Path Restricting**: Limit `AllowedReadRoots` and `AllowedWriteRoots` to your project directory.

Common approval list (example): `["shell","write_file","home_assistant_write","mqtt_publish","notion_write","code_exec","git"]`

### Autonomy Modes (recommended)
`OpenClaw:Tooling:AutonomyMode` adds a hard “deny layer” across all tools:
- `readonly`: denies write-capable tools (shell, write_file, git, etc.) outright.
- `supervised` (default): enables tool approvals by default and prompts for approval on write-capable tools.
- `full`: no approval prompts by default (still respects allowlists/policies/forbidden paths).

### Hardening Setups (progressive, not rigid)
Defaults in OpenClaw.NET intentionally favor compatibility (`ReadOnlyMode=false`, approvals optional).
Use these profiles to harden in stages:

1. **Compatibility-first (default-like)**
   - Keep existing behavior, then tighten targeted policies.
   - Suggested knobs:
     - `OpenClaw:Tooling:ReadOnlyMode=false`
     - `OpenClaw:Tooling:RequireToolApproval=false`
     - Restrict plugin write scopes with provider-specific allow/deny globs.

2. **Supervised operations (recommended for most teams)**
   - Keep writes available, but require approval for mutating tools.
   - Suggested knobs:
     - `OpenClaw:Tooling:ReadOnlyMode=false`
     - `OpenClaw:Tooling:RequireToolApproval=true`
     - `OpenClaw:Tooling:ApprovalRequiredTools=["shell","write_file","code_exec","database","email","home_assistant_write","mqtt_publish","notion_write"]`
     - Optional: `OpenClaw:Security:RequireRequesterMatchForHttpToolApproval=true`

3. **Read-only lockdown (incident response / audit mode)**
   - Disable all mutating tool actions globally while preserving read/analysis capabilities.
   - Suggested knobs:
     - `OpenClaw:Tooling:ReadOnlyMode=true`
     - Keep `RequireToolApproval=true` for defense in depth on any remaining risky tools.

When `ReadOnlyMode=true`, OpenClaw denies write-capable actions for:
- core tools: `shell`, `write_file`
- native plugin tools: `code_exec`, `database` (`execute` action), `email` (`send` action), `home_assistant_write`, `mqtt_publish`, `notion_write`

This model lets you start permissive and progressively harden without breaking existing deployments by default.

### Copy/Paste Hardening Profiles
These snippets are intentionally minimal. Merge into `OpenClaw` settings and adjust per environment.

#### Dev (fast iteration, low friction)
```json
{
  "OpenClaw": {
    "Tooling": {
      "ReadOnlyMode": false,
      "RequireToolApproval": false,
      "AllowShell": true,
      "WorkspaceOnly": false,
      "AllowedReadRoots": ["*"],
      "AllowedWriteRoots": ["*"],
      "ApprovalRequiredTools": ["shell", "write_file"]
    }
  }
}
```

#### Staging (supervised, production-like)
```json
{
  "OpenClaw": {
    "Security": {
      "RequireRequesterMatchForHttpToolApproval": true
    },
    "Tooling": {
      "ReadOnlyMode": false,
      "RequireToolApproval": true,
      "AllowShell": true,
      "WorkspaceOnly": true,
      "WorkspaceRoot": "env:OPENCLAW_WORKSPACE",
      "AllowedReadRoots": ["/app/workspace"],
      "AllowedWriteRoots": ["/app/workspace"],
      "ApprovalRequiredTools": [
        "shell",
        "write_file",
        "code_exec",
        "database",
        "email",
        "home_assistant_write",
        "mqtt_publish"
      ]
    }
  }
}
```

#### Prod (read-only lockdown baseline)
```json
{
  "OpenClaw": {
    "Security": {
      "RequireRequesterMatchForHttpToolApproval": true
    },
    "Tooling": {
      "ReadOnlyMode": true,
      "RequireToolApproval": true,
      "AllowShell": false,
      "WorkspaceOnly": true,
      "WorkspaceRoot": "env:OPENCLAW_WORKSPACE",
      "AllowedReadRoots": ["/app/workspace"],
      "AllowedWriteRoots": ["/app/workspace"],
      "ApprovalRequiredTools": [
        "shell",
        "write_file",
        "code_exec",
        "database",
        "email",
        "home_assistant_write",
        "mqtt_publish"
      ]
    }
  }
}
```

Notes:
- `ReadOnlyMode=true` blocks mutating tool actions but still allows read/analysis operations.
- Start with staging profile in non-prod to discover legitimate write workflows before enforcing prod lockdown.
- Keep secrets as `env:` references instead of inline values.

Helpful knobs:
- `WorkspaceOnly` + `WorkspaceRoot` (workspace-only file access)
- `AllowedShellCommandGlobs` (shell allowlist)
- `ForbiddenPathGlobs` (deny sensitive paths even if other rules would allow)

### Tool Approvals (Supervised Mode)
When a tool requires approval, the gateway emits a `tool_approval_required` event to WebSocket envelope clients.
- WebChat supports approvals via a confirmation dialog.
- On non-loopback/public binds, HTTP approval behavior depends on `OpenClaw:Security:RequireRequesterMatchForHttpToolApproval`.
  - `true`: approval is tied to the original requester (`channelId` + `senderId`).
  - `false`: any authenticated admin/operator can approve the pending request by id.
- Fallbacks:
  - Reply in chat: `/approve <approvalId> yes|no`
  - HTTP: `POST /tools/approve?approvalId=...&approved=true|false` (admin override; Bearer-protected on non-loopback binds)
- Read-only simulator:
  - `POST /admin/approvals/simulate`
  - `openclaw admin approvals simulate`
  - The simulator now explains the effective execution backend, sandbox mode, workspace requirement, approval verdict, and the first blocking policy (`autonomy` vs `approval`) without mutating the live queue.

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
They also include a security posture section covering public-bind approval mode, browser-session/proxy safety, sandbox posture, and plugin transport risk.

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
- `ChannelId="telegram"` → `RecipientId="<numeric chat id>"` (see “Telegram Webhook channel” in `../README.md`)
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
- **Mode**: `OpenClaw:Runtime:Mode=aot` for the strict low-memory lane, or `jit` for channels/commands/providers/hooks and native dynamic plugins.
- **Note**: Bridged tools may have slightly higher latency than native (C#) tools due to Inter-Process Communication (IPC).

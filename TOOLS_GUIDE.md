# OpenClaw.NET Tool Guide

This guide provides a comprehensive overview of the native tools available in OpenClaw.NET and how to configure them securely.

---

## 🚀 How to Use / Install Tools

### How the Agent Uses Tools
You don't need to manually invoke tools! OpenClaw's cognitive architecture (the "ReAct" loop) analyzes your prompt, looks at the list of enabled tools, and decides which ones to use to accomplish your goal. 

For example, if you say *"Email my weekly report to my boss,"* the agent will automatically formulate the `email` tool call, execute it, and tell you when it's done.

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
- **Security**: Can be restricted by setting `RequireToolApproval: true`.

### 2. File System Tools (`read_file`, `write_file`, `list_dir`)
Allows basic file operations.
- **Config**: 
  - `AllowedReadRoots`: Array of paths (use `["*"]` for everything, or specify directories).
  - `AllowedWriteRoots`: Array of paths.

### 3. Browser Tool (`browser`)
Allows the agent to navigate and interact with websites using Playwright.
- **Config**: `OpenClaw:Tooling:EnableBrowserTool` (bool)
- **Options**: `BrowserHeadless` (default: true), `BrowserTimeoutSeconds` (default: 30).

---

## 🔌 Native Plugin Tools
These must be enabled in the `Plugins:Native` section of your `appsettings.json`.

### 4. Email Tool (`email`)
Send (SMTP) and Read (IMAP) emails.
- **Required Config**:
  - `SmtpHost`, `SmtpPort`, `SmtpUseTls`
  - `ImapHost`, `ImapPort`
  - `Username`, `PasswordRef` (recommended: `env:VARIABLE`)
  - `FromAddress`

### 5. Git Tool (`git-tools`)
Perform git operations (Clone, Pull, Commit, Push).
- **Options**: `AllowPush` (default: false).

### 6. Web Search (`web-search`)
Search the web using Tavily, Brave, or SearXNG.
- **Providers**: `tavily` (default), `brave`, `searxng`.
- **Required**: `ApiKey`.

### 7. Code Execution (`code-exec`)
Execute Python, JavaScript, or Bash code in a isolated environment.
- **Backends**: `process` (local), `docker` (isolated).
- **Options**: `DockerImage`, `AllowedLanguages`.

### 8. PDF Reader (`pdf-read`)
Extract text from PDF documents.
- **Options**: `MaxPages`, `MaxOutputChars`.

### 9. Image Generation (`image-gen`)
Generate images using DALL-E.
- **Provider**: `openai`.
- **Required**: `ApiKey`.

### 10. Database Tool (`database`)
Query SQLite, PostgreSQL, or MySQL databases.
- **Required**: `Provider`, `ConnectionString`.
- **Options**: `AllowWrite` (default: false).

---

## 🛡 Security Best Practices
1. **Approval Mode**: Enable `RequireToolApproval: true` to review dangerous commands before they run.
2. **Environment Variables**: Always use `env:SECRET_NAME` for API keys and passwords instead of plain text in `appsettings.json`.
3. **Path Restricting**: Limit `AllowedReadRoots` and `AllowedWriteRoots` to your project directory.

---

## 🌉 Bridged Tools (TypeScript/JS)

OpenClaw.NET can run original OpenClaw plugins via the **Plugin Bridge**. These tools are loaded dynamically from the `.openclaw/extensions` folder or custom paths.

### 11. Third-Party Plugin Tools
Any tool provided by a TypeScript or JavaScript plugin (e.g., `notion-search`, `spotify-control`) is automatically exposed as a bridged tool.

- **Requirement**: Node.js 18+ installed on your system.
- **Config**: Ensure `OpenClaw:Plugins:Enabled` is set to `true`.
- **Note**: Bridged tools may have slightly higher latency than native (C#) tools due to Inter-Process Communication (IPC).

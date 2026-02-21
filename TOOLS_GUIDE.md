# OpenClaw.NET Tool Guide

This guide provides a comprehensive overview of the native tools available in OpenClaw.NET and how to configure them securely.

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

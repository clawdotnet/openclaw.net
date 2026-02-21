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

## Interacting With Your Agent

### WebChat UI (Built-In)
The easiest way to interact with OpenClaw locally is via the embedded frontend:
1. Start the Gateway: `dotnet run --project src/OpenClaw.Gateway`
2. Open your browser to `http://127.0.0.1:18789/chat`
3. Enter your `OPENCLAW_AUTH_TOKEN` value into the **Auth Token** field at the top of the page.

### Avalonia Desktop Companion
You can also interact via the C# desktop interface:
1. Start the Gateway: `dotnet run --project src/OpenClaw.Gateway`
2. Start the UI: `dotnet run --project src/OpenClaw.Companion`
The app will connect to `ws://127.0.0.1:18789/ws` automatically.

### Webhook Channels
You can configure OpenClaw to listen to SMS or Telegram messages in the background natively.
Enable them under the `Channels` block in your config.
---

## Email Features

OpenClaw.NET includes a built-in **Email Tool** that allows your agent to interact with the world via email. Unlike Telegram or SMS which act as "Channels" to talking to the agent, the Email Tool is a capability the agent uses to perform tasks like sending reports or reading your inbox.

### Configuring the Email Tool

To enable the email tool, update the `Plugins:Native` section in your `appsettings.json` or use environment variables.

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

---

## Plugin Bridge (Ecosystem Compatibility)

OpenClaw.NET is designed to be compatible with the original [OpenClaw](https://github.com/openclaw/openclaw) TypeScript/JavaScript plugin ecosystem. This allows you to leverage hundreds of community plugins without rewriting them.

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

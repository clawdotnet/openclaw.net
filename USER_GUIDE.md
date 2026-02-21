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
For the quickest start, set your API key as an environment variable before running the gateway:
```bash
export MODEL_PROVIDER_KEY="sk-..."
```
If you need to change the endpoint (e.g., for Azure or local models), set:
```bash
export MODEL_PROVIDER_ENDPOINT="https://my-endpoint.com/v1"
```

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

---

## Interacting With Your Agent

### Avalonia Desktop Companion
The easiest way to interact with OpenClaw locally is via the desktop interface:
1. Start the Gateway: `dotnet run --project src/OpenClaw.Gateway`
2. Start the UI: `dotnet run --project src/OpenClaw.Companion`
The app will connect to `ws://127.0.0.1:18789/ws` automatically.

### Webhook Channels
You can configure OpenClaw to listen to SMS or Telegram messages in the background natively.
Enable them under the `Channels` block in your config.

> **Tip**: If you configure a Cron Job under the `Cron` block, OpenClaw can actively "wake up" and begin a task without you prompting it!

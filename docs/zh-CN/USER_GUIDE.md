# OpenClaw.NET 用户指南

欢迎阅读 **OpenClaw.NET** 用户指南！本文档将带你了解核心概念、通过 API 密钥配置你首选的 AI 提供商，以及部署你的第一个 Agent。

> 从早期版本升级？请参阅本指南末尾的[破坏性更改](#breaking-changes)。

## 推荐的首次运行

从引导式设置路径开始：

```bash
openclaw start
```

从源代码检出，使用：

```bash
dotnet run --project src/OpenClaw.Cli -c Release -- start
```

当你准备反向代理或面向互联网的部署时，使用 `--profile public`。如果 `openclaw start` 找到现有配置，它会复用它；如果需要运行设置，流程会写入外部配置文件、匹配的环境变量示例，并打印该配置的确切网关启动、`--doctor` 和 `openclaw admin posture` 命令。

对于从 tailnet 中其他设备的私有访问，保持 OpenClaw.NET 绑定到 `127.0.0.1` 并使用 Tailscale Serve。引导助手打印推荐的 Serve 命令并检查：

```bash
openclaw setup tailscale serve
```

请参阅 [deployment/TAILSCALE.md](deployment/TAILSCALE.md)。

继续支持的引导流程：

```bash
openclaw start
openclaw setup launch --config ~/.openclaw/config/openclaw.settings.json
openclaw setup service --config ~/.openclaw/config/openclaw.settings.json --platform all
openclaw setup status --config ~/.openclaw/config/openclaw.settings.json
openclaw upgrade check --config ~/.openclaw/config/openclaw.settings.json
openclaw upgrade rollback --config ~/.openclaw/config/openclaw.settings.json --offline
```

对于本地模型安装，支持的路径现在是：

```bash
openclaw setup --non-interactive --profile local --workspace ./workspace --provider ollama --model llama3.2 --model-preset ollama-general
openclaw models presets
openclaw models doctor
openclaw maintenance scan --config ~/.openclaw/config/openclaw.settings.json
```

这给了你一个显式的本地预设、原生 Ollama 路由、兼容性或回退差距的医生指导，以及报告存储漂移、提示预算压力和首要运维操作的维护扫描。

如果你直接从本地终端启动网关而不是使用 `setup launch`，直接回退是：

```bash
dotnet run --project src/OpenClaw.Gateway -c Release -- --quickstart
```

该流程仅交互式。它应用最小本地环回配置文件，提示缺失的提供商输入，在常见的启动失败时重试，并在成功启动后可以将工作配置保存到 `~/.openclaw/config/openclaw.settings.json`。

如果你想要原始启动文件而不是引导流程，使用 `openclaw init`。对于受支持的上游技能、插件和频道兼容性表面，将[兼容性指南](COMPATIBILITY.md)视为真实来源。

在基础配置存在后，使用频道特定设置向导进行常见的聊天集成：

```bash
openclaw setup channel telegram --config ~/.openclaw/config/openclaw.settings.json
openclaw setup channel slack --config ~/.openclaw/config/openclaw.settings.json
openclaw setup channel discord --config ~/.openclaw/config/openclaw.settings.json
openclaw setup channel teams --config ~/.openclaw/config/openclaw.settings.json
openclaw setup channel whatsapp --config ~/.openclaw/config/openclaw.settings.json
```

这些向导更新现有的外部配置，并使就绪状态和管理表面与 CLI 生成的内容保持一致。

重要区别：

- `openclaw start` 是主要的一命令本地入口点
- `openclaw setup` 和 `openclaw init` 生成受支持的引导配置
- 直接编辑 `src/OpenClaw.Gateway/appsettings.json` 是较低级别的路径，可能暴露不是最简单首次运行一部分的可选功能
- 直接网关启动现在打印显式的启动阶段和带 `/chat`、`/admin`、`/doctor/text`、`/health`、`/mcp` 和 `/ws` 的就绪横幅

## A2A 任务快速视图

如果启用了 A2A（`OpenClaw:MicrosoftAgentFramework:EnableA2A=true`），OpenClaw 暴露：

- HTTP+JSON：`/a2a`
- JSON-RPC：`/a2a/rpc`
- Agent Card 发现：`/.well-known/agent-card.json`

A2A 请求以协议任务语义运行。`message:send` 和 `message:stream` 都在任务上下文中执行，流式传输遵循标准生命周期：

- `submitted`
- `working`
- 终端 `completed` 或 `failed`

当提供任务 id 时，任务取消通过 A2A 处理器进行。当前任务存储是内存中的（`ITaskStore`），因此任务状态在进程重启后不持久。

有关完整的 A2A 行为和运维说明，请参阅 [a2a.md](a2a.md)。

## 运维人员认证模型

OpenClaw.NET 现在有三个固定的运维角色：

- `viewer`：只读仪表板、审计、设置状态、可观测性和导出访问
- `operator`：viewer 权限加上审批、记忆/配置文件/学习更改、自动化执行、会话提升和 webhook 重放
- `admin`：operator 权限加上设置、插件、提供商策略、账户和组织策略

推荐的认证流程：

1. 在非环回部署上使用 `OPENCLAW_AUTH_TOKEN` 一次来引导第一个运维账户。
2. 使用运维账户用户名和密码登录 `/admin`。
3. 在设置 Companion、API 客户端、CLI 自动化或 websocket 集成时，将凭据交换为运维账户令牌。

运维令牌交换在 `POST /auth/operator-token` 可用。

## 核心概念

OpenClaw 分为三个主要逻辑层：
1. **网关（Gateway）**：处理 WebSocket、HTTP 和 Webhook 连接（例如 Telegram/Twilio）。它执行认证并传递消息。
2. **Agent 运行时（Agent Runtime）**：框架的认知循环。它处理"ReAct"（推理和行动）循环，执行 Shell、Browser 或 File I/O 等工具，直到目标完成。
3. **工具（Tools）**：一组原生能力（本文撰写时 48 个内置工具），Agent 可以调用它们与世界交互，例如 Web 获取、文件写入或 Git 操作。

---

## API 密钥设置与 LLM 提供商

OpenClaw.NET 依赖 `Microsoft.Extensions.AI` 来抽象提供商复杂性。你可以通过 `appsettings.json` 或环境变量配置使用哪个提供商。

### 外部配置文件（推荐用于桌面应用 / 安装程序）
你可以将 Gateway 指向一个额外的 JSON 配置文件（合并在默认值之上）：
- `--config /path/to/openclaw.json`
- 或 `OPENCLAW_CONFIG_PATH=/path/to/openclaw.json`

当你想要将配置保留在操作系统应用数据文件夹中，而不是编辑安装目录中的 `appsettings.json` 时，这很有用。

### 环境变量默认值
为了最快启动，在运行网关之前将 API 密钥设置为环境变量。

**Bash / Zsh（Linux/macOS）：**
```bash
export MODEL_PROVIDER_KEY="sk-..."
```

**PowerShell（Windows/macOS/Linux）：**
```powershell
$env:MODEL_PROVIDER_KEY = "sk-..."
```

如果你需要更改端点（例如 Azure 或本地模型），类似地设置 `MODEL_PROVIDER_ENDPOINT`。

### 高级提供商配置（`appsettings.json`）

要显式定义 LLM 配置，在 `Llm` 块下编辑 `src/OpenClaw.Gateway/appsettings.json`：

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

> **关于弹性和流式的说明**：配置的属性如 `FallbackModels` 和 Agent 约束如 `SessionTokenBudget` 在标准 HTTP API 请求和实时 WebSocket 流式会话（`RunStreamingAsync`）中都统一强制执行。如果主提供商中途断开，网关将无瑕疵地故障转移并使用回退模型恢复生成。

### 受支持的提供商

OpenClaw 开箱即支持多个提供商的原生路由。更改配置中的 `Provider` 字段以使用它们：

#### 1. OpenAI（默认）
- **Provider**：`"openai"`
- **必需**：`ApiKey`
- **可选**：`Endpoint`（如果通过代理路由）。

#### 2. Azure OpenAI
- **Provider**：`"azure-openai"`
- **必需**：`ApiKey` 和 `Endpoint`
- **说明**：`Endpoint` 必须是你的 Azure 资源 URL（例如 `https://myresource.openai.azure.com/`）。

#### 3. Ollama（本地 AI）
- **Provider**：`"ollama"`
- **必需**：`Model`（例如 `"llama3"` 或 `"mistral"`）
- **默认端点**：`http://127.0.0.1:11434`
- **推荐设置**：选择显式预设，如 `ollama-general`、`ollama-agentic` 或 `ollama-vision`
- **说明**：OpenClaw 使用 Ollama 的原生 `/api/chat` 和 `/api/embed` 端点。旧版 `/v1` 兼容 URL 仍然加载，但 `openclaw models doctor` 会警告，以便你可以迁移到原生基础 URL。

#### 4. 嵌入式本地模型
- **Provider**：`"embedded"`
- **必需**：一个经过验证的本地模型包和一个本地 sidecar 运行时
- **推荐设置**：`embedded-gemma-small-q4` 用于首次运行的私密/离线辅助任务
- **说明**：OpenClaw 拥有包安装/验证、缓存路径、sidecar 启动、健康检查和请求映射。视频支持是基于帧的：本地 `video/*` 输入在模型调用前被采样为有序的图像帧。LiteRT-LM 包是实验性的，需要 OpenClaw 兼容的适配器二进制文件。请参阅[嵌入式本地模型](LOCAL_MODELS.md)。对于可以将 `T0`-`T3` 回合映射到现有模型配置文件和工具允许列表的 OpenSquilla 风格回合路由层，请参阅 [OpenSquilla 动态回合路由](opensquilla-dynamic-turn-routing.md)。

#### 5. Claude / Anthropic
- **Provider**：`"anthropic"` 或 `"claude"`
- **必需**：`ApiKey` 和 `Model`
- **可选**：`Endpoint`
- **说明**：这使用原生 Anthropic 客户端。你仅在通过代理或兼容网关路由时需要 `Endpoint`。

#### 6. Gemini / Google
- **Provider**：`"gemini"` 或 `"google"`
- **必需**：`ApiKey` 和 `Model`
- **可选**：`Endpoint`
- **说明**：这使用原生 Gemini 客户端进行聊天和嵌入。你仅在通过代理或兼容网关路由时需要 `Endpoint`。

#### 7. Groq / Together AI / LM Studio / OpenAI 兼容
- **Provider**：`"groq"`、`"together"`、`"lmstudio"` 或 `"openai-compatible"`
- **必需**：`ApiKey`、`Model`，通常还有 `Endpoint`
- **说明**：这些提供商通过 OpenAI 兼容的 REST 抽象进行访问。确保在目标服务需要时提供适当的基础 API URL 作为 `Endpoint`。

#### 8. Tailscale 的 Aperture
- **Provider**：`"aperture"` 或带有 Aperture 端点的 `"openai-compatible"`
- **必需**：`Endpoint`/`BaseUrl` 和 `Model`；`ApiKey` 对于 bearer-token 模式是必需的
- **可选**：`AuthMode = "tailnet-identity"` 用于无需提供商 bearer 令牌的 tailnet 身份访问
- **说明**：Aperture 是一个可选的上游 AI 网关路由。OpenClaw.NET 仍然拥有 Agent、工具、会话、审批、记忆、频道、MCP 和运行时治理。请求元数据头默认禁用，仅在 `SendRequestMetadata` 显式启用时发送。关联 ID 头名称可通过模型配置项 `CorrelationIdHeader` 自定义（默认：`X-OpenClaw-Correlation-Id`）。

设置助手：

```bash
openclaw setup provider aperture \
  --endpoint https://YOUR_APERTURE_ENDPOINT \
  --model YOUR_APERTURE_MODEL_ROUTE \
  --auth-mode bearer \
  --env-var OPENCLAW_APERTURE_TOKEN
```

对于私有运行时访问，请参阅 [deployment/TAILSCALE.md](deployment/TAILSCALE.md)。Aperture 是一个单独的可选上游模型网关路由。

#### 9. Microsoft.Extensions.AI 提供商桥接
- **Provider**：你的动态提供商 id，例如 `"my-meai-provider"`
- **必需**：JIT 运行时模式、动态原生插件已启用、实现 `IMicrosoftExtensionsAiChatClientFactory` 的工厂，以及至少一个模型 id
- **说明**：此可选桥接适用于你已经拥有 `IChatClient` 的高级 .NET 集成。OpenClaw 仍然拥有路由、策略、预算检查、追踪、审批、会话和用量核算。请参阅 [Microsoft.Extensions.AI 提供商桥接](providers/microsoft-extensions-ai.md)。

---

## 工具与沙盒

OpenClaw 赋予 AI 极大的权力。默认情况下，它可以运行 bash 命令（`ShellTool`）、导航动态网站（`BrowserTool`）以及读写你的本地机器。

### 大多数本地用户应该首先做什么

如果你只是试图从源代码在本地运行项目：

- 使用 `openclaw setup` 生成的配置
- 打开 `http://127.0.0.1:18789/chat` 进行聊天
- 打开 `http://127.0.0.1:18789/admin` 进行运维/管理工作
- 或访问 `http://127.0.0.1:18789/`，它会重定向到 `/chat`
- 忽略沙盒，除非你明确想要隔离执行

如果你直接从 Visual Studio 运行网关并想要最简单的行为，设置：

```json
{
  "OpenClaw": {
    "Sandbox": {
      "Provider": "None"
    }
  }
}
```

这会为支持沙盒的原生工具禁用可选沙盒路由，保持执行在本地进行。

### 为什么沙盒故事令人困惑

当前代码库支持 OpenSandbox，但它是可选的：

- 检入的网关配置文件现在默认 `OpenClaw:Sandbox:Provider=None`
- 默认网关构建不包含 OpenSandbox 集成，除非你使用 `OpenClawEnableOpenSandbox=true` 编译
- 受支持的引导流程不需要 OpenSandbox

所以如果你是新接触该项目，正确的理解是：

- 沙盒是一个高级部署/运行时选项
- 正常的本地首次运行不需要它

有关完整的可选路径，请参阅 [sandboxing.md](sandboxing.md)。

### 安全配置
你可以通过 `Tooling` 配置块锁定 Agent：
```json
{
  "OpenClaw": {
    "Tooling": {
      "AllowShell": false,
      "AllowedReadRoots": ["/Users/telli/safe-dir"],
      "AllowedWriteRoots": ["/Users/telli/safe-dir"],
      "RequireToolApproval": true,
      "ApprovalRequiredTools": ["shell", "write_file"],
      "EnableBrowserTool": false
    }
  }
}
```

设置生成的本地配置文件默认禁用浏览器工具，除非你显式配置非本地执行后端或沙盒。仅在该后端可用后才打开 `EnableBrowserTool`。

# 快速入门指南

本指南通过支持的设置路径让 OpenClaw.NET 运行起第一个 Agent。

如果你想先了解更广泛的评估者概述，请从 [START_HERE.md](START_HERE.md) 开始。如果你想要仓库地图和面向贡献者的项目结构，请阅读 [GETTING_STARTED.md](GETTING_STARTED.md)。

## 克隆到成功冒烟测试

在配置提供商之前运行此测试。它证明运行时循环和工具调用路径无需外部服务即可工作：

```bash
git clone https://github.com/clawdotnet/openclaw.net
cd openclaw.net

dotnet restore OpenClaw.Net.slnx
dotnet build OpenClaw.Net.slnx --configuration Release --no-restore
dotnet run --project samples/OpenClaw.HelloAgent -c Release --no-build
```

预期输出：

```text
OpenClaw.HelloAgent
User: hello
Agent: hello from OpenClaw.NET
Tool: echo(hello): ok
```

成功后，继续下面的本地网关流程。

## 前 10 分钟

在转向其他任何内容之前，端到端地遵循此路径。忽略每个"可选"部分、每个频道以及涉及 Docker 或沙盒的内容，直到这些能正常工作。

**1. 安装先决条件。** .NET 10 SDK 和 Git。首次运行不需要其他任何内容。

**2. 设置提供商密钥并运行主要启动路径。**

```bash
export MODEL_PROVIDER_KEY="sk-..."
dotnet run --project src/OpenClaw.Cli -c Release -- start
```

接受默认值。如果配置尚不存在，`openclaw start` 首先运行设置，写入 `~/.openclaw/config/openclaw.settings.json`，然后启动。

**3. 打开浏览器界面。**

**预期：** 启动阶段行（`Loading configuration`、`Building services`、`Initializing runtime`、`Starting listener`）后跟列出工作 URL 的 `OpenClaw gateway ready.` 块。如果你看到 `Started with notices:`，网关仍然在运行；这些是非致命的启动建议。如果你没有看到就绪块，网关尚未准备好。

前往 `http://127.0.0.1:18789/chat`（不是 `/`，不是根 URL）。你应该看到聊天界面。发送消息；你应该收到回复。

**4. 如果有任何问题，运行医生诊断。**

```bash
dotnet run --project src/OpenClaw.Gateway -c Release -- --config ~/.openclaw/config/openclaw.settings.json --doctor
```

这就是整个首次运行。在这之前跳过下面的所有内容。

如果你故意跳过 CLI 流程并直接启动网关进程，使用：

```bash
dotnet run --project src/OpenClaw.Gateway -c Release -- --quickstart
```

`--quickstart` 是直接的终端回退。它将网关保持在 `127.0.0.1`，提示缺失的提供商值，在常见的本地启动失败时在进程中重试，并在成功启动后可以将结果配置保存到标准的 `~/.openclaw/config/openclaw.settings.json`。

## 预构建 GitHub 发布下载

对于非技术桌面用户，从[最新的 GitHub Release](https://github.com/clawdotnet/openclaw.net/releases/latest) 桌面捆绑包开始，而不是源代码检出或 Actions 工件。

下载匹配的资产：

- Windows：[`openclaw-desktop-win-x64.zip`](https://github.com/clawdotnet/openclaw.net/releases/latest/download/openclaw-desktop-win-x64.zip)
- Apple Silicon macOS：[`openclaw-desktop-osx-arm64.zip`](https://github.com/clawdotnet/openclaw.net/releases/latest/download/openclaw-desktop-osx-arm64.zip)
- Linux：[`openclaw-desktop-linux-x64.zip`](https://github.com/clawdotnet/openclaw.net/releases/latest/download/openclaw-desktop-linux-x64.zip)

解压归档文件，从 `companion` 文件夹启动 Companion。打开**设置**选项卡，输入提供商/模型/密钥或选择 Ollama，然后点击**设置并启动**。Companion 写入本地配置，在 `127.0.0.1` 上启动捆绑的网关，并连接到它。

当前的 Windows 和 macOS 归档文件未签名。Windows 用户可能会看到 SmartScreen 警告；macOS 用户可能需要右键点击打开或移除隔离以进行本地测试。有关校验和和发布资产，请参阅 [RELEASES.md](RELEASES.md)。

运维人员仍然可以从同一发布下载独立的 AOT 归档：

```bash
gh release download \
  --repo clawdotnet/openclaw.net \
  --pattern 'openclaw-gateway-aot-linux-x64.zip' \
  --dir ./openclaw-gateway-aot

gh release download \
  --repo clawdotnet/openclaw.net \
  --pattern 'openclaw-cli-aot-linux-x64.zip' \
  --dir ./openclaw-cli-aot
```

解压这些独立归档后：

```bash
chmod +x ./openclaw-gateway-aot/OpenClaw.Gateway
chmod +x ./openclaw-cli-aot/openclaw
```

最快的交互式本地运行：

```bash
export MODEL_PROVIDER_KEY="sk-..."
./openclaw-gateway-aot/OpenClaw.Gateway --quickstart
```

可复用的配置：

```bash
./openclaw-cli-aot/openclaw setup
./openclaw-gateway-aot/OpenClaw.Gateway --config ~/.openclaw/config/openclaw.settings.json
```

GitHub Actions 工件仍然可用于提交验证，但它们不是受支持的用户下载界面，因为它们可能过期且可能需要 GitHub 访问权限。

你**明确不需要**以下任何内容即可开始：

- Docker
- OpenSandbox（仅当确定需要时才参阅 [sandboxing.md](sandboxing.md)）
- 频道设置（Telegram、Slack、Discord、Teams、WhatsApp）
- 公共 / 反向代理部署
- 运行时模式调整（`aot` / `jit`）

---

## 先决条件

- .NET 10 SDK
- 可选：Node.js 20+ 如果你想要上游风格的 TS/JS 插件支持

第一次完整的 `dotnet test` 运行可能会下载 Playwright 浏览器资产以进行浏览器工具覆盖。测试套件在首次安装后使用 Playwright 的正常浏览器缓存；仅当你故意想要隔离的测试浏览器安装时，设置 `OPENCLAW_TEST_ISOLATE_PLAYWRIGHT_BROWSERS=true`。

以下示例使用 `openclaw ...`。从源代码检出，将其替换为 `dotnet run --project src/OpenClaw.Cli -c Release -- ...`。

对于从源代码的首次运行，优先使用 `openclaw setup` 生成的外部配置。不要从依赖检入的 `src/OpenClaw.Gateway/appsettings.json` 开始，除非你故意想要调试原始仓库默认值。

## 选择正确的入口点

| 命令 | 使用场景 |
| --- | --- |
| `openclaw start` | 你想要单命令本地路径，使用现有配置（如果存在）或首先运行设置然后启动。 |
| `openclaw setup` | 你想要引导式引导流程，写入配置，打印启动命令，并给你 `--doctor` 加 `admin posture` 后续操作。 |
| `dotnet run --project src/OpenClaw.Gateway -c Release -- --quickstart` | 你想直接从仓库检出启动网关，让网关恢复到安全的本地配置文件，而不是先准备配置。 |
| `openclaw init` | 你想要原始引导文件，在运行网关之前手动编辑。 |
| 直接配置编辑 | 你已经知道想要的运行时形状，不需要引导路径。 |

如果你要通过 OpenClaw.NET 托管一个 MCP App 的浏览器 UI，不要让浏览器直接连接 App 自己的上游 MCP URL。正确入口是：先通过 `/apps/health` 发现 App，再让浏览器侧 MCP client 连接 `/apps/mcp/{appId}`，聊天宿主事件走 `/apps/chat`。详细说明见 [MCPAPP.md](MCPAPP.md)。

根 URL 重定向到 `/chat`。完整的首次运行指南（包括"前 10 分钟"操作手册和调试流程），参见 [docs/QUICKSTART.md](../QUICKSTART.md)。在修改代码前了解项目结构和仓库地图，参见 [docs/GETTING_STARTED.md](../GETTING_STARTED.md)。
> **破坏性更改**：`OPENCLAW_AUTH_TOKEN` 现在是非环回部署的引导和紧急凭证。浏览器管理使用是账户/会话优先的，Companion、CLI、API 和 websocket 客户端应使用运维人员账户令牌。

## 最快的本地启动

1. 运行引导式设置流程：

```bash
openclaw start
```

2. 接受本地默认值或提供你首选的提供商、模型、API 密钥引用、工作区路径和可选执行后端。如果配置已存在，`openclaw start` 跳过设置并直接启动。

3. 如果你想要显式的拆分流程而不是单命令路径，使用：

```bash
openclaw setup
openclaw setup launch --config ~/.openclaw/config/openclaw.settings.json
```

如果你更喜欢直接运行网关进程，使用打印的命令，例如：

```bash
dotnet run --project src/OpenClaw.Gateway -c Release -- --config ~/.openclaw/config/openclaw.settings.json
```

如果直接启动在监听器启动前失败且你在交互式终端中，网关现在会打印可操作的指导并提供最小本地恢复流程，而不是直接抛出未处理异常。对于最短的直接路径，优先使用 `--quickstart`。

4. 在网关启动后运行打印的验证命令：

```bash
dotnet run --project src/OpenClaw.Gateway -c Release -- --config ~/.openclaw/config/openclaw.settings.json --doctor
```

```bash
OPENCLAW_BASE_URL=http://127.0.0.1:18789 OPENCLAW_AUTH_TOKEN=... openclaw admin posture
```

```bash
openclaw upgrade check --config ~/.openclaw/config/openclaw.settings.json
```

该预检现在还捕获生成配置的最后已知良好快照、环境示例和部署工件。如果升级导致设置回退，使用以下命令恢复：

```bash
openclaw upgrade rollback --config ~/.openclaw/config/openclaw.settings.json --offline
```

对于本地 Ollama 优先设置，保留原生根端点并选择显式预设而不是旧版 `/v1` 填充程序：

```bash
openclaw setup --non-interactive \

# OpenClaw.NET 入门

OpenClaw.NET 是一个面向 .NET 的本地优先、自托管 AI Agent 运行时和网关。它强调 NativeAOT 友好、清晰诊断、可选扩展隔离，以及对 OpenClaw 生态中实用部分的兼容。

如果你刚克隆仓库，建议先验证最小样例，而不是直接启动完整网关。

## 最小验证路径

```bash
git clone https://github.com/clawdotnet/openclaw.net
cd openclaw.net

dotnet restore OpenClaw.Net.slnx
dotnet build OpenClaw.Net.slnx --configuration Release --no-restore
dotnet run --project samples/OpenClaw.HelloAgent -c Release --no-build
```

期望输出：

```text
OpenClaw.HelloAgent
User: hello
Agent: hello from OpenClaw.NET
Tool: echo(hello): ok
```

这个样例不需要模型密钥、Ollama、Docker 或浏览器。它证明运行时可以构建、调用工具、完成 Agent 循环，并输出确定结果。

## 项目组成

| 部分 | 作用 |
| --- | --- |
| `OpenClaw.Core` | 配置、会话、内存、安全、诊断、序列化和共享模型。 |
| `OpenClaw.Agent` | Agent 循环、工具执行、插件桥接、委派和运行时逻辑。 |
| `OpenClaw.Gateway` | 本地或自托管 HTTP 网关，提供聊天、管理、健康检查、MCP、WebSocket 和 OpenAI 兼容接口。 |
| `OpenClaw.Cli` | 设置、启动、诊断、模型配置、插件和技能命令。 |
| `OpenClaw.Companion` | 桌面伴侣应用，用于本地操作流程。 |

## 能力边界

- 默认路径保持本地优先和 NativeAOT 友好。
- 浏览器、MQTT、动态插件、部分 provider、工作流后端和工业适配都属于可选能力。
- JIT-only 能力不会在 AOT 模式下静默部分加载；不支持时应快速失败并给出诊断。
- OpenClaw.NET 不是完整上游 OpenClaw 克隆；兼容范围以英文文档 [COMPATIBILITY.md](../COMPATIBILITY.md) 和 [CAPABILITY_MATRIX.md](../CAPABILITY_MATRIX.md) 为准。

## 下一步

| 目标 | 文档 |
| --- | --- |
| 快速启动本地网关 | [QUICKSTART.md](../QUICKSTART.md) |
| 理解仓库结构 | [GETTING_STARTED.md](../GETTING_STARTED.md) |
| 理解 OpenSquilla 风格动态 turn 路由 | [opensquilla-dynamic-turn-routing.md](opensquilla-dynamic-turn-routing.md) |
| 理解会话生命周期与每轮 Token 统计 | [SESSIONS.md](SESSIONS.md) |
| 查看能力矩阵 | [CAPABILITY_MATRIX.md](../CAPABILITY_MATRIX.md) |
| 查看兼容性说明 | [COMPATIBILITY.md](../COMPATIBILITY.md) |
| 参与贡献 | [CONTRIBUTING.md](../../CONTRIBUTING.md) |

中文文档目前是入门路径。完整参考资料仍以英文文档为主。

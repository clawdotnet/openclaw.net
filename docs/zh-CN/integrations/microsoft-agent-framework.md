# Microsoft Agent Framework

OpenClaw.NET 在常规网关构建中包含一个受支持的 Microsoft Agent Framework (MAF) 适配器。它是一等公民，但不是默认的运行时路径。

使用默认原生运行时以获得快速的本地 Agent 推理轮次：

```json
{
  "OpenClaw": {
    "Runtime": {
      "Orchestrator": "native"
    }
  }
}
```

当你想让 OpenClaw.NET 通过 Microsoft Agent Framework 适配器运行推理轮次时使用 MAF：

```json
{
  "OpenClaw": {
    "Runtime": {
      "Orchestrator": "maf"
    }
  }
}
```

无需任何构建属性、条件符号或 `OpenClawEnableMafExperiment` 标志。

## 配置

支持的 MAF 配置节：

```json
{
  "OpenClaw": {
    "MicrosoftAgentFramework": {
      "AgentName": "OpenClaw.NET",
      "AgentDescription": "OpenClaw.NET gateway agent",
      "EnableA2A": false,
      "A2APathPrefix": "/a2a",
      "A2AVersion": "1.0.0",
      "A2APublicBaseUrl": null
    }
  }
}
```

## A2A

A2A 端点即使在 MAF 适配器存在时也是按需启用的：

```json
{
  "OpenClaw": {
    "MicrosoftAgentFramework": {
      "EnableA2A": true
    }
  }
}
```

默认端点：

| 接口 | 路径 |
| --- | --- |
| Agent Card | `/.well-known/agent-card.json` |
| 旧版 Agent Card | `/a2a/.well-known/agent-card.json` |
| HTTP+JSON | `/a2a` |
| JSON-RPC | `/a2a/rpc` |

关于端点的行为、认证和当前的流式传输说明，请参阅 [../a2a.md](../a2a.md)。

## 迁移

实验阶段迁移开关已移除，请使用当前等价配置：

| 旧设置 | 当前状态 |
| --- | --- |
| `OpenClaw:Experimental:MicrosoftAgentFramework` | 已移除。请使用 `OpenClaw:MicrosoftAgentFramework`。 |
| `OpenClawEnableMafExperiment=true` | 不再需要 |
| `OPENCLAW_ENABLE_MAF_EXPERIMENT` | 不再使用 |
| `gateway-maf-enabled-*` 构建产物 | 常规网关构建产物 |

运行 MAF 适配器：

```bash
dotnet run --project src/OpenClaw.Gateway -c Release -- \
  --OpenClaw:Runtime:Orchestrator=maf
```

保持默认运行时，省略该设置或设置 `OpenClaw:Runtime:Orchestrator=native`。

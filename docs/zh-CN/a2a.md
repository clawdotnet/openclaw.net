# A2A

OpenClaw 可以通过受支持的 Microsoft Agent Framework 适配器将其网关 Agent 以 A2A 协议暴露。A2A 通过配置按需启用，而适配器已包含在常规网关构建中。

## 启用方式

A2A 支持在常规网关构建中可用。运行时设置：

```json
{
  "OpenClaw": {
    "MicrosoftAgentFramework": {
      "EnableA2A": true
    }
  }
}
```

旧版 `OpenClaw:Experimental:MicrosoftAgentFramework` 配置节仍会在一个发布周期内被读取。使用该旧版配置节时启动会记录一条警告。

## 服务发现

标准 A2A 发现端点为：

```text
/.well-known/agent-card.json
```

为兼容早期 OpenClaw 实验构建，同一张卡片也在 A2A 路径前缀下可用：

```text
/a2a/.well-known/agent-card.json
```

客户端应优先使用标准的根发现端点。

## 协议端点

默认情况下，OpenClaw 暴露以下 A2A 协议绑定：

| 绑定 | 默认路径 |
| --- | --- |
| HTTP+JSON | `/a2a` |
| JSON-RPC | `/a2a/rpc` |

路径前缀可通过 `OpenClaw:MicrosoftAgentFramework:A2APathPrefix` 更改。

## 任务支持

OpenClaw 的 A2A 接口包含协议任务模型。

- `message:send` 和 `message:stream` 在任务上下文中执行，返回任务作用域的 ID
- 流式传输发出标准的任务生命周期状态（`submitted`、`working`、终端状态 `completed`/`failed`）
- 当客户端提供任务 ID 时，取消操作通过 A2A 处理器（`CancelAsync`）连接

面向运维者的实现说明：

- 任务状态存储在内存中的 `ITaskStore` 中（进程重启后不持久化）
- A2A 服务器运行模式为 `DisallowBackground`，后台任务执行被有意禁用

## Agent 名称

OpenClaw 使用两个面向 A2A 的名称，具有不同的稳定性契约：

| 接口 | 值 | 用途 |
| --- | --- | --- |
| 托管的 A2A 服务 ID | `openclaw` | 稳定的 Microsoft Agent Framework 宿主键，用于键控 A2A 服务器、任务存储和处理器注册。 |
| Agent Card `name` | `OpenClaw:MicrosoftAgentFramework:AgentName` | 在服务发现中向 A2A 客户端公布的公开显示名称。 |

即使 Agent Card 显示名称被自定义，托管服务 ID 也刻意保持为 `openclaw`。用户可见的回退消息使用 Agent Card 显示名称，使响应与服务发现元数据相关联。仅在同时更改宿主注册、端点映射和兼容性测试时，才将托管 ID 和卡片名称保持一致。

## 公共基础 URL

Agent Card 中的 URL 默认从当前请求主机生成。当网关运行在反向代理、容器入口、隧道或任何绑定地址无法从外部访问的主机之后时，设置 `OpenClaw:MicrosoftAgentFramework:A2APublicBaseUrl`。

示例：

```json
{
  "OpenClaw": {
    "MicrosoftAgentFramework": {
      "EnableA2A": true,
      "A2APublicBaseUrl": "https://agents.example.com/openclaw"
    }
  }
}
```

使用该配置后，Agent Card 公布的端点为 `https://agents.example.com/openclaw/a2a` 和 `https://agents.example.com/openclaw/a2a/rpc`。

## 认证

服务发现默认公开，以便标准 A2A 卡片解析器可以获取 Agent Card。执行端点继续使用网关认证和 IP 速率限制策略。

对于公开部署，在暴露 A2A 执行路径之前配置网关认证。

## 流式传输

当 `OpenClaw:MicrosoftAgentFramework:EnableStreaming=true` 时，OpenClaw 公布协议级别的 A2A 流式传输。

启用流式传输后：

- Agent Card 暴露 `capabilities.streaming=true`
- `POST /a2a/message:stream` 通过 SSE 发出任务生命周期和产物事件
- `POST /a2a/rpc` 通过 JSON-RPC 绑定暴露相同的流式语义

流式传输契约：

- submitted → working → 产物块 → completed/failed
- 所有文本增量追加到 `artifactId: "text-delta"`
- 成功流在任务完成前将最终发出的 `text-delta` 块标记为 `lastChunk=true`
- 失败/取消的流可能在没有 `lastChunk=true` 产物关闭事件的情况下终止
- 如果在部分文本已发出后发生失败，之前发送的块会保留，任务以 `failed` 终止
- 任务完成通过 `CompleteAsync` 最终确定；完成后不会发出额外的产物块

## REST 响应物化

REST `POST /a2a/message:send` 路径必须为每个未取消的请求物化至少一个 A2A 协议事件。如果 A2A 服务器从处理器收到零事件，A2A SDK 将返回 HTTP 500，错误类似于：

```text
A2A.A2AException: Agent handler did not produce any response events.
```

OpenClaw 通过为 A2A 服务器执行路径使用显式的键控 `OpenClawA2AAgentHandler` 来避免此问题。该处理器将 OpenClaw 运行时流桥接到直接的 A2A Agent `Message` 事件，而非依赖 SDK 从 `AIAgent` 流式更新转换响应。这确保了即使底层 OpenClaw 推理轮次在没有文本增量的情况下完成，`message:send` 也是确定性的。

预期的处理器行为：

| 端点 | 运行时结果 | A2A 响应行为 |
| --- | --- | --- |
| `POST /a2a/message:send` | 产生文本增量 | 将文本增量拼接为一条 Agent 消息。 |
| `POST /a2a/message:send` | 运行时完成但无文本 | 返回 `[<AgentName>] Request completed.` 作为 Agent 消息。 |
| `POST /a2a/message:send` | 发生可恢复的桥接/运行时异常 | 记录异常并返回 `A2A request failed.` 作为 Agent 消息。 |
| `POST /a2a/message:send` | 请求被取消 | 传播取消而非捏造响应。 |
| `POST /a2a/message:stream` | 产生文本增量 | 通过 SSE 发出 `submitted → working → artifactUpdate* → completed`。 |
| `POST /a2a/message:stream` | 运行时完成但无文本 | 发出回退产物块 `[<AgentName>] Request completed.` 然后完成任务。 |
| `POST /a2a/message:stream` | 在任何文本之前发生运行时错误或桥接异常 | 发出 `failed` 并附带错误消息。 |
| `POST /a2a/message:stream` | 在部分文本之后发生运行时错误或桥接异常 | 保留已发出的产物块，然后发出 `failed`。 |
| `POST /a2a/message:stream` | 请求被取消 | 传播取消以便 SDK 将任务移至 `canceled`。 |

网关仍注册 Microsoft Agent Framework 的 `AIAgent` 宿主接口用于元数据和会话集成，但 REST 执行被有意路由到显式的 A2A 事件队列处理器。这一点很重要，因为预览版宿主包可能在 OpenClaw 推理轮次完成时不产生任何可物化的 A2A 响应事件。

HTTP+JSON 和 JSON-RPC 预期保持语义对齐。传输负载无需逐字节相同，但两种绑定必须保持相同的事件顺序、产物追加语义和终端状态行为。

## AOT 和 JIT 说明

此集成使用 Microsoft Agent Framework 和 A2A SDK 包接口，因此会为网关构建增加依赖。核心 OpenClaw 运行时行为保持独立，因为 `Runtime.Orchestrator=native` 仍然是默认设置，并且 A2A 端点默认禁用，除非设置 `OpenClaw:MicrosoftAgentFramework:EnableA2A=true`。

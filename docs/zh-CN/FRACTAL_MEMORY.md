# 分形记忆（Fractal Memory）

Fractal Memory 集成是 OpenClaw.NET 的可选结构化项目记忆 Provider。旨在减少上下文过载、改善 Runtime Pulse 交接，并让运维者在不替换现有记忆和会话存储的情况下检查紧凑的项目状态。

OpenClaw 以 MCP 优先方式集成 [agentqi/fractal-memory](https://github.com/agentqi/fractal-memory)。

## 新增功能

- 启用时提供只读结构化记忆工具
- `ContextBudgetPlanner` 搜索 Fractal Memory 并导出紧凑上下文
- 可选的 Agent 推理轮次自动上下文注入
- Admin 和 CLI 接口

## 配置

```yaml
OpenClaw:
  Memory:
    Fractal:
      Enabled: false
      Mode: "mcp"
      RepositoryRoot: ""
      McpCommand: "fractalmem-mcp"
      DefaultDepth: 1
      MaxContextChars: 24000
      AutoContextMode: "off"
      AllowWrites: false
      RequireApprovalForWrites: true
```

`AutoContextMode`：`off` 不注入、`manual` 仅工具/Admin/CLI、`pulse` Runtime Pulse 可附加、`auto` 正常推理轮次和 Pulse 可附加。

## 工具

只读工具：`fractal_memory_search`、`fractal_memory_open`、`fractal_memory_recent`、`fractal_memory_export`、`fractal_memory_validate`。

仅在 `AllowWrites=true` 时注册写入工具：`fractal_memory_handoff_create`、`fractal_memory_index_refresh`。

## CLI

```bash
openclaw memory fractal status
openclaw memory fractal search "context bloat"
openclaw memory fractal open projects/openclaw-net --depth 1
openclaw memory fractal export projects/openclaw-net --mode compact
```

## 上下文预算规划器

搜索 Fractal Memory，选择相关节点，导出紧凑上下文，保留来源标签，执行字符/令牌预算。注入块标记为不受信任的参考数据。

## 写入

V1 以读取为主。持久化写入仅限于显式的交接创建和索引刷新，均在 `AllowWrites=true` 且需审批路径。

## 排查

- `status=disabled`：设置 `Enabled=true`
- MCP 命令无法启动：安装 Fractal Memory MCP 或更新 `McpCommand`
- 仓库警告：设置 `RepositoryRoot` 指向包含 `.fractal-memory/config.yaml` 的文件夹
- 无上下文附加：检查 `AutoContextMode`

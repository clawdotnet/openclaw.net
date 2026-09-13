# Nacos MCP Router 集成（PoC，Issue #229）

本页介绍 OpenClaw.NET Gateway 与 [Nacos MCP Router](https://www.nacos.io/)
的 PoC 集成（对应 [issue #229](https://github.com/clawdotnet/openclaw.net/issues/229)，
epic [#228](https://github.com/clawdotnet/openclaw.net/issues/228) 的子任务 #1）。
Router 位于 Gateway 与一组 Nacos 注册的 MCP Server 之间，向上层暴露
三个稳定的 MCP 工具 —— `search_mcp_server`、`add_mcp_server`、
`use_tool` —— Gateway 可以将其作为原生 MetaSkill 步骤来编排。

> **状态（2026-09-13）。** T0（探测真实 Router 契约）在本提交中标记为
> **BLOCKED-EXTERNAL**：本地 Nacos 3.2.4 compose 栈位于
> `E:/GitHub/RedNb.Nacos/deploy/docker-compose`（Docker、端口
> 8080/8848/9848），`nacos-mcp-router` HTTP 端点位于
> `127.0.0.1:8000/mcp`，在沙箱测试环境中均不可达。本 PoC 的其余步骤
> （`mcp.json` 示例、技能示例、Mock Router 夹具、集成测试、文档）
> 均基于进程内的
> [`FakeNacosRouterMcpTools`](../../src/OpenClaw.Tests/FakeNacosRouterMcpTools.cs)
> 完成。文档末尾的 Notes 部分记录了真实环境可用时的精确执行命令。

## Router 为 Gateway 解决了什么

| Nacos / Router 概念 | OpenClaw.NET 对应 |
| --- | --- |
| MCP Server 元数据（name / version / description） | `capabilityRef` payload，后续由 Capability Resolver 解析（参见 epic #228 子任务 #230） |
| MCP Server `description` | MetaSkill `intent` / `task_description` / `key_words`（参见 [`meta-skills.md`](meta-skills.md)） |
| Nacos `namespace` / `group` | DDD 限界上下文标识符（本 PoC 未接通） |
| `search_mcp_server` | Capability Resolver 前置（PoC 阶段直接由 Router 响应） |
| `add_mcp_server` | 把上游 server 绑定到本地会话，便于后续工具调用 |
| `use_tool` | DAG 中真正调用上游 MCP Server 工具的步骤 |

也就是说，Router 是 Gateway **发现并调用 Nacos 注册 MCP Server 的间接层**，
Runtime 无需直接调用 Nacos SDK。

## 在线 Router 的 `mcp.json` 示例

将下面的片段放入 `<workspace>/.openclaw/mcp/mcp.json`（workspace MCP
配置，参见 [`McpConfigStore.cs`](../../src/OpenClaw.Gateway/Mcp/McpConfigStore.cs)）
或网关启动配置中的 `mcp.json` 中即可启用 Router。四个环境变量都是
占位符 —— 通过 `.env` 或密钥管理服务注入，**严禁入库**。

```json
{
  "enabled": true,
  "servers": {
    "nacos-mcp-router": {
      "enabled": true,
      "transport": "http",
      "url": "http://127.0.0.1:8000/mcp",
      "startupTimeoutSeconds": 30,
      "requestTimeoutSeconds": 60,
      "headers": {
          "X-Nacos-Addr":      "env:NACOS_ADDR",
          "X-Nacos-Username":  "env:NACOS_USERNAME",
          "X-Nacos-Password":  "env:NACOS_PASSWORD",
          "X-Nacos-Transport": "env:TRANSPORT_TYPE"
        }
      }
    }
  }
}
```

`NACOS_ADDR` 是上游 Nacos 3.2.4 地址（通常为 `http://127.0.0.1:8848`），
`NACOS_USERNAME` / `NACOS_PASSWORD` 是 Nacos 管理员凭据（**严禁入库**），
`TRANSPORT_TYPE` 通常为 `http`。Router 通过 streamable-HTTP 暴露 MCP
服务，地址 `/mcp`。Gateway 的 MCP Server 工具注册中心会自动发现恰好
三个带前缀的工具：

- `nacos-mcp-router_search_mcp_server`
- `nacos-mcp-router_add_mcp_server`
- `nacos-mcp-router_use_tool`

（`nacos-mcp-router.` 前缀是 server id 中的点被替换为下划线的结果 —
参见 [`McpServerToolRegistry.ResolveToolName`](../../src/OpenClaw.Agent/Plugins/McpServerToolRegistry.cs)。）

## 编写一个由 Router 支撑的 MetaSkill

`examples/skills/` 目录下交付了两个 PoC 技能：

- [`nacos-router-weather/SKILL.md`](../../examples/skills/nacos-router-weather/SKILL.md)
  —— DAG：`bind` → `query` → `answer`，其中 `query_fallback` 是
  上游 `use_tool` 失败时的 `on_failure` 替代步骤。
- [`nacos-router-weather-explore/SKILL.md`](../../examples/skills/nacos-router-weather-explore/SKILL.md)
  —— DAG：`search` → `bind` → `query` → `answer`。用于与静态
  Resolver（epic #228 子任务 #231 即将提供）按 step 比对 token。

编写 Router 支撑的 MetaSkill 时需要记住的 DAG 约束：

1. **不要共享 `on_failure` 替代步骤。** 两个不同的步骤不能指向
   同一个 `on_failure: <step_id>` 目标 —— `TryValidateMetaPlan`
   校验器会报 “fallback step X is shared by A and B”。
2. **不要直接依赖 fallback-only 步骤。** 正常步骤只能依赖原始步骤
   （`query`），不能依赖其替代步骤（`query_fallback`）。Runtime 通过
   `failureAliases` 把替代步骤的输出镜像回 `outputs.query`，所以下游
   步骤可以走任意路径。
3. **工具名前缀。** `tool_call` 步骤里的 `tool` 字段必须使用带
   server id 前缀的本地名（`nacos-mcp-router_use_tool`），不要使用
   远端名（`use_tool`）。`McpNativeTool` 包装器会处理两者之间的映射。

更完整的 MetaSkill 编写指南请参考 [`docs/meta-skill-orchestration.md`](meta-skill-orchestration.md)
和 [`docs/meta-skills.md`](meta-skills.md)。

## Token 对比方法

要在 PoC（在线 Router）和未来的 Runtime Resolver 之间做对比，对同样
五条意图分别走两条路径，记录 meta-run 回放里的
`input_tokens + output_tokens`：

| 意图 | 静态 Resolver（计划） | Router PoC（本技能） |
| --- | --- | --- |
| "北京天气预报" | 待 #231 | `nacos-router-weather`（3 步 DAG） |
| "上海空气质量指数" | 待 #231 | `nacos-router-weather`（3 步 DAG） |
| "明天日出时间" | 待 #231 | `nacos-router-weather`（3 步 DAG） |
| "今明天气对比" | 待 #231 | `nacos-router-weather-explore`（4 步 DAG） |
| "查询 X→Y 的路线" | 待 #231 | 不在 PoC 范围（transit-mcp，#231 后） |

每条意图跑 5 次，记录中位数 + p95。PoC 相比未来的直接 capability
调用，会多一次 `add_mcp_server` 绑定和一次 `use_tool` 真实调用。

## 验收标准覆盖

| # | 标准 | 落地位置 |
| --- | --- | --- |
| 1 | Mock Router 恰好暴露 3 个 `nacos-mcp-router_*` 工具并由 `McpServerToolRegistry` 注册 | `NacosRouterIntegrationTests.RegistryAgainstFakeRouter_DiscoversExactlyThreeNacosRouterTools` |
| 2 | DAG `bind → query → answer` 端到端运行，所有步骤 `completed` | `NacosRouterIntegrationTests.MetaSkill_FullHappyPath_BindQueryAnswer_AllStepsCompleted` |
| 3 | DAG 校验正确连接 `on_failure`，失败时 `query` 激活替代分支 | `NacosRouterIntegrationTests.MetaSkill_OutputContractFailure_TriggersOnFailureFallbackBranch` |
| 4 | 重复调用 `RegisterToolsAsync` 不会重复注册这 3 个条目 | 由 `RegistryAgainstFakeRouter_DiscoversExactlyThreeNacosRouterTools` 覆盖 |
| 5 | `dotnet test` 全部通过（不影响既有测试） | `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj` |
| 6 | `OPENCLAW_NACOS_LIVE=1` 触发的在线集成探针 | 见下文，暂未运行（环境 BLOCKED-EXTERNAL） |
| 7 | T4 文档覆盖在线 Router 流程并把 T0 标记为 `BLOCKED-EXTERNAL` | 本页 |

## 在线集成探针（由 `OPENCLAW_NACOS_LIVE` 控制）

[`NacosRouterIntegrationTests.cs`](../../src/OpenClaw.Tests/NacosRouterIntegrationTests.cs)
中新增的测试均针对进程内 Mock 夹具。真实环境探针由
`OPENCLAW_NACOS_LIVE` 环境变量开启：

```bash
# 启动 Nacos 栈（参见 /e/GitHub/RedNb.Nacos/deploy/docker-compose/README.md）
cd /e/GitHub/RedNb.Nacos/deploy/docker-compose
umask 077
printf 'NACOS_AUTH_TOKEN=%s\nNACOS_AUTH_IDENTITY_VALUE=%s\n' \
    "$(openssl rand -base64 48 | tr -d '\n')" \
    "$(openssl rand -hex 24)" > .env
docker compose up -d

# 启动 Router
uvx nacos-mcp-router@latest \
    --nacos-addr      http://127.0.0.1:8848 \
    --nacos-username  nacos \
    --nacos-password  "$NACOS_PASSWORD" \
    --transport-type  http

# 运行在线探针
OPENCLAW_NACOS_LIVE=1 dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj \
    --filter "FullyQualifiedName~NacosRouterLiveProbe"
```

在线探针测试将在后续提交中追加，复用同一套 `McpServerToolRegistry`
+ AgentRuntime 链路，断言：

- `search_mcp_server` 返回 Nacos 注册的 server（而非 mock 目录）
- `add_mcp_server` + `use_tool` 联合调用对真实 Router 成功闭环

## Notes

- **T0 状态：BLOCKED-EXTERNAL。** 当前测试沙箱无法访问 Docker 守护进程，
  也无法访问 `127.0.0.1:8080/8848/9848/8000`。本提交的所有断言都
  针对进程内夹具执行；在线探针已记录但未运行。
- **不携带任何真实凭据。** 示例 `mcp.json` 与测试都使用占位环境变量
  引用（`env:NACOS_*`）；真实 `NACOS_PASSWORD` 只能保留在本地
  `.env` 或密钥管理服务中。
- **不在 #229 范围内。** Capability Resolver（`resolve_capability`）、
  capabilityRef schema（#230 / #231）、Nacos 注册表同步（#232）、
  `meta-runs` 绑定（#234）都是后续 issue。本 PoC 仅验证 Gateway 已经在
  不用改 Runtime 的前提下把 Router 暴露的 MCP 工具编排为 MetaSkill
  DAG 步骤。
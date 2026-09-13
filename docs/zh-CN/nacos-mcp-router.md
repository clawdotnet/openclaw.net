# Nacos MCP Router 概念验证

状态：**已提供可复现 mock 集成基础；真实部署验收尚未完成**。对应
[#229](https://github.com/clawdotnet/openclaw.net/issues/229)，完整配置和命令见
[英文指南](../nacos-mcp-router.md)。没有宣称已验证真实召回率或模型 token 基线。

## 已确认的契约差异

依据上游 Python Router 固定提交 `0ee95f4f353d6f66184dafdb3e0ffd342c4edb09` 的源码：

- search 参数是 `task_description` 和逗号分隔字符串 `key_words`。
- use 参数是 `mcp_server_name`、`mcp_tool_name`、`params`，不是 `tool_name`。
- search 返回嵌有 JSON 对象的说明文字，没有 score/version 字段。
- 部分安装、健康检查、执行错误只是普通文本，不设置 MCP `isError`。
- server ID 中的连字符会保留。显式设置 `toolNamePrefix: nacos_mcp_router_`
  才能保证示例中的三个下划线工具名。

这些是源码观察，不是假称的真实端点抓包。协议级失败与普通错误文本必须区分；
Resolver 已定义解析（信封解析器）、排序（位置 `rank`）与错误归一化契约（见下文），
版本约束仍待 #231 收尾时确定。

## 验证范围

`examples/skills/nacos-router-weather` 使用 bind → query 的静态 DAG，并直接返回
工具结果。`nacos-router-weather-explore` 提供模型驱动的 search → add → use 基线。
示例不进入生产技能索引，需在独立测试 workspace 手动启用。

运行 mock 测试：

```sh
dotnet test src/OpenClaw.Tests -c Release --filter FullyQualifiedName~NacosRouterIntegrationTests
```

已有真实 Router 和 weather-mcp 注册后，可设置 `OPENCLAW_NACOS_LIVE=1`、
`OPENCLAW_NACOS_ROUTER_URL`，运行 `FullyQualifiedName~LiveRouter` 筛选器。
未设置时跳过 live 测试，普通 CI 不依赖外部服务。

## 能力解析器（issue #230）

`resolve_capability` 原生工具把模型驱动的三步链变成确定性代码路径：
模型只需产出 intent，绑定在代码中完成。

输入：

- `task_description`（必填）— 与 Router `search_mcp_server` 接受的形式一致。
- `key_words`（可选）— 逗号分隔字符串，与 Router 的线上形式一致。
- `selection_policy`（可选）— `first`（默认）或 `exact_name`（对
  `task_description` 做大小写不敏感的名称匹配）；精确匹配无结果时返回
  `failure_code: "selection_policy_no_match"` 且 `tried` 为空。

成功输出：

```json
{
  "server": "weather-mcp",
  "tool": "get_weather",
  "schema": "{\"type\":\"object\",\"properties\":{\"city\":{\"type\":\"string\"}},\"required\":[\"city\"]}",
  "tried": [
    {"name": "weather-mcp", "description": "...", "rank": 1}
  ]
}
```

`schema` 是上游工具 schema 的 JSON 编码字符串，使用前先解析。

失败输出（Router 各类失败都以 JSON 返回，不抛异常）：

```text
{ "failure_code": "no_candidates", "tried": [] }
{ "failure_code": "selection_policy_no_match", "tried": [] }
{ "failure_code": "all_adds_failed", "tried": [{"name":"...","description":"...","rank":1}, ...] }
{ "failure_code": "router_unavailable", "tried": [] }
```

行为契约：

1. 该工具从不调用 `use_tool`；绑定后的工具由下游 DAG 节点执行。
2. 该工具从不调用任何 LLM；往返次数为零（测试断言 `chat.ReceivedCalls()` 为空）。
3. 该工具遇到 Router 失败不抛异常；prose 失败、传输失败、协议级 `isError`
   全部归一化为 `failure_code`。
   - search 未到达 Router（传输失败）或返回 `isError` → `router_unavailable`。
   - add 返回 `isError` 只判该候选失败并继续轮替；`isError` 优先于文本检查，
     错误结果中的「安装完成」不能导致绑定成功。
   - add 未到达 Router 则终止轮替 → `router_unavailable`，`tried` 为已尝试候选。
   - 调用方取消仍以 `OperationCanceledException` 传播。
4. `tried` 只列实际尝试过 add 的候选，而非全部返回候选。
5. `rank` 是候选在上游确定性 Top-N 排序中的位置。上游 search 不返回分数，
   因此不做本地伪造。

## 尚未具备的验收材料

Issue 引用的 Windows compose 部署与架构文档不在仓库中。必须补充真实 Router
版本、三个工具的参数及成功/错误响应、注册 payload 和可复制的启动命令。
使用同一模型、输入与配置，对两个示例各执行五次，记录真实 session usage 的
input+output 总和中位数，并单独记录召回质量。目前这两组数据均未测量。

因此 #229 仍未全部完成，#230–#234 的依赖验收仍待完成；不能把 mock 数据当成
真实 token 基线，也不能凭空补全搜索分数、版本信息或错误语义。

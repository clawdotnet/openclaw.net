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
后续 Resolver 需要定义可靠的解析、排序、版本约束及错误归一化契约。

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

## 尚未具备的验收材料

Issue 引用的 Windows compose 部署与架构文档不在仓库中。必须补充真实 Router
版本、三个工具的参数及成功/错误响应、注册 payload 和可复制的启动命令。
使用同一模型、输入与配置，对两个示例各执行五次，记录真实 session usage 的
input+output 总和中位数，并单独记录召回质量。目前这两组数据均未测量。

因此 #229 仍未全部完成，#230–#234 的依赖验收仍待完成；不能把 mock 数据当成
真实 token 基线，也不能凭空补全搜索分数、版本信息或错误语义。

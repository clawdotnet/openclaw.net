# Nacos MCP Router 集成

当前实现遵循[供应商无关能力解析契约](../capability-resolution.md)：默认 provider
为 `local`；Nacos 需要显式 `provider: nacos` 与 `OpenClawEnableNacos=true`。
配置位于 `adapterSettings.nacos`。事件订阅额外启用
`OpenClawEnableNacosEvents=true`，支持 JIT 和 NativeAOT；JIT 发布使用
`PublishAot=false`。RedNb.Nacos 2.1.0 提供源生成协议 JSON 元数据，无需重新开启
JSON 反射。默认 Gateway 不引入 Nacos SDK。

> **当前 .NET 实现：** [.NET live acceptance 与 Gateway 集成架构](nacos-live-architecture.md)介绍 .NET harness、Streamable HTTP fixture、managed/NativeAOT smoke 和固定版本 `NacosMcpRouter` 1.0.0。本文后续的 Router 0.2.2 协议捕获属于历史兼容资料，不是当前验收搭建方式。

## 协议与能力槽位

Router 0.2.2 的三个工具仍是 `search_mcp_server`、`add_mcp_server`、`use_tool`。
显式配置 `toolNamePrefix: nacos_mcp_router_`，可以获得示例中的工具名。

- search 参数为 `task_description` 与逗号分隔的 `key_words`，结果是嵌入说明文字的
  JSON 对象；上游没有 score/version，不伪造这些字段，候选使用位置 `rank`。
- add 参数为 `mcp_server_name`；候选注册必须有非空 description，stdio 配置必须
  使用 `{"mcpServers":{"weather-mcp":{...}}}` 包装。
- use 参数为 `mcp_server_name`、`mcp_tool_name`、JSON 编码字符串 `params`。
  部分失败以普通文本返回，适配器分别归一化传输、协议和已知文本失败。
- `resolve_capability` 支持 `provider`、`task_description`、`keywords` 数组、
  `selection_policy`（`first` / `exact_name`）；兼容旧 `key_words`，不可同时声明。
  `top_k`、`prefer_version` 等不支持的约束明确拒绝。
- 解析仅选择并绑定，不执行工具；MetaSkill 能力节点通过现有授权、审批、hooks
  与审计路径调用绑定工具。缓存命中仍重新校验权限。
- 失败码使用供应商无关名称，如 `provider_unavailable`、`all_bindings_failed`；
  具体执行失败见[英文指南](../nacos-mcp-router.md)。

`examples/skills/nacos-router-weather` 为静态能力槽位，
`nacos-router-weather-dynamic` 为动态能力槽位，均不增加 LLM 轮次。
`nacos-router-weather-explore` 保留模型驱动基线。示例不会默认启用。

## 缓存、事件与验收

事件订阅在后台注册，暴露 disabled/starting/active/degraded/stopped 状态，失败时
按退避策略重试。事件经通用失效接口推进缓存 generation，清除所有静态和动态绑定（未实现按 server 精细失效）；
不覆盖 workspace 配置。未启用或不可达时，TTL 与显式 reload 继续有效。

当前隔离验收使用 .NET harness、带认证的 Nacos 3.2.4、固定版本 .NET Router global tool，
以及 loopback Streamable HTTP `get_weather(city)` fixture。完整架构、验收断言、运行命令和
证据说明见[当前 .NET 验收文档](nacos-live-architecture.md)与[运行指南](../../eng/nacos-live/README.md)。
fixture 天气是有明确标签的确定性 Oslo 测试数据，不是实时观测；隔离验收也不代表任意生产
注册表的搜索召回率。本文前面的 Router 0.2.2 协议细节及后面的贡献测量继续作为历史记录保留。

## Zhang 的历史贡献与测量

@geffzhang 提供原始实现、协议捕获和 2026-09-14 的五次运行 token 基线：静态
DAG 的中位数为 0 input / 0 output，模型驱动探索为 17479 / 1181。原测试环境
把 `weather-mcp` 指向了时间服务，因此探索成功率为 0/5；这不代表当前正确注册
天气服务的结果。早期事件路径 +310 ms 的测量同样保留为历史证据。
原始数据、限制与贡献记录见[英文指南历史章节](../nacos-mcp-router.md#historical-contributor-evidence)。

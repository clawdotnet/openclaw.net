# Nacos MCP Router 集成

当前实现遵循[供应商无关能力解析契约](../capability-resolution.md)：默认 provider
为 `local`；Nacos 需要显式 `provider: nacos` 与 `OpenClawEnableNacos=true`。
配置位于 `adapterSettings.nacos`。事件订阅额外启用
`OpenClawEnableNacosEvents=true`，支持 JIT 和 NativeAOT；JIT 发布使用
`PublishAot=false`。RedNb.Nacos 2.1.0 提供源生成协议 JSON 元数据，无需重新开启
JSON 反射。默认 Gateway 不引入 Nacos SDK。

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

[隔离验收工具与完整命令](../../eng/nacos-live/README.md)启动带认证的 Nacos 3.2.4、
Router 0.2.2，以及真正暴露 `get_weather(city)` 的独立 MCP 服务。它检查：

1. 双 runtime 下静态/动态技能返回天气结果、复用缓存、不重复注册工具、零模型调用。
2. 托管与 NativeAOT 进程均禁用 JSON 反射，生产订阅适配器进入 active。
3. 真实配置发布后 2 秒内清除已预热的绑定，下次静态/动态调用都重新绑定成功。

默认天气结果明确标注为 fixture，真实 Nacos/Router/事件传输照常执行；
`--live-weather` 改为查询 Open-Meteo 的 Oslo 天气。不可把 fixture 温度当作现场
天气观测，也不把隔离部署的成功推断为任意生产注册表的召回率。
专用 CI 自动运行隔离验收，普通测试在未设置 `OPENCLAW_NACOS_LIVE=1` 时跳过
现场测试。成功报告含 managed/native JSON 和双 runtime TRX。

## Zhang 的历史贡献与测量

@geffzhang 提供原始实现、协议捕获和 2026-09-14 的五次运行 token 基线：静态
DAG 的中位数为 0 input / 0 output，模型驱动探索为 17479 / 1181。原测试环境
把 `weather-mcp` 指向了时间服务，因此探索成功率为 0/5；这不代表当前正确注册
天气服务的结果。早期事件路径 +310 ms 的测量同样保留为历史证据。
原始数据、限制与贡献记录见[英文指南历史章节](../nacos-mcp-router.md#historical-contributor-evidence)。

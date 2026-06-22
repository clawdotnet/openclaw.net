# 可选依赖拆分

OpenClaw.NET 保持默认运行时以本地优先（local-first）且 NativeAOT 友好。当可选集成引入协议特定依赖、Provider SDK、动态加载或浏览器自动化等重量级组件时，应当放置在明确的项目或包边界之后。

## 当前拆分

MQTT 是从 `OpenClaw.Agent` 中提取的原生协议层：

- 项目：`src/OpenClaw.Protocols.Mqtt`
- 包依赖：`MQTTnet`
- 配置归属：`OpenClaw.Core` 仍持有 `MqttConfig`
- 组装归属：`OpenClaw.Gateway` 通过 `NativePluginRegistry.RegisterExternalTool(...)` 注册 MQTT 工具
- 行为：现有 `OpenClaw:Plugins:Native:Mqtt` 配置保持不变

浏览器自动化也已从 `OpenClaw.Agent` 中拆分：

- 项目：`src/OpenClaw.Protocols.Browser`
- 包依赖：`Microsoft.Playwright`
- 配置归属：`OpenClaw.Core` 仍持有 `Tooling.EnableBrowserTool` 及浏览器工具选项
- 组装归属：`OpenClaw.Gateway` 在浏览器可用性检查通过后，将浏览器工具作为内置网关工具层的一部分注册
- 行为：现有 `OpenClaw:Tooling:EnableBrowserTool` 配置及本地/沙箱/后端回退行为保持不变

Agent 执行器不再依赖浏览器特定类型来实现沙箱回退。无法安全回退到本地执行的工具，需实现 `OpenClaw.Core` 中定义的协议无关的 `IToolLocalExecutionPolicy` 契约。

这些拆分使协议包保持可选，同时保留了运维人员已经使用的网关行为。

## 待处理的边界

以下依赖目前有意保留在 `OpenClaw.Agent` 中：

| 模块 | 当前阻碍 | 下一步拆分点 |
| --- | --- | --- |
| MCP 工具注册表 | MCP 注册参与网关启动和原生注册表组装。 | 将 MCP 注册契约与 Agent 持有的工具注册表实现分离。 |
| 插件桥接 | 插件宿主、桥接进程、动态原生宿主、hooks、skills、providers 和诊断共享运行时启动状态。 | 先拆分桥接契约和宿主生命周期，再移动传输层特定代码。 |
| OpenAI 特定 Provider 包 | Provider 构造仍流经当前的 Agent/运行时组装。 | 将 Provider 特定 SDK 的使用限定在 Provider 项目或网关组装中，再移除 Agent 对相关包的依赖。 |

## 贡献者规则

不要创建空的可选项目来暗示分离。只有在目标项目拥有完整实现，且默认运行时行为可以通过构建、测试和 HelloAgent 冒烟用例验证时，才迁移依赖。

如果拆分需要改变公共运行时语义，请先记录所需的拆分点，在拆分点经过评审之前保持依赖不变。

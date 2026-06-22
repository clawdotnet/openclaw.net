# 网关启动重构

## 概述

网关启动路径现已拆分为三层：

1. **`Bootstrap/`（引导层）**
   - 加载配置覆盖（config overrides）。
   - 绑定规范的 `GatewayConfig`。
   - 应用环境变量覆盖和显式的密钥解析。
   - 解析 `GatewayRuntimeState`。
   - 处理 `--health-check` 和 `--doctor` 的提前退出。
   - 处理交互式 `--quickstart` 和监听器启动前的本地终端启动恢复。
   - 强制执行非 loopback 的 auth-token 要求和公开绑定安全加固。

2. **`Composition/`（组装层）和 `Profiles/`（配置层）**
   - 以分组扩展方法注册构建前服务。
   - 保持现有 Core/Agent 运行时模型作为 AOT vs JIT 的单一事实来源。
   - 在 `builder.Build()` 之后通过 `InitializeOpenClawRuntimeAsync(...)` 构建仅运行时对象。
   - 运行时组装按以下阶段进行：
     - 严格的公开绑定预设应用
     - 服务解析
     - 通道/Webhook 组合
     - 内置 Provider 和工具注册
     - 插件加载和组合
     - Agent 运行时创建
     - 技能监视器和自动化刷新
     - 最终的 `GatewayAppRuntime` 构造
     - 配置钩子和集成启动
   - 保持插件加载、Provider 注册、技能加载、hooks 和工作线程启动顺序不变。

3. **`Pipeline/`（管道层）和 `Endpoints/`（端点层）**
   - 应用转发头部、CORS、WebSocket、工作线程启动、通道启动和异步关闭协调。
   - 打印终端就绪启动横幅、启动通知和就绪后的本地保存/浏览器提示。
   - 在 `Program.cs` 之外映射所有 HTTP/WebSocket 路由。

## AOT vs JIT

运行时模式选择仍然来自 `GatewayConfig.Runtime` 和 `RuntimeModeResolver`。

- `auto` 在支持动态代码时解析为 `jit`，否则为 `aot`。
- `aot` 保持裁剪安全的主流桥接通道。
- `jit` 保持扩展兼容性通道，包括原生动态插件。

网关配置层不替代插件/运行时能力强制执行。现有的快速失败阻断和 `/doctor` 诊断仍然来自核心运行时/插件子系统。

## 各模块迁移位置

- `Program.cs`
  - 现在编排引导、quickstart/恢复选择、服务注册、运行时初始化、管道映射、端点映射和 `Run(...)`
- `Bootstrap/GatewayBootstrapExtensions.cs`
  - 配置加载、验证、运行时状态解析和提前退出
- `Composition/*`
  - 分组的 DI 注册和构建后运行时组装
- `Composition/RuntimeInitializationExtensions*.cs`
  - 薄编排层加上提取的运行时组合和工厂阶段
- `Profiles/*`
  - 基于已解析的有效运行时模式的薄网关组合钩子
- `Pipeline/PipelineExtensions.cs`
  - 中间件、通道启动、工作线程和关闭连接
- `Pipeline/GatewayRuntimeShutdownCoordinator.cs`
  - 负责运行时创建资源和集成的有序异步清理
- `Endpoints/*`
  - 按诊断、OpenAI 接口、UI/控制、WebSocket 和 Webhook 边界分组的路由映射
  - 大型 admin 和 OpenAI 接口进一步拆分为薄映射器加领域特定的 partial 文件

## 添加新的启动行为

- 当行为必须在 `builder.Build()` 之前执行时，在 `Bootstrap/` 中添加配置/引导规则。
- 在相应的 `Composition/*ServicesExtensions.cs` 文件中添加 DI 友好的服务。
- 如果依赖已构建的服务、应用生命周期、插件加载或运行时排序，则在相关的 `InitializeOpenClawRuntimeAsync(...)` 阶段助手中添加仅运行时初始化。
- 将运行时拥有的清理工作注册到关闭协调器，而非阻塞同步的生命周期回调。
- 在相关的 `Endpoints/*` 模块中添加新的路由处理器，并通过 `MapOpenClawEndpoints(...)` 注册。
- 仅在 `Profiles/*` 中添加特定配置的网关组合；不要在其中重复运行时模式或插件能力策略。

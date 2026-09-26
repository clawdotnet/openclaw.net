# Python-free Nacos Live 验收设计

## 状态

设计已在对话中确认，等待用户审阅本文档后再进入计划阶段。

## 目标

将 `eng/nacos-live` 中的所有 Python 组件替换为 .NET 10 实现。现场验收仍须针对隔离且启用认证的 Nacos 3.2.4 部署验证 OpenClaw 正式 Nacos 适配器，并通过 `NacosMcpRouter` 完成真实 MCP 代理调用。

替换后须保留托管与 NativeAOT smoke、两种 agent runtime、事件驱动缓存失效、有界进程清理及验收证据。开发环境和 `nacos-live` CI workflow 均不得依赖 Python、pip、Python 包或 Python 脚本。

## 非目标

- 不改变 OpenClaw 生产适配器行为，也不扩大兼容性声明。
- 不在本仓库的验收流程中修改或重新构建 Router 实现。
- 不替换隔离 Nacos Java 服务，也不更改固定的 Nacos 版本。
- 除使用 HTTP MCP fixture 所必需的调整外，不改变现有托管、NativeAOT 或双 runtime 验收断言。

## 组件

保留现有 `eng/nacos-live` 路径，将其中的 Python 文件替换为 .NET 10 验收项目。该项目负责编排、临时部署、证据输出，并提供子进程模式来运行 weather MCP fixture。Fixture 作为独立子进程运行，以保留真实 HTTP 边界，同时避免增加另一个项目或绕过进程通信。

使用已发布的 .NET global tool 包 `NacosMcpRouter`，固定版本 `1.0.0`。安装命令：

```sh
dotnet tool install --global NacosMcpRouter --version 1.0.0
```

启动安装后的 `nacos-mcp-router` 命令。本地与 CI 使用相同包版本，不 clone Router 源码仓库。该包目标框架为 .NET 10。NuGet 包约 139 MB，另需下载 embedding 模型；这是使用已发布 tool 的已接受成本。

Fixture 是仅绑定 loopback 的 .NET 10 Streamable HTTP MCP 服务，公开 `get_weather(city)`，默认返回确定且明确标注为 fixture 的数据；不调用 LLM 或外部天气服务。

## 数据流

1. CI 安装 .NET 10、Java 17 和固定版本的 Router global tool。保留现有启用可选 Nacos 的 NativeAOT Gateway 发布、`NacosLiveSmoke` 构建及 `OpenClaw.Tests` 构建。
2. .NET 验收项目通过 HTTPS 下载官方 Nacos 3.2.4 压缩包，校验现有固定 SHA-256，将其解压至唯一临时目录，生成临时凭据和端口，启用认证并等待服务就绪。
3. 项目通过 HTTPS 将 `model.onnx`、`tokenizer.json`、`vocab.txt` 和 `config.json` 下载到临时模型目录，并通过 `EMBEDDING_MODEL_DIR` 传入该目录的绝对路径。默认仓库和分支与 Router 当前的 ModelScope 下载脚本一致（`sentence-transformers/all-MiniLM-L6-v2`、`master`）；endpoint、仓库和分支可由 `HF_ENDPOINT`、`HF_REPO`、`HF_BRANCH` 覆盖。
4. 项目启动 weather fixture 子进程并等待 HTTP readiness endpoint。通过 Nacos HTTP AI API 发布包含 `mcp-streamable` 协议元数据、tool specification、`RemoteServerConfig.ServiceRef` 和匹配 REF endpoint specification 的 MCP 服务；再按 Nacos 3.2.4 要求，通过 Nacos gRPC AI API 注册实际 backend endpoint。
5. 项目使用隔离的 Nacos 凭据、动态 HTTP 端口、临时数据目录及已下载模型目录启动 `nacos-mcp-router`。等待 `/health` 就绪后再运行验收客户端。
6. 现有托管和 NativeAOT smoke 程序通过 Streamable HTTP 连接 Router，验证 Router 恰好公开三个工具、静态及动态能力绑定、缓存复用、天气 payload、能力执行路径零模型调用、认证后的 Nacos event subscription 进入 active，以及真实配置发布后两秒内失效并重新绑定。验收还检查 Router 日志中没有 `vector search failed`，避免 embedding/vector 搜索失败后回退到关键词搜索而造成假阳性。双 runtime 测试继续使用现有 `OPENCLAW_NACOS_LIVE=1` 契约。
7. 项目在新的输出目录下写出已有的 managed/native JSON 报告、acceptance JSON、TRX 结果及进程日志。成功时打印 `NACOS_LIVE_ACCEPTANCE_PASS`；任一必需断言失败则以非零退出码结束。

## 生命周期与失败处理

所有端口、凭据、解压后的 Nacos 文件、模型文件和 Router 数据均按运行隔离。Router 客户端凭据通过环境变量传给子进程，不放入命令行参数或报告。Nacos 自身的认证密钥只写入隔离部署的配置文件，并限制文件访问权限。服务仅监听 loopback。

所有 readiness 等待和子进程操作都有超时。启动失败时保留并指出相关进程日志。清理在 `finally` 中使用独立且有界的取消预算，逐项继续执行：通过 gRPC 注销 Nacos MCP endpoint，通过 HTTP 删除 MCP 注册，最后按启动逆序终止所拥有的子进程树。清理失败须报告，但不得掩盖原始验收失败。证据保留在输出目录；子进程停止后删除临时部署和模型数据。

下载、解压、Nacos readiness、注册、MCP 初始化、Router health、embedding/vector 搜索、smoke 或测试失败均使本次运行失败。日志和验收 JSON 不得包含秘密。模型源不可用或 Router 无法成功执行 embedding/vector 搜索时必须失败，不能静默退化为关键词搜索。

## CI 与本地运行契约

`nacos-live` workflow 移除 `actions/setup-python`、pip 缓存和安装、Python 模型预检及 `verify.py` 调用。Workflow 安装固定版本的 global tool，确保 global tool 目录位于 `PATH`，然后在现有构建之后调用 .NET 验收项目。Artifact 上传继续保留 JSON、TRX 和进程日志。

本地运行需要 .NET 10、Java 17+、访问固定 Nacos release、NuGet 和配置的模型 endpoint 的网络。安装相同版本的 global tool，并从仓库根目录运行 .NET 项目。删除 Python requirements 文件、Python Router wrapper、Python weather server 和 Python verifier。

## 验证

- 构建验收项目及 fixture 模式；适用时保持 JSON reflection disabled。
- 运行覆盖编排 helper、模型下载校验、Nacos 注册 payload 构造和清理行为的定向 .NET 测试。
- 针对隔离 Nacos 3.2.4 运行完整 live CI workflow，并核对 managed 与 NativeAOT 报告、双 runtime TRX 和成功标记。
- 确认 `nacos-live` workflow 与 `eng/nacos-live` 不含 Python setup、pip requirements 或 Python 入口。
- 验证故意错误的 Nacos 压缩包摘要、不可用的模型文件、失败的子进程和失败的 MCP 请求都会返回非零结果、保留诊断证据并尝试清理。

## 兼容性与安全说明

本变更仅影响工程验收工具，不影响已发布的 Gateway 行为。Router NuGet 包及 embedding 模型是外部构建/运行输入，须通过 HTTPS 获取。Nacos 凭据仅在单次运行中有效。Nacos 3.2.4 的 HTTP AI 接口不提供 MCP endpoint 注册，因此 endpoint 使用 gRPC 注册；release、读取和删除使用 HTTP，流程参考 `RedNb.Nacos.Sample.AI`。

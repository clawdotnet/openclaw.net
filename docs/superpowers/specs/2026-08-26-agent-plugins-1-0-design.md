# Agent Plugins 1.0.0 核心兼容层设计

## 目标

为 OpenClaw.NET 增加 Agent Plugins 1.0.0 可移植插件格式的核心兼容层，同时保留现有 `openclaw.plugin.json` 插件格式。两种格式并行发现，内部通过独立适配模型接入现有技能加载器和 MCP 运行时。Agent Plugin 的完整发现面是 `plugin.json` → `mcp.json` → `skills/`；客户端不读取或执行服务器源代码来推断插件能力。

本期只覆盖本地插件包的发现、验证、技能加载、MCP 配置适配和运行时刷新。不包含 GitHub 安装/更新/卸载、管理 UI。

## 包格式

Agent Plugin 是一个目录，结构如下：

```text
plugins/<name>/
├── plugin.json
├── skills/
│   └── <skill>/SKILL.md
└── mcp.json
```

`plugin.json` 必须包含 `name`、`version`、`description` 和 `license`，并可通过 `keywords` 提供注册表和客户端用于发现的关键词。`$schema` 必须精确匹配本地常量：

```text
https://agent-plugins.org/schemas/1.0.0/plugin.schema.json
```

加载时不联网获取 schema。未知字段忽略；`extensions` 字段和 `com.*` 反向域名扩展目录不读取、不校验。

技能发现只检查 `skills/` 的直接子目录，并且只接受名字精确为 `SKILL.md` 的文件，不递归扫描。有效技能通过现有 `SkillLoader` 进入技能优先级链；插件带来的技能是只读的，不能单独移除。

## 完整发现面

Agent 客户端只依赖插件包中的三个标准构件理解插件的功能和集成需求：

- `plugin.json`：提供插件身份、版本、描述、许可证、关键词和规范版本，是注册表及客户端读取的顶层入口契约。
- `mcp.json`：声明插件提供的 MCP 服务器、传输方式、启动参数、环境变量、工作目录和连接地址，是 MCP 集成需求的唯一来源。
- `skills/`：提供可被发现和加载的 Agent Skills；每个直接子目录中的 `SKILL.md` 自描述该技能何时以及如何调用相关工具。

这三个构件共同构成可移植插件的静态能力描述，并将三类关注点解耦：服务器能做什么由 MCP 工具实现决定，如何访问由 `mcp.json` 决定，何时以及如何使用由 `skills/` 决定。客户端无需检查服务器内部实现即可获得发现、配置和调用所需的上下文。服务器源代码、构建产物之外的私有目录、`extensions` 字段和 `com.*` 扩展目录都不属于 OpenClaw.NET 的发现面，不读取、不执行、不用于补全或覆盖标准构件声明。构件缺失时按各自的可选性处理：清单始终必需，缺少 `mcp.json` 表示没有 MCP 集成，缺少 `skills/` 表示没有技能；存在但不合规的构件则按后文失败边界处理。

## 发现与适配

两种插件格式共享发现流程，但使用不同的清单识别规则：

- Agent Plugins：识别 `plugin.json`，适配为 `AgentPluginPackage`。
- 现有 OpenClaw 插件：继续识别 `openclaw.plugin.json`，保持当前 bridge、channel、provider、service 和 command 行为。

Agent Plugin 的发现顺序为：

```text
显式 Plugins:Load:Paths
  -> 工作区 plugins/
  -> 用户级 ~/.openclaw/plugins/
```

同名插件由高优先级来源覆盖低优先级来源。现有插件格式不改变原有优先级和配置语义。

Agent Plugin 不伪装成现有 JS/TS bridge 插件，也不映射出规范没有定义的 channel、provider 或 service 能力。

## MCP 适配

`mcp.json` 顶层解析成功后，每个服务器条目独立验证，并转换成现有 `McpServerConfig`，复用当前 MCP 启动、工具注册、超时和生命周期机制。

一期支持：

- `stdio`
- `streamable-http`

遗留 `sse` 不实现；遇到时只跳过该条并生成诊断。

字段映射如下：

- `command` 和 `args` 映射到 stdio 命令及参数。
- `url` 和 `headers` 映射到 Streamable HTTP。
- `cwd` 映射到工作目录。
- `env` 映射到进程环境变量。

支持以下变量展开：

- `${PLUGIN_ROOT}`：插件根目录。
- `${PLUGIN_DATA}`：现有网关存储根目录下的 `plugin-data/<plugin-name>`。

插件数据目录位于插件目录之外，替换或卸载插件时保留。插件内资源路径必须限制在插件根目录内；`${PLUGIN_DATA}` 必须限制在统一的 `plugin-data` 根目录内。路径越界、非法绝对路径或无法安全解析的路径直接拒绝。

Streamable HTTP 使用 manual redirect 策略。遇到重定向时停止并报告，不把配置中的 headers 转发到新地址。

## 失败边界与诊断

失败隔离固定如下：

| 错误 | 结果 |
| --- | --- |
| `plugin.json` 缺失或不合规 | 整个 Agent Plugin 不加载 |
| `mcp.json` 顶层无效 | 仅关闭该插件 MCP，技能仍可用 |
| 单个技能无效 | 只跳过该技能 |
| 单条 MCP 无效 | 只跳过该条 MCP |
| 路径越界 | 拒绝对应零件并记录诊断 |
| 不支持的 MCP 传输 | 跳过该条并记录诊断 |
| `extensions` 或 `com.*` 内容 | 忽略 |

所有被跳过的零件都产生结构化诊断，至少包含插件名、来源路径、组件类型、错误代码和可读消息。运行时状态应能据此显示插件存在被跳过的零件，不能静默吞掉错误。

## 运行时刷新

刷新采用“构建新快照，验证成功后替换旧快照”：

1. 重新发现并验证两种插件格式。
2. 构建新的技能列表和 MCP 配置快照。
3. 新快照准备完成后切换到运行时。
4. 被移除或替换插件的 MCP 进程先停止，再移除旧注册。
5. 新 MCP 连接失败时，不影响已有技能或其他插件。

刷新必须避免暴露半套状态；插件移除顺序必须先停进程再删注册或目录。

## AOT/JIT 约束

Agent Plugins 核心层只处理 JSON、文件系统路径、技能内容和现有 MCP 配置，不引入动态程序集加载或反射执行，因此应保持 NativeAOT 友好。不得为了兼容 Agent Plugins 而扩大现有 JS/TS bridge 或 native dynamic plugin 的 AOT 能力声明。

## 验收测试

测试必须覆盖：

- 两种插件格式并存且互不破坏。
- 发现目录优先级和同名覆盖。
- schema、必需字段、未知字段和扩展字段处理。
- 客户端仅凭 `plugin.json`、`mcp.json` 和 `skills/` 识别能力，不读取服务器源代码或私有扩展目录。
- 只发现 `skills/` 直接子目录中的 `SKILL.md`。
- `${PLUGIN_ROOT}`、`${PLUGIN_DATA}` 展开和数据目录保留。
- 路径穿越、绝对路径和非法工作目录拒绝。
- stdio、Streamable HTTP 映射以及 SSE 跳过诊断。
- manual redirect 不转发 headers。
- 五级失败边界和结构化诊断。
- 刷新时先停止旧 MCP，再替换运行时快照。
- AOT 目标不引入动态代码依赖。

## 后续拆分

以下能力不属于本期设计，应分别形成后续规格：

- GitHub 稀疏浅克隆、临时目录验证、原子安装和来源记录。
- 更新、卸载和插件数据目录管理 API。
- 管理 UI 的插件卡片、跳过零件提示和只读组件展示。
# OpenClaw.NET 工具指南

本指南全面介绍 OpenClaw.NET 中可用的原生工具及其安全配置方法。

> **工具总数：75**（原生 C# `ITool` / `IToolWithContext` 实现，分布在 `Agent`、`Core`、`Gateway`、`Protocols`、`Plugins` 和 `SemanticKernelAdapter` 中）。更新于 2026-06-20。

---

## 🚀 如何使用 / 安装工具

### Agent 如何使用工具
无需手动调用工具！OpenClaw 的认知架构（"ReAct" 循环）会分析你的提示词，查看已启用的工具列表，并决定使用哪些工具来达成目标。

例如，如果你说 *"把周报邮件发给老板"*，Agent 会自动构造 `email` 工具调用、执行，并在完成后通知你。

### 工具名称（重要）
工具**名称**是 Agent 和工具审批系统使用的稳定标识符（如 `home_assistant_write`）。代码库中有些地方提到"插件 id"（通常用连字符，如 `home-assistant`）——那些**不是**工具名称。

### 如何安装新工具

有两种主要方式为你的 Agent 添加新能力：

1. **原生 C# 工具**
   在 `src/OpenClaw.Gateway/appsettings.json` 中配置。原生工具（如 `email`、`browser` 或 `shell`）内建于高性能 .NET 运行时中，提供最佳性能和 AOT 兼容性。参见下方的 Core 和 Native Plugin 工具列表。

2. **社区 Node.js 插件（桥接）**
   OpenClaw.NET 支持上游 [OpenClaw](https://github.com/openclaw/openclaw) 插件格式中已实现和测试的桥接接口。
   - 确保机器上已安装 Node.js 18+
   - 将社区插件下载或克隆到 `.openclaw/extensions/` 文件夹
   - 在该插件文件夹内运行 `npm install`
   - 对于 TypeScript 插件，还需确保 `jiti` 存在于插件依赖树中
   - 重启 OpenClaw.NET gateway，gateway 会自动检测、加载并桥接插件
   - `Runtime:Mode=aot`：支持 `registerTool`、工具执行、`registerService`、插件打包的 skills、`.js`/`.mjs`/`.ts` 发现以及文档化的 config-schema 子集
   - `Runtime:Mode=jit`：额外支持 `registerChannel`、`registerCommand`、`registerProvider` 和 `api.on(...)`
   - 不支持的扩展宿主 API 或 AOT 模式下的 JIT 专属能力会快速失败并提供明确的诊断信息

---

## 📊 工具清单（84 个工具，截至 2026-06-20）

| 类别 | 工具 |
|----------|-------|
| 文件与 Shell | `shell`、`read_file`、`write_file`、`edit_file` |
| 记忆 | `memory`、`memory_search`、`memory_get`、`project_memory` |
| 网页与搜索 | `browser`、`web_search`、`web_fetch`、`x_search` |
| 代码与执行 | `code_exec`、`git`、`apply_patch`、`pdf_read` |
| 通讯 | `email`、`message`、`inbox_zero` |
| 数据库与 Notion | `database`、`notion`、`notion_write` |
| 家庭自动化 | `home_assistant`、`home_assistant_write`、`mqtt`、`mqtt_publish` |
| 日历与图像 | `calendar`、`image_gen` |
| 会话与委托 | `sessions`、`delegate_agent`、`process` |
| Canvas 与 A2UI | `canvas_present`、`canvas_hide`、`canvas_navigate`、`canvas_snapshot`、`a2ui_push`、`a2ui_reset`、`a2ui_eval`、`a2ui_create_surface`、`a2ui_update_components`、`a2ui_update_data_model`、`a2ui_delete_surface` |
| Gateway 与管理 | `automation`、`cron`、`gateway`、`agents_list`、`profile_read`、`profile_write`、`session_search`、`sessions_history`、`sessions_send`、`sessions_spawn`、`sessions_yield`、`session_status`、`todo` |
| Goal 与 Loop | `get_goal`、`create_goal`、`update_goal`、`loop_control` |
| 分形记忆 | `fractal_memory_search`、`fractal_memory_open`、`fractal_memory_recent`、`fractal_memory_export`、`fractal_memory_validate`、`fractal_memory_handoff_create`、`fractal_memory_index_refresh` |
| 元技能 | `emit_text`、`meta_skill_fill_slots`、`meta_skill_assemble`、`meta_skill_lint_run`、`meta_skill_smoke_run`、`meta_skill_runtime_e2e_run`、`meta_skill_persist_proposal` |
| 技能 | `load_skill`、`read_skill_resource`、`meta_invoke`、`list_tools` |
| 外部与 MCP | `external_cli`、`mcp_native`（动态） |
| Semantic Kernel | `semantic_kernel_entrypoint`、`semantic_kernel_function` |
| 支付与 Mempalace | `payment`、`mempalace_knowledge_graph` |
| 流式与测试 | `streaming_smoke_echo`、`bridged_plugin`（动态） |

---

## 🏗 核心工具
这些工具默认启用，但可通过 `Security` 和 `Tooling` 配置进行限制。

### 1. Shell 工具 (`shell`)
允许 Agent 执行终端命令。
- **配置**: `OpenClaw:Tooling:AllowShell` (bool)
- **自主模式**（推荐）：通过 `OpenClaw:Tooling:AllowedShellCommandGlobs` 限制可运行的命令，通过 `OpenClaw:Tooling:ForbiddenPathGlobs` 阻止敏感路径
- **安全**: 可通过设置 `RequireToolApproval: true` 进行限制

### 2. 文件系统工具 (`read_file`、`write_file`)
基本的文件操作。
- **配置**: `AllowedReadRoots`、`AllowedWriteRoots`
- **安全**: 将 `write_file` 加入 `ApprovalRequiredTools`
- **自主模式**（推荐）：设置 `WorkspaceOnly=true` + `WorkspaceRoot` 限制工作区

### 3. 浏览器工具 (`browser`)
使用 Playwright 导航和交互网站。
- **配置**: `OpenClaw:Tooling:EnableBrowserTool` (bool)
- **选项**: `BrowserHeadless`（默认 true）、`BrowserTimeoutSeconds`（默认 30）

### 4. 记忆工具 (`memory`、`memory_search`、`memory_get`)
在配置的记忆存储中保存和检索笔记。
- 支持 SQLite FTS5 关键词搜索
- `OpenClaw:Memory:Recall:Enabled=true` 可自动注入相关记忆到上下文

### 5. 项目记忆工具 (`project_memory`)
项目范围的记忆读写（适用于长期项目）。

### 6. 会话工具 (`sessions`)
管理/运维工具：列出活跃会话、检查历史记录、发送跨会话消息。

### 7. 委托 Agent 工具 (`delegate_agent`)
生成"子 agent"进行多 agent 委托（需 `OpenClaw:Delegation:Enabled=true`）。

### 7b. Canvas 和 A2UI 工具
控制当前 WebSocket 会话的 Canvas 可视化工作区。详见 [CANVAS_A2UI.md](CANVAS_A2UI.md)。

---

## 🔌 原生插件工具
需在 `appsettings.json` 的 `OpenClaw:Plugins:Native` 中启用。

### 8. 邮件工具 (`email`)
通过 SMTP 发送、IMAP 读取邮件。

### 9. Git 工具 (`git`)
执行 git 操作（Clone、Pull、Commit、Push）。建议禁用 push。

### 10. 网页搜索 (`web_search`)
使用 Tavily、Brave 或 SearXNG 搜索网页。

### 11. 网页抓取 (`web_fetch`)
从 URL 获取和提取内容。

### 12. 代码执行 (`code_exec`)
在隔离环境中执行 Python、JavaScript 或 Bash 代码。

### 13. PDF 阅读器 (`pdf_read`)
从 PDF 文档提取文本。

### 14. 图像生成 (`image_gen`)
使用 DALL-E 生成图像。

### 15. 日历工具 (`calendar`)
通过 Google Calendar REST API 管理日历事件。

### 16. 数据库工具 (`database`)
查询 SQLite、PostgreSQL 或 MySQL 数据库。

### 17. 收件箱归零 (`inbox_zero`)
AI 驱动的邮件分类整理。

### 18. Home Assistant (`home_assistant`、`home_assistant_write`)
通过 Home Assistant 控制智能家居设备。

### 19. MQTT (`mqtt`、`mqtt_publish`)
集成 MQTT 代理，用于 DIY 自动化。

### 20. Notion (`notion`、`notion_write`)
使用 Notion 作为可选的共享便签或笔记数据库。

### 21. 工具列表 (`list_tools`)
运行时发现所有已注册的工具。返回每个工具的名称、描述和完整的 JSON 参数 schema。
- **用途**：使 MetaSKILL 和其他编排流程能够在运行时自省可用能力，无需硬编码工具名称。
- **参数**：
  - `filter`（可选）：对工具名称进行子串匹配（不区分大小写）。省略 `filter` 会返回所有工具。
- **输出**：JSON 数组，每个元素包含 `name`（字符串）、`description`（字符串）和 `parameterSchema`（JSON 对象，即工具的输入 schema）。
- **使用方式**：可由 MetaSKILL `kind: fan_out` 步骤程序化调用来验证工具可用性，也可由 Agent 在推理使用哪些工具时直接调用。

---

## 🛡 安全最佳实践
1. **审批模式**: 启用 `RequireToolApproval: true` 以在执行前审查危险命令
2. **环境变量**: 始终使用 `env:SECRET_NAME` 存储 API 密钥和密码
3. **路径限制**: 将 `AllowedReadRoots` 和 `AllowedWriteRoots` 限制到项目目录

### 自主模式（推荐）
- `readonly`：拒绝所有写入工具
- `supervised`（默认）：启用工具审批
- `full`：无需审批（仍遵守策略）

---

## ⏰ 定时任务（Cron）
OpenClaw.NET 可通过 `OpenClaw:Cron` 运行定时提示词。设置 `ChannelId` 和 `RecipientId` 以通过频道适配器发送响应。

详见 [LOOP_TECHNICAL_ARCHITECTURE.md](zh-CN/LOOP_TECHNICAL_ARCHITECTURE.md)。

---

## 🌉 桥接工具（TypeScript/JS）
OpenClaw.NET 可通过**插件桥接**运行原始 OpenClaw 插件。这些工具从 `.openclaw/extensions` 文件夹动态加载。

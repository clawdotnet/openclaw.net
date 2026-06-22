# Canvas 和 A2UI

Canvas 和 A2UI 是面向 WebSocket 客户端的第一方可视化工作区。OpenClaw.NET 支持现有的 A2UI v0.8 JSONL 流程和 A2UI v0.9 结构化 Surface 操作。它不是浏览器工具的替代品，不支持任意远程网页导航或远程页面脚本执行。

## 功能范围

支持的：

- 通过 `a2ui_push` 的 A2UI v0.8 JSONL 帧
- A2UI v0.9 结构化 Surface 操作：`createSurface`、`updateComponents`、`updateDataModel`、`deleteSurface`、`action`、`syncUIToData` 和 `error`
- 按 `surfaceId` 路由的多独立 Surface
- 按 Surface 的组件树、数据模型、诊断和快照
- 通过 `supportedCatalogIds` 进行目录协商
- Present/Hide/Reset/Push/Snapshot 命令流
- WebChat 中通过 `canvas_navigate` 传递 `html` 的本地 HTML 导航
- `about:blank` 导航
- A2UI 交互反馈作为结构化会话轮次返回

不支持的：

- `http:` 和 `https:` Canvas 导航
- 针对远程网页的脚本执行
- 跨会话 Canvas 共享
- Companion 中的本地 HTML/WebView 渲染

## 配置

| 设置 | 默认值 | 说明 |
| --- | --- | --- |
| `Enabled` | `true` | loopback/本地启用 |
| `AllowOnPublicBind` | `false` | 非 loopback 绑定时需显式启用 |
| `MaxCommandBytes` | `262144` | 最大序列化命令大小 |
| `MaxSnapshotBytes` | `262144` | 最大快照大小 |
| `CommandTimeoutSeconds` | `10` | 待处理命令超时 |
| `MaxFramesPerPush` | `100` | 单次推送最大帧数 |
| `EnableLocalHtml` | `true` | 允许内联 HTML 导航 |
| `EnableRemoteNavigation` | `false` | 远程网页导航不支持 |
| `EnableEval` | `true` | 能力门控的 eval（第一方客户端不声明此能力） |

## 协议

服务器到客户端 WebSocket 信封类型：`canvas_present`、`canvas_hide`、`canvas_navigate`、`canvas_snapshot`、`a2ui_push`、`a2ui_reset`、`a2ui_eval` 以及 v0.9 的 `createSurface`、`updateComponents`、`updateDataModel`、`deleteSurface`、`syncUIToData`。

客户端到服务器：`canvas_ready`、`canvas_ack`、`canvas_snapshot_result`、`a2ui_event`、`a2ui_action`、`a2ui_error` 等。

## A2UI v0.9 Surface 生命周期

- `a2ui_create_surface` 创建或替换目标 Surface 并锁定协商的目录
- `a2ui_update_components` 仅替换目标 Surface 的组件树
- `a2ui_update_data_model` 仅更新数据模型
- `a2ui_delete_surface` 删除 Surface 并清除目录锁
- `canvas_snapshot` 返回 Surface 特定状态

## 目录协商

内置目录 ID：`urn:a2ui:catalog:openclaw_v0_8` 和 `urn:a2ui:catalog:agenui_catalog`。

选择规则：客户端在 `canvas_ready` 中声明 `supportedCatalogIds`，broker 验证并锁定每个 Surface 生命周期的目录。

## 安全模型

- 命令仅作用于当前 WebSocket 会话
- 非 WebSocket 会话不能接收 Canvas 命令
- 公开部署需显式启用 Canvas 转发
- 远程网页导航被拒绝
- A2UI eval 受能力门控，不针对第三方页面
- 命令和结果大小受限

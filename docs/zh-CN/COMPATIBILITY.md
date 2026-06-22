# 兼容性指南

这是 OpenClaw.NET 的权威兼容性矩阵。涵盖运行时模式、上游 `SKILL.md` 复用、插件兼容性、通道运维者对等性以及当前需要关注的限制。

## 状态图例

- `Supported`：预期的生产路径，有自动化测试支持
- `Supported with caveats`：当前可用，但有重要的范围限制或模式要求
- `Not supported`：快速失败并输出显式诊断，不会部分加载

## 运行时模式

- `auto` 在有动态代码时解析为 `jit`，否则为 `aot`
- `aot` 强制严格的裁剪安全通道
- `jit` 启用扩展的插件和动态原生通道

## 上游 Skill 兼容性

| 接口 | 状态 |
| --- | --- |
| 独立 `SKILL.md` 包 | Supported |
| ClawHub skill 安装流程 | Supported |
| 插件打包的 skills | Supported |
| 工作区/托管/内置 skill 优先级 | Supported |

## 插件包兼容性

| 接口 | 状态 |
| --- | --- |
| `api.registerTool()` | Supported（aot + jit） |
| `api.registerService()` | Supported（aot + jit） |
| `api.registerChannel()` | jit only |
| `api.registerCommand()` | jit only |
| `api.on(...)` | jit only |
| `api.registerProvider()` | jit only |
| 独立 `.js`/`.mjs`/`.ts` | `.ts` 需 `jiti` |
| 原生动态 .NET 插件 | jit only |
| 上游 TypeScript `payment` 插件 | Not supported（使用原生支付运行时） |

## 当前不支持

| 接口 | 失败码 |
| --- | --- |
| `api.registerGatewayMethod()` | `unsupported_gateway_method` |
| `api.registerCli()` | `unsupported_cli_registration` |

## Canvas 和 A2UI 兼容性

| 接口 | 状态 |
| --- | --- |
| Canvas present/hide/snapshot | Supported |
| 本地 Canvas 导航 | Supported（`about:blank` + sandbox 内联 HTML） |
| 远程网页 Canvas 导航 | Not supported |
| A2UI v0.8 JSONL | Supported |
| A2UI v0.9 结构化 Surface | Supported |
| A2UI 交互反馈 | Supported |

## 通道兼容性

Telegram、Twilio SMS、WhatsApp、Teams、Slack、Discord、Signal 均支持 DM policy、动态允许列表和诊断。Email 和通用 Webhook 需单独处理。

## 受支持的配置 Schema 子集

支持的 JSON Schema 关键字：`type`、`properties`、`required`、`additionalProperties`、`items`、`enum`、`const`、`minLength`/`maxLength`、`minimum`/`maximum`、`pattern`、`oneOf`/`anyOf` 等。

## 运维者信任工作流

- 插件安装候选按 `first-party`/`upstream-compatible`/`untrusted` 分类
- 可从 Admin UI 或 API 提升插件信任级别
- `GET /admin/plugins` 和 `GET /admin/skills` 暴露信任级别和诊断信息

## 已知限制

- 公开绑定默认禁用桥接插件和 shell
- JIT-only 能力保持 JIT-only
- TypeScript 插件需 `jiti`
- 工具名冲突：先到先得，重复跳过

## 自动化验证

兼容性声明由 `src/OpenClaw.Tests` 中的自动化测试支持：`PluginBridgeIntegrationTests.cs`、`NativeDynamicPluginHostTests.cs`、`PublicCompatibilitySmokeTests.cs`。

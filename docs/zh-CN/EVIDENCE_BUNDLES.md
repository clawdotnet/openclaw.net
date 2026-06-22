# 证据包（Evidence Bundles）

证据包是 Agent 运行或操作期间发生事件的结构化记录。它捕获工具结果、检查、审批、运行时事件、风险、假设、未测试区域和人工审查。

证据包在此版本中是被动的。仅在代码或运维者显式创建时生成。不会改变正常聊天、quickstart、Provider 或工具执行行为。

## 存在目的

证据包帮助运维者回答：

- Agent 做了什么？
- 调用了哪些工具？
- 运行了哪些检查？
- 什么通过或失败了？
- 还有哪些风险？
- 谁审查或批准了该操作？
- 什么证据支持接受或拒绝结果？

## 包结构

每个包可记录：源会话 ID、Harness Contract ID、置信度、证据项、检查和命令结果、风险及缓解措施、假设和未测试区域、人工审查、标签。

## Admin 检查

运维者可通过以下端点检查和追加证据包：

- `GET /admin/harness/evidence`
- `GET /admin/harness/evidence/{id}`
- `POST /admin/harness/evidence`
- `POST /admin/harness/evidence/{id}/items`
- `POST /admin/harness/evidence/{id}/checks`
- `POST /admin/harness/evidence/{id}/reviews`

读取端点需认证 admin viewer 权限，变更端点需 operator-level 权限和 CSRF 保护。

## 与 Harness Contracts 的关系

- Harness Contract = Agent 计划做什么
- Evidence Bundle = 实际发生了什么、检查了什么、什么仍然不确定、为什么结果值得或不值得信任

## 目前不支持的功能

- 非完整 Plan-Execute-Verify 模式
- 非每次运行自动验证
- 非默认对所有正常聊天启用
- 非每次工具调用自动创建
- 非自动回滚
- 不替代工具审批或人工审查

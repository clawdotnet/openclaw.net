# Connector 与 CLI / MCP 衔接设计（V1）

- 文档日期：2026-07-15
- 设计状态：已评审（brainstorming 会话结论）
- 适用范围：OpenClaw.NET 的 Action Connector Contract、CLI 入口、MCP 入口

## 1. 背景与目标

当前 Action 桥接已具备 `action_execute`、策略门禁、审批语义与治理映射能力。下一步需要把 Connector 能力统一暴露给 CLI 与 MCP，避免出现三套不一致契约。

V1 目标：

1. 统一 Connector Contract（运行时强类型 + 对外 schema）。
2. CLI 与 MCP 入口都走同一条同步请求-响应链路。
3. 保持治理语义一致（风险、审批、拒绝、证据映射）。
4. 未知 Connector 明确拒绝：`policy_denied`。

## 2. 关键决策

1. **双轨契约（主）**：C# interface + DTO 作为运行时权威模型；从该模型导出 JSON Schema 供 CLI/MCP 校验。
2. **同步语义（V1）**：CLI/MCP 都采用同步请求-响应，不引入异步作业编排。
3. **审批严格条件必填**：当 `decision=require_approval` 时，审批字段缺失即 `approval_denied`。
4. **未知 Connector 拒绝**：不降级默认 Connector，不透传。

## 3. 架构边界

### 3.1 Contract Core（权威模型）

- `IConnectorActionExecutor`
- `ActionRequest`
- `ActionResponse`
- `ApprovalRecord`
- `GovernanceMapping`

该层仅定义语义与字段，不包含 CLI/MCP 依赖。

### 3.2 Contract Schema（导出层）

- `SchemaExporter`：从 Contract Core DTO 导出 JSON Schema。
- `SchemaVersion`：为 CLI/MCP 提供明确版本边界。

### 3.3 Adapter Runtime（执行层）

- `ConnectorRegistry`：Connector 名称解析与白名单判断。
- `PolicyGate`：风险判级与执行决策。
- `ApprovalValidator`：审批严格条件校验。
- `ExecutionOrchestrator`：按既有 action 语义执行并组装结果。

### 3.4 CLI / MCP Adapter（入口层）

- CLI：命令参数先按 schema 校验，再调用统一 Facade。
- MCP：tool handler 参数先按 schema 校验，再调用统一 Facade。
- 两者都不直接依赖具体业务 Connector 实现。

## 4. 统一数据流

`Request -> Normalize/Validate -> Policy -> (Approval?) -> Execute -> GovernanceMapping -> Response`

说明：

1. Normalize/Validate 在入口层与 Runtime 双重执行（入口做快速失败，Runtime 做最终守门）。
2. `decision=require_approval` 时必须先过审批校验，才可进入 preCheck/execution。
3. 未知 Connector 在 Runtime Facade 阶段直接 `policy_denied`。

## 5. Contract 字段规则（V1）

### 5.1 核心必填

- `actionName`
- `target.system`
- `target.operation`
- `idempotencyKey`
- `traceId`
- `decision`
- `riskLevel`
- `governanceMapping`（至少包含可追踪标识）

### 5.2 审批扩展（条件必填）

当 `decision=require_approval` 时，下列字段必须完整：

- `approver`
- `decisionAt`（ISO 8601 UTC）
- `decisionReason`
- `ticketRef`

否则返回 `approval_denied`。

### 5.3 回滚扩展（按执行阶段回填）

当执行失败且进入补偿时，回填：

- rollback `status`
- rollback `history`
- rollback evidence 引用

V1 不要求所有场景都自动补偿成功，但必须可审计。

## 6. 错误模型

统一错误码（最小集）：

- `invalid_proposal`
- `policy_denied`
- `approval_denied`
- `connector_unavailable`
- `execution_failed`
- `rollback_failed`
- `idempotency_conflict`

CLI 与 MCP 均返回同一错误语义，不做各自私有错误翻译。

## 7. CLI / MCP 对齐策略

1. **同源 schema**：CLI 与 MCP 使用同一份 schema 版本。
2. **同源执行面**：都调用统一 Runtime Facade。
3. **同源响应结构**：状态、失败码、治理映射字段名称保持一致。
4. **同源拒绝行为**：未知 Connector 均返回 `policy_denied`。

## 8. 测试策略

### 8.1 Contract 层单元测试

- 必填字段校验
- 条件必填（审批）校验
- DTO 与导出 schema 对齐测试

### 8.2 Adapter 层集成测试

- 未知 Connector -> `policy_denied`
- `require_approval` 且字段缺失 -> `approval_denied`
- 同步链路成功返回治理映射

### 8.3 端到端回归

- MetaSkill `action_execute` 路径与 CLI/MCP 路径返回语义一致
- 治理字段在 Session/审计对象中可追踪

## 9. 非目标（V1）

1. 不引入异步作业总线与回调编排。
2. 不开放数据库直写路径。
3. 不为 CLI 与 MCP 分别维护独立契约模型。

## 10. 验收标准

1. 三入口（MetaSkill、CLI、MCP）在同一契约下可互换使用。
2. 未知 Connector 拒绝行为一致。
3. `require_approval` 场景严格条件必填生效。
4. 聚焦回归测试通过并可追踪治理映射。

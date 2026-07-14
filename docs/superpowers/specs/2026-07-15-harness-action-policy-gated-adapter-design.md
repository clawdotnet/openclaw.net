# Harness Action 策略网关适配器设计说明（Policy-Gated Action Adapter）

- 文档日期：2026-07-15
- 设计状态：待评审
- 适用范围：OpenClaw.NET，MetaSkill 推理结果到业务 API 回写链路
- 文档语言：中文

## 1. 背景与问题定义

当前 MetaSkill 已支持以 DAG 方式消费临时图，并通过模型完成推理。但在“推理结果到业务系统落地”这一步，缺少一个统一、可治理、可审计、可回滚的执行层。

目标不是让 MetaSkill 直接写数据库，也不是将 Harness 简化为普通 API 调用器，而是建立一个具备策略决策能力的 Action 适配层：

1. MetaSkill 负责推理与 Action 提案。
2. Harness 策略层负责风险判级与执行决策。
3. Action 适配层负责 preCheck、执行编排、补偿回滚、审计落证。
4. 写路径仅允许业务 API Connector，禁止数据库直写。

## 2. 设计目标与非目标

### 2.1 目标

1. 建立标准 ActionProposal 协议，承接 MetaSkill 推理输出。
2. 建立半自动触发机制：提案由 MetaSkill 产出，执行由策略层决策。
3. 建立风险分级策略：low 自动执行，medium 审批执行，high 与 critical 默认仅提案。
4. 建立可追溯审计链：proposal、判级、审批、执行、补偿均可回放。
5. 与现有 PEV、HarnessContract、EvidenceBundle 最小侵入集成。

### 2.2 非目标

1. 不在首版引入分布式事件总线与异步编排中台。
2. 不将 Harness 扩展为通用 BPM 引擎。
3. 不开放数据库直写能力。
4. 不承诺所有失败都能自动回滚成功。
5. 不改变现有未接入 Action 机制的 MetaSkill 行为。

## 3. 方案选择与结论

候选方案：

1. Policy-Gated Action Adapter（推荐）
2. Tool-Native Orchestration（过渡方案）
3. Event-Driven Action Bus（长期演进）

结论：采用方案 1。

原因：

1. 与当前 Harness 治理能力一致性最高。
2. 对现有运行时改动最小。
3. 能在首版实现“可执行 + 可治理 + 可审计”的平衡。

## 4. 总体架构

端到端流程：

1. MetaSkill DAG 推理后输出 ActionProposal。
2. ActionPolicyEngine 自动判级并输出决策：
   - proceed_execute
   - require_approval
   - proposal_only
3. ActionAdapter 在允许执行时运行：
   - preCheck
   - execution
   - rollback
4. ActionAuditBridge 将全过程映射到：
   - SessionMetaRunRecord
   - HarnessContract
   - Plan-Execute-Verify Run
   - EvidenceBundle

组件边界：

1. ActionProposalBuilder：仅构建提案，不负责执行决策。
2. ActionPolicyEngine：负责判级与决策。
3. ActionAdapter：负责执行编排与补偿语义。
4. BusinessApiConnector：封装业务 API，不向上暴露 DB 写接口。
5. ActionAuditBridge：负责治理记录落地与关联。

## 5. ActionProposal DSL 规范（首版）

### 5.1 最小字段

- actionName
- source
  - metaSkill
  - runId
  - stepId
- trigger
  - condition
  - evidenceRefs
- target
  - system
  - operation
- preChecks
- execution
- rollback
- idempotencyKey
- metadata

### 5.2 语义约束

1. execution 至少包含一个动作。
2. rollback 可为空，但建议显式声明。
3. call 必须命中 Connector 白名单。
4. 禁止出现数据库连接或 SQL 写入字段。
5. 模板变量仅允许读取声明上下文。

### 5.3 状态机

- proposed
- classified
- gated
- approved（需要审批时）
- precheck_passed 或 precheck_failed
- executing
- succeeded 或 failed
- rolling_back
- rolled_back 或 rollback_failed
- closed

### 5.4 标准失败码

- policy_denied
- approval_denied
- precheck_failed
- connector_unavailable
- execution_failed
- rollback_failed
- invalid_proposal
- idempotency_conflict

## 6. 执行语义

### 6.1 preCheck

1. 所有 preCheck 通过后才能进入 execution。
2. preCheck 默认只读、无副作用。
3. preCheck 失败直接终止执行并记录证据。

### 6.2 execution

1. 首版按顺序执行，避免并发竞态。
2. 任一步失败，默认停止后续动作。
3. 执行仅可通过 BusinessApiConnector。

### 6.3 rollback

1. rollback 是补偿动作，不等价数据库事务回滚。
2. execution 失败后触发补偿链。
3. rollback 失败保留原失败并升级人工处理建议。

### 6.4 幂等

1. proposal 必须携带 idempotencyKey。
2. 同 key 在窗口期内至多成功一次。
3. rollback 也需支持幂等保护。

## 7. 策略判级与决策矩阵

### 7.1 判级输入维度

1. 目标系统与操作类型。
2. 影响范围（单对象或批量）。
3. 参数敏感度。
4. 环境上下文（prod、时间窗等）。
5. 历史失败率与回滚率。

### 7.2 默认决策矩阵

1. low
   - decision: proceed_execute
   - approval: false
2. medium
   - decision: require_approval
   - approval: true
3. high
   - decision: proposal_only
   - approval: true（仅用于人工升级执行）
4. critical
   - decision: proposal_only
   - approval: true（强约束）

### 7.3 判级结果结构

- decision
- riskLevel
- reasonCodes
- requiredApprovals
- constraints

### 7.4 保护性降级

1. 策略引擎不可用时降级 proposal_only。
2. 判级不确定时按高风险处理。
3. 连接器未知时拒绝执行。

## 8. 与现有系统集成

### 8.1 接线策略（最小侵入）

1. 新增受控工具入口 action_execute。
2. MetaSkill 通过 tool_call 调 action_execute。
3. action_execute 内部串联 PolicyEngine 与 ActionAdapter。
4. 现有 ToolExecutor 的 PEV 机制继续生效。

### 8.2 记录映射

1. SessionMetaRunRecord：DAG 步骤级结果。
2. HarnessContract：行动意图、资源写集合、验证和回滚计划。
3. PEV Run：决策、审批、验证状态。
4. EvidenceBundle：执行和补偿细粒度证据。

### 8.3 向后兼容

1. 未调用 action_execute 的旧技能行为不变。
2. 可按环境与技能灰度启用。
3. 未配置连接器时自动 proposal_only。

## 9. 配置模型（建议）

建议新增 OpenClaw.Harness.ActionAdapter 配置域，核心项包括：

1. Enabled
2. DefaultDecisionMode（risk-tiered）
3. Policy（low、medium、high、critical）
4. RiskClassifier（规则表达）
5. Connectors（白名单与操作白名单）
6. Guards
   - BlockDirectDatabaseWrite
   - DenyUnknownConnector
   - DenyUnknownOperation
7. IdempotencyWindowMinutes
8. MaxExecutionSteps 与 MaxRollbackSteps
9. RequireEvidence

## 10. 测试与验收

### 10.1 测试矩阵

1. 功能：proposal 解析、判级、执行、补偿、幂等。
2. 治理：审批阻断、证据完整、合同关联、可解释决策。
3. 安全：拦截数据库直写、拒绝未知连接器、参数越权防护。
4. 回归：旧 MetaSkill 不受影响，双运行时语义一致。

### 10.2 验收门槛

1. 功能、治理、安全用例全部通过。
2. staging 通过拒绝审批与补偿失败演练。
3. 生产默认策略满足高风险不自动执行。

## 11. 上线与迁移计划

### 11.1 阶段化

1. 阶段 0：影子模式，全量 proposal_only。
2. 阶段 1：low 自动执行，medium 审批执行。
3. 阶段 2：策略调优与覆盖面扩展。

### 11.2 回退策略

1. 关闭 ActionAdapter 即回退到 proposal-only。
2. 连接器级熔断，不影响其他连接器。
3. rollback 失败自动升级人工处置。

### 11.3 发布后观测

1. 决策分布占比。
2. 审批等待时长与拒绝率。
3. 执行成功率与 rollback 触发率。
4. 策略拒绝与未知连接器拒绝次数。

## 12. 风险与缓解

1. 风险：判级误报导致执行过保守或过激进。
   - 缓解：阶段 0 影子模式校准规则。
2. 风险：连接器语义不一致导致补偿失败。
   - 缓解：Connector 契约测试与幂等约束。
3. 风险：审批链路成为瓶颈。
   - 缓解：仅对 medium+ 启用审批，优化审批 SLA。

## 13. 结论

本设计在不破坏现有 MetaSkill 与 Harness 主体架构的前提下，补齐了“推理结果到业务回写”的治理闭环：

1. 推理与执行职责分离。
2. 策略驱动的风险分级与执行决策。
3. 可审计、可补偿、可回退。
4. 严格禁止数据库直写路径。

该方案可作为工业场景下 Harness Action 的首版落地基线。

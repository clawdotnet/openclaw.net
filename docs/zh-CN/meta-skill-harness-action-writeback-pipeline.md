# MetaSkill 临时图推理 → Harness Action 业务 API 回写全链路指南

- 文档日期：2026-07-16
- 适用范围：OpenClaw.NET
- 文档语言：中文

## 1. 概述

本文档描述从 MetaSkill DAG 消费临时图（`load_temporary_graph`），经 LLM 推理产出结构化提案
（ActionProposal），到 Harness Action 策略判级并最终通过 HTTP Connector 调用业务 API 回写
的完整数据流和代码路径。

**核心原则：**

- 推理与执行职责分离：MetaSkill 负责推理提案，Harness 策略层负责风险判级与执行决策
- 写路径仅允许业务 API Connector，禁止数据库直写
- 全链路可审计：proposal → 判级 → 执行 → 补偿均可追溯
- 不改变未接入 Action 机制的旧 MetaSkill 行为

> **外部切片器：** 临时图由 `openclaw graph slice` CLI 命令生成，基于 dotNetRDF 3.5.2，
> 支持从 SPARQL 端点、本地 RDF 文件、关系数据库+R2RML 三种源执行 SPARQL CONSTRUCT +
> JSON-LD Framing。详见 [图切片器设计说明](../superpowers/specs/2026-07-16-graph-slicer-sparql-construct-jsonld-design.md)。

## 2. 架构总览

```
外部切片器 (SPARQL CONSTRUCT + JSON-LD Framing)
        │
        ▼
  临时图文件 (.jsonld / .json / .md)
        │
        ▼
┌─ MetaSkill DAG ─────────────────────────────────────────────────────┐
│                                                                      │
│  Step 1: load_graph           kind: tool_call                       │
│           tool: load_temporary_graph                                 │
│           读取临时图 → outputs["load_graph"]                          │
│                                                                      │
│  Step 2: reason               kind: llm_chat                        │
│           depends_on: [load_graph]                                   │
│           system_prompt: {{ outputs.load_graph }}                    │
│           LLM 推理产出 ActionProposal JSON                            │
│           output_contract 校验 → outputs["reason"]                   │
│                                                                      │
│  Step 3: execute              kind: tool_call                       │
│           depends_on: [reason]                                       │
│           tool: action_execute                                       │
│           proposal: {{ outputs.reason }}                             │
│                                                                      │
└──────────────────────────────────────────────────────────────────────┘
        │
        ▼
┌─ Harness Action 执行层 ──────────────────────────────────────────────┐
│                                                                      │
│  ActionExecuteTool                                                   │
│    ├─ ActionProposalBuilder.Normalize()  → 归一化为强类型提案        │
│    ├─ ActionPolicyEngine.Evaluate()      → 风险判级 + 决策          │
│    │    ├─ low       → proceed_execute    → 自动执行                  │
│    │    ├─ medium    → require_approval   → 审批后执行                │
│    │    ├─ high/critical → proposal_only  → 仅提案，不执行            │
│    │    └─ unknown   → policy_denied      → 拒绝                     │
│    └─ ActionAdapter.ExecuteAsync()        → preCheck/execution/rollback │
│                                                                      │
└──────────────────────────────────────────────────────────────────────┘
        │
        ▼
┌─ HTTP Connector 层 ──────────────────────────────────────────────────┐
│                                                                      │
│  HttpActionAdapterConnector                                          │
│    ActionCall { Call: "crm.updateCustomerTier", Args: {...} }        │
│      ↓                                                               │
│    POST https://crm.example.com/api/v1/updateCustomerTier            │
│    Authorization: Bearer <token-from-env>                            │
│      ↓                                                               │
│    2xx → Succeeded │ 4xx/5xx → connector_error │ timeout → unavailable │
│                                                                      │
└──────────────────────────────────────────────────────────────────────┘
        │
        ▼
┌─ 治理落证 ───────────────────────────────────────────────────────────┐
│                                                                      │
│  governanceMapping:                                                  │
│    sessionMetaRunRecord → DAG 步骤级审计                              │
│    harnessContractId    → 行动意图与约束                               │
│    pevId                → 决策与审批状态                               │
│    evidenceBundleId     → 执行与补偿细粒度证据                          │
│                                                                      │
└──────────────────────────────────────────────────────────────────────┘
```

## 3. 数据流详解

### 3.1 临时图加载

MetaSkill DAG 的 `tool_call` 步骤调用 `load_temporary_graph` 工具。

**工具定义：** [TemporaryGraphTool.cs](src/OpenClaw.Agent/Tools/TemporaryGraphTool.cs)

```yaml
# DAG 步骤定义
- id: load_graph
  kind: tool_call
  tool: load_temporary_graph
  with:
    path: "./tmp/quality-slice.jsonld"
    format: "jsonld"
    max_chars: 120000
```

**支持的输入格式：**

| 格式 | 说明 |
|------|------|
| `json` / `jsonld` | 直接读取 JSON/JSON-LD 文件 |
| `markdown` | 从 Markdown 中提取 fenced code block，按 `code_block_language` 匹配 |
| `auto` | 根据文件扩展名自动推断 |

**返回值结构：**

```json
{
  "status": "ok",
  "source": "./tmp/quality-slice.jsonld",
  "input_format": "jsonld",
  "payload_format": "jsonld",
  "payload_length": 2847,
  "truncated": false,
  "payload_text": "{\"@context\":...}",
  "payload_json": "{\"@context\":...}"
}
```

执行后，结果存入 `outputs["load_graph"]`，供下游步骤通过 Jinja2 模板引用。

### 3.2 LLM 推理

第二步 `llm_chat` 消费上一步的图数据，进行推理并产出 ActionProposal。

**模板插值机制：** [MetaTemplateRenderer.cs](src/OpenClaw.Core/Skills/Meta/MetaTemplateRenderer.cs)

```yaml
# DAG 步骤定义
- id: reason
  kind: llm_chat
  depends_on: [load_graph]
  with:
    system_prompt: |
      你是工业质量分析助手。输入为临时实例图 JSON-LD，
      请依据图内信息给出根因候选与下一步行动，输出为 ActionProposal JSON。
    input: |
      临时图载荷：
      {{ outputs.load_graph }}
    output_contract:
      format: json
      required_properties: [actionName, target, execution, idempotencyKey]
```

`{{ outputs.load_graph }}` 由 `MetaTemplateRenderer` 通过 Jinja2.NET 渲染。渲染上下文包含：

- `input` — 用户原始输入
- `outputs` — 所有已完成步骤的输出（以 stepId 为 key 的字典）
- `inputs` — 外部传入的输入参数字典
- `steps` — 步骤配置

LLM 返回的 JSON 经过 `output_contract` 校验（至少包含 `actionName`、`target`、`execution`、
`idempotencyKey`），然后存入 `outputs["reason"]`。

### 3.3 Harness Action 执行

第三步 `tool_call` 调用 `action_execute` 工具，将 LLM 产出的提案送入 Harness 执行链路。

```yaml
- id: execute
  kind: tool_call
  depends_on: [reason]
  tool: action_execute
  with:
    proposal: "{{ outputs.reason }}"
    decision: proceed
```

**`action_execute` 工具内部流程：** [ActionExecuteTool.cs](src/OpenClaw.Agent/Tools/ActionExecuteTool.cs)

#### 3.3.1 提案归一化

```
ActionProposalBuilder.Normalize(rawLLMOutput) → ActionProposalNormalizationResult
  ├─ JSON 反序列化为 ActionProposal
  ├─ 数据库直写拦截（target.system 含 db/database/sql → policy_denied）
  ├─ execution 步骤至少一个
  └─ 返回 Strongly-typed ActionProposal
```

#### 3.3.2 策略判级

```
ActionPolicyEngine.Evaluate(proposal) → ActionPolicyDecision
  ├─ 已知系统白名单：crm, salesforce, hubspot, zendesk, stripe, slack, notion
  ├─ 未知系统 → policy_denied (high risk)
  ├─ metadata.policyDecision 覆盖 → require_approval | proposal_only
  └─ 默认 → proceed_execute (low risk)
```

#### 3.3.3 决策矩阵

| 风险级别 | Policy 决策 | tool 返回状态 | ActionAdapter 是否调用 | 说明 |
|---------|------------|--------------|---------------------|------|
| low | `proceed_execute` | `execution_completed` | ✅ 是 | 自动执行 preCheck→execution |
| medium | `require_approval` | `pending_approval`（首次）/ `execution_completed`（审批后） | ✅ 审批后调用 | 首次返回待审批，审批 payload 二次调用后执行 |
| high | `proposal_only` | `proposal_only` | ❌ 否 | 仅提案，不执行 |
| critical | `proposal_only` | `proposal_only` | ❌ 否 | 强约束仅提案 |
| unknown | `policy_denied` | `failed` | ❌ 否 | 未知连接器拒绝 |

#### 3.3.4 Adapter 执行语义

当决策为 `proceed_execute` 且 `ActionAdapter` 已注入时：

```
ActionAdapter.ExecuteAsync(proposal)
  ├─ 1. 幂等检查：TryRegister(idempotencyKey)
  │      已存在 → idempotency_conflict
  ├─ 2. preCheck: 遍历 proposal.PreChecks
  │      IActionAdapterConnector.InvokeAsync(preCheck)
  │      任一失败 → 终止（不触发 rollback）
  ├─ 3. execution: 遍历 proposal.Execution
  │      IActionAdapterConnector.InvokeAsync(step)
  │      任一失败 → 触发 rollback 链
  └─ 4. rollback: 遍历 proposal.Rollback（仅 execution 失败时）
         IActionAdapterConnector.InvokeAsync(rollbackStep)
         全部成功 → rolled_back
         任一失败 → rollback_failed（升级人工处理）
```

**实现：** [ActionAdapter.cs](src/OpenClaw.Agent/Actions/ActionAdapter.cs)

### 3.4 HTTP Connector 回写

`HttpActionAdapterConnector` 将 `ActionCall` 映射为 HTTP POST 请求。

**实现：** [HttpActionAdapterConnector.cs](src/OpenClaw.Agent/Actions/HttpActionAdapterConnector.cs)

#### 映射规则

```
ActionCall.Call  →  {system}.{operation}  例: "crm.updateCustomerTier"
ActionCall.Args  →  JSON Request Body      例: {"customerId": "C123", "tier": "B"}
ConnectorConfig  →  BaseUrl + Auth + Timeout
```

**解析流程：**

```
1. 从 Call 提取 system（"."之前）和 operation（"."之后）
2. 从 ActionAdapterConfig.Connectors 查找 system 对应的 ConnectorDefinition
3. 验证 operation 在 AllowedCalls 白名单中
4. 构造 URL: {BaseUrl}/{operation}
5. 应用认证: Bearer Token / ApiKey Header / None
6. 发送 POST 请求，Body = Args 的 JSON
7. 响应映射:
   2xx → ActionAdapterStepResult.Succeeded()
   4xx/5xx → ActionAdapterStepResult.Failure("connector_error")
   timeout → ActionAdapterStepResult.Failure("connector_unavailable")
```

#### 安全防护

| 防护层 | 位置 | 机制 |
|-------|------|------|
| 第一层 | `ActionProposalBuilder` | 拦截 `target.system` = db/database/sql，拦截含 SQL 写入关键词的 execution.call |
| 第二层 | `ActionPolicyEngine` | 未知 Connector 系统 → `policy_denied` |
| 第三层 | `HttpActionAdapterConnector` | `AllowedCalls` 白名单校验，含 sql/db/database 关键词的 call 直接拒绝 |
| 第四层 | `ConnectorActionContractValidator` | `require_approval` 时审批字段完整性校验，UTC ISO-8601 格式校验 |

### 3.5 治理落证

每次 `action_execute` 调用都会产出治理映射，关联四个审计实体：

**实现：** [ActionExecuteTool.cs:160-170](src/OpenClaw.Agent/Tools/ActionExecuteTool.cs#L160-L170)

```json
{
  "governanceMapping": {
    "sessionMetaRunRecord": "session_meta_run_record_pending",
    "harnessContractId": "hctr_<idempotencyKey>",
    "pevId": "pev_<idempotencyKey>",
    "evidenceBundleId": "evb_<idempotencyKey>"
  }
}
```

当 adapter 执行时，额外输出：

```json
{
  "status": "execution_completed",
  "rollbackTriggered": false,
  "statusHistory": ["succeeded"],
  "failureCode": null
}
```

## 4. 配置

### 4.1 启用 ActionAdapter

配置路径：`Harness:ActionAdapter`

```json
{
  "Harness": {
    "ActionAdapter": {
      "Enabled": true,
      "DefaultDecisionMode": "risk-tiered",
      "IdempotencyWindowMinutes": 60,
      "MaxExecutionSteps": 10,
      "MaxRollbackSteps": 5,
      "Connectors": {
        "crm": {
          "BaseUrl": "https://crm.example.com/api/v1",
          "Auth": {
            "Type": "Bearer",
            "TokenEnv": "CRM_API_TOKEN"
          },
          "TimeoutSeconds": 30,
          "AllowedCalls": ["updateCustomerTier", "createCase", "addNote"],
          "RetryCount": 0
        },
        "stripe": {
          "BaseUrl": "https://api.stripe.com/v1",
          "Auth": {
            "Type": "Bearer",
            "TokenEnv": "STRIPE_API_KEY"
          },
          "AllowedCalls": ["refundCharge", "updateSubscription"]
        }
      }
    }
  }
}
```

### 4.2 安全降级

| 场景 | 行为 |
|------|------|
| `Enabled: false` | Adapter 不实例化，全部返回 `execution_started` / `pending_approval`（仅提案） |
| `DefaultDecisionMode: "proposal-only"` | 忽略 risk tier，全部 `proposal_only` |
| 环境变量 Token 未设置 | `InvokeAsync` 返回 `connector_unavailable`，触发 rollback |
| 策略引擎不可用 | 降级为 `proposal_only` |

### 4.3 零配置默认行为

不配置 `Harness:ActionAdapter` 或 `Enabled: false` 时，`action_execute` 完全向后兼容——
策略判级正常运行，治理映射正常产出，但不执行业务 API 调用。所有现有 MetaSkill 行为不受影响。

## 5. DAG 完整示例

```yaml
kind: meta
name: quality-root-cause-assistant
description: |
  消费临时图 JSON-LD，推理根因并给出下一步行动，低风险行动自动执行。
composition:
  steps:
    - id: load_graph
      kind: tool_call
      tool: load_temporary_graph
      with:
        path: "./tmp/quality-slice.jsonld"
        format: "jsonld"
        max_chars: 120000

    - id: reason
      kind: llm_chat
      depends_on: [load_graph]
      with:
        system_prompt: |
          你是工业质量分析助手。输入为临时实例图 JSON-LD。
          1. 分析图中产品批次的缺陷率和关联因素。
          2. 给出根因候选。
          3. 输出下一步行动提案为 ActionProposal JSON。
        input: |
          临时图载荷：
          {{ outputs.load_graph }}
        output_contract:
          format: json
          required_properties: [actionName, target, execution, idempotencyKey]

    - id: execute
      kind: tool_call
      depends_on: [reason]
      tool: action_execute
      tool_allowlist: [action_execute]
      with:
        proposal: "{{ outputs.reason }}"
```

**请求-响应示例：**

```bash
# 推理产出的 ActionProposal（由 reason 步骤的 LLM 输出）：
{
  "actionName": "update_customer_risk_tier",
  "source": {
    "metaSkill": "quality-root-cause-assistant",
    "runId": "run_20260716_001",
    "stepId": "reason"
  },
  "trigger": {
    "condition": "defectRate > 0.02",
    "evidenceRefs": ["ev_batch_001_defect"]
  },
  "target": {
    "system": "crm",
    "operation": "updateCustomerTier"
  },
  "preChecks": [
    {"call": "crm.getCustomer", "args": {"customerId": "C123"}}
  ],
  "execution": [
    {"call": "crm.updateCustomerTier", "args": {"customerId": "C123", "tier": "B", "reason": "quality_risk"}}
  ],
  "rollback": [
    {"call": "crm.updateCustomerTier", "args": {"customerId": "C123", "tier": "A"}}
  ],
  "idempotencyKey": "quality-C123-20260716-001",
  "metadata": {
    "env": "prod",
    "policyDecision": "proceed_execute"
  }
}

# action_execute 返回（adapter 已注入）：
{
  "status": "execution_completed",
  "decision": "proceed_execute",
  "riskLevel": "low",
  "reasonCodes": ["policy_passed"],
  "requiredApprovals": [],
  "constraints": [],
  "governanceMapping": {
    "sessionMetaRunRecord": "session_meta_run_record_pending",
    "harnessContractId": "hctr_quality-C123-20260716-001",
    "pevId": "pev_quality-C123-20260716-001",
    "evidenceBundleId": "evb_quality-C123-20260716-001"
  },
  "rollbackTriggered": false,
  "statusHistory": ["succeeded"]
}
```

此时 CRM 系统已收到：

```
POST https://crm.example.com/api/v1/updateCustomerTier
Authorization: Bearer <CRM_API_TOKEN>
Content-Type: application/json

{"customerId": "C123", "tier": "B", "reason": "quality_risk"}
```

## 6. 关键代码索引

| 组件 | 文件 | 职责 |
|------|------|------|
| 临时图加载工具 | [TemporaryGraphTool.cs](src/OpenClaw.Agent/Tools/TemporaryGraphTool.cs) | 读取 JSON-LD/JSON/Markdown 临时图文件 |
| DAG 步骤执行 | [AgentRuntime.cs:2545](src/OpenClaw.Agent/AgentRuntime.cs#L2545) | `tool_call` 步骤调度与执行 |
| 模板渲染 | [MetaTemplateRenderer.cs](src/OpenClaw.Core/Skills/Meta/MetaTemplateRenderer.cs) | Jinja2 `{{ outputs.X }}` 插值 |
| Action 执行工具 | [ActionExecuteTool.cs](src/OpenClaw.Agent/Tools/ActionExecuteTool.cs) | proposal 归一化 + 策略判级 + adapter 执行 |
| 提案构建器 | [ActionProposalBuilder.cs](src/OpenClaw.Agent/Actions/ActionProposalBuilder.cs) | LLM 输出 → 强类型 ActionProposal |
| 策略引擎 | [ActionPolicyEngine.cs](src/OpenClaw.Agent/Actions/ActionPolicyEngine.cs) | Connector 白名单 + 风险判级 |
| 执行适配器 | [ActionAdapter.cs](src/OpenClaw.Agent/Actions/ActionAdapter.cs) | preCheck/execution/rollback/幂等 |
| HTTP 连接器 | [HttpActionAdapterConnector.cs](src/OpenClaw.Agent/Actions/HttpActionAdapterConnector.cs) | ActionCall → HTTP POST 映射 |
| Contract 校验 | [ConnectorActionContractModels.cs](src/OpenClaw.Core/Models/ConnectorActionContractModels.cs) | 审批字段校验、决策语义 |
| 集成 API 外观 | [IntegrationApiFacade.cs](src/OpenClaw.Gateway/Composition/IntegrationApiFacade.cs) | CLI/MCP/HTTP 统一入口 |
| 配置模型 | [GatewayConfig.cs](src/OpenClaw.Core/Models/GatewayConfig.cs) (ActionAdapterConfig) | Connector 配置、认证、白名单 |
| DI 注册 | [CoreServicesExtensions.cs](src/OpenClaw.Gateway/Composition/CoreServicesExtensions.cs) | Adapter 条件注册链 |

## 7. 测试覆盖

| 测试文件 | 测试数 | 覆盖内容 |
|---------|--------|---------|
| `TemporaryGraphToolTests.cs` | 2 | 临时图加载（JSON + Markdown） |
| `ActionProposalBuilderTests.cs` | 3 | 归一化 + DB 写拦截 |
| `ActionPolicyEngineTests.cs` | 5 | 完整决策矩阵 |
| `ActionExecuteToolTests.cs` | 15 | 决策路由 + adapter 注入 + 向后兼容 |
| `ActionAdapterTests.cs` | 5 | preCheck/execution/rollback/幂等 |
| `HttpActionAdapterConnectorTests.cs` | 6 | HTTP 映射 + 白名单 + 认证 + 超时 |
| `ConnectorActionContractTests.cs` | 2 | Contract 校验 + Schema 导出 |
| `ConnectorCommandsTests.cs` | 多条 | CLI `connector execute` |
| `GatewayAdminEndpointTests.cs` | 多条 | MCP 工具 + Integration 入口 |
| `FullPipelineE2ETests.cs` | 3 | 全链路：load_graph → execute → mock HTTP |
| 全量回归 | 2460 | 零失败，零回归 |

## 8. 相关文档

- [MetaSkill 功能概览](meta-skills.md)
- [MetaSkill 用户指南](meta-skill-user-guide.md)
- [MetaSkill 编排架构](meta-skill-orchestration.md)
- [Harness Action 策略网关适配器设计](../superpowers/specs/2026-07-15-harness-action-policy-gated-adapter-design.md)
- [ActionAdapter HTTP Connector 桥接设计](../superpowers/specs/2026-07-15-action-adapter-http-connector-bridge-design.md)
- [Harness Action Adapter 桥接实现计划](../superpowers/plans/2026-07-15-harness-action-adapter-bridge-implementation.md)
- [Connector CLI MCP Contract 实现计划](../superpowers/plans/2026-07-15-connector-cli-mcp-contract-implementation.md)
- [ActionAdapter HTTP Connector 桥接实现计划](../superpowers/plans/2026-07-15-action-adapter-http-connector-bridge-implementation.md)

---

[站点地图](../SITE_MAP.md)
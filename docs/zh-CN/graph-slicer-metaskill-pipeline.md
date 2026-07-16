# 图切片器与 MetaSkill 推理回写全链路技术文档

- 文档日期：2026-07-16
- 适用范围：OpenClaw.NET
- 文档语言：中文
- 依赖：dotNetRDF 3.5.2、System.Text.Json、Jinja2.NET

## 1. 概述

工业知识图谱场景中，从原始 RDF 数据到可执行的业务操作通常需要四步：

1. **切片** —— 从知识图谱中提取与当前问题相关的子图
2. **推理** —— LLM 基于子图进行根因分析、异常检测、决策建议
3. **提案** —— 将推理结果结构化为可治理的 ActionProposal
4. **执行** —— 策略引擎判级后通过 HTTP Connector 回写业务系统

本文档将**图切片器（Graph Slicer）**和 **MetaSkill Harness Action 管线**串联为一条完整的技术链路，
覆盖从 SPARQL CONSTRUCT 查询到业务 API 回写的全流程。

---

## 第一部分：图切片器（Graph Slicer）

### 1.1 定位

图切片器是一个独立的 .NET 类库（`OpenClaw.GraphSlicer`），通过 CLI 命令
`openclaw graph slice` 暴露。它的职责是：

- 从多种数据源执行 SPARQL CONSTRUCT 查询
- 对结果应用 JSON-LD Framing 规范化
- 输出 MetaSkill DAG 可直接消费的 `.jsonld` 文件

切片器与 MetaSkill 管线完全解耦，通过文件系统交换数据。

### 1.2 架构

```
graph-slice.json（配置文件）
        │
        ▼
┌─ Graph Slicer Pipeline ─────────────────────────────────────────────┐
│                                                                      │
│  ISparqlSource                                                       │
│    ├─ RemoteEndpointSource（SPARQL 端点 / Ontop 虚拟端点）          │
│    └─ LocalFilesSource（本地 .ttl/.rdf/.jsonld/.nt 文件）           │
│                                                                      │
│  GraphSlicerEngine                                                   │
│    1. foreach source: ExecuteConstructAsync(query) → IGraph          │
│    2. Merge all graphs into one                                      │
│    3. JsonLdWriter.Save() → JSON-LD string                          │
│    4. (可选) ApplySimpleFrame() → JSON-LD Framing                   │
│    5. Write to output file                                           │
│                                                                      │
└──────────────────────────────────────────────────────────────────────┘
        │
        ▼
  ./tmp/quality-slice.jsonld
```

### 1.3 三种数据源

| 数据源 | 配置 Kind | dotNetRDF API | 认证 |
|--------|----------|---------------|------|
| 远程 SPARQL 端点（Fuseki/Stardog/GraphDB） | `remote-endpoint` | HTTP POST → `NTriplesParser` | Basic Auth（环境变量） |
| 本地 RDF 文件 | `local-files` | `FileLoader` + `LeviathanQueryProcessor` | N/A |
| 关系数据库 + Ontop/R2RML | `remote-endpoint` | 同上（Ontop 暴露虚拟 SPARQL 端点） | 视 Ontop 配置 |

关键设计决策：类型 C（关系数据库）并不需要独立适配器。Ontop/virtuoso 等工具将关系数据库
映射为标准的 SPARQL 端点，切片器通过 `RemoteEndpointSource` 即可连接。

### 1.4 配置文件

```json
{
  "Profiles": {
    "production": {
      "Sources": [
        {
          "Kind": "remote-endpoint",
          "Endpoint": "https://fuseki.example.com/production/query",
          "Auth": {
            "Type": "basic",
            "UsernameEnv": "FUSEKI_USER",
            "PasswordEnv": "FUSEKI_PASS"
          },
          "TimeoutSeconds": 60
        },
        {
          "Kind": "local-files",
          "Paths": ["./data/reference-taxonomy.ttl"]
        },
        {
          "Kind": "remote-endpoint",
          "Endpoint": "http://localhost:8080/sparql",
          "Auth": { "Type": "none" }
        }
      ],
      "Construct": "PREFIX ex: <http://openclaw.net/ontology/industrial#>\nCONSTRUCT {\n  ?batch ex:defectRate ?rate ;\n         ex:hasMaterial ?material .\n  ?material ex:supplierQuality ?sq .\n}\nWHERE {\n  ?batch a ex:ProductBatch ;\n         ex:hasMeasurement ?m .\n  ?m ex:value ?rate ; ex:type \"defect_rate\" .\n  OPTIONAL {\n    ?batch ex:usesMaterial ?material .\n    ?material ex:qualityScore ?sq .\n  }\n}",
      "FrameJson": "{\"@context\": \"http://openclaw.net/ontology/industrial.jsonld\", \"@type\": \"ex:QualitySlice\"}",
      "Output": {
        "Path": "./tmp/quality-slice.jsonld",
        "MaxTriples": 50000,
        "Compaction": true
      }
    }
  }
}
```

### 1.5 CLI 命令

```bash
# 执行切片
openclaw graph slice --profile production

# 覆盖输出路径
openclaw graph slice --profile production --output ./today.jsonld

# 干跑（验证配置和查询语法）
openclaw graph slice --profile production --dry-run

# 查看 profile 配置
openclaw graph slice --profile production --info
```

### 1.6 项目结构

```
src/OpenClaw.GraphSlicer/           ← 独立类库（dotNetRDF 3.5.2 隔离在此）
  ISparqlSource.cs                  ← 数据源接口
  RemoteEndpointSource.cs           ← HTTP POST → SPARQL 端点
  LocalFilesSource.cs               ← FileLoader + LeviathanEngine
  GraphSlicerEngine.cs              ← 编排引擎 + JSON-LD Framing

src/OpenClaw.Cli/
  GraphSliceCommands.cs             ← CLI 命令入口

src/OpenClaw.Core/Models/
  GraphSliceProfile.cs              ← 配置模型
```

**依赖链：**

```
OpenClaw.Cli → OpenClaw.GraphSlicer → dotNetRDF 3.5.2
                                    → OpenClaw.Core
```

dotNetRDF 依赖完全闭环在 `OpenClaw.GraphSlicer` 内，Gateway 零接触。

### 1.7 错误处理与限制

| 场景 | 行为 |
|------|------|
| 远程端点超时 | `SliceResult(Success=false, ErrorMessage="...")` |
| 本地文件不存在 | `SliceResult(Success=false, ErrorMessage="...")` |
| CONSTRUCT 结果为空 | `SliceResult(Success=false, ErrorMessage="CONSTRUCT produced an empty graph.")` |
| Triple 数超限 | 继续输出，`Truncated=true`（不阻断，因为截断由 `MaxTriples` 配置决定） |
| 所有源成功 | 写入 `.jsonld` 文件 |

`MaxTriples` 默认为 50000。超大图切片建议在 SPARQL 查询中添加 `LIMIT`。

---

## 第二部分：MetaSkill Harness Action 回写管线

### 2.1 架构总览

```
外部切片器（openclaw graph slice）
        │
        ▼
  临时图文件（.jsonld）
        │
        ▼
┌─ MetaSkill DAG ─────────────────────────────────────────────────────┐
│                                                                      │
│  Step 1: load_graph    tool: load_temporary_graph                   │
│          读取临时图 → outputs["load_graph"]                          │
│                                                                      │
│  Step 2: reason        kind: llm_chat, depends_on: [load_graph]     │
│          {{ outputs.load_graph }} → LLM 推理 → ActionProposal JSON   │
│                                                                      │
│  Step 3: execute       tool: action_execute, depends_on: [reason]   │
│          {{ outputs.reason }} → ActionExecuteTool                    │
│                                                                      │
└──────────────────────────────────────────────────────────────────────┘
        │
        ▼
┌─ Harness Action 执行层 ──────────────────────────────────────────────┐
│                                                                      │
│  ActionExecuteTool                                                   │
│    ├─ ActionProposalBuilder.Normalize() → 强类型 ActionProposal     │
│    ├─ ActionPolicyEngine.Evaluate()   → 风险判级                    │
│    └─ ActionAdapter.ExecuteAsync()    → preCheck/execution/rollback │
│                                                                      │
└──────────────────────────────────────────────────────────────────────┘
        │
        ▼
┌─ HTTP Connector ─────────────────────────────────────────────────────┐
│                                                                      │
│  HttpActionAdapterConnector                                          │
│    ActionCall { Call: "crm.updateCustomerTier", Args: {...} }        │
│      ↓                                                               │
│    POST https://crm.example.com/api/v1/updateCustomerTier            │
│    Authorization: Bearer <token-from-env>                            │
│                                                                      │
└──────────────────────────────────────────────────────────────────────┘
        │
        ▼
┌─ 治理落证 ───────────────────────────────────────────────────────────┐
│  governanceMapping:                                                  │
│    sessionMetaRunRecord / harnessContractId / pevId / evidenceBundleId │
└──────────────────────────────────────────────────────────────────────┘
```

### 2.2 数据流详解

#### Step 1: 加载临时图

MetaSkill DAG 的 `tool_call` 步骤调用 `load_temporary_graph` 工具。该工具
（`TemporaryGraphTool`）支持从 JSON/JSON-LD 文件或 Markdown 文档中的 fenced code block
读取临时图数据，返回 `payload_text` 和 `payload_json`。

```yaml
- id: load_graph
  kind: tool_call
  tool: load_temporary_graph
  with:
    path: "./tmp/quality-slice.jsonld"
    format: "jsonld"
    max_chars: 120000
```

执行后，结果存入 `outputs["load_graph"]`。

#### Step 2: LLM 推理

`llm_chat` 步骤通过 Jinja2 模板（`MetaTemplateRenderer`）引用上一步的输出：

```yaml
- id: reason
  kind: llm_chat
  depends_on: [load_graph]
  with:
    system_prompt: |
      你是工业质量分析助手。输入为临时实例图 JSON-LD。
      1. 分析缺陷率和关联因素。
      2. 给出根因候选。
      3. 输出下一步行动提案 ActionProposal JSON。
    input: |
      临时图载荷：
      {{ outputs.load_graph }}
    output_contract:
      format: json
      required_properties: [actionName, target, execution, idempotencyKey]
```

`{{ outputs.load_graph }}` 由 `MetaTemplateRenderer` 使用 Jinja2.NET 渲染。渲染上下文
包含 `outputs`（所有已完成步骤的输出字典）、`input`、`inputs`、`steps`。

LLM 产出 ActionProposal JSON，经 `output_contract` 校验后存入 `outputs["reason"]`。

#### Step 3: Harness Action 执行

`action_execute` 工具接收 LLM 产出的提案，执行完整的 Harness 管线：

```
ActionProposalBuilder.Normalize()
  ├─ JSON → 强类型 ActionProposal
  ├─ 数据库直写拦截（target.system = db/database/sql → policy_denied）
  └─ execution 至少一个步骤

ActionPolicyEngine.Evaluate()
  ├─ 已知系统白名单：crm, salesforce, hubspot, zendesk, stripe, slack, notion
  ├─ low → proceed_execute（自动执行）
  ├─ medium → require_approval（审批后执行）
  ├─ high/critical → proposal_only（仅提案）
  └─ unknown → policy_denied（拒绝）

ActionAdapter.ExecuteAsync()
  ├─ 幂等检查（idempotencyKey 窗口期内至多成功一次）
  ├─ preCheck → execution → rollback 顺序执行
  └─ 执行仅通过 IActionAdapterConnector（禁止直连数据库）

HttpActionAdapterConnector.InvokeAsync()
  ├─ ActionCall.Call → {system}.{operation}
  ├─ AllowedCalls 白名单校验
  ├─ POST {BaseUrl}/{operation} + Bearer/ApiKey 认证
  └─ 2xx → Succeeded / 4xx/5xx → connector_error / timeout → connector_unavailable
```

### 2.3 决策矩阵

| 风险级别 | 决策 | 行为 | 需审批 |
|---------|------|------|--------|
| low | `proceed_execute` | 自动执行 preCheck→execution→rollback | 否 |
| medium | `require_approval` | 审批后执行（带 `approver/decisionAt/decisionReason/ticketRef` 的二次调用） | 是 |
| high/critical | `proposal_only` | 仅提案，不执行 | 是（仅用于人工升级） |
| unknown connector | `policy_denied` | 拒绝执行 | — |

### 2.4 治理落证

每次 `action_execute` 调用产生四个审计实体的映射：

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

当 adapter 实际执行时，额外输出 `statusHistory`、`rollbackTriggered`、`failureCode`。

### 2.5 配置接入点

```json
{
  "Harness": {
    "ActionAdapter": {
      "Enabled": true,
      "DefaultDecisionMode": "risk-tiered",
      "IdempotencyWindowMinutes": 60,
      "Connectors": {
        "crm": {
          "BaseUrl": "https://crm.example.com/api/v1",
          "Auth": { "Type": "Bearer", "TokenEnv": "CRM_API_TOKEN" },
          "AllowedCalls": ["updateCustomerTier", "createCase"]
        }
      }
    }
  }
}
```

`Enabled: false` 时不注入 `ActionAdapter`，`action_execute` 仅产出提案和治理映射，
不做实际 API 调用。所有现有 MetaSkill 行为不变。

---

## 第三部分：完整示例

### 3.1 图切片配置

```json
{
  "Profiles": {
    "quality-daily": {
      "Sources": [
        {
          "Kind": "remote-endpoint",
          "Endpoint": "https://fuseki.example.com/production/query",
          "Auth": { "Type": "basic", "UsernameEnv": "FUSEKI_USER", "PasswordEnv": "FUSEKI_PASS" }
        },
        {
          "Kind": "local-files",
          "Paths": ["./data/quality-taxonomy.ttl"]
        }
      ],
      "Construct": "PREFIX ex: <http://openclaw.net/ontology/industrial#>\nCONSTRUCT {\n  ?batch ex:defectRate ?rate ; ex:hasMaterial ?material .\n  ?material ex:supplierQuality ?sq ; ex:name ?materialName .\n}\nWHERE {\n  ?batch a ex:ProductBatch ; ex:hasMeasurement ?m .\n  ?m ex:value ?rate ; ex:type \"defect_rate\" .\n  OPTIONAL {\n    ?batch ex:usesMaterial ?material .\n    ?material ex:qualityScore ?sq ; rdfs:label ?materialName .\n  }\n}",
      "FrameJson": "{\"@context\": \"http://openclaw.net/ontology/industrial.jsonld\", \"@type\": \"ex:QualitySlice\"}",
      "Output": { "Path": "./tmp/quality-slice.jsonld", "MaxTriples": 50000 }
    }
  }
}
```

### 3.2 MetaSkill DAG 定义

```yaml
kind: meta
name: quality-root-cause-assistant
description: 消费质量切片图，推理根因，低风险行动自动回写 CRM

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
          你是工业质量分析助手。
          1. 分析产品批次的缺陷率和原材料供应商质量。
          2. 如果缺陷率 > 0.02 且某一原材料供应商质量分 < 70，建议升级该客户的风险等级。
          3. 输出 ActionProposal JSON。
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

### 3.3 执行流程

```bash
# 1. 外部切片器生成临时图
openclaw graph slice --profile quality-daily

# 输出：./tmp/quality-slice.jsonld（2.8KB, 47 triples）

# 2. 用户发起 MetaSkill（或 Automation 定时触发）
curl -X POST http://localhost:18789/api/integration/messages \
  -H "Content-Type: application/json" \
  -d '{"text": "分析今天的质量切片图并给出行动建议", "sessionId": "quality-daily"}'

# 3. MetaSkill DAG 自动执行三步：

# Step 1: load_temporary_graph 读取 quality-slice.jsonld
#   → outputs["load_graph"] = { "status": "ok", "payload_json": "..." }

# Step 2: llm_chat LLM 分析
#   → ActionProposal JSON:
{
  "actionName": "update_customer_risk_tier",
  "source": { "metaSkill": "quality-root-cause-assistant", "runId": "run_001", "stepId": "reason" },
  "trigger": { "condition": "defectRate > 0.02 AND supplierQuality < 70", "evidenceRefs": ["ev_001"] },
  "target": { "system": "crm", "operation": "updateCustomerTier" },
  "preChecks": [{"call": "crm.getCustomer", "args": {"customerId": "C123"}}],
  "execution": [{"call": "crm.updateCustomerTier", "args": {"customerId": "C123", "tier": "B", "reason": "quality_risk"}}],
  "rollback": [{"call": "crm.updateCustomerTier", "args": {"customerId": "C123", "tier": "A"}}],
  "idempotencyKey": "quality-C123-20260716-001",
  "metadata": { "env": "prod" }
}

# Step 3: action_execute → ActionPolicyEngine 判级 → low → proceed_execute
#   → ActionAdapter.ExecuteAsync() → preCheck: crm.getCustomer → OK
#   → execution: POST https://crm.example.com/api/v1/updateCustomerTier {"tier":"B"}
#   → 治理落证: governanceMapping 全部字段写入

# 最终输出：
{
  "status": "execution_completed",
  "decision": "proceed_execute",
  "riskLevel": "low",
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

### 3.4 Automation 集成

通过 Gateway 现有的 Automation 基础设施实现定时切片：

```yaml
# Gateway Automation 模板
- id: daily-quality-pipeline
  kind: shell
  cron: "0 6 * * *"
  command: |
    openclaw graph slice --profile quality-daily &&
    curl -X POST http://localhost:18789/api/integration/messages \
      -H "Content-Type: application/json" \
      -d '{"text": "分析今天的 batch 数据", "sessionId": "quality-auto"}'
```

---

## 第四部分：关键代码索引

### 图切片器

| 组件 | 文件 | 职责 |
|------|------|------|
| 项目定义 | [OpenClaw.GraphSlicer.csproj](src/OpenClaw.GraphSlicer/OpenClaw.GraphSlicer.csproj) | 独立类库，dotNetRDF 3.5.2 |
| 数据源接口 | [ISparqlSource.cs](src/OpenClaw.GraphSlicer/ISparqlSource.cs) | `Task<IGraph> ExecuteConstructAsync(string, CancellationToken)` |
| 远程端点适配器 | [RemoteEndpointSource.cs](src/OpenClaw.GraphSlicer/RemoteEndpointSource.cs) | HTTP POST → SPARQL 端点（NTriples 解析） |
| 本地文件适配器 | [LocalFilesSource.cs](src/OpenClaw.GraphSlicer/LocalFilesSource.cs) | FileLoader + LeviathanQueryProcessor |
| 编排引擎 | [GraphSlicerEngine.cs](src/OpenClaw.GraphSlicer/GraphSlicerEngine.cs) | CONSTRUCT→merge→JSON-LD→frame→文件 |
| CLI 命令 | [GraphSliceCommands.cs](src/OpenClaw.Cli/GraphSliceCommands.cs) | `openclaw graph slice` |
| 配置模型 | [GraphSliceProfile.cs](src/OpenClaw.Core/Models/GraphSliceProfile.cs) | Profile/Source/Auth/Output 配置类型 |

### MetaSkill Harness Action 管线

| 组件 | 文件 | 职责 |
|------|------|------|
| 临时图加载 | [TemporaryGraphTool.cs](src/OpenClaw.Agent/Tools/TemporaryGraphTool.cs) | JSON-LD/JSON/Markdown 文件读取 |
| DAG 步骤执行 | [AgentRuntime.cs:2545](src/OpenClaw.Agent/AgentRuntime.cs#L2545) | `tool_call` / `llm_chat` 步骤调度 |
| 模板渲染 | [MetaTemplateRenderer.cs](src/OpenClaw.Core/Skills/Meta/MetaTemplateRenderer.cs) | Jinja2 `{{ outputs.X }}` 插值 |
| Action 执行工具 | [ActionExecuteTool.cs](src/OpenClaw.Agent/Tools/ActionExecuteTool.cs) | proposal→normalize→policy→adapter |
| 提案构建器 | [ActionProposalBuilder.cs](src/OpenClaw.Agent/Actions/ActionProposalBuilder.cs) | LLM 输出→强类型 ActionProposal |
| 策略引擎 | [ActionPolicyEngine.cs](src/OpenClaw.Agent/Actions/ActionPolicyEngine.cs) | Connector 白名单 + 风险判级 |
| 执行适配器 | [ActionAdapter.cs](src/OpenClaw.Agent/Actions/ActionAdapter.cs) | preCheck/execution/rollback/幂等 |
| HTTP 连接器 | [HttpActionAdapterConnector.cs](src/OpenClaw.Agent/Actions/HttpActionAdapterConnector.cs) | ActionCall→HTTP POST+认证 |
| DI 注册 | [CoreServicesExtensions.cs](src/OpenClaw.Gateway/Composition/CoreServicesExtensions.cs) | Enabled→DI链 |
| 集成 API | [IntegrationApiFacade.cs](src/OpenClaw.Gateway/Composition/IntegrationApiFacade.cs) | CLI/MCP/HTTP 统一入口 |

---

## 第五部分：设计文档索引

| 文档 | 内容 |
|------|------|
| [图切片器设计说明](../superpowers/specs/2026-07-16-graph-slicer-sparql-construct-jsonld-design.md) | SPARQL CONSTRUCT + JSON-LD Framing 外部切片器设计 |
| [图切片器实现计划](../superpowers/plans/2026-07-16-graph-slicer-implementation.md) | 6 任务实现计划 |
| [Harness Action 策略网关适配器设计](../superpowers/specs/2026-07-15-harness-action-policy-gated-adapter-design.md) | 策略判级 + 审批门禁 + adapter 设计 |
| [ActionAdapter HTTP Connector 桥接设计](../superpowers/specs/2026-07-15-action-adapter-http-connector-bridge-design.md) | ActionExecuteTool → ActionAdapter → HTTP 设计 |
| [MetaSkill Harness Action 回写全链路指南](meta-skill-harness-action-writeback-pipeline.md) | MetaSkill DAG 完整管线文档（中文） |
| [MetaSkill Feature Overview](meta-skills.md) | MetaSkill DAG 能力总览 |

---

[站点地图](../SITE_MAP.md)
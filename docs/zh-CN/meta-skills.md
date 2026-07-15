# MetaSkill 功能概览 (zh-CN)

MetaSkill 将重复的多步工作封装为可复用、可审查的 DAG 工作流。当一个请求需要
超过一个普通 Skill、工具、检查点或最终综合步骤时，使用 MetaSkill。

完整用户指南：[`meta-skill-user-guide.md`](meta-skill-user-guide.md)。
编写指南：[`../authoring/meta-skills.md`](../authoring/meta-skills.md)。
编排架构深入：[`meta-skill-orchestration.md`](meta-skill-orchestration.md)（[English](../meta-skill-orchestration.md)）。

## Skill vs MetaSkill

| 能力 | 适用场景 |
| --- | --- |
| **Skill** (`kind: standard`) | 一个聚焦任务 — 指令作为 system prompt 注入。1 步，无 DAG，无暂停，无降级。 |
| **MetaSkill** (`kind: meta`) | 3-12 步可复用 DAG，带 `depends_on`、`on_failure`、`user_input` 暂停点，完整审计轨迹。 |

举例："总结这份文档"是 Skill 形态。"将这份合同、报价和邮件转化为签/拒/谈决策建议，
包含风险和后续行动"是 MetaSkill 形态。

## 内置 MetaSkill

OpenClaw.NET Gateway 内置了精选的 MetaSkill 模板：

| MetaSkill | 用途 |
| --- | --- |
| `meta-skill-creator` | 将重复的多 Skill 协作模式转化为新 MetaSkill 提案。支持 3 种 DAG 模式：`p1_sequential`、`p2_fan_out_merge`、`p3_condition_gated` |
| `history-explorer` | 检查并输出最近会话历史供下游步骤使用 |

内置标准 Skill：

| Skill | 用途 |
| --- | --- |
| `summarize` | 生成结构化摘要，包含关键要点、详细说明和行动项 |
| `daily-news-digest` | 生成每日新闻简报，包含链接、要点和关注事项 |

可通过 `openclaw skills install` 或插件 Skill 目录安装额外的领域专用 MetaSkill。

## 核心能力

### DAG 执行

步骤通过 `depends_on` 声明形成有向无环图。独立步骤并行执行（波次调度）。
DAG 引擎在 `AgentRuntime`（原生）和 `MafAgentRuntime`（Microsoft Agent Framework
适配器）之间共享——二者执行 MetaSkill 的行为一致。运行时强制执行依赖顺序、
波次调度和环路检测。所有等价测试在两个运行时上均通过。

```yaml
composition:
  steps:
    - id: fetch
      kind: skill_exec
      skill: data-fetcher
    - id: analyze
      kind: llm_chat
      depends_on: [fetch]
```

### 失败处理 (`on_failure`)

每个步骤可以声明一个 `on_failure` 替代步骤。当主步骤失败时（超时、工具错误、
校验失败），运行时激活 fallback，并将其输出镜像到主步骤 ID——
下游步骤无感知。

**5 条工程约束**（parse-time + runtime 双重校验）：
1. fallback 目标必须存在
2. 不能自引用
3. fallback 不能有 `on_failure`（禁止链式）
4. 同一 fallback 只能被一个 primary 引用
5. fallback 不能有 `depends_on`

### 暂停与恢复 (`user_input`)

`kind: user_input` 的步骤暂停 DAG 等待人工输入。运行时保存完整 checkpoint
（`pending`/`blocked`/`outputs`/`stepResults`）到 Session，用户输入后恢复。
可配置 `timeout_seconds` + `on_failure` fallback 防止无限等待。

### 审计与恢复

每次执行记录 `SessionMetaRunRecord`，包含每步耗时、失败码和执行证据。运维人员
可通过 CLI 查看、回放预览和审计重建运行记录：

```sh
openclaw skills meta-runs <sid> --run <id> --verbose --json
openclaw skills meta-runs replay <sid> --run <id>
openclaw skills meta-runs reconstruct <sid> --run <id>
```

当 MetaSkill 的 `tool_call` 步骤调用 `action_execute` 时，工具结果会额外包含
`governanceMapping`，用于把动作治理对象映射到可审计实体：

```json
{
  "status": "proposal_only",
  "decision": "proposal_only",
  "governanceMapping": {
    "sessionMetaRunRecord": "session_meta_run_record_pending",
    "harnessContractId": "hctr_xxx",
    "pevId": "pev_xxx",
    "evidenceBundleId": "evb_xxx"
  }
}
```

运行时会在当前会话的 `SessionMetaRunRecord` 上保留这组映射字段，便于后续按会话追溯
Harness Contract、PEV 和 Evidence Bundle。未使用 `action_execute` 的 MetaSkill 路径保持原有行为不变。

### 有界执行

四层超时保护：
1. **每步**：`timeout_seconds` + `CancellationToken`
2. **每步重试**：`retry.max_attempts` + `backoff_ms`
3. **会话合约**：`ContractPolicy.MaxRuntimeSeconds`（网关级）
4. **Agent 循环**：`maxIterations` + 熔断器

## 步骤类型

| Kind | 用途 |
| --- | --- |
| `llm_chat` | 一次有界 LLM 生成，无工具循环 |
| `llm_classify` | 从闭集合返回恰好一个值（路由） |
| `agent` | 通过 LLM 委托到另一个 Skill 的指令 |
| `tool_call` | 直接工具执行，带 `tool_allowlist` |
| `skill_exec` | 作为子进程运行 Skill 的 entrypoint |
| `user_input` | 暂停等待结构化人工输入 |
| `fan_out` | 动态步骤展开：对运行时列表逐项克隆模板并批量并行执行 |

### 动态步骤展开 (`fan_out`)

当子任务数量依赖前一步输出时，`fan_out` 将 Jinja 表达式求值为列表，为每个元素
克隆步骤模板，并在受控的并行批次中执行子步骤。共享逻辑位于 `MetaFanOutExecutor`
——双重运行时之间无代码重复。

```yaml
- id: search_all
  kind: fan_out
  iterable: "{{ outputs.extract | from_json }}"
  fan_out_max_concurrency: 3
  fan_out_merge_mode: json_array
  fan_out_template:
    kind: tool_call
    tool: web_search
    with:
      query: "{{ item }}"
    continue_on_error: true
```

### 运行时工具发现 (`list_tools`)

内置的 `list_tools` 工具返回每个已注册工具的名称/描述/参数模式。在调用未知
工具之前，在 `tool_call` 步骤中使用：

```yaml
- id: discover
  kind: tool_call
  tool: list_tools
  with:
    filter: "web"
```

### DAG 消费临时图 (`load_temporary_graph`)

当你的流程采用“按需实例化”时，可以先用 `tool_call` 步骤加载一次临时图，
再在后续步骤通过 `outputs.<step_id>` 消费该图内容。`load_temporary_graph`
支持三类输入：

- JSON/JSON-LD 文件（例如 `.json`、`.jsonld`）
- Markdown 文档中的 fenced code block（例如 `jsonld` 代码块）
- 通过 `max_chars` 控制返回体积，避免大图直接撑满上下文

```yaml
composition:
  steps:
    - id: load_graph
      kind: tool_call
      tool: load_temporary_graph
      with:
        path: "./tmp/quality-slice.md"
        format: "markdown"
        code_block_language: "jsonld"
        max_chars: 120000

    - id: reason
      kind: llm_chat
      depends_on: [load_graph]
      with:
        system_prompt: |
          你是工业质量分析助手。输入包含临时实例图 JSON/JSON-LD，
          只依据图内信息给出根因候选与下一步行动。
        input: |
          临时图载荷如下：
          {{ outputs.load_graph }}
```

建议：临时图由外部切片器（例如 SPARQL CONSTRUCT + JSON-LD Framing）生成，
MetaSkill 只负责“消费和推理”，不要在 DAG 内做大规模图构建。

## 激活方式

自然语言触发：

```
从我团队本周的 commit 生成周报。
```

显式指定 Skill 名称：

```
使用 meta-skill `weekly-report`。
```

## 配置

网关级 MetaSkill 策略：

```json
{
  "Skills": {
    "MetaSkill": {
      "Enabled": true,
      "AllowedRiskLevels": ["low", "medium"],
      "RequiredCapabilities": []
    }
  }
}
```

按 Skill 覆盖：

```json
{
  "Skills": {
    "Entries": {
      "weekly-report": { "Enabled": true, "MetaPriority": 80 }
    }
  }
}
```

## 提案生命周期

生成的 MetaSkill 先进入提案，审查后才成为已安装 Skill：

```
CREATE (草稿) → LINT → SMOKE → RUNTIME_E2E → PERSIST (提案)
                                                   ↓
                                            ACCEPT / DISMISS
                                                   ↓
                                              已安装 Skill
```

每个生命周期转换记录 `audit` 字段（`actorId`、`changedAtUtc`、`transitionAction`）
和 `provenanceHistory`。

---

[用户指南](meta-skill-user-guide.md) · [编写指南](../authoring/meta-skills.md) · [编排架构](meta-skill-orchestration.md) · [站点地图](../SITE_MAP.md)

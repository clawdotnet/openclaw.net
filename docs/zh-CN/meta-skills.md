# MetaSkill 功能概览 (zh-CN)

MetaSkill 将重复的多步工作封装为可复用、可审查的 DAG 工作流。当一个请求需要
超过一个普通 Skill、工具、检查点或最终综合步骤时，使用 MetaSkill。

完整用户指南：[`meta-skill-user-guide.md`](meta-skill-user-guide.md)。
编写指南：[`../authoring/meta-skills.md`](../authoring/meta-skills.md)。

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

[用户指南](meta-skill-user-guide.md) · [编写指南](../authoring/meta-skills.md) · [站点地图](../SITE_MAP.md)

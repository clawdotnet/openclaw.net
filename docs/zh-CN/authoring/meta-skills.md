# MetaSkill 编写指南 (zh-CN)

本指南面向编写、校验和审查 OpenClaw.NET MetaSkill 的作者和维护者。
用户指南：[`../meta-skill-user-guide.md`](../meta-skill-user-guide.md)。

## 什么是 MetaSkill

一个 MetaSkill 是一个包含以下内容的 `SKILL.md` 文件：

- `kind: meta`
- 一个或多个自然语言 `triggers`
- 一个 `composition:` 块，定义有向无环步骤图

运行时，OpenClaw.NET 的 `AgentRuntime.ExecuteMetaSkillAsync`（使用 Microsoft Agent
Framework 适配器时为 `MafAgentRuntime.ExecuteMetaSkillAsync`）逐步执行声明的
composition——强制执行依赖顺序、模板渲染、meta 策略门禁、工具白名单、
暂停/恢复检查点和失败分支激活。用户的自然语言意图通过 Gateway 的 Skill
匹配层触发工作流。

运维人员可以按 Skill 或全局禁用 MetaSkill 调用：

```json
{
  "Skills": {
    "MetaSkill": { "Enabled": false }
  }
}
```

禁用后，MetaSkill 仍然加载用于目录和历史检查，但不会被激活。

## 何时使用 MetaSkill

当任务可重复且自然分解为 3-12 步 DAG 时使用 MetaSkill：

- 分类用户请求，然后路由到正确的专用 Skill
- 并行运行两个独立分析 Skill，然后合并它们的输出
- 搜索或检查上下文，然后总结为用户可读的答案
- 执行确定性 CLI 支持的 Skill，然后审查或持久化结果
- 暂停等待结构化用户输入后再继续

**不要**在以下场景使用 MetaSkill：

- 一次性指令（使用标准 Skill）
- 应保持对话式的开放式规划
- 需要任意递归的流程
- 超过 12 步的任务（拆分为多个 MetaSkill 或使用 Microsoft Agent Framework / LangGraph 等外部编排器）

一个 MetaSkill 不能调用另一个 MetaSkill（`TryValidateMetaPlan` 拒绝
`kind: meta` 的委托 Skill）。

## 文件位置

```
src/OpenClaw.Gateway/skills/<skill-name>/SKILL.md    # Gateway 内置
~/.openclaw/skills/<skill-name>/SKILL.md             # 本地管理
```

生成的提案审查通过后才安装。接受提案后，OpenClaw.NET 将其提升并刷新 Skill
加载器。

## 打包子 Skill

MetaSkill 可通过 `kind: agent` 或 `kind: skill_exec` 委托标准 Skill。这些被委托的子 Skill
可以与 MetaSkill 一起打包在嵌套子目录中：

```
~/.openclaw/skills/my-meta/
  SKILL.md                        # kind: meta (my-meta)
  subskills/
    fetcher/SKILL.md               # kind: standard (fetcher)
    reporter/SKILL.md              # kind: standard (reporter)
    pdf/SKILL.md                   # kind: standard (pdf)
```

在配置中启用递归扫描，使加载器能发现嵌套的 `SKILL.md` 文件：

```json
{
  "Skills": {
    "Load": {
      "ScanSubdirectories": true
    }
  }
}
```

当 `ScanSubdirectories` 为 `true` 时，`SkillLoader` 使用 `SearchOption.AllDirectories`
替代 `TopDirectoryOnly`。这使得每个嵌套的 `SKILL.md` 文件都可被发现，无需将每个子
Skill 目录单独注册为 `ExtraDirs` 条目。

默认为 `false` 以保持向后兼容。该标志统一应用于 ExtraDirs、Bundled、Managed、Plugin
和 Workspace 技能目录。

## 必需的前置元数据

```yaml
---
name: short-stable-name
kind: meta
description: 一句话告诉模型何时适用此工作流。
triggers:
  - 用户自然输入的短语
meta_priority: 50
always: false
final_text_mode: auto
composition:
  steps: []
---
```

| 字段 | 必需 | 用途 |
| --- | --- | --- |
| `name` | 是 | CLI 和跨 Skill 引用的稳定标识符 |
| `kind` | 是 | 必须为 `meta` |
| `description` | 是 | 面向模型的激活时机描述 |
| `triggers` | 是 | 用于意图匹配的自然语言短语 |
| `meta_priority` | 否 | 多个 MetaSkill 可能匹配时的排序键（默认 50） |
| `always` | 否 | 应为 `false`。MetaSkill 不无条件注入 |
| `final_text_mode` | 否 | 最终答案的派生方式（见下文） |
| `composition.steps` | 是 | 有序 DAG 定义 |

## 步骤类型

### `llm_chat`

一次有界 LLM 生成，无工具循环。最适合输入规范化、紧凑草稿或轻量综合。

```yaml
- id: normalize
  kind: llm_chat
  with:
    system: "提取请求字段。不要提问。"
    task: "{{ input | xml_escape | truncate(1000) }}"
```

### `llm_classify`

从闭集合返回恰好一个值。最适合路由和分诊。

```yaml
- id: classify
  kind: llm_classify
  output_choices: [BUG, FEATURE, QUESTION]
  with:
    text: "{{ input | xml_escape | truncate(512) }}"
```

### `agent`

通过 LLM 委托到另一个 Skill 的指令。面向用户的推理和综合的默认选择。

```yaml
- id: summarize
  kind: agent
  skill: summarize
  with:
    text: "{{ outputs.search | truncate(2000) }}"
```

### `tool_call`

直接工具执行。声明 `tool_allowlist` 并保持参数精简。

```yaml
- id: persist
  kind: tool_call
  tool: memory_save
  tool_allowlist: [memory_save]
  with:
    text: "{{ outputs.summary | truncate(2000) }}"
```

### `skill_exec`

将 Skill 的 entrypoint 作为子进程运行。最适合确定性 CLI 支持的 Skill。

```yaml
- id: render
  kind: skill_exec
  skill: html-to-pdf
  skill_exec_entrypoint: scripts/render.py
  skill_exec_args:
    - "{{ outputs.report | truncate(12000) }}"
  skill_exec_parse_mode: json
```

### `user_input`

暂停等待结构化人工输入，带 `clarify` schema 校验。

```yaml
- id: collect_project
  kind: user_input
  when: "outputs.intake contains 'NEEDS_CLARIFICATION'"
  clarify:
    mode: form
    fields:
      - name: topic
        type: string
        required: true
        min_length: 3
      - name: priority
        type: enum
        options: [low, medium, high]
        default: "medium"
    cancel_words: [cancel, 取消]
    timeout_seconds: 300
    skip_if: "outputs.auto_approve == '1'"
```

支持的字段类型：`string`、`enum`、`integer`、`boolean`。使用 `skip_if` 在上下文
足够时跳过。

## 依赖与并行

没有 `depends_on` 的步骤可以并行执行（波次调度）。有 `depends_on` 的步骤等待
所有命名步骤完成。

```yaml
steps:
  - id: inspect_code
    kind: agent
    skill: code-reviewer

  - id: inspect_tests
    kind: agent
    skill: test-engineer

  - id: merge
    kind: llm_chat
    depends_on: [inspect_code, inspect_tests]
    with:
      task: |
        Code: {{ outputs.inspect_code | truncate(2000) }}
        Tests: {{ outputs.inspect_tests | truncate(2000) }}
```

图必须无环。一个步骤只能依赖同 composition 中声明的步骤 ID。

## 路由

在 `agent` 或 `skill_exec` 步骤上使用 `route` 根据输出进行分支：

```yaml
- id: classify
  kind: llm_classify
  output_choices: [DOCS, BUG, SECURITY]

- id: handle
  kind: agent
  skill: summarize
  depends_on: [classify]
  route:
    - when: "outputs.classify == 'DOCS'"
      to: writer
    - when: "outputs.classify == 'BUG'"
      to: debugger
    - when: "outputs.classify == 'SECURITY'"
      to: security-reviewer
```

无 `when` 的 route 充当默认 fallback。

## 错误处理

### `on_failure` —— 替代步骤

```yaml
- id: llm_summarize
  kind: llm_chat
  on_failure: fallback_template
  timeout_seconds: 15

- id: fallback_template
  kind: tool_call
  tool: emit_text
  with:
    text: "摘要不可用——使用模板。"
```

**5 条约束**（parse + runtime 强制执行）：
1. fallback 目标必须在 composition 中存在
2. 步骤不能引用自身
3. fallback 不能有 `on_failure`（禁止链式）
4. 每个 fallback 只能服务于一个 primary
5. fallback 不能有 `depends_on`

### `continue_on_error` —— 失败时跳过

```yaml
- id: optional_step
  kind: skill_exec
  skill: analytics
  with:
    continue_on_error: true
```

失败时将步骤标记为 `Continued: true`，DAG 继续。

## 最终文本模式

| 模式 | 行为 |
| --- | --- |
| `auto` | 默认。运行时将步骤输出总结为简洁的最终答案 |
| `raw` | 逐字返回最后一个非替代步骤的输出 |
| `step:<id>` | 逐字返回一个特定步骤的输出 |
| `structured` | 返回带 `error_code` 和每步 `status`/`failure_code` 的 JSON 信封 |

```yaml
final_text_mode: auto
final_text_mode: raw
final_text_mode: "step:summarize"
final_text_mode: structured
```

## 模板安全

模板是由 `MetaTemplateRenderer` 渲染的 Jinja2 表达式。只允许 4 个 filter：
`xml_escape`、`slugify`、`truncate`、`tojson`。

始终过滤用户输入和之前步骤的输出：

```yaml
# 安全
query: "{{ input | xml_escape | truncate(512) }}"
text: "{{ outputs.search | truncate(2000) }}"
slug: "{{ input | slugify | truncate(80) }}"
payload: "{{ outputs.plan | tojson }}"
```

```yaml
# 不安全——绝对不要这样做
query: "{{ input }}"
text: "{{ outputs.search }}"
```

Jinja2 沙箱实施三道防线：
1. 经典逃逸向量（`__class__`、`__bases__`、`.GetType()`）被阻断
2. 只有 4 个注册 filter 有效——38+ 个内置 Jinja2 filter 被覆盖
3. 全局函数（`range()`、`dict()`）抛出 `NotSupportedException`，由渲染器捕获

## 有界执行

```yaml
- id: api_call
  kind: tool_call
  tool: external_api
  timeout_seconds: 30
  retry:
    max_attempts: 2
    backoff_ms: 500
```

## 激活指导

- 将触发词写成用户自然输入的短语：`总结最近历史`，而不是 `运行内部 DAG 组合 meta skill`
- 使用 2-5 个触发词，除非有经过测试的理由使用更多
- 避免触发词与解释性问题冲突（如"这个 meta-skill 是如何工作的？"）
- 设置 `description` 引导模型选择。模型主要看到前置元数据和注入的 Skill 摘要

## 校验清单

启用 MetaSkill 前：

1. 前置元数据解析为有效 YAML
2. `kind: meta` 和 `composition.steps` 存在
3. 所有 `depends_on`、`route.to` 和 `on_failure` 目标存在
4. 依赖图中无环路
5. 所有用户输入和步骤输出都通过 `xml_escape`/`truncate` 过滤
6. `on_failure` 目标通过全部 5 条约束
7. 触发词通过误报测试
8. `final_text_mode` 匹配预期的交付物形态

## 故障排查

**MetaSkill 未激活**：
- 确认 `SKILL.md` 在已加载的 Skill 目录下
- 确认 `kind: meta` 且 `composition.steps` 非空
- 确认用户措辞匹配触发词或描述
- 检查 `Skills.MetaSkill.Enabled` 不是 `false`

**解析失败**：
- 检查重复的步骤 ID
- 检查未知的 `kind` 值
- 检查 `agent` 或 `skill_exec` 步骤缺少 `skill`
- 检查 `llm_classify` 缺少 `output_choices`
- 检查 `user_input` 缺少 `clarify.fields`
- 检查环路和未定义的 `depends_on` 引用

**Fallback 未激活**：
- 检查 5 条 `on_failure` 约束未被违反
- 验证 fallback 步骤存在且没有 `depends_on`

---

[用户指南](../meta-skill-user-guide.md) · [功能概览](../meta-skills.md) · [站点地图](../SITE_MAP.md)

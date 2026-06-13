# OpenSquilla 元技能迁移说明

这份说明总结了当前 OpenClaw.NET 对 OpenSquilla 风格元技能路径的实现水平，重点放在已经对齐的能力和还需要补齐的迁移项。

## 当前状态

OpenClaw.NET 已经实现了 OpenSquilla 风格元技能编排的核心骨架：

- `kind: meta` 与 `composition.steps` DAG 编排
- `depends_on` 依赖顺序与循环依赖校验
- `llm_classify` 的 `options + route` 分支路由
- `user_input` 的暂停 / 恢复与会话检查点恢复
- `final_text_mode: auto | raw | structured | step:<id>`
- 结构化执行结果 envelope，便于自动化和诊断

这说明当前运行时已经可以承载“先组装技能图，再分类分支，然后执行工具或模型步骤”的基础流程。

参考 OpenSquilla 源码树 `E:\GitHub\opensquilla\src\opensquilla\skills\meta` 的基线可以看到：

- `parser.py` 把 `on_failure` 当作一类显式的失败分支契约来解析与校验，要求目标步骤存在、不能自引用、不能嵌套链式 failover、且一个 fallback 目标只能被一个主步骤占用。
- `types.py` 与 parser 层把 `output_choices`、`tool_allowlist`、`clarify` schema 当成强类型契约，而不是只靠运行时约定。

OpenClaw.NET 现在已经补上了与之对应的首类本地能力：显式失败替代分支、step 级重试 / 超时策略、JSON 中间结果校验、下文所述的 P0 原生 DSL / Jinja 兼容层，以及 P1 首轮运行时对齐切片中的 `skill_exec`、meta-run 持久化和专用 meta policy gating。这一已交付切片现已端到端实现完成，并通过 OpenClaw 测试项目整体验证（`1907 passed, 0 failed, 0 skipped`）。但更完整的 OpenSquilla 元策略面仍然比当前 OpenClaw.NET 实现更宽。

## 新近完成的对齐项

- OpenSquilla 原生 DSL 字段现在已经成为首类 parser/runtime 契约：`output_choices`、composition `tool_args`、step `tool_args`、`tool_allowlist`、`clarify`、`when`、route arrays。
- `user_input.clarify` 现在会校验 typed chat/form 输入，并把成功的多字段结果标准化为 canonical JSON 文本。
- Jinja 渲染现在使用 `Jinja2.NET 1.4.1`，并提供 OpenSquilla 兼容的 `xml_escape`、`slugify`、`truncate`、`tojson` 过滤器。
- 运行时对齐的最后一轮加固也已经完成：包括陈旧 checkpoint 拒绝恢复、continued non-tool failure 的 completion routing 对齐，以及跨恢复边界保留 `user_input_required` pause trace。
- `skill_exec` 现在已经有首类 parser/runtime 契约，覆盖 `entrypoint`、`args`、`cwd` 和 `parse_mode`，并通过工具执行层运行脚本资源，同时做路径安全校验，不再退化为模型委托聊天步骤。
- Meta run 现在会把 completed、failed、paused 三类执行结果以最小记录形式持久化进 session 模型，为后续审计和 replay 打下基础，但尚未单独暴露运维界面。
- 现在已经有专用 meta-layer policy gating：通过 `SkillsConfig.MetaSkill.Enabled` 保留元技能安装态，同时从 prompt index 中隐藏、抑制 routing hint，并拒绝显式 `meta_invoke` 调用。

## 验证状态

当前实现已经通过 focused meta-skill 回归和 OpenClaw 整体测试项目验证：

- focused P1 回归切片：`skill_exec` parser/runtime 对齐（`7 passed`）、meta-run 持久化（`4 passed`）、专用 meta policy gating（`3 passed`）
- 完整测试项目：`1907 passed, 0 failed, 0 skipped`

因此，下文描述的是当前已经落地并验证过的 OpenClaw.NET 行为，而不是一个计划中或部分完成的 parity 层。

## Proposal review 叠加层（2026-06-13）

派生 proposal 层现在补上了 operator review 命令，同时继续保持“证据优先”的只读派生语义：

- `openclaw skills meta-runs proposals accept <session-id> --proposal <id>` 与 `... dismiss ...` 仅记录 operator review 决策。
- 这些 review 决策不会触发工具执行、模型执行、replay、resume 或 proposal 生命周期变更。
- 同动作重复提交按幂等成功处理；反向动作会按冲突拒绝。
- `meta-runs proposals` 与 `meta-runs proposals show` 会在既有派生证据字段之外，additive 输出 review 状态（`reviewStatus`、`reviewedAtUtc` 与 detail `review` 对象）。

## 已经对齐的部分

### 1. DAG 编排

OpenClaw.NET 会把元技能的步骤定义为 `composition.steps`，并在执行前校验编排图：

- 重复 step ID
- 缺失依赖
- 自依赖
- 依赖环
- `llm_classify` 的路由目标是否有效

这让元路径具备“先失败快、后执行”的治理能力，而不是默默接受坏图并在执行中后置失败。

### 2. 步骤类型

当前运行时支持的核心编排类型包括：

- `agent`
- `skill_exec`
- `tool_call`
- `llm_chat`
- `llm_classify`
- `user_input`

这对应了当前 OpenClaw.NET 元技能执行面上的主要能力边界。

### 3. 结构化输出与诊断

当开启 `final_text_mode: structured` 时，运行时可以返回结构化载荷，包含：

- `skill`
- `final_text`
- `error` / `error_code`
- `steps[]`，包含状态、耗时、失败代码与继续执行标记

此外，元步骤现在也支持：

- `on_failure` 替代分支，并在 parser/runtime 两层校验
- `retry` 与 `timeout_seconds`，用于工具和模型步骤的有界重试与超时
- `output_contract` / `output_schema`，用于校验 JSON 中间结果的必填字段

这有利于自动化测试、日志排障和运维观测，不需要从自由文本最终回答里反向解析执行状态。

## 迁移 checklist

把 OpenSquilla 元技能迁移到 OpenClaw.NET 时，建议按下面的顺序做：

1. 用 `composition.steps` 表达编排图，并用 `depends_on` 控制顺序。
2. 用 `llm_classify` 处理分支选择，而不是靠字符串判断。
3. 如果失败步骤应该激活一个替代步骤，并把替代步骤输出镜像回主步骤 ID 给下游依赖使用，就使用 `on_failure`。
4. 如果只是希望失败后继续执行、但不需要替代分支语义，再使用 `with.continue_on_error`。
5. 对需要有界执行的工具或模型步骤，配置 `retry.max_attempts`、可选的 `retry.backoff_ms`，以及 `timeout_seconds`。
6. 如果下游依赖结构化中间结果，使用 `output_contract` / `output_schema`，并声明 `format: json` 与 `required_properties`。
7. 若需要机器可读结果，优先使用 `final_text_mode: structured`。
8. 对交互式流程，使用 `user_input` 来承载暂停与恢复边界。

## 当前剩余的迁移差距

当前 OpenClaw.NET 的元路径已经覆盖 DAG 执行、fail-fast 校验、显式失败替代、有界 step 执行、结构化执行结果、P0 原生 DSL 兼容层、typed `user_input.clarify`、共享 Jinja 过滤器、`skill_exec` 子进程 entrypoint 执行、最小化的 meta-run 持久化、专用 meta policy gating，以及这一轮 slice 所需的 checkpoint / routing runtime 对齐。但它还不是 OpenSquilla 原生元技能契约的完全等价替代。

| 差距项 | 影响 | 当前状态 |
| --- | --- | --- |
| Meta run history 细节查看、replay、proposals CLI | OpenSquilla 暴露 `skills meta runs ...`、dry-run replay、proposal list/show/accept 等命令，便于审计和运维。 | OpenClaw.NET 现在已经把最小化的 per-run 记录持久化到 session state，并提供了本地 `openclaw skills meta-runs <session-id>` inspection 能力，默认输出 run 摘要，使用 `--verbose` 可展开 per-step trace，支持 `--run <run-id>` 精确筛选，并可通过 `--json` 输出机器可读结果；同时提供了 preview-only 的 `meta-runs replay` 可用性检查，会报告最小 replay plan 和按证据区分的 operator 结论；另外新增了独立的 `meta-runs reconstruct` 命令，可基于 persisted run history 和可选 checkpoint 证据构建审计型 replay result，而不会重新执行工具或模型；现在也新增了只读的 `meta-runs proposals` / `meta-runs proposals show` 运维面，只会针对 paused 或 failed run 基于持久化 meta-run 证据派生候选 proposal 摘要。`proposals show` 现在还能进一步展开一个 additive 的 run 级 `evidence` 摘要（`timelineStepIds`、`errorCode`、`error`、`finalText`）、持久化的 step 级证据（`steps[]`，包含 kind/status/failure/duration/continued 元数据）以及结构化 checkpoint 摘要（`checkpoint.pendingStepId`、pending/blocked step 集合、`promptPresent`、output step IDs、failure-alias step IDs），同时仍然保持只读语义。为兼容现有消费者，早期的顶层 detail 字段仍会继续输出为 legacy-compatible mirrors，但 operator 应优先读取分组后的 `evidence` 与 `checkpoint` 字段。稳定的 replay preview、reconstruct 和 derived proposal 契约字符串现在都集中在共享 session model 常量中，而不再散落为 CLI 本地字面量。`proposals accept/dismiss` 的 review lifecycle 已经接入 `LearningProposal`（`meta_run_review` kind + approved/rejected status）；剩余差距集中在 derived proposal 本体仍是运行时派生、尚未沉淀完整 provenance 快照与域层回滚/变更工作流。 |
| 更完整的 `skill_exec` stdin / replay 运维能力 | OpenSquilla 的 `skill_exec` 除了子进程执行本身，还覆盖更丰富的 stdin 工作流和周边运维工具。 | OpenClaw.NET 现在已经能以校验过的子进程形式执行 skill entrypoint，并且已支持 `stdin` 透传执行；当前剩余差距主要是围绕 `skill_exec` 运行结果的 replay/inspection 运维面与失败分支契约覆盖仍不完整。 |
| 真正的并行 step 调度 | OpenSquilla 可以在 scheduler 限制内并发执行独立 steps。 | OpenClaw.NET 保留了 DAG 顺序正确性，但当前通过运行时循环推进 ready steps，不是并行 scheduler。 |
| 内置 MetaSkill 目录与 creator/proposal 流程 | OpenSquilla 文档包含 `meta-web-research-to-report`、`meta-document-to-decision`、`meta-skill-creator` 等内置工作流，以及 proposal inspection 和 auto-enable audit。 | 当前 OpenClaw.NET 路径聚焦运行时编排；更宽的产品级目录和 proposal 工作流尚未迁移。 |

## 建议

把当前 OpenClaw.NET 的元技能路径理解为已经交付并验证过的强 OpenSquilla 风格实现，覆盖：

- DAG 编排
- 显式失败替代
- 有界 step 执行
- JSON 中间结果校验
- 结构化执行结果
- `output_choices`、`tool_args`、`tool_allowlist`、`clarify`、`when`、route arrays 的原生 DSL 对齐
- `xml_escape`、`slugify`、`truncate`、`tojson` 的共享 Jinja 渲染
- 面向脚本资源、带 parser/runtime 安全校验的 `skill_exec` entrypoint 执行
- session state 中最小化的持久化 meta-run history
- 运行时级别的专用 meta policy gating
- 当前元运行时模型内的 pause/resume checkpoint 安全性与 continued-failure routing 对齐

后续如果要继续追求更完整的 OpenSquilla parity，建议按“仍未补齐且直接影响运行/运维”的顺序推进：

1. **P1：Meta run history 的 replay 与运维能力。** 现在已经有本地 CLI inspection，默认输出 run 摘要，并可通过 `--verbose` 展开 per-step trace，支持 `--run <run-id>` 精确筛选和 `--json` 机器输出；同时也已经区分了 preview-only replay 可用性检查与独立的 audit reconstruction 命令，后者可根据 persisted run history 和可选 checkpoint 证据重建 replay result，但不会重新执行工具或模型；现在也补上了一个基于 paused/failed run 审计证据的只读 `meta-runs proposals` 派生视图，其中 `meta-runs proposals show` 已能展开 run 级 `evidence` 摘要、step 级证据和结构化 checkpoint 元数据，便于 operator 审阅。`proposals accept/dismiss` 的 review lifecycle 也已迁移到 `LearningProposal` 存储层；这一运维面剩下的差距主要是 derived proposal 本体的持久化/provenance 语义仍不完整。
2. **P1：`skill_exec` 的运维 ergonomics。** `stdin` 执行链路已打通；若迁移技能依赖 replay/inspection，可继续补齐围绕 `skill_exec` 的运维可见性与失败路径契约覆盖。
3. **P2：真正的并行 step 调度。** 在保持 DAG 正确性的同时，让独立 steps 并发执行。这能改善性能并贴近 OpenSquilla 行为，但大多数流程不依赖它才能完成迁移。
4. **P2：产品级 catalog、creator 与 proposal 流程。** 只有在 OpenClaw.NET 需要产品级 OpenSquilla parity，而不只是 runtime 可移植性时，再补内置 MetaSkill、`meta-skill-creator`、proposal inspection 与 auto-enable audit。

## P1 验收清单（DoD）

以下清单用于 P1 里程碑验收，按“能力面 -> 契约 -> 回归”分层检查。

### A. P1-1：Meta run history replay 与运维面

- [x] 提供 `meta-runs` inspection 基础面（摘要、`--run`、`--verbose`、`--json`）。
- [x] 提供 `meta-runs replay`（preview-only）与 `meta-runs reconstruct`（审计重建，非执行）。
- [x] 提供 `meta-runs proposals` 与 `meta-runs proposals show` 的派生证据视图。
- [x] 提供 `proposals accept` / `proposals dismiss` review-only 操作（不触发工具/模型/replay/resume）。
- [x] 同动作幂等成功，反向动作冲突拒绝，JSON 失败路径不输出 partial JSON。
- [x] 列表与详情 additive 暴露 review 状态（`reviewStatus`、`reviewedAtUtc`、`review`）。
- [x] 帮助文案覆盖新命令（skills 帮助与顶层 CLI 帮助）。

验收命令（已通过示例）：

- `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_Proposals_Accept|FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_Proposals_Dismiss|FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_Proposals_Show_Json_IncludesReviewSection|FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_Proposals_Json_IncludesReviewStatus|FullyQualifiedName~CliProgramTests.Main_Help_ListsSkillsMetaRunsProposalReviewCommands"`
- `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsTests.RunAsync_MetaRuns_|FullyQualifiedName~CliProgramTests.Main_Help_ListsSkillsMetaRuns"`

当前缺口（P1-1 未闭环项）：

- [x] `proposals accept/dismiss` review lifecycle 已迁移到 `LearningProposal` 存储域模型。
- [ ] proposal provenance 与 lifecycle 语义的完整域层对齐（derived proposal 本体仍以派生视图为主，避免长期停留在派生层扩展）。

### B. P1-2：skill_exec stdin 与运维 ergonomics

- [x] 支持 stdin-heavy `skill_exec` 契约与执行路径（已支持 `stdin` 透传执行）。
- [ ] 提供围绕 `skill_exec` 的 inspection/replay 运维可见性（当前不足）。
- [ ] 为 stdin/replay 失败路径补齐 machine-readable 合同和回归测试。

建议验收门槛：

- P1-1 可判定为“基本完成”；
- P1-2 的 stdin 基线已完成，但在 inspection/replay 运维可见性与失败路径合同补齐前，整体 P1 仍判定为“部分完成”。

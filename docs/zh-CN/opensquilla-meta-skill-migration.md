# OpenSquilla 元技能迁移缺口（精简版）

本文档仅保留“未完成迁移缺口”与“验收口径”，用于 P1/P2 跟踪。

## 当前结论（2026-06-14）

- 元技能基础能力已可用：DAG 编排、`llm_classify`、`user_input` pause/resume、`final_text_mode`、结构化结果。
- P1 中 `meta-runs` operator 基线已完成并升级：在保留 **session 维度入口**（`meta-runs <session-id> ...`）兼容性的同时，新增了全局视图 `meta-runs list/show/failures`。
- proposal lifecycle/provenance 已完成域层闭环（durable `LearningProposal` + snapshot/history additive 输出）。
- `skill_exec` 已具备 stdin 执行、evidence 持久化与 replay/reconstruct machine-readable 契约。
- P2-1 并行 step 调度已完成：当前并发语义为独立 ready `tool_call` steps 的波次并发（子集并行），且 Agent/MAF 双实现已回归通过。
- 本次补齐了最关键的 meta 语义收口：MetaSkill 不能 compose MetaSkill、`skip_if` clarify 语义落点、以及 meta 专属 risk/capabilities 门禁。

## 逐项验收表

> 说明：本表用于把 OpenSquilla 的用户/作者文档要求，与 OpenClaw 当前实现逐项对齐。

| 验收项 | OpenSquilla 要求 | OpenClaw 现状 | 证据 | 结论 |
| --- | --- | --- | --- | --- |
| MetaSkill 基本定义 | `SKILL.md` 里要有 `kind: meta`、`triggers`、`composition.steps` | 已支持 | [SkillLoader.cs](../../src/OpenClaw.Core/Skills/SkillLoader.cs#L261)；[SkillModels.cs](../../src/OpenClaw.Core/Skills/SkillModels.cs#L100) | 已完成 |
| 自然触发 / 显式触发 | 支持自然语言触发和显式指定 meta skill | 已支持 `triggers` + `meta_invoke` + 优先级匹配 | [MetaSkillResolver.cs](../../src/OpenClaw.Core/Skills/MetaSkillResolver.cs#L1)；[MetaInvokeTool.cs](../../src/OpenClaw.Core/Skills/MetaInvokeTool.cs#L1) | 已完成 |
| 前置限制（运行时与测试） | 作者/用户指南强调要做结构校验、触发检查、运行时测试、安全边界评估 | 结构/触发解析与运行时测试覆盖已具备，核心执行链路可验证 | OpenSquilla 用户/作者文档；[SkillLoader.cs](../../src/OpenClaw.Core/Skills/SkillLoader.cs#L261)；[MetaSkillResolver.cs](../../src/OpenClaw.Core/Skills/MetaSkillResolver.cs#L1)；[SkillTests.cs](../../src/OpenClaw.Tests/SkillTests.cs#L240) | 已完成 |
| 审核流程对象（治理层） | 对高风险 meta 变更应有独立审核流程与可追踪治理对象 | 目前主要依赖运行时约束与测试回归，尚无单独的 meta-skill 审核流程对象 | OpenSquilla 用户/作者文档；[SkillTests.cs](../../src/OpenClaw.Tests/SkillTests.cs#L240) | 部分完成 |
| 风险元数据 | `metadata.opensquilla.risk` / `capabilities` 要成为作者约束的一部分 | Meta skill 加载已按配置执行 risk/capability 门禁 | [SkillLoader.cs](../../src/OpenClaw.Core/Skills/SkillLoader.cs#L2218)；[SkillTests.cs](../../src/OpenClaw.Tests/SkillTests.cs#L1185) | 已完成 |
| 是否允许再 compose MetaSkill | 作者文档明确说 MetaSkill 不能 compose 另一个 MetaSkill | 运行时预检会拒绝 meta->meta composition | [AgentRuntime.cs](../../src/OpenClaw.Agent/AgentRuntime.cs#L1988)；[MafAgentRuntime.cs](../../src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs#L721)；[AgentRuntimeTests.cs](../../src/OpenClaw.Tests/AgentRuntimeTests.cs#L1590) | 已完成 |
| Final text mode | `auto` / `raw` / `structured` / `step:<id>` | 已支持 | [AgentRuntime.cs](../../src/OpenClaw.Agent/AgentRuntime.cs#L3035)；[AgentRuntime.cs](../../src/OpenClaw.Agent/AgentRuntime.cs#L3047) | 已完成 |
| DAG / depends_on / route / on_failure | 组合步骤、依赖、路由、失败替代都要可执行 | 已支持 | [SkillModels.cs](../../src/OpenClaw.Core/Skills/SkillModels.cs#L151)；[AgentRuntime.cs](../../src/OpenClaw.Agent/AgentRuntime.cs#L2888)；[AgentRuntime.cs](../../src/OpenClaw.Agent/AgentRuntime.cs#L3549) | 已完成 |
| Step 类型覆盖 | `agent`、`llm_chat`、`llm_classify`、`user_input`、`tool_call`、`skill_exec` | 已支持 | OpenSquilla authoring 文档；[AgentRuntime.cs](../../src/OpenClaw.Agent/AgentRuntime.cs#L2257)；[AgentRuntime.cs](../../src/OpenClaw.Agent/AgentRuntime.cs#L2584)；[AgentRuntime.cs](../../src/OpenClaw.Agent/AgentRuntime.cs#L2696)；[AgentRuntime.cs](../../src/OpenClaw.Agent/AgentRuntime.cs#L3433) | 已完成 |
| user_input / clarify 语义 | 文档里有 form/chat、fields、skip_if、timeout、cancel 等更完整语义 | form/chat、fields、timeout、cancel、默认值、类型校验与 `skip_if` 都已支持 | [SkillLoader.cs](../../src/OpenClaw.Core/Skills/SkillLoader.cs#L1477)；[AgentRuntime.cs](../../src/OpenClaw.Agent/AgentRuntime.cs#L2696)；[MafAdapterTests.cs](../../src/OpenClaw.Tests/MafAdapterTests.cs#L1707) | 已完成 |
| meta-runs / proposals 运维面 | 需要可运行检查、回放、重建、提案生命周期 | 已具备 session 维度 inspect/replay-preview/reconstruct/proposals 生命周期，并新增全局 `meta-runs list/show/failures` 运维入口（保持旧入口兼容） | OpenSquilla 用户文档；[SkillCommands.cs](../../src/OpenClaw.Cli/SkillCommands.cs#L65)；[SkillCommands.cs](../../src/OpenClaw.Cli/SkillCommands.cs#L167)；[SkillCommands.cs](../../src/OpenClaw.Cli/SkillCommands.cs#L2182)；[SkillCommandsGlobalMetaRunsTests.cs](../../src/OpenClaw.Tests/SkillCommandsGlobalMetaRunsTests.cs#L10) | 已完成 |
| 质量门禁（creator 草稿） | 作者流程至少要有结构/描述等基础质量检查，低质量草稿应被拦截 | `skills create --proposal-draft` 已执行阻断型门禁，低质量返回 `proposal_draft_quality_gate_failed` | OpenSquilla 用户/作者文档；[SkillCommands.cs](../../src/OpenClaw.Cli/SkillCommands.cs#L1404)；[SkillCommands.cs](../../src/OpenClaw.Cli/SkillCommands.cs#L1714)；[SkillCommandsTests.cs](../../src/OpenClaw.Tests/SkillCommandsTests.cs#L703) | 已完成 |
| 质量门禁（proposal 接受前） | 文档要求作者在接受前做结构验证、触发检查、运行测试、安全边界评估 | `accept` 与 `change --to accept` 已接入统一门禁；低质量提案在 `--json` 下返回 `proposal_accept_quality_gate_failed`，并阻断生命周期变更 | OpenSquilla 用户/作者文档；[SkillCommands.cs](../../src/OpenClaw.Cli/SkillCommands.cs#L658)；[SkillCommands.cs](../../src/OpenClaw.Cli/SkillCommands.cs#L1067)；[SkillCommandsMetaGovernanceTests.cs](../../src/OpenClaw.Tests/SkillCommandsMetaGovernanceTests.cs#L80) | 已完成 |
| bundled catalog / stable meta catalog | 文档描述了稳定的内置 meta catalog | 已提供产品化入口：`openclaw skills catalog --stable --kind meta`（bundled first-party meta 集合，支持 text/json） | OpenSquilla 用户文档；[SkillCommands.cs](../../src/OpenClaw.Cli/SkillCommands.cs#L1490)；[SkillCommandsMetaGovernanceTests.cs](../../src/OpenClaw.Tests/SkillCommandsMetaGovernanceTests.cs#L12) | 已完成 |
| disable model-visible meta behavior | 文档允许全局关闭 meta 可见性，保留库存但拒绝显式调用 | OpenClaw 有对应配置和运行时拒绝 | [SkillModels.cs](../../src/OpenClaw.Core/Skills/SkillModels.cs#L1)；[MafAgentRuntime.cs](../../src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs#L680)；[OpenClawToolExecutor.cs](../../src/OpenClaw.Agent/OpenClawToolExecutor.cs#L483) | 已完成 |
| 运行证据 / 可审计性 | 文档强调可审计、可重放、可恢复 | 已有 history / evidence / checkpoint 方向，且测试覆盖到位 | [opensquilla-meta-skill-migration.md](opensquilla-meta-skill-migration.md#L1)；[AgentRuntimeTests.cs](../../src/OpenClaw.Tests/AgentRuntimeTests.cs#L1752)；[MafAdapterTests.cs](../../src/OpenClaw.Tests/MafAdapterTests.cs#L896) | 已完成 |

### 严格版迁移结论

- **运行时主链路已完成迁移**：DAG、路由、失败替代、pause/resume、`skill_exec`、risk/capabilities 门禁与审计证据主路径均可用。
- **运维/产品命令面已完成同构闭环**：在保留 session 维度 `meta-runs` 入口的前提下，已补齐全局 runs 视图命令（`list/show/failures`）与稳定 meta catalog 产品入口。
- **治理同构主缺口已收口**：proposal 接受前统一质量门禁已接入 `accept/change --to accept`，并具备 machine-readable 失败契约。

### 最关键的 3 个收口

- MetaSkill 不能 compose MetaSkill 的硬禁已经收口。

  运行时预检会拒绝 meta->meta composition，Agent/MAF 双路径都已覆盖回归，错误信息也已明确指出 meta->meta nesting 被拒绝。

- `skip_if` 这类 clarify 语义已经收口。

  `skip_if` 已进入 clarify schema，并在运行时用于跳过用户提示；Agent/MAF 两条路径都能在 skip 条件命中时继续执行而不落 checkpoint。

- `risk` / `capabilities` 的 meta 专属门禁已经收口。

  meta skill 加载阶段会按配置门禁筛掉不满足风险/能力要求的技能，`SkillLoader.LoadAll` 会保留普通技能，同时过滤掉不满足 meta policy 的 meta skill。

## 已完成范围（简表）

| 主题 | 状态 | 说明 |
| --- | --- | --- |
| Meta run inspection | 已完成 | `meta-runs` 支持摘要、`--run`、`--verbose`、`--json` |
| Replay preview contract | 已完成 | `meta-runs replay` 输出缺口原因与 requirements |
| Audit reconstruction | 已完成 | `meta-runs reconstruct` 输出 timeline/checkpoint；`skill_exec` 含 notes |
| Proposal review lifecycle | 已完成 | `proposals accept/dismiss/rollback/change` 更新 durable `LearningProposal`，并输出 `lifecycle/provenanceHistory` |
| skill_exec contract | 已完成 | stdin 透传 + evidence（`input_mode/stdin_bytes/parse_mode/command`） |

## 剩余迁移缺口（按优先级）

### P2（能力增强，不阻塞基础迁移）

- creator 质量门禁深度基本补齐

  现状：已具备 `proposalDraft.quality`、分级建议字段与阻断型阈值；`skills create --proposal-draft` 在低质量草稿上会返回 machine-readable `proposal_draft_quality_gate_failed`。

  仍可继续增强：如果后续要引入更细的门禁策略，可以再细分阻断项/警告项层级与 creator 分类规则。

- 产品层 E2E 验收口径继续扩展中

  现状：已完成 `create -> proposal -> lifecycle -> audit` 验收切片，并补入权限失败分支、双向冲突分支（dismiss 后 accept 冲突、accept 后 dismiss 冲突）与 `invalid_lifecycle_transition` 分支；失败分支已校验 JSON 错误契约（`status/command/errorCode/message`），并覆盖失败后 `lifecycle/audit/provenance` 不漂移；仍可继续覆盖更多失败/越权/冲突场景，提升长期治理信心。

  建议：在现有切片基础上继续补充失败与授权边界链路的端到端回归。

## P2 实施任务单（可直接开工）

### Task C：并行 step 调度（P2-1）

状态：已完成（2026-06-14）

目标：在不破坏 DAG 语义和现有 pause/resume 行为的前提下，让独立 ready steps 并发执行。

建议改动文件：

- `src/OpenClaw.Agent/AgentRuntime.cs`
- `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs`
- `src/OpenClaw.Tests/AgentRuntimeTests.cs`
- `src/OpenClaw.Tests/MafAdapterTests.cs`

实施要点（TDD）：

1. RED：新增并发红测，验证两个互不依赖 `tool_call` steps 能并行（`MaxConcurrent >= 2`）。
2. GREEN：在 meta 执行循环中加入 ready-set 波次调度，对“无路由副作用、无失败分支依赖”的独立 steps 并发执行。
3. REFACTOR：统一 Agent/MAF 两条实现路径的并行判定与结果归并规则，保持错误码与 structured 输出兼容。

验收测试（建议最小切片）：

- `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~ExecuteMetaSkillAsync_IndependentToolSteps_RunConcurrently|FullyQualifiedName~MafAgentRuntime_ExecuteMetaSkillAsync_IndependentToolSteps_RunConcurrently"`
- `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~ExecuteMetaSkillAsync_|FullyQualifiedName~MafAgentRuntime_ExecuteMetaSkillAsync_"`

DoD：

- 独立 steps 在运行证据上可观测到并发执行。
- `depends_on`、`route`、`on_failure`、`user_input` 语义与现有契约保持兼容。
- replay/reconstruct/proposals 相关回归不退化。

完成证据：

- 新增回归：`ExecuteMetaSkillAsync_IndependentToolSteps_RunConcurrently`
- 新增回归：`MafAgentRuntime_ExecuteMetaSkillAsync_IndependentToolSteps_RunConcurrently`
- 切片回归：`FullyQualifiedName~ExecuteMetaSkillAsync_|FullyQualifiedName~MafAgentRuntime_ExecuteMetaSkillAsync_`（通过）

风险控制：

- 并发只覆盖可证明独立的 ready steps，避免扩大到有路由副作用分支。
- 失败传播策略保持现有错误码与文本契约，避免 CLI/operator 侧回归。

### Task D：产品级 MetaSkill catalog / creator / proposal flow（P2-2）

目标：补齐产品层工作流能力（catalog 发现、creator 生成、proposal 评审入口），与 runtime 核心解耦。

建议改动文件（第一阶段）：

- `src/OpenClaw.Core/Skills/*`（catalog/creator 抽象）
- `src/OpenClaw.Cli/SkillCommands.cs`（catalog/creator/proposal 入口命令）
- `src/OpenClaw.Tests/SkillCommandsTests.cs`
- `docs/opensquilla-meta-skill-migration.md`
- `docs/zh-CN/opensquilla-meta-skill-migration.md`

实施要点（分期）：

1. Phase 1：只落“目录发现 + 只读提案查看”最小闭环，确保不影响现有 meta-runs 运维面。（已完成，2026-06-14）
2. Phase 2：增加 creator 脚手架与提案草稿生成（含验证与失败路径）。
3. Phase 3：接入 proposal pipeline（状态流、审计字段、权限边界）。

Phase 2 当前进展（2026-06-14）：

- [x] `openclaw skills create` 最小脚手架入口（`standard|meta`）
- [x] 支持 `--json` 合同输出（`name/slug/kind/path/created/overwrote`）
- [x] 支持冲突保护与 `--force` 覆盖 `SKILL.md`
- [x] proposal 草稿输出契约（`--proposal-draft`，text/json 双输出）
- [x] proposal 草稿失败路径（`standard` + `--proposal-draft` 返回 usage 错误）
- [x] proposal 草稿质量摘要（JSON `proposalDraft.quality` + 文本质量行）
- [x] proposal 草稿质量明细（`proposalDraft.quality.checks[]` + `warnings[]`）
- [x] `--json` 失败路径 machine-readable 错误码（示例：`invalid_proposal_draft_kind`）
- [x] creator 质量检查分级（`pass|warn|fail`）与建议字段（`checks[].recommendation`）
- [x] creator 质量门禁阈值（`skills create --proposal-draft` 在低质量草稿上返回 `proposal_draft_quality_gate_failed`）
- [x] create 失败 JSON 统一 schema（`status/command/errorCode/message`）
- [x] 错误码体系已扩展到 `skills proposals` 与 `skills meta-runs proposals show` 的参数校验失败路径
- [x] 错误码体系已扩展到 `replay/reconstruct/proposals change/rollback` 的参数校验失败路径
- [x] 错误码体系已扩展到 `catalog/inspect/install` 的参数校验失败路径
- [x] 错误码体系已扩展到 meta-runs 运行时失败分支（例如 session/run/proposal not found 与 lifecycle transition 拒绝），`--json` 下统一输出 machine-readable 错误 schema
- [x] 错误码体系已扩展到 `inspect/install` 的运行时检查失败分支（如 source inspect 失败与 install 执行异常），`--json` 下统一输出 machine-readable 错误 schema
- [x] 错误码体系已扩展到 `skills` 顶层 unknown subcommand 分支，`--json` 下输出统一 machine-readable 错误 schema（`unknown_subcommand`）
- [x] Phase 3 权限边界第一步：`proposals accept|dismiss|rollback|change` 需要 `OPENCLAW_OPERATOR_ID`，缺失时在 `--json` 下返回 `permission_denied`
- [x] Phase 3 审计字段第一步：`proposals show` 与 mutation 响应新增 additive `audit` 字段（`schemaVersion/actorId/changedAtUtc/transitionAction`），来源于 durable transition metadata
- [x] Phase 3 产品级 E2E 验收切片已落地：`create --proposal-draft --json -> dismiss -> rollback -> change -> show`，并新增权限失败分支回归，对应 `RunAsync_Phase3_E2E_CreateToLifecycleToAudit_ReachesConsistentState` 通过
- [x] Phase 3 产品级 E2E 冲突分支回归：`dismiss -> accept(conflict) -> show`，对应 `RunAsync_Phase3_E2E_DismissThenAcceptConflict_PreservesDismissedState` 通过
- [x] Phase 3 产品级 E2E 反向冲突分支回归：`accept -> dismiss(conflict) -> show`，对应 `RunAsync_Phase3_E2E_AcceptThenDismissConflict_PreservesApprovedState` 通过
- [x] 错误码体系已覆盖当前 `skills` 命令面主要失败路径（参数校验、运行时 not-found、inspect/install 运行时失败、unknown subcommand）

Phase 1 完成证据（additive，兼容旧调用方）：

- [x] `openclaw skills catalog`（含 `--kind meta` 与 `--json`）
- [x] `openclaw skills proposals` 只读 alias（list/show；拒绝 lifecycle 写操作）
- [x] proposals list/detail JSON 增加入口标识：`entrypoint`、`readOnlyAlias`
- [x] 顶层与 skills 帮助文本更新，并有对应回归测试

验收测试（建议最小切片）：

- `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~RunAsync_Skills_|FullyQualifiedName~RunAsync_MetaRuns_Proposals_"`

DoD：

- catalog/creator/proposal 命令具备稳定帮助文本与 JSON 契约。
- 运行时编排与产品流程分层清晰，避免 runtime 路径耦合产品策略。

风险控制：

- 先做只读与脚手架能力，再推进写路径和状态流。
- 所有新增合同字段保持 additive，避免破坏现有调用方。

## P1 验收口径（更新）

### P1-1 Meta-runs 运维面

- [x] `meta-runs` inspection（摘要/过滤/verbose/json）
- [x] `replay` preview-only + `reconstruct` audit-only
- [x] `proposals` / `proposals show` 证据视图
- [x] `proposals accept/dismiss/rollback/change` durable lifecycle（`meta_run_proposal`）
- [x] 幂等/冲突/JSON 失败路径契约
- [x] proposal provenance 的域层闭环（snapshot + 回滚/变更）

### P1-2 skill_exec 运维面

- [x] stdin-heavy 执行路径
- [x] evidence 持久化与 reconstruct notes
- [x] replay 缺口 machine-readable 合约（含 `skill_exec_inputs_not_persisted`）
- [x] 更高层 operator UX（聚合与导诊）

## 建议下一步（执行顺序）

1. 继续扩展产品级 E2E 验收切片（增加更多失败/越权/冲突路径）。
  优先建议：按 failure matrix 维度补齐更多组合链路，并保持 JSON 错误契约 + non-drift 双断言。
2. 如未来需要更细 creator 分类，再拆分阻断项/警告项门禁策略。
3. 将扩展后的产品级 E2E 切片纳入常规回归基线。

## Phase 3 验收清单（DoD 草案）

- [x] proposal pipeline 状态机定义并文档化（含允许/禁止迁移、幂等与冲突语义）。
- [x] 写操作入口完成权限边界定义（操作者身份、只读入口限制、越权失败契约）。
- [x] 审计字段在 list/show/mutation 输出中保持 additive 且前后兼容。
- [x] `--json` 失败路径继续使用统一 schema（`status/command/errorCode/message`）。
- [x] 完成端到端验收切片并通过（产品链路 + 回放/重建链路）。

## P1 执行任务单（归档）

P1-1 与 P1-2 已完成并通过回归验证。该节仅保留历史记录，后续工作聚焦 P2。

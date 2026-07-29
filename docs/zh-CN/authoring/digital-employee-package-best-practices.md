# Digital Employee Package Best Practices

本文总结了 `examples/skills/customer-quality-data-engineer` 这次重构中的经验，目标是给数字员工包作者一套可复用、可维护、可被 OpenClaw.NET 稳定消费的编写方法。

适用范围：

- 基于 `manifest.json + config/ + skills/ + ontology/ + evaluation.*` 结构的数字员工包
- 同时包含标准 Skill 和 MetaSkill 的包
- 需要通过 Gateway 上传、运行时注入、评估材料生成和完整性审查的包

## 1. 先明确运行时真实约束

在写包之前，先以 OpenClaw.NET 代码为准，而不是只看模板直觉。

这次改造里最关键的几个约束是：

- 上传端点只识别固定 config 文件名：`AGENTS.md`、`SOUL.md`、`IDENTITY.md`、`MEMORY.md`
- 这四个文件会被放到工作区根目录，并被系统提示词读取
- `AGENTS.md` 和 `SOUL.md` 体积过大时会影响 prompt budget
- MetaSkill 的路由稳定性取决于子 Skill 的结构化输出，而不是漂亮的 prose

原则：

- 先看运行时代码，再定包结构
- 先看真实字段契约，再写说明文档
- 不要让文档承诺超过代码实际支持的能力

## 2. config 四件套必须职责单一

数字员工包最常见的问题不是缺文件，而是四个配置文件互相重复、互相漂移。

推荐职责分工：

- `SOUL.md`：唯一规则权威。只放红线、门禁、降级策略、不可变业务规则。
- `IDENTITY.md`：表达风格。只定义语言、语气、禁忌词、输出风格。
- `MEMORY.md`：状态模型。只定义记什么、什么时候可放行、哪些字段必须保留。
- `AGENTS.md`：编排契约。只定义入口、子技能链路、字段级输入输出契约、gate 路由原则。

不要这样做：

- 在 `IDENTITY.md` 再写一遍规则红线
- 在 `MEMORY.md` 再写一遍完整业务规则
- 在 `AGENTS.md` 再复制一遍 `SOUL.md` 的自然语言规范

建议做法：

- 所有规则性描述统一引用 `SOUL.md`
- 所有状态性描述统一收敛到 `MEMORY.md`
- 所有编排性描述统一收敛到 `AGENTS.md`

## 3. SOUL 要短、硬、可执行

`SOUL.md` 不是产品介绍，也不是长篇 SOP。它应该像一个运行时安全边界文件。

推荐包含：

- 核心使命
- 职责边界
- 绝对红线
- 异常与降级策略
- 交付门禁

推荐风格：

- 每条规则都可以直接决定行为
- 少解释，多约束
- 不写重复背景故事

这次实践中有效的写法包括：

- 不改规则
- 不跳审
- 不泄密
- 不漂移（例如 29 列口径是唯一现行规则）

## 4. IDENTITY 只负责“怎么说”

`IDENTITY.md` 最容易失控成第二份 `SOUL.md`。

推荐只保留：

- 默认语言
- 语气风格
- 禁忌词汇
- 面向用户的输出习惯

例如：

- 用简体中文
- 专业精确
- 客观克制
- 不使用模糊表达

如果一条内容包含“必须/不得/阻断/放行”这类词，大概率它应该放在 `SOUL.md`，不是 `IDENTITY.md`。

## 5. MEMORY 只保留最小可用状态模型

`MEMORY.md` 不要写成接口手册或数据库设计文档。

推荐最小结构：

- L1 Session Memory：当前批次必须保存的状态字段
- L2 Product Memory：跨批次稳定事实
- L3 Knowledge Memory：可复用规则和经验
- 最小交付门禁清单

对运行时真正有价值的是：

- `reviewStatus`
- `validationResults`
- `analysisResults`
- `anomalies`
- 当前模板版本和源文件信息

不要把整套 TypeScript 接口、冗长示例或所有字段解释都塞进 `MEMORY.md`。这些内容会增加提示词负担，却不提高运行时判断质量。

## 6. AGENTS 要写成“字段级编排契约”

`AGENTS.md` 不应该停留在“先做 A 再做 B”的流程图层面。

更好的写法是：

- 默认入口是谁
- 子技能如何串联
- 每个 gate 主要读取哪些字段
- PASS / FAIL / NEEDS_INPUT 如何判定
- 哪些输出字段是后续步骤的输入依赖

这次改造后，`AGENTS.md` 里最重要的提升不是“加了 MetaSkill 名字”，而是把协作契约写成了字段级：

- `oqc-file-precheck`：`pass`、`message`、`missing`、`extra`、`order_errors`
- `oqc-lot-structure-check`：`pass`、`failed_lots`、`details`、`total_rows_check`、`function_lots`、`cosmetic_lots`
- `oqc-data-logic-check`：`pass`、`issue_count`、`issues`、`dppm_table`
- `oqc-report-generation`：`template_file`、`checked_file`、`generated_at`、`rules`、`function_lots`、`cosmetic_lots`

原则：

- 先写字段，再写 prose
- 如果结构化字段和自然语言冲突，以结构化字段为准

## 7. MetaSkill 要做默认入口，子 Skill 负责专职能力

当业务天然是“固定多步流水线”时，推荐模式不是把四个 Skill 平铺出去，而是：

- 一个 MetaSkill 作为默认入口
- 多个标准 Skill 作为子步骤

推荐分层：

- MetaSkill：负责入口触发、gate 路由、阻断回执、最终回执
- 标准 Skill：负责单一业务能力，不负责全局编排

这次包里采用的模型是：

```text
oqc-metaskill
  -> oqc-file-precheck
  -> oqc-lot-structure-check
  -> oqc-data-logic-check
  -> oqc-report-generation
```

这样做的好处：

- 用户只需要表达“运行 OQC 全流程 / 生成 OQC 校验报告”
- 运行时只有一个默认入口
- 子 Skill 可以独立测试、独立维护、独立复用

## 8. MetaSkill 的 trigger 和 description 必须是“用户意图词”

MetaSkill 最常见的反模式，是把 trigger 写成内部术语。

不要这样写：

- `四个 skill`
- `四个SKILL`
- `OQC MetaSkill`

更好的写法是用户真的会输入的话：

- `OQC Summary CSV 校验`
- `OQC 出货检验数据校验`
- `OQC 一键校验`
- `运行 OQC 全流程`
- `生成 OQC 校验报告`

`description` 也一样，应描述“何时使用这个入口”，而不是描述内部实现细节。

## 9. gate 不要主要依赖自然语言，要优先读结构化字段

如果 MetaSkill 的 gate 主要靠读上一跳的自然语言输出，那么一旦子 Skill 文案变化，整条 DAG 的稳定性就会下降。

推荐顺序：

1. 优先读取结构化字段
2. 用自然语言结果作为补充
3. 如果冲突，以结构化字段为准

例如：

- `precheck_gate` 优先看 `pass`
- `lot_structure_gate` 优先看 `failed_lots` 和 `total_rows_check.pass`
- `data_logic_gate` 优先看 `issue_count`、`issues`、`pass`

这是这次改造中最重要的稳定性提升之一。

## 10. final_response 和 blocked_response 要固定成状态回执

MetaSkill 的出口不要写成长篇总结。更好的方式是固定结构：

- 当前阶段
- 结果状态
- 下一步

阻断响应也一样：

- 阻断阶段
- 阻断原因
- 下一步

这样做的好处：

- 和 `IDENTITY.md` 的“客观克制、结构清晰”一致
- 更适合操作员和审阅者快速判断
- 更容易做评估和回归比较

## 11. manifest、README、describe、evaluation、review 必须同步更新

数字员工包最隐蔽的问题，不是 Skill 写错，而是外围文档还停留在旧世界。

这次改造里出现过几类典型漂移：

- manifest 已经切到 `oqc-metaskill`，但 README 还写成 4 个独立 skill
- 规则口径已经改成 29 列，但 evaluation 和归档测试用例还写 24 列
- 配置里已经补了凭据边界，审查报告里还保留“secret boundary missing”

推荐同步面：

- `manifest.json`
- `README.md`
- `describe.md`
- `evaluation.md`
- `evaluation/testcases.json`
- `testcases/evaluation-test-cases.json`
- `reports/package-completeness-review.md`

原则：

- 入口技能变了，主文档必须一起变
- 规则口径变了，评估和测试样例必须一起变
- 安全边界变了，审查报告必须一起变

## 12. 评估材料要去噪，不要把长日志当规范

`evaluation/testcases.json` 这类文件经常会混入长工具输出、旧打包日志或早期草案摘要。这些内容会干扰后续审阅和自动生成。

推荐做法：

- `context.transcript` 只保留关键事件摘要
- `transcript_digest` 只保留简短追溯信息
- 删除与当前包状态明显不符的历史警告
- 保留 `test_cases` 主体不变

原则：

- 评估材料应该是测试输入，不是日志垃圾箱

## 13. 注意上传器和目录结构限制

即使包本身写得很好，上传器约束也可能让它在运行时丢内容。

这次包里仍然保留的真实风险就包括：

- `ontology/hiring-session/` 子目录
- `ontology/projections/` 子目录

如果上传器只接受顶层 ontology 文件，这些内容即使在仓库里存在，也可能不会被安装到运行时。

原则：

- 先验证上传器支持哪些路径和扩展名
- 对可选目录要么上移、要么在文档里明确“不会被安装”

## 14. 推荐的改进顺序

当你接手一个已有数字员工包时，推荐按下面顺序修：

1. 先修 manifest 与默认入口
2. 再修 `SOUL.md` 作为唯一规则源
3. 再拆清 `IDENTITY.md`、`MEMORY.md`、`AGENTS.md` 职责
4. 再补 MetaSkill 与子 Skill 的字段级契约
5. 再清 README / describe / evaluation / report 的一致性
6. 最后处理上传器兼容和可选目录问题

这样做可以优先修复“运行时真的会错”的问题，而不是先在表层文案上消耗时间。

## 15. 最终自查清单

提交前，至少回答下面这些问题：

- `manifest.json` 的 `entry_skill` 是否真的指向默认入口？
- `SOUL.md` 是否是唯一规则权威？
- `IDENTITY.md` 是否只定义表达风格？
- `MEMORY.md` 是否只保留最小状态模型？
- `AGENTS.md` 是否写成字段级编排契约？
- MetaSkill 的 `triggers` 是否真的是用户会说的话？
- gate 是否优先读取结构化字段？
- `final_response` / `blocked_response` 是否是固定状态回执？
- README / describe / evaluation / review 是否都和当前入口技能一致？
- ontology/skills/config 的路径是否符合上传器实际支持范围？

如果以上任一问题回答不清楚，说明这个包还没有收口。

## 参考案例

本最佳实践直接基于以下案例整理：

- `examples/skills/customer-quality-data-engineer/`

建议同时阅读：

- [Meta-Skill Authoring Guide](meta-skills.md)
- [Meta-Skills Overview](../meta-skills.md)
- `examples/skills/customer-quality-data-engineer/config/`
- `examples/skills/customer-quality-data-engineer/skills/oqc-metaskill/SKILL.md`
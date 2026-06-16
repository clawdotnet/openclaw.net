## Example: .NET 10 采用趋势分析

### 场景

技术研究团队对 3 个企业开发团队进行访谈，了解 .NET 10 升级意愿和阻碍。

### 输入 (transcript_or_notes)

```text
访谈对象：3 个企业开发团队（金融、电商、政府），共 12 名开发者

Q1: 当前 .NET 版本？
- 金融团队：.NET 8 LTS，核心交易系统，升级窗口在 2026 Q4
- 电商团队：.NET 6，计划 Q3 跳到 .NET 9，跳过 8
- 政府团队：.NET Framework 4.8，刚启动迁移评估

Q2: .NET 10 的关注点？
- 金融：NativeAOT 对低延迟交易的意义重大，但担心第三方库兼容（Oracle EF Core、gRPC 拦截器）
- 电商：关注 Minimal API 性能提升和容器镜像体积缩小（当前 Docker 镜像 380MB）
- 政府：更关心迁移路径而非特定特性，需要从 .NET Framework 直接跳到 10 的可行性评估
- 共同关注：OpenAPI 原生支持（不再依赖 Swashbuckle）、MAUI 混合应用的稳定性

Q3: 升级阻碍？
- 金融：合规审计要求每个依赖项有 SBOM，2026/6 前 32 个 NuGet 包未提供 .NET 10 兼容版本
- 电商：CI/CD 流水线（GitHub Actions + Azure DevOps）的 runner 镜像需要预装 .NET 10 SDK
- 政府：内部安全审批流程 6-8 周，加上采购周期
- 通用：团队培训成本，特别是 source generators 和 NativeAOT 调试

Q4: 期望的支持形式？
- 技术文档 + 迁移 playbook（比工具更优先）
- Microsoft 官方 .NET 10 升级助手覆盖 Framework 迁移
- 供应商（Oracle、JetBrains、Microsoft）明确 .NET 10 支持时间线
- 内部试点项目（非关键系统先上）的绿色通道
```

### 项目上下文 (project_context)

```text
团队负责组织级技术雷达更新。目标：在 Q3 技术雷达中给出 .NET 10 的采用建议（Adopt / Trial / Assess / Hold）。
```

### 目标受众 (target_audience)

```text
CTO、平台工程团队、各业务线技术负责人
```

### 预期输出

```json
{
  "executive_summary": ".NET 10 在企业团队中关注度高，NativeAOT 和容器优化是主要驱动力，但第三方库兼容性（尤其是 Oracle EF Core）和 .NET Framework 迁移路径是核心阻碍。建议评级：Trial（金融/电商）、Assess（政府）。",

  "key_pain_points": [
    {
      "issue": "第三方 NuGet 包未提供 .NET 10 兼容版本",
      "evidence": "金融团队 32 个依赖包无 .NET 10 版本，合规要求 SBOM",
      "severity": "high"
    },
    {
      "issue": ".NET Framework 4.8 迁移路径不明确",
      "evidence": "政府团队刚启动评估，需 Framework→10 直迁可行性分析",
      "severity": "high"
    },
    {
      "issue": "CI/CD runner 镜像滞后",
      "evidence": "电商团队的 GitHub Actions/Azure DevOps 无预装 .NET 10 SDK 镜像",
      "severity": "medium"
    },
    {
      "issue": "培训成本与 NativeAOT 调试学习曲线",
      "evidence": "所有团队提到 source generators 和 NativeAOT 调试技能缺口",
      "severity": "medium"
    }
  ],

  "stakeholder_needs": [
    {
      "group": "金融团队",
      "need": "Oracle EF Core + gRPC 拦截器的 .NET 10 兼容保证",
      "urgency": "high"
    },
    {
      "group": "电商团队",
      "need": "Minimal API 性能基准 + 容器镜像体积优化数据",
      "urgency": "medium"
    },
    {
      "group": "政府团队",
      "need": "Framework→10 迁移 playbook + 试点绿色通道",
      "urgency": "high"
    },
    {
      "group": "平台工程",
      "need": "CI/CD runner 镜像更新 + SBOM 合规工具链",
      "urgency": "medium"
    }
  ],

  "opportunity_map": [
    {
      "opportunity": "NativeAOT 低延迟交易系统 PoC",
      "feasibility": "medium",
      "impact": "high"
    },
    {
      "opportunity": "容器镜像体积优化（Minimal API + NativeAOT）",
      "feasibility": "high",
      "impact": "medium"
    },
    {
      "opportunity": "内部 .NET 10 迁移 playbook（Framework + 现代 .NET 双路径）",
      "feasibility": "high",
      "impact": "high"
    },
    {
      "opportunity": "OpenAPI 原生支持替代 Swashbuckle 的标准化",
      "feasibility": "high",
      "impact": "medium"
    }
  ],

  "risks_and_cautions": [
    {
      "risk": "Oracle 等关键供应商未在 .NET 10 GA 前提供兼容版本",
      "likelihood": "medium",
      "mitigation": "提前联系供应商，获取支持时间线承诺"
    },
    {
      "risk": "Framework 迁移窗口过长导致技术雷达建议过期",
      "likelihood": "medium",
      "mitigation": "分阶段迁移：先现代化到 .NET 8 LTS，再评估 10"
    },
    {
      "risk": "过早推荐 Adopt 导致团队在 GA 前投产",
      "likelihood": "low",
      "mitigation": "严格遵守 Trial→Assess 评级，GA+3 个月后再考虑 Adopt"
    }
  ],

  "follow_up_questions": [
    "Oracle 是否有公开的 .NET 10 EF Core 支持路线图？",
    "金融团队的合规审计是否接受预览版依赖的例外审批？",
    "政府团队的 Framework 迁移是否可以利用 .NET Upgrade Assistant 加速？",
    "各团队是否愿意在非关键系统上运行 .NET 10 Preview 试点？",
    "是否需要与 Microsoft 客户团队安排 .NET 10 技术深度交流会？"
  ],

  "missing_information": [
    "各团队的具体应用规模和性能基线数据",
    "Oracle EF Core 的官方 .NET 10 支持声明",
    "政府团队 Framework 应用的代码行数和依赖图"
  ],

  "attribution_note": "以上发现全部基于 2026-06 对 3 个团队 12 名开发者的访谈记录。严重度和紧急性评级为基于访谈上下文的分析推断，非团队直接声明。"
}
```

### MetaSkill 执行路径

```
用户输入 "community research insight .NET 10 adoption"
  → 触发词匹配: "community research" + "research insight"
    → collect (user_input)
      → 用户粘贴访谈记录 + 项目上下文 + 受众
        → analyze (llm_chat)
          → 提取 pain_points / needs / opportunities 的 JSON
            → draft (llm_chat)
              → 生成 6 节完整简报 JSON
                → validate (llm_chat)
                  → PASS → preview (llm_chat)
                    → Markdown 渲染 → 显示给用户等待审批
                      → review (user_input)
                        → "approve" / "revise" / "reject"
                          → final_response (llm_chat)
                  → REVISE → validation_revise (llm_chat)
```

### 技术雷达评级映射

| 评级 | 条件 | 本案例 |
| --- | --- | --- |
| **Adopt** | 生态成熟、GA+6 个月、无关键阻碍 | — |
| **Trial** | 有明确收益但生态未完全成熟 | 金融、电商 |
| **Assess** | 需要进一步调研或等待关键依赖 | 政府 |
| **Hold** | 存在阻塞性风险 | — |
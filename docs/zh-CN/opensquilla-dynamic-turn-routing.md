# OpenSquilla Dynamic Turn Routing

英文版： [opensquilla-dynamic-turn-routing.md](../opensquilla-dynamic-turn-routing.md)

OpenClaw.NET 现在提供了一个可选的、参考 OpenSquilla 思路实现的动态回合路由层。它可以在每个用户回合开始时，将请求归类到 `T0` 到 `T3` 之一，然后把该决策投影到 OpenClaw 现有的模型档位选择、工具过滤和系统提示词机制上。

这不是一套独立的新路由栈。它复用了现有的会话级路由字段：

- `Session.ModelProfileId`
- `Session.PreferredModelTags`
- `Session.RouteAllowedTools`
- `Session.SystemPromptOverride`
- `Session.RouteModelTier`
- `Session.RouteReason`

因此，这个能力更像是“每个回合的轻量决策层”，而不是替换 OpenClaw 原有模型选择逻辑。

## 它做什么（What It Does）

当所有回合都把完整系统提示词、完整工具声明和同一套昂贵模型配置发给上游模型时，简单请求和复杂请求会走同样的成本路径。

动态回合路由的目标是：

- 对简单回合使用更轻量的模型档位
- 对只读或窄范围任务只暴露必要工具
- 用更短的路由指令（route instruction）收紧提示词体积
- 在不重写 OpenClaw 主执行栈的前提下，为未来本地分类器留出稳定接缝

在每个回合开始时，运行时会先解析一份 `TurnRoutingDecision`，其中包含：

- tier（如 `T0` 到 `T3`）
- 可选的模型档位覆盖
- 可选的工具允许列表（allowlist）
- 可选的档位标签偏好
- 可选的路由指令（route instruction）后缀
- 机器可读原因（machine-readable reason）

这份决策只对当前回合生效，回合结束后会恢复原始会话路由状态。

有一个字段是刻意保留的 sticky 语义：`Session.RouteModelTier` 会跨回合保留，以便 `EnableStickyTier` 策略在 Native/MAF 路径上保持一致。

## 架构形态（Architecture Shape）

实现分成三层。

### 1. Core 配置契约

`OpenClaw.Core` 只保存配置和校验模型：

- `DynamicTurnRoutingConfig`
- `DynamicTurnRoutingAssetsConfig`
- `DynamicTurnRoutingPolicyConfig`
- `DynamicTurnRoutingTierMap`
- `DynamicTurnRoutingTierTarget`
- `ResolvedDynamicTurnRoutingConfig`
- `ResolvedDynamicTurnRoutingAssets`

这样可以保证 `OpenClaw.Core` 不直接依赖 ONNX 或 tokenizer 运行时。

### 2. 运行时抽象层

`OpenClaw.Agent` 只定义运行时抽象：

- `ITurnRoutingPolicy`
- `TurnRoutingRequest`
- `TurnRoutingDecision`
- `NoopTurnRoutingPolicy`

native `AgentRuntime` 和 MAF `MafAgentRuntime` 都基于这个抽象，在实际模型调用前应用 turn-scoped（按回合作用域）覆盖，并在回合完成后恢复原始会话状态。

### 3. 可选 ONNX 实现层

`OpenClaw.Routing.Onnx` 是可选实现边界。只有当 `OpenClaw:DynamicTurnRouting:Enabled=true` 时，网关才会组合这层实现。

这保持了仓库当前的边界原则：

- `OpenClaw.Core` 保持 AOT-friendly
- `OpenClaw.Agent` 只依赖接口，不依赖 ONNX 细节
- `OpenClaw.Gateway` 决定是否启用 ONNX 路由实现

## 配置（Configuration）

配置入口是 `OpenClaw:DynamicTurnRouting`。

OpenClaw 当前只支持 modern 配置形态：通过 `Assets` 和 `Policy` 提供路由参数。

如果配置了 `BundlePath`，网关会先加载 OpenSquilla 风格 bundle，再把 bundle 值与显式 `Assets` / `Policy` 覆盖合并为一份内部归一化路由模型（resolved routing model）。

推荐的现代配置形态：

```json
{
  "OpenClaw": {
    "DynamicTurnRouting": {
      "Enabled": true,
      "BundlePath": "models/routing/opensquilla-v4",
      "Assets": {
        "ClassifierModelPath": "models/routing/override/classifier.onnx"
      },
      "Policy": {
        "EnableStickyTier": true,
        "EnableMarginUpgrade": true,
        "EnableUnderRoutingSafety": true
      }
    }
  }
}
```

`Policy.Tiers` 是唯一受支持的 tier 映射位置。

## Tier 映射模型（Tier Mapping Model）

每个 tier target（分层目标）当前可控制以下几个维度：

- `ModelProfileId`：当前回合使用哪个现有模型档位
- `AllowedTools`：当前回合暴露哪些工具
- `PreferredTags`：当前回合偏好哪些档位标签
- `PromptMode` / `DisableTools`：为当前回合追加路由指令（route instruction）

当前内置的路由指令（route instruction）保持最小化：

- `minimal`：`Respond directly with minimal reasoning.`
- `compact`：`Keep the reply short and skip planning.`
- `DisableTools=true`：`Respond directly and do not call tools.`

## 校验规则

启动期校验现在只覆盖 modern（现代）路由（routing）配置形态。

当前会快速失败（fail-fast）的主要场景包括：

- 配置的 tier 指向了不存在于 `OpenClaw:Models:Profiles` 的模型档位
- 分类器（classifier）资产存在但向量化模型/分词器（embedding / tokenizer）配置不完整

对于 bundle 资产缺失或解析失败，当前契约是允许启动，运行时按回退（fallback）语义降级到 `T2` 并记录机器可读原因（machine-readable reason），而不是在启动阶段中断进程。

## 当前运行时行为（Current Runtime Behavior）

当前实现可以分成两部分理解：

1. turn-scoped（按回合作用域）路由契约与运行时（runtime）接线已实现并覆盖测试
2. 可选 ONNX 路由策略在资产存在时会执行真实本地推理路径

这套能力目前已经完成：

- 动态回合路由的配置契约
- tier 到模型档位的启动期校验
- native `AgentRuntime` 的 turn-scoped（按回合作用域）路由接线
- `MafAgentRuntime` 的 turn-scoped（按回合作用域）路由接线
- 路由状态（route state）的应用/恢复语义
- Gateway 的依赖注入（DI）组合
- 回退（fallback）到 `T2` 的基础行为

也就是说，运行时接缝、配置入口、恢复语义和测试覆盖都已经建立好了。

但这仍然只是一个实验性路由能力，不是已经调优完成的生产级分类器。运行时接线是稳定的，但提示词特征和阈值默认值都刻意保持保守，不能据此宣称已经达到 OpenSquilla 等价精度。

## 当前 ONNX 能力边界

当前生产构造下的 `OnnxTurnRoutingPolicy` 已经不再是纯脚手架（scaffold），而是会在资产存在时真正执行一条本地推理流水线。

目前已经完成的 ONNX 侧能力包括：

- `LocalOnnxEmbeddingGenerator` 会加载 Hugging Face 风格的 BPE `tokenizer.json`
- 它会对用户回合文本做分词编码（tokenization），调用向量化 ONNX 模型（embedding），并把输出整理成固定维度向量
- `PromptFeatureExtractor` 会把提示词（prompt）的轻量手工特征和向量化结果拼成分类器输入特征
- `OnnxTurnRoutingPolicy` 会把该特征向量送入分类器 ONNX 模型（classifier），并把预测结果映射回配置里的 `T0` 到 `T3`

当前的回退（fallback）语义仍然保留：

- 如果缺少模型文件，当前行为会回退到 `T2`，而不是中断整条回合流程
- 如果本地推理阶段抛异常，也会回退到 `T2`，并记录机器可读原因（machine-readable reason）
- 分类器（classifier）输出既支持整数标签（label），也支持浮点 logits，后者会取 `argmax`

因此当前实现状态更准确的说法是：

- 运行时接线（runtime wiring）：已完成
- 运维方配置面（operator config surface）：已完成
- 本地向量化 + 分类器推理路径（classifier inference path）：已完成
- 回退（fallback）与 tier 映射契约（tier mapping contract）：已完成

仍然存在的限制主要是：

- 分词器加载器（tokenizer loader）当前只支持 Hugging Face 风格的 BPE `tokenizer.json`，并不覆盖所有 tokenizer 家族
- 向量化模型（embedding）侧目前按常见 transformer 输入名和常见输出形态适配，不是任意 ONNX embedding 图都能直接套上
- 提示词（prompt）特征和分类阈值仍然比较保守，不能直接宣称已经达到 OpenSquilla 等价精度

## Native 与 MAF 语义（Native And MAF Semantics）

动态回合路由现在同时覆盖两条编排器（orchestrator）路径：

- native `AgentRuntime`
- MAF `MafAgentRuntime`

这一点很重要，因为 `MafAgentRuntime` 不是通过 `NativeAgentRuntimeFactory` 构造的独立路径。如果只给 native 运行时（runtime）接线，MAF 路径会漏掉动态回合路由。

目前这条缺口已经补上，并且也加入了测试。

MAF 还有一个额外语义：如果路由决策（routing decision）返回的是空 `AllowedTools`，运行时不会覆盖用户手工预先设置的 `Session.RouteAllowedTools`。这样可以避免把原有的手工路由允许列表（allowlist）语义冲掉。

## 与 OpenSquilla `squilla-router.md` 的对齐情况（2026-06）

当前实现与 OpenSquilla 的对齐可以分成两层看：

### 1. 已对齐：架构与路由契约

- 都采用“每回合先分类，再投影到模型和策略”的思路，而不是替换主执行栈
- OpenClaw 的 `TurnRoutingDecision` 已覆盖 OpenSquilla 文档里提到的核心决策面：tier、模型覆盖、工具范围、推理强度、响应策略、路由原因
- Gateway 侧采用配置驱动 + 可选组合：关闭时 `NoopTurnRoutingPolicy`，开启时组合 ONNX 路由策略
- native 与 MAF 两条 orchestrator 路径都已接线动态回合路由

### 2. 未完全对齐：模型资产格式与推理流水线

OpenSquilla 当前发布的 `squilla_router` 模型目录（`src/opensquilla/squilla_router/models/v4.2_phase3_inference`）不能直接当作 OpenClaw 的 bundle 使用，原因包括：

- **资产命名不一致**：OpenClaw bundle loader 默认查找 `manifest.json`、`classifier.onnx`、`embeddings.onnx`、`tokenizer.json`、`runtime-config.json`；OpenSquilla v4.2 使用的是 `artifact_manifest.json`、`inference_manifest.json`、`bge_onnx/model.onnx`、`mlp/model.onnx`、`lgbm_main.bin` 等
- **tokenizer 家族不一致**：OpenClaw 当前 tokenizer loader 仅支持 Hugging Face 风格 BPE `tokenizer.json`；OpenSquilla v4.2 的 `bge_onnx/tokenizer.json` 是 `WordPiece`
- **分类器形态不一致**：OpenClaw 当前是单 classifier ONNX 输入；OpenSquilla v4.2 是多头融合（LightGBM + MLP + postprocess）
- **tier 命名代际差异**：OpenSquilla 新规范以 `c0..c3` 为 canonical tier（保留 `t0..t3` 兼容别名）；OpenClaw 当前路由标签是 `T0..T3`

结论：在“策略设计”和“运行时接缝”层面对齐度高；在“模型包即插即用”层面对齐度低，需要适配层。

## 将 OpenSquilla 模型用在 OpenClaw 的方案

推荐按两阶段推进：先打通可用，再追求高保真。

### 阶段 A：OpenClaw 兼容产物导出（推荐先做）

目标：不重写 OpenClaw 主路由器，仅通过兼容资产接入 OpenSquilla 训练成果。

1. 在 OpenSquilla 侧新增导出脚本，把 v4.2 资产导出为 OpenClaw 兼容目录：
  - `classifier.onnx`
  - `embeddings.onnx`
  - `tokenizer.json`（优先导出 BPE 版本）
  - `runtime-config.json`
  - `manifest.json`
2. 导出时显式固化 tier 映射：`c0/c1/c2/c3 -> T0/T1/T2/T3`
3. 在 OpenClaw 用 `OpenClaw:DynamicTurnRouting:BundlePath` 指向该兼容目录，并用 `Policy.Tiers` 映射到 OpenClaw 的 `Models.Profiles`
4. 先用离线评测验证 `fallback` 比例、tier 分布和成本分布，再放量

这是最小改动、最快可上线的路径。

### 阶段 B：专用高保真策略适配（可选）

目标：尽量保留 OpenSquilla v4.2 的多头融合和后处理语义。

1. 新建可选实现边界（例如 `OpenClaw.Routing.OpenSquillaV4`）
2. 解析 OpenSquilla 原生 bundle（`inference_manifest.json`、`router.runtime.yaml`、`bge_onnx`、`mlp`、`lgbm_*`）
3. 在该实现里复刻 v4.2 的融合与后处理，再映射到 `TurnRoutingDecision`
4. 保持 `OpenClaw.Core` 和 `OpenClaw.Agent` 仅依赖抽象，继续由 Gateway 决定是否启用

该路径工程量更大，但行为最接近 OpenSquilla 原生 router。

## 当前接入风险与建议

- **风险 1：WordPiece tokenizer 不兼容**。若直接引用 OpenSquilla v4.2 tokenizer，OpenClaw 当前会进入 `classifier_unavailable` 并回退 `T2`
- **风险 2：bundle 命名不匹配**。即便模型文件存在，也可能因路径约定不一致导致未被加载
- **风险 3：native/MAF 行为差异**。native 路径目前比 MAF 路径消费了更多路由字段（例如 direct fallback、reasoning、response policy）

建议优先级：

1. 先做阶段 A，快速打通兼容资产路径
2. 再补 tokenizer 家族兼容（或在导出阶段统一转 BPE）
3. 最后评估是否值得进入阶段 B 做高保真策略适配

## 阶段 A 可执行接入清单（建议直接按此推进）

下面给出一份可落地的最小闭环，目标是在不改 OpenClaw 主执行栈的前提下，把 OpenSquilla 训练产物转换成 OpenClaw 可消费的 bundle。

### A1. OpenSquilla 导出产物规范（Export Contract）

导出目录建议结构：

```text
<export-root>/
  manifest.json
  classifier.onnx
  embeddings.onnx
  tokenizer.json
  runtime-config.json
```

字段约定建议：

- `manifest.json`：至少包含 `classifierModelPath`、`embeddingModelPath`、`tokenizerPath`、`runtimeConfigPath`
- `runtime-config.json`：至少包含 embedding 维度字段（`dimensions` 或 `embeddingDimensions`）
- `classifier.onnx`：输出 4 类（对应 `T0/T1/T2/T3`）
- `tokenizer.json`：建议导出 BPE 版本；如果只能提供 WordPiece，则需要同步扩展 OpenClaw tokenizer loader

tier 映射建议固定为：

- `c0 -> T0`
- `c1 -> T1`
- `c2 -> T2`
- `c3 -> T3`

### A2. OpenClaw 配置模板（可直接套用）

```json
{
  "OpenClaw": {
    "DynamicTurnRouting": {
      "Enabled": true,
      "BundlePath": "models/routing/opensquilla-v4-compat",
      "Policy": {
        "EnableStickyTier": true,
        "EnableMarginUpgrade": true,
        "EnableUnderRoutingSafety": true,
        "Tiers": {
          "T0": {
            "ModelProfileId": "local-freeform",
            "DisableTools": true,
            "PromptMode": "minimal"
          },
          "T1": {
            "ModelProfileId": "mini-readonly",
            "AllowedTools": ["read_file", "grep_search"],
            "PromptMode": "compact"
          },
          "T2": {
            "ModelProfileId": "frontier-tools"
          },
          "T3": {
            "ModelProfileId": "frontier-deep"
          }
        }
      }
    }
  }
}
```

说明：

- `BundlePath` 指向导出的兼容目录
- `Policy.Tiers` 必须引用已存在于 `OpenClaw:Models:Profiles` 的 profile id
- 先不要在第一轮引入过多策略开关，优先确认可加载、可路由、可恢复

### A3. 最小验收用例（Definition of Done）

建议至少覆盖以下 8 项：

1. 启动时无 `DynamicTurnRouting` 配置校验错误
2. bundle 资产存在时，路由不持续落入 `classifier_unavailable`
3. 缺资产时可回退 `T2` 且服务可用
4. `T0/T1/T2/T3` 能映射到预期 `ModelProfileId`
5. 工具收敛生效：`DisableTools` 与 `AllowedTools` 行为正确
6. `SystemPromptSuffix` 正确拼接，回合结束后会话状态恢复
7. native 与 MAF 两条路径均可工作（至少一条 smoke case + 一条 fallback case）
8. 离线样本评测里 tier 分布与成本分布在可接受范围内

### A3.1. 阶段 A 离线样本验证记录

为了补足“阶段 A 先打通可用”的证据，这里保留两版 10 样本离线快照，方便后续回溯：

- [balanced 10-sample report](../../artifacts/testing/phase-a-offline-validation-10sample-report.json)：5 个 simple + 5 个 complex，gold / predicted 均为 `T0:5`、`T2:5`，fallback 为 0
- [T1-inclusive 10-sample report](../../artifacts/testing/phase-a-offline-validation-10sample-report-t1.json)：3 个 `T0`、3 个 `T1`、2 个 `T2`、2 个 `T3`，gold / predicted 完全一致，fallback 为 0

说明：这两份报告都基于代码层的 heuristic 路由规则，不是在当前会话里实际触发 ONNX fallback 的实测结果，因此 fallback 只能视为“未触发”，不能解读为“ONNX fallback 已验证通过”。

### A4. 常见历史风险与排查建议

- 信号：路由长期 `classifier_unavailable`
  - 优先检查 `BundlePath` 下文件命名是否符合 OpenClaw 约定
- 信号：推理阶段异常后频繁回退 `T2`
  - 检查 embedding 输入名/输出 shape 是否与当前 `LocalOnnxEmbeddingGenerator` 兼容
- 信号：加载 tokenizer 时报不支持
  - 当前多见于 WordPiece；可选方案是导出 BPE tokenizer，或扩展 OpenClaw loader
- 历史风险/排查建议：MAF 行为与 native 不一致
  - 如果仍观察到该现象，先确认是否依赖了当前仅 native 消费的扩展路由字段

### A5. 建议实施节奏

1. 先打通“可加载 + 可路由 + 可回退”三件套
2. 再做“分布调优”（阈值与 tier 映射）
3. 最后再考虑是否进入阶段 B 的高保真适配

### A6. 导出文件草案与联调指令样例

下面给出可直接落地的文件草案，便于 OpenSquilla 导出端和 OpenClaw 消费端对齐。

`manifest.json` 草案：

```json
{
  "schemaVersion": 1,
  "bundleName": "opensquilla-v4-compat",
  "classifierModelPath": "classifier.onnx",
  "embeddingModelPath": "embeddings.onnx",
  "tokenizerPath": "tokenizer.json",
  "runtimeConfigPath": "runtime-config.json",
  "tierMap": {
    "c0": "T0",
    "c1": "T1",
    "c2": "T2",
    "c3": "T3"
  }
}
```

`runtime-config.json` 草案：

```json
{
  "schemaVersion": 1,
  "embeddingDimensions": 384,
  "classifier": {
    "numClasses": 4,
    "classLabels": ["T0", "T1", "T2", "T3"]
  },
  "notes": {
    "tokenizerFamily": "BPE",
    "source": "opensquilla-v4.2-phase3"
  }
}
```

导出端最小检查项：

1. `classifier.onnx` 的输出类别数为 4
2. `embeddings.onnx` 与 `tokenizer.json` 可联动生成固定维度向量
3. `manifest.json` 路径字段全部为相对路径且文件存在
4. `runtime-config.json` 的维度字段与 embedding 实际输出一致

联调指令样例（按你的本地路径替换）：

```powershell
# 1) 准备兼容 bundle（示例路径）
$bundle = "E:\GitHub\openclaw.net\models\routing\opensquilla-v4-compat"

# 2) 快速检查关键文件是否齐全
Get-ChildItem $bundle -File | Select-Object Name

# 3) 启动前检查 JSON 可解析
Get-Content "$bundle\manifest.json" -Raw | ConvertFrom-Json | Out-Null
Get-Content "$bundle\runtime-config.json" -Raw | ConvertFrom-Json | Out-Null
```

验收通过门槛建议：

- 10 条简单样本中 `T0/T1` 占比显著高于未启用路由时
- 10 条复杂样本中 `T2/T3` 占比不低于当前人工预期
- 不出现持续性 `classifier_unavailable`（偶发异常回退允许，但要可观测）
- 回合结束后会话路由状态恢复符合预期（含工具 allowlist 与提示词后缀）

## 运维建议（Operator Guidance）

适合：

- 需要为未来本地分类器预留稳定接缝
- 想把简单回合映射到更便宜的模型档位
- 想对读文件、只读总结这类回合缩小工具面
- 想在不改主执行栈的情况下实验路由策略（routing policy）

暂时不适合把它当成：

- 已成熟的 OpenSquilla 等价本地智能分类系统
- 已完成调优的生产级 ONNX 分类器流水线（classifier pipeline）

如果你需要更强的能力声明，先运行离线路由评测基线，再用样本集对比报告确认没有回退后再扩大使用范围。

## 相关文档

- [LOCAL_MODELS.md](../LOCAL_MODELS.md)：本地模型和本地资产说明
- [ARCHITECTURE_BOUNDARIES.md](../ARCHITECTURE_BOUNDARIES.md)：为什么 ONNX 路由（routing）不进入 `OpenClaw.Core`
- [MODEL_PROFILES.md](../MODEL_PROFILES.md)：被路由的模型档位（routed profile）如何继续走现有模型选择策略
- [integrations/microsoft-agent-framework.md](../integrations/microsoft-agent-framework.md)：MAF 运行时（runtime）路径说明

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

- `OpenClaw.Core` 保持 AOT 友好
- `OpenClaw.Agent` 只依赖接口，不依赖 ONNX 细节
- `OpenClaw.Gateway` 决定是否启用 ONNX 路由实现

## 配置（Configuration）

配置入口是 `OpenClaw:DynamicTurnRouting`。

OpenClaw 当前推荐使用现代配置形态：通过 `Assets` 和 `Policy` 提供路由参数。

如果配置了 `BundlePath`，网关会先加载 OpenSquilla 风格 bundle，再把 bundle 值与显式 `Assets` / `Policy` 覆盖合并为一份内部归一化路由模型（resolved routing model）。

推荐的现代配置形态：

```json
{
  "OpenClaw": {
    "DynamicTurnRouting": {
      "Enabled": true,
      "BundlePath": "models/routing/opensquilla-v4-compat",
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

兼容模式仍可用：当不使用 `BundlePath` 时，可以继续直接配置 `Assets.ClassifierModelPath`、`Assets.EmbeddingModelPath`、`Assets.TokenizerPath`。

## CLI 命令面（CLI Surface）

Routing CLI 详细命令见 [../cli/routing.md](../cli/routing.md)。

动态路由仍是配置驱动（`OpenClaw:DynamicTurnRouting`），CLI 命令只是对同一配置面的管理入口，不会引入第二套路由引擎。

推荐的现代路由形态是 `BundlePath` + `Policy.Tiers`。

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

启动期校验现在只覆盖现代路由配置形态。

当前会快速失败（fail-fast）的主要场景包括：

- 配置的 tier 指向了不存在于 `OpenClaw:Models:Profiles` 的模型档位
- 分类器（classifier）资产存在但向量化模型/分词器（embedding / tokenizer）配置不完整

对于 bundle 资产缺失或解析失败，当前契约是允许启动：运行时按回退语义降级到 `T2`，并记录机器可读原因（`machine-readable reason`），而不是在启动阶段中断进程。

## 当前运行时行为（Current Runtime Behavior）

当前实现可以分成两部分理解：

1. turn-scoped（按回合作用域）路由契约与运行时接线已实现并覆盖测试
2. 可选 ONNX 路由策略在资产存在时会执行真实本地推理路径

这套能力目前已经完成：

- 动态回合路由的配置契约
- tier 到模型档位的启动期校验
- native `AgentRuntime` 的 turn-scoped（按回合作用域）路由接线
- `MafAgentRuntime` 的 turn-scoped（按回合作用域）路由接线
- 路由状态（route state）的应用/恢复语义
- Gateway 的依赖注入（DI）组合
- 回退到 `T2` 的基础行为

也就是说，运行时接缝、配置入口、恢复语义和测试覆盖都已经建立好了。

但这仍然只是一个实验性路由能力，不是已经调优完成的生产级分类器。运行时接线是稳定的，但提示词特征和阈值默认值都刻意保持保守，不能据此宣称已经达到 OpenSquilla 等价精度。

当前仍应判定为实验态，主要依据：

- 现有质量基线距离常见生产分类器门槛仍有差距。可参考 [turn-routing-quality.grid-report.json](../testing/turn-routing-quality.grid-report.json)（当前网格搜索结果里 `best.MacroF1` 约为 `0.65`）。
- 两份 10 样本离线快照可作为冒烟证据，但不足以支撑生产级质量声明，且不能单独证明 ONNX fallback 执行链路。可参考 [phase-a-offline-validation-10sample-report.json](../testing/phase-a-offline-validation-10sample-report.json) 与 [phase-a-offline-validation-10sample-report-t1.json](../testing/phase-a-offline-validation-10sample-report-t1.json)。
- tokenizer 家族兼容性与 bundle 形态互操作仍需在目标环境中做运维侧实证验证后再放量。

是否可升格为生产态，请以本文 Stage B 启动门禁表作为最低准入标准。

## 当前 ONNX 能力边界

当前生产构造下的 `OnnxTurnRoutingPolicy` 已经不再是纯脚手架（scaffold），而是会在资产存在时真正执行一条本地推理流水线。

目前已经完成的 ONNX 侧能力包括：

- `LocalOnnxEmbeddingGenerator` 会加载 Hugging Face 风格的 BPE `tokenizer.json`
- 它会对用户回合文本做分词编码（tokenization），调用向量化 ONNX 模型（embedding），并把输出整理成固定维度向量
- `PromptFeatureExtractor` 会把提示词（prompt）的轻量手工特征和向量化结果拼成分类器输入特征
- `OnnxTurnRoutingPolicy` 会把该特征向量送入分类器 ONNX 模型（classifier），并把预测结果映射回配置里的 `T0` 到 `T3`

当前的回退语义仍然保留：

- 如果缺少模型文件，当前行为会回退到 `T2`，而不是中断整条回合流程
- 如果本地推理阶段抛异常，也会回退到 `T2`，并记录机器可读原因（`machine-readable reason`）
- 分类器（classifier）输出既支持整数标签（label），也支持浮点 logits，后者会取 `argmax`

因此当前实现状态更准确的说法是：

- 运行时接线（runtime wiring）：已完成
- 运维方配置面（operator config surface）：已完成
- 本地向量化 + 分类器推理路径（classifier inference path）：已完成
- 回退与 tier 映射契约（tier mapping contract）：已完成

仍然存在的限制主要是：

- 分词器加载器（tokenizer loader）现在已经支持 Hugging Face 风格 `tokenizer.json` 的 BPE 与 WordPiece 模型，但仍不覆盖所有 tokenizer 家族，也不保证兼容所有 pre-tokenizer 形态
- 向量化模型（embedding）侧仍然假设使用常见 transformer 输入名，且输出要么是直接向量（rank-1 / rank-2），要么是可做 mean pooling 的 rank-3 hidden-state 张量；任意 ONNX embedding 图并不能保证无改动接入
- 分类器路径仍假设 4 档输出契约（`T0`..`T3`）以及与训练产物一致的特征向量维度；如果 classifier 模型与 feature extractor 不是同一训练批次，运行时会失败并回退
- 提示词（prompt）特征、后处理规则和阈值默认值仍然偏保守，不能直接宣称已经达到 OpenSquilla 等价精度

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

OpenSquilla 当前发布的 `squilla_router` 模型目录（`src/opensquilla/squilla_router/models/v4.2_phase3_inference`）仍然不能直接当作 OpenClaw 路由 bundle 使用，但真实差距已经比早期文档写得更窄。

按当前实现重新评估后，主要差异是：

- **bundle 契约不一致现在是“部分不对齐”，不是“完全不能识别”**：OpenClaw 的 bundle loader 仍然优先使用兼容目录形态（`manifest.json`、`classifier.onnx`、`embeddings.onnx`、`tokenizer.json`、`runtime-config.json`），但它已经能从嵌套 manifest 元数据里解析资产路径和 embedding 维度。当前真正的问题是 OpenSquilla v4.2 发布的是自己的原生目录结构（`artifact_manifest.json`、`inference_manifest.json`、`bge_onnx/model.onnx`、`mlp/model.onnx`、`lgbm_main.bin`），而不是 OpenClaw 可直接消费的 compat bundle。因此当前的接入方案是先在 OpenSquilla 侧做一次兼容导出，产出一个符合 OpenClaw bundle 规范的目录（例如 `models/routing/opensquilla-v4-compat`），而不是直接指向 v4.2 的原生目录。
- **tokenizer 不再是 WordPiece 一票否决**：OpenClaw 当前 tokenizer loader 已支持 Hugging Face 风格 `tokenizer.json` 的 BPE 和 WordPiece。剩下的不兼容点在于它还不覆盖所有 tokenizer 家族和所有 pre-tokenizer 形态，因此 OpenSquilla 的 tokenizer 资产仍需要针对目标 bundle 做联调验证。
- **分类器推理流水线仍然没有对齐**：OpenClaw 当前 ONNX 路径仍假设“一套 embedding + 一个 4 类 ONNX classifier”，而 OpenSquilla v4.2 是包含 MLP、LightGBM 和后处理融合的原生多阶段 router。因此当前 `OnnxTurnRoutingPolicy` 还不能无适配直接消费 v4.2 原生推理产物。
- **tier 词汇差异主要是命名映射，但仍需要适配契约**：OpenSquilla 使用 `c0..c3`（并保留 `t0..t3` 兼容别名），OpenClaw 投影的是 `T0..T3`。这在 compat 导出里很好处理，但本质上仍是一次显式转换，而不是原生即插即用。

结论：在“策略设计”和“运行时接缝”层面对齐度高，bundle loader 也比早期判断更宽松；但在“原生 OpenSquilla v4.2 模型包直接互操作”这件事上，仍然需要 compat 导出或专用适配层。

## 将 OpenSquilla 模型用在 OpenClaw 的方案

按当前仓库状态重新评估后，这一节更准确的表述不应是“先从零设计阶段 A”，而应是“两轨推进”：先固化已经存在的 compat bundle 参考实现，再按需决定是否进入高保真适配。

### 阶段 A：固化现有 compat bundle（当前已具备参考实现）

目标：不重写 OpenClaw 主路由器，直接复用当前仓库里已经存在的兼容资产形态，把它从“参考产物”推进到“可重复导出、可验收、可运维”的标准接入路径。

当前仓库已经有一份可直接检查的参考 bundle：`models/routing/opensquilla-v4-compat/`。它已经包含：

- `classifier.onnx`
- `embeddings.onnx`
- `tokenizer.json`
- `runtime-config.json`
- `manifest.json`

而且这份参考 bundle 不是停留在理论草案：

- `manifest.json` 已显式固化 tier 映射：`c0/c1/c2/c3 -> T0/T1/T2/T3`
- `runtime-config.json` 已声明当前参考 embedding 维度为 `512`
- `tokenizer.json` 当前实际是 `WordPiece` + `BertPreTokenizer` 形态，而这正是当前 loader 已支持、并且已有测试覆盖的组合

因此阶段 A 现在更合理的推进方式是：

1. 先把仓库内的 `models/routing/opensquilla-v4-compat` 视为基线参考产物，而不是重新定义一套抽象目录规范
2. 在 OpenSquilla 侧补一个可重复导出步骤，稳定地产出与这份参考 bundle 等价的目录形态
3. 在 OpenClaw 中继续使用 `OpenClaw:DynamicTurnRouting:BundlePath` 指向该 compat 目录，并用 `Policy.Tiers` 映射到 `Models.Profiles`
4. 先做离线评测、fallback 统计、tier 分布和成本分布验证，再决定是否放量

这是当前最短路径，因为“兼容目录长什么样”这件事已经在仓库里有现成答案，剩下的工作重点是自动化导出、验收和运维固化。

### 阶段 B：专用高保真策略适配（可选）

目标：尽量保留 OpenSquilla v4.2 的多头融合和后处理语义。

1. 新建可选实现边界（例如 `OpenClaw.Routing.OpenSquillaV4`）
2. 解析 OpenSquilla 原生 bundle（`inference_manifest.json`、`router.runtime.yaml`、`bge_onnx`、`mlp`、`lgbm_*`）
3. 在该实现里复刻 v4.2 的融合与后处理，再映射到 `TurnRoutingDecision`
4. 保持 `OpenClaw.Core` 和 `OpenClaw.Agent` 仅依赖抽象，继续由 Gateway 决定是否启用

该路径工程量更大，但行为最接近 OpenSquilla 原生 router。

## 当前接入风险与建议

- **风险 1：OpenSquilla 原生 bundle 目录形态仍不符合 OpenClaw compat 契约**。即便资产存在，也可能因为 v4.2 发布目录不是 OpenClaw-ready bundle 而无法直接组合。
- **风险 2：tokenizer 兼容是“具体资产级别”的问题，不是“WordPiece 必挂”**。WordPiece 已不再自动不兼容，但具体 tokenizer / pre-tokenizer 变体仍需要按目标 bundle 验证。
- **风险 3：分类器流水线不匹配**。即便 tokenizer 和 embedding 能加载，OpenSquilla v4.2 的多头融合路径也不能直接映射到当前单 classifier ONNX 运行时。
- **风险 4：native/MAF 行为差异**。native 路径目前比 MAF 路径消费了更多路由字段（例如 direct fallback、reasoning、response policy）。

建议优先级：

1. 先把仓库内现有的 `models/routing/opensquilla-v4-compat` 当成 baseline，验证端到端加载、路由和 fallback 行为。
2. 再把这份 baseline bundle 固化成 OpenSquilla 侧的可重复导出流程，并补齐 checksum / 元信息生成。
3. 无论导出的是 BPE 还是 WordPiece，都先在目标 bundle 上完成 tokenizer 联调验证，再扩大流量。
4. 最后再评估是否值得进入阶段 B 做高保真策略适配。

## 阶段 A 可执行接入清单（建议直接按此推进）

下面给出的不是一份纯理论草案，而是基于当前仓库内 `models/routing/opensquilla-v4-compat` 提炼出的最小闭环，目标是在不改 OpenClaw 主执行栈的前提下，把 OpenSquilla 训练产物稳定转换成 OpenClaw 可消费的 bundle。

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
- `runtime-config.json`：至少包含 embedding 维度字段（`dimensions` 或 `embeddingDimensions`）；当前参考 bundle 的值为 `512`
- `classifier.onnx`：输出 4 类（对应 `T0/T1/T2/T3`）
- `tokenizer.json`：导出已经针对当前 OpenClaw tokenizer loader 与 pre-tokenizer 支持范围验证通过的 tokenizer 版本；当前参考 bundle 使用的是 `WordPiece` + `BertPreTokenizer`

tier 映射建议固定为：

- `c0 -> T0`
- `c1 -> T1`
- `c2 -> T2`
- `c3 -> T3`

### A1.1 兼容 bundle 关键文件职责

为了便于运维排障，下面明确 `models/routing/opensquilla-v4-compat` 中三个关键 JSON 文件的运行时职责：

- `manifest.json`
  - 作用：bundle 的资产索引入口。主要提供 `classifierModelPath`、`embeddingModelPath`、`tokenizerPath`、`runtimeConfigPath`。
  - 运行时行为：Gateway 会优先读取该文件并解析相对路径；如果字段缺失，会回退到 bundle 目录下的默认文件名（例如 `classifier.onnx`、`embeddings.onnx`、`tokenizer.json`、`runtime-config.json`）。
  - 风险信号：路径写错或字段缺失会提升 `classifier_unavailable` 概率，常见表现是持续回退到 `T2`。

- `tokenizer.json`
  - 作用：本地 embedding 推理前的分词配置来源，`LocalOnnxEmbeddingGenerator` 会使用它把回合文本编码成模型输入。
  - 运行时行为：该文件不可用或解析不兼容时，ONNX 路由策略会降级到 fallback（`T2`），并记录机器可读原因。
  - 实践建议：优先使用与当前 loader 兼容的 tokenizer 形态；如果使用 WordPiece，请先做联调验证。

- `runtime-config.json`
  - 作用：提供运行时元信息，当前最关键的是 embedding 维度（`dimensions` / `embeddingDimensions` / `embeddingSize`）。
  - 运行时行为：Gateway 会先从该文件读取维度并写入路由资产配置；读不到时会回退到 `manifest.json`，再读不到则使用默认值 `384`。
  - 风险信号：维度配置与真实 embedding 输出不一致，可能导致特征拼接异常或路由质量下降。

排障速查表（建议顺序）：

1. 出现 `classifier_unavailable` 且持续回退 `T2`：先检查 `manifest.json` 里的路径是否存在且可读
2. 启动正常但推理阶段频繁 fallback：检查 `tokenizer.json` 是否与当前 tokenizer loader 兼容
3. 路由质量明显波动或特征异常：检查 `runtime-config.json` 的维度是否与 embedding 输出一致
4. 三者都正常但仍异常：核对 `classifier.onnx` 输入 shape 与特征拼接维度是否匹配

5 分钟最小自检命令（PowerShell）：

```powershell
$bundle = "models/routing/opensquilla-v4-compat"

# 1) 文件存在性
Get-Item "$bundle/manifest.json", "$bundle/tokenizer.json", "$bundle/runtime-config.json" |
  Select-Object FullName, Length, LastWriteTime

# 2) JSON 可解析
Get-Content "$bundle/manifest.json" -Raw | ConvertFrom-Json | Out-Null
Get-Content "$bundle/tokenizer.json" -Raw | ConvertFrom-Json | Out-Null
Get-Content "$bundle/runtime-config.json" -Raw | ConvertFrom-Json | Out-Null

# 3) 读取 runtime-config 中维度字段（命中其一即可）
$rc = Get-Content "$bundle/runtime-config.json" -Raw | ConvertFrom-Json
$dims = $rc.embeddingDimensions
if (-not $dims) { $dims = $rc.dimensions }
if (-not $dims) { $dims = $rc.embeddingSize }
"resolved embedding dims = $dims"
```

Checksum 一致性提示：

- 只要修改了 bundle 内任意 JSON（尤其是 `runtime-config.json`），就应同步更新 `manifest.json` 的 `checksums` 对应项，否则校验工具可能判定为旧包或哈希不匹配。
- 快速计算 SHA256：

```powershell
(Get-FileHash "models/routing/opensquilla-v4-compat/runtime-config.json" -Algorithm SHA256).Hash.ToLowerInvariant()
```

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

- [balanced 10-sample report](../testing/phase-a-offline-validation-10sample-report.json)：5 个 simple + 5 个 complex，gold / predicted 均为 `T0:5`、`T2:5`，fallback 为 0
- [T1-inclusive 10-sample report](../testing/phase-a-offline-validation-10sample-report-t1.json)：3 个 `T0`、3 个 `T1`、2 个 `T2`、2 个 `T3`，gold / predicted 完全一致，fallback 为 0

两份报告现在都包含 `costDistribution` 字段，使用 unit-cost 代理（`T0=1`、`T1=2`、`T2=4`、`T3=8`）做离线成本对比。

说明：这两份报告都基于代码层的 heuristic 路由规则，不是在当前会话里实际触发 ONNX fallback 的实测结果，因此 fallback 只能视为“未触发”，不能解读为“ONNX fallback 已验证通过”。

### A4. 常见历史风险与排查建议

- 信号：路由长期 `classifier_unavailable`
  - 优先检查 `BundlePath` 下文件命名是否符合 OpenClaw 约定
- 信号：推理阶段异常后频繁回退 `T2`
  - 检查 embedding 输入名/输出 shape 是否与当前 `LocalOnnxEmbeddingGenerator` 兼容
- 信号：加载 tokenizer 时报不支持
  - 先检查 tokenizer 是否超出了当前 loader 已覆盖的具体形态；当前参考 bundle 使用的 `WordPiece` + `BertPreTokenizer` 已被支持，因此问题更可能出在其他 tokenizer family 或未覆盖的 pre-tokenizer 组合上
- 历史风险/排查建议：MAF 行为与 native 不一致
  - 如果仍观察到该现象，先确认是否依赖了当前仅 native 消费的扩展路由字段

### A5. 建议实施节奏

1. 先打通“可加载 + 可路由 + 可回退”三件套
2. 再做“分布调优”（阈值与 tier 映射）
3. 最后再考虑是否进入阶段 B 的高保真适配

### A5.1 阶段 B 启动门槛表（里程碑可直接使用）

| 维度 | 指标 | 启动阈值（Go） | 观察窗口 | 不达标处理 | 回滚条件（硬门槛） |
|---|---|---|---|---|---|
| 可用性 | 总 fallback 率 | <= 0.5% | 连续 14 天（生产 10% 灰度） | 延长灰度观察 7 天并排查 bundle/模型加载链路 | 任意 30 分钟窗口 > 2.0% 立即回滚到阶段 A 策略 |
| 可用性 | classifier_unavailable | <= 0.1% | 连续 14 天 | 优先检查 bundle 命名、路径和资产完整性 | 任意 30 分钟窗口 > 0.5% 立即回滚 |
| 稳定性 | classifier_runtime_error | <= 0.2% | 连续 14 天 | 定位 ONNX 推理、shape、tokenizer 兼容问题 | 任意 15 分钟窗口 > 1.0% 立即回滚 |
| 质量 | 标注样本集 Macro-F1 | >= 0.90（N >= 500） | 每周滚动评测 2 周 | 不进入全量，继续阈值与映射调优 | 连续 2 次周评测 < 0.88 回滚 |
| 质量 | 高成本层召回（T2/T3 Recall） | >= 0.92 | 每周滚动评测 2 周 | 仅允许小流量灰度，不扩流 | 连续 2 次周评测 < 0.90 回滚 |
| 分布 | tier 分布漂移（相对阶段 A 基线） | 各 tier 偏移绝对值 <= 5pp，T3 偏移 <= +2pp | 连续 14 天 | 冻结扩流，分析 prompt/场景漂移 | 任意 24 小时窗口 T3 偏移 > +5pp 且质量无提升则回滚 |
| 成本 | 单千回合成本（单位成本代理） | 相对阶段 A <= +8% | 连续 14 天 | 限流并调整升级/降级阈值 | 任意 24 小时窗口 > +15% 回滚 |
| 性能 | 路由附加延迟（p95） | <= +80ms（相对阶段 A） | 连续 14 天 | 优化模型加载与缓存 | 连续 6 小时 > +150ms 回滚 |
| 一致性 | native/MAF 决策一致率 | >= 99.5%（同输入同配置） | 每日对账 14 天 | 阻止扩流并补齐差异定位 | 任何一天 < 99.0% 回滚 |
| 运维风险 | Sev1/Sev2 路由事故 | Sev1 = 0，Sev2 <= 1/周 | 连续 14 天 | 停止扩流并进入故障复盘 | 任一 Sev1 立即回滚 |

阶段 B 启动/扩流决策建议：

1. 满足全部 Go 阈值且无硬回滚触发，才允许从 10% 灰度扩到 50%
2. 50% 灰度再观察 7 天，仍满足阈值后再申请全量
3. 任一硬回滚条件触发，直接回退到阶段 A 策略，并冻结阶段 B 变更至少 72 小时后再评审

阶段 B 回滚执行口径（简版）：

1. 配置回切：将路由策略切回阶段 A 兼容路径（保持现有 BundlePath 与 Tier 映射）
2. 流量回切：5 分钟内把阶段 B 流量降至 0%
3. 证据留存：保留触发前后 24 小时的 fallback、分层分布、成本、延迟、native/MAF 对账快照
4. 复盘门槛：根因明确并验证通过后，才允许重新进入 10% 灰度

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

`runtime-config.json` 参考草案：

```json
{
  "schemaVersion": 1,
  "embeddingDimensions": 512,
  "classifier": {
    "numClasses": 4,
    "classLabels": ["T0", "T1", "T2", "T3"]
  },
  "notes": {
    "tokenizerFamily": "WordPiece",
    "sourceModelDir": "opensquilla/squilla_router/models/v4.2_phase3_inference",
    "sourceRuntimeConfig": "opensquilla/squilla_router/models/v4.2_phase3_inference/inference_manifest.json"
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

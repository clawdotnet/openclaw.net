# ResourceOntology JSON-LD 输入渲染 — 可行性设计

**日期：** 2026-08-03  
**状态：** 仅文档评估（本周期不实现）  
**范围路径：** `tools/ResourceOntology`  
**读者：** 工程 Go/No-Go、产品/路线图表述、与主仓本体栈对齐  

## 1. 目的与非目标

### 1.1 目的

评估 `tools/ResourceOntology` 能否将 **JSON-LD 本体文件作为渲染输入**（而不仅是导出 JSON-LD），并产出一份 **可直接拍板** 的说明，覆盖：

1. **工程 Go/No-Go** — 工作量、风险、最小路径  
2. **路线图安全表述** — 能承诺什么、必须明确不做的边界  
3. **主仓对齐** — 工具独立演进 vs 薄适配 vs 强复用 GraphSlicer  

### 1.2 非目标（本文）

- 本设计周期不写功能代码、不改 API、不升依赖、不做发布向提交  
- 不写合规认证叙事  
- 不承诺具体上线日期  
- 不要求 v1 对任意知识图谱达到完整 OWL 浏览器保真度  

### 1.3 当前基线（事实）

| 层 | 基线 |
| --- | --- |
| 输入 | 仅 RDF/XML（`.owl` / `.rdf` / `.xml`）；`OntologyParser` 硬编码 `RdfXmlParser` |
| 提升 | `IGraph` → `BuildModel` → `OntologyDto`（以 OWL/RDFS 为中心） |
| UI | Svelte + Cytoscape；只消费 `OntologyDto`（与源序列化格式解耦） |
| 现有 JSON-LD | 经 `JsonLdWriter` **导出**（可选 expand）；**不能**作为加载/渲染输入 |
| 主仓 | `OpenClaw.GraphSlicer` 等相关能力已读写 JSON-LD；`FileLoader.Load` 多格式；dotNetRDF **3.5.2** |
| 本工具包 | dotNetRDF **3.3.1**；API 项目启用 trimmed / single-file 发布 |

主要参考：

- `tools/ResourceOntology/README.md`  
- `tools/ResourceOntology/server/Services/OntologyParser.cs`  
- `tools/ResourceOntology/server/Program.cs`  
- `src/OpenClaw.GraphSlicer/`  
- `docs/openclaw-ontology-implementation.md`  

## 2. 判定量尺

| 判定 | 含义 |
| --- | --- |
| **Go** | 最小路径清晰；风险已知且可缓解；不得破坏现有 RDF/XML 路径 |
| **Go with constraints** | 仅在明确格式/OWL/context 边界下可行；路线图不得写成「任意 JSON-LD」 |
| **No-Go（本工具 / 本阶段）** | 目标变成通用 KG 浏览器，或强绑完整 GraphSlicer 管线 → 应另立项目 |
| **Defer** | 缺少 spike 证据（如 trimmed 发布 + 远程 `@context`）前不承诺 |

**总判定：** **Go with constraints**

## 3. JSON-LD 输入形态（分档）

### 3.1 定义

| 形态 | 定义 | 典型来源 |
| --- | --- | --- |
| **A. OWL-as-JSON-LD** | 仍是 OWL 公理（Class、属性、个体、限制等），仅序列化为 JSON-LD | Protégé 导出、本工具 `export-jsonld` 再读回、标准 OWL JSON-LD |
| **B. 通用 JSON-LD 知识图** | 任意 `@graph` / 节点文档 + `@context`；不保证 OWL 词汇完备 | 业务 KG、schema.org 风格、混搭词表 |
| **C. 主仓 JSON-LD 产物** | GraphSlicer framing、TemporaryGraphTool、ontology slice 等实际输出 | `OpenClaw.GraphSlicer`、agent 临时图、slice 导出 |

### 3.2 与当前管线的匹配度

```text
字节流 →（解析器）→ IGraph → BuildModel（OWL 提升）→ OntologyDto → 图谱 / 树 / 详情
```

| 形态 | 解析为 `IGraph` | `BuildModel` 质量 | 完整 OWL 浏览器体验 | 可行性 |
| --- | --- | --- | --- | --- |
| **A** | 高（`JsonLdParser` / `FileLoader`） | 高（词汇对齐） | **高** — 与 RDF/XML 对等 | **Go** |
| **B** | 中–高（语法通常 OK；远程 context 有风险） | **低–中**（常无 `owl:Class` 等） | **低** — 层级/限制可能空或残缺 | **Go with constraints**（仅实体/图级，非 OWL 级） |
| **C** | 高（主仓已有 JSON-LD 实践） | **取决于 frame** | **中** — 在文档化的 context/frame 下可用；否则 ≈ B | **Go with constraints**（需「受支持产物配置」） |

### 3.3 路线图安全表述

**可以写（推荐）：**

- ResourceOntology 可将 **以 JSON-LD 序列化的 OWL 本体** 作为可视化输入，渲染能力与现有 RDF/XML 路径 **对等**（形态 A）。  
- **主仓约定输出的 JSON-LD**（形态 C）在 documented context/frame 配置下可浏览；缺口用兼容矩阵列出。

**v1 能力声明中不应出现：**

- 「支持任意 JSON-LD / 任意知识图谱的完整本体浏览器保真度」（形态 B 全量）。  
- 「JSON-LD 往返位级一致 / 排版稳定」（导出已有；完整 round-trip 保真度属另项）。  
- 「浏览器端 JSON-LD framing/推理」（解析仍在服务端）。

### 3.4 形态 B 的降级路径（明确非 v1）

若日后单独决策推进形态 B：

1. **严格 OWL 提升（现逻辑）** — 非 OWL 类型 → UI 近乎空白（体验最差）  
2. **启发式提升** — 将 `rdf:type` 目标当 class，其余当 individual/断言 — 「有图」，但非正式 OWL  
3. **通用 RDF 图模式** — 新 DTO/UI（节点 = IRI，边 = predicate）— 本质是新产品面  

形态 B 不得与形态 A 共用同一个路线图 checkbox。

### 3.5 形态 C 验收探针（宣称 C「已支持」之前）

| 探针 | 目的 |
| --- | --- |
| 主仓 sample JSON-LD → ResourceOntology 上传/解析 | 端到端能否产出 DTO |
| 同一本体 RDF/XML vs 主仓 JSON-LD 的 class/individual 计数 | 检测提升丢公理 |
| 内嵌/本地 `@context` vs 需 HTTP 解析的 context | 离线/CI 可复现性 |
| framed vs compact vs expanded | 声明受支持的输入配置 |
| 工具 dotNetRDF 3.3.1 vs 主仓 3.5.2 | 解析/行为漂移 |

### 3.6 工作量量级（仅供规划，非排期承诺）

| 档 | 工作 | 量级 | 依赖 |
| --- | --- | --- | --- |
| **A0 Spike** | 再加载 `export-jsonld` 产物；对比 `OntologyDto` | 0.5–1 人日 | — |
| **A1 MVP** | 扩展名/Content-Type 分派；`.jsonld` 列表/上传 UX；测试；README 边界 | 1–2 人日 | A0 通过 |
| **C1** | 1–2 个主仓 fixture + 兼容说明；可选包版本对齐 | +1–2 人日 | A1 |
| **B\*** | 启发式或通用图模式 | 3–8+ 人日 | 单独 Go |
| **本文档** | 仅规格 | 约 0.5 人日 | — |

## 4. 实现策略选项（与主仓关系）

| 维度 | **O1 工具内独立** | **O2 薄适配主仓（推荐目标态）** | **O3 强复用 GraphSlicer** |
| --- | --- | --- | --- |
| 做法 | 本地 `JsonLdParser` 分派；DTO/UI 不动 | 解析选项、包版本、fixture、context 策略与主仓对齐；可选抽小共享模块；UI 仍吃 `OntologyDto` | 经 GraphSlicer 源/frame/slice 接入，再映射 DTO 或改 UI |
| 形态 A | 优 | 优 | 优但过重 |
| 形态 C | 易漂移 | **平衡最佳** | 理论上限最高 |
| 形态 B | 不解决 | 不解决 | 仍可能要新 UI |
| 耦合 | 无 | 低–中 | 高（`tools` ↔ `src`、API、发布） |
| 技术债 | JSON-LD 双栈 | 可控 | 架构重绑 |
| 路线话术 | 「工具支持 OWL-JSON-LD」 | **「与主仓 JSON-LD 习惯一致」** | 「统一本体管线可视化」 |
| 到 A1 量级 | 1–2 人日 | 3–5 人日（含对齐/fixture） | 5–10+ 人日 |
| 何时选 | 先证明「能看」 | 工程 + 路线图 + 对齐 | 产品升级为平台级 KG 浏览器 |

**推荐立场：** 默认目标态为 **O2**；允许 **A0/A1 先用 O1 手法**，再把版本与 fixture 收进 O2。本评估下 **O3 对 v1 为 No-Go**。

```text
A0 spike（O1 手法）→ A1 MVP → C1 fixture/对齐（进入 O2）→ B / O3 另项决策
```

## 5. 风险

| ID | 风险 | 影响 | 可能性 | 缓解 | 是否阻塞 |
| --- | --- | --- | --- | --- | --- |
| R1 | 工具 dotNetRDF **3.3.1** vs 主仓 **3.5.2** | 形态 C 行为不一致 | 中 | A0 对比；A1 前/时评估升级 | 否（先测） |
| R2 | **Trimmed/single-file** 裁掉 JSON-LD 依赖 | 发布包解析失败 | 中 | 对 publish 产物跑 A0；必要时 trimmer roots | **可能阻塞发布通道** |
| R3 | 远程 `@context`（HTTP/离线/SSRF） | CI/内网失败 | 若允许远程则高 | v1 仅内嵌/本地 context；文档写明策略 | 策略问题，非技术否决 |
| R4 | 把形态 B 当形态 A 对外宣传 | 空图/信任受损 | 话术不严则高 | 分档表述；可选 UI「非 OWL」提示 | 产品风险 |
| R5 | `BuildModel` 的 OWL 词汇假设 | B / 部分 C 提升稀疏 | 确定 | 分档预期；C 配置文件 | 预期管理 |
| R6 | 整文件内存 + 大图布局 | OOM / UI 卡顿 | 中 | 与现 OWL 同等限制；后续再限额 | 已知债 |
| R7 | O3 范围膨胀 | 延误 | 若选 O3 则中 | v1 排除 O3 | 范围 |
| R8 | README/API 与代码漂移 | 实现误解 | 已存在 | 实施时顺带修文档 | 否 |
| R9 | 往返美观/位级一致 | 源码页预期落差 | 中 | v1 不承诺 bit-exact round-trip | 否 |

## 6. Go / No-Go 决策表

| 决策项 | 判定 | 条件 |
| --- | --- | --- |
| 在 ResourceOntology 支持 JSON-LD **渲染输入** | **Go with constraints** | 按形态分档；非「任意 JSON-LD」 |
| 形态 **A** 作为 v1 目标 | **Go** | A0：export-jsonld 再读回；DTO 关键计数/结构对等（允许空白节点标注差异） |
| 形态 **C** 作为 v1.x | **Go with constraints** | ≥1 个主仓 fixture + 受支持 context/frame 说明；C 失败不得挡住 A |
| 形态 **B** 完整 OWL 体验 | **No-Go（v1）** | 日后探索或「通用图视图」另项 |
| 策略 **O1** | **Go（spike/MVP 手法）** | 本地 parser 分派即可 |
| 策略 **O2** | **Go（默认目标态）** | 版本/fixture/文档对齐 |
| 策略 **O3** | **No-Go（本评估的 v1）** | 除非产品升级为平台级 KG 浏览器 |
| 本周期实现 | **No** | 仅交付规格 |
| 升级 dotNetRDF / 修 trim | **Defer 到 A0/A1** | 以证据驱动 |
| 对外路线图一句话 | **Go（用下文）** | 必须保留边界 |

### 6.1 建议路线图一句话

> ResourceOntology 将支持以 **OWL 的 JSON-LD 序列化** 作为可视化输入，渲染能力与 RDF/XML 路径对等；并在 documented context/frame 下浏览 **主仓约定配置的 JSON-LD 产物**。不宣称对任意 JSON-LD 知识图提供完整 OWL 级可视化。

### 6.2 应避免的表述

- 不加限定的「全面支持 JSON-LD 本体」  
- 「与 GraphSlicer 同一套可视化管线」（除非单独立项并预算 O3）  

## 7. 分阶段建议（现在 / 下一步 / 以后 / 不做）

| 阶段 | 动作 | 产出 | 门禁 |
| --- | --- | --- | --- |
| **现在** | 落盘本可行性设计；无功能代码 | `docs/superpowers/specs/` 下本文 | 维护者审阅规格 |
| **下一步（若批准实现）** | A0 spike（再读回 + 可选 trimmed publish） | R1/R2 通过/失败记录 | 失败 → 修栈或推迟发布通道 |
| **MVP** | A1：分派、`.jsonld` UX、测试、README 边界 | 可演示的 OWL-JSON-LD | 通过 → 可标 v1 能力 |
| **以后** | C1 fixture + O2 对齐；能力矩阵条目 | 「主仓产物可用」 | 按 fixture 扩展 |
| **不做（本工具 v1）** | 任意 JSON-LD 的 OWL 级体验；O3 强复用；bit-exact 往返 | — | 另立专项 |

## 8. 未来实现草图（未授权开工）

仅供信息参考 — 若批准后再进入 `writing-plans` 周期。

### 8.1 可能改动点

- `tools/ResourceOntology/server/Services/OntologyParser.cs` — 按扩展名 / Content-Type / 嗅探选择解析器  
- `tools/ResourceOntology/server/Program.cs` — 文件列表 glob、上传校验、与导出路径共享加载辅助  
- `tools/ResourceOntology/client/src/App.svelte`（及 i18n）— 适当接受 `.jsonld` / `.json`  
- 工具或仓库测试 — round-trip 与 `.owl` 回归  
- `tools/ResourceOntology/README.md` — 输入格式与非目标  

### 8.2 错误处理原则

- 非法 JSON / JSON-LD 解析失败 → **400** + 可读信息（与现 OWL 失败同类）  
- 解析成功但 0 classes 且像非 OWL → **200**，DTO/meta 可选 warning（形态 B 实验勿当硬错误）  
- 远程 context 失败 → 明确错误；v1 可禁用远程 context  

### 8.3 最小测试集（实施时）

1. 自 `export-jsonld` round-trip ≈ 原 OWL `OntologyDto`  
2. 非法 JSON-LD → 400  
3. 现有 `.owl` 路径行为不变  
4. （C1）1 个主仓 fixture 快照或计数断言  

### 8.4 架构偏好

**序列化入口** 放在服务端；**OWL 提升** 集中在一个 `BuildModel`；**UI 只吃 `OntologyDto`**。不要按序列化格式重做可视化。

## 9. 结论摘要

| 问题 | 答案 |
| --- | --- |
| JSON-LD 渲染输入是否可行？ | **是，且须按形态分档** |
| 与现架构是否冲突？ | **A：否；B：语义模型不匹配；C：取决于产物配置** |
| 最小有价值档 | **A（经 A0 spike）** |
| 默认对齐策略 | **O2 薄适配；spike/MVP 可用 O1** |
| 总判定 | **Go with constraints** |
| 本周期交付 | **仅设计/可行性 — 不实现** |

## 10. 认可记录

| 项 | 状态 |
| --- | --- |
| 脑暴范围（仅评估；读者 A+B+C；形态 A/B/C 分档；O1/O2/O3 并列；决策骨架 E） | 会话中已确认 |
| 设计章节讨论推进 | 用户以「继续」/「ok」推进 |
| 成文规格 | 本文件（中文） |
| 用户要求 | 规格使用中文撰写 |

实现规划（`writing-plans`）仅在 **明确认可本文件** 且 **另行要求实现或写实现计划** 后开始。

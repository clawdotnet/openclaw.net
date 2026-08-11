# openclaw.net 标准数字化本体子系统技术文档

- 文档日期：2026-08-11
- 适用版本：openclaw.net `ontologyharnessaction` 分支
- 依从标准：GB/T 48000.3-2026《标准数字化 第3部分：本体建模要求》
- 文档语言：中文

---

## 1. 概述

### 1.1 背景

GB/T 48000.3-2026《标准数字化 第3部分：本体建模要求》于 **2026-01-28 发布，2026-08-01 实施**，是 GB/T 48000《标准数字化》系列标准的核心组成部分。该标准规定了标准数字化活动中本体构建的通用要求，涵盖实体建模（第 6 章）、实体关系与属性（第 7 章）、本体公理与规则（第 8 章）及扩展原则（第 9 章）。

openclaw.net 在 `ontologyharnessaction` 分支上实现了一套完整对齐 GB/T 48000.3-2026 的标准数字化本体子系统，完整覆盖所有**规范性要求**。

### 1.2 能力总览

| 能力 | 实现 | 标准条款 |
|------|------|---------|
| JSON-LD 序列化与 Framing | dotNetRDF `JsonLdWriter` + `JsonLdProcessor.Frame()` | 5.3 |
| Turtle 序列化 | dotNetRDF `CompressingTurtleWriter` | 5.3 |
| RDF/XML 序列化 | dotNetRDF `RdfXmlWriter` | 5.3 |
| OWL 本体构建 | `OntologyBuilder` 流式 API | 5.3 |
| SHACL 约束验证 | `ShaclValidator` + `StandardShapes`（6 个 NodeShape） | 5.3 |
| SPARQL 知识图谱查询 | `RemoteEndpointSource` + `LocalFilesSource` | 6.2 |
| 标准领域本体 | `StandardOntology`（677 triples：18 核心实体 + 26 派生类 + 34 对象属性 + 45 数据属性 + 8.2 公理） | 附录 B/C |
| 版本追溯 | `VersionTracer`（replaces 链 + Diff + CLI `ontology versions`） | 6.3.1 |
| MetaSkill DAG 验证 | `OntologyValidateTool`（ITool，已在 Gateway 注册） | — |
| 本体可视化 | `tools/ResourceOntology`（Cytoscape.js，加载 RDF/XML + JSON-LD） | — |

---

## 2. 系统架构

### 2.1 模块总览

```
src/
├── OpenClaw.Core/Models/
│   ├── GraphSliceProfile.cs          ← 图切片配置模型
│   └── OntologyProfile.cs            ← 本体构建配置模型
│
├── OpenClaw.GraphSlicer/             ← RDF 知识图谱切片器
│   ├── GraphSlicerEngine.cs          ← 编排引擎：CONSTRUCT→merge→frame
│   ├── JsonLdFramer.cs               ← JSON-LD 1.1 Framing（dotNetRDF）
│   ├── ISparqlSource.cs              ← 数据源接口
│   ├── RemoteEndpointSource.cs        ← SPARQL 远程端点适配器
│   └── LocalFilesSource.cs            ← 本地 RDF 文件适配器
│
├── OpenClaw.Ontology/                ← OWL 本体构建器
│   ├── OntologyBuilder.cs             ← OWL Class/Property/Axiom 流式 API
│   ├── ShaclValidator.cs              ← SHACL 约束验证器
│   └── OntologyValidateTool.cs         ← MetaSkill DAG 验证工具（ITool）
│
├── OpenClaw.StandardOntology/         ← GB/T 48000.3 标准领域本体
│   ├── StandardOntology.cs            ← 预置标准本体（677 triples）
│   ├── StandardShapes.cs              ← 默认 SHACL shapes（6 个 NodeShape）
│   └── VersionTracer.cs               ← 版本追溯（replaces 链 + Diff）
│
├── OpenClaw.Cli/
│   └── OntologyCommands.cs             ← CLI：ontology build/validate/versions
│
└── OpenClaw.Agent/Tools/
    └── TemporaryGraphTool.cs           ← 运行时 JSON-LD 图加载
```

### 2.2 依赖关系

```
OpenClaw.Cli
  ├── OpenClaw.GraphSlicer       → dotNetRDF 3.5.2
  ├── OpenClaw.Ontology          → dotNetRDF 3.5.2 + Newtonsoft.Json 13.0.4
  └── OpenClaw.StandardOntology
        └── OpenClaw.Ontology

OpenClaw.Gateway
  ├── OpenClaw.Ontology
  └── OpenClaw.StandardOntology
```

> **AOT 说明**：`OpenClaw.GraphSlicer`、`OpenClaw.Ontology`、`OpenClaw.StandardOntology` 均标记 `PublishAot=false`——dotNetRDF 不兼容 NativeAOT（大量反射使用）。Core 层仅保留配置模型（AOT-safe），实现类留在 JIT 项目中。

---

## 3. 核心组件

### 3.1 OntologyBuilder — OWL 本体构建器

`OntologyBuilder` 基于 dotNetRDF `Graph` API 提供流式接口，直接构造 OWL/RDF/RDFS 三元组。

| 方法 | 对应 OWL 构造 |
|------|--------------|
| `DeclareClass(iri, label, comment, subClassOf, disjointWith, hasKey, ...)` | `owl:Class` + `rdfs:label` + `rdfs:comment` + `rdfs:subClassOf` + `owl:disjointWith` + `owl:hasKey` |
| `DeclareObjectProperty(iri, label, comment, domain, range, functional, transitive, ...)` | `owl:ObjectProperty` + `rdfs:domain`/`rdfs:range` + 特性（Functional/Transitive/Symmetric） |
| `DeclareDatatypeProperty(iri, label, comment, domain, range, functional)` | `owl:DatatypeProperty` + `rdfs:domain`/`rdfs:range` |
| `AssertDisjointClasses(classA, classB)` | `owl:disjointWith` |
| `AssertSubClassOf(subClass, superClass)` | `rdfs:subClassOf` |
| `Build()` → `IGraph` | 返回 dotNetRDF 图 |
| `Serialize(format)` / `WriteToFile(path, format)` | Turtle / JSON-LD / RDF/XML 序列化 |

```csharp
var ob = new OntologyBuilder("http://openclaw.net/ontology/standard#");
ob.WithPrefix("std", "http://openclaw.net/ontology/standard#")
  .DeclareClass("std:Standard", "标准实体", "标准文件的核心根节点",
      hasKey: ["std:standardNumber"])
  .DeclareObjectProperty("std:replaces", "代替", "新版本代替旧版本",
      "std:Standard", "std:Standard")
  .DeclareDatatypeProperty("std:standardNumber", "文件编号",
      "符合 GB/T 1.1 规定的编号格式", "std:Standard", "xsd:string",
      functional: true)
  .AssertDisjointClasses("std:Standard", "std:Term")
  .WriteToFile("./ontology.ttl", OntologyOutputFormat.Turtle);
```

### 3.2 StandardOntology — GB/T 48000.3 预置本体

`StandardOntology.Build()` 对齐 GB/T 48000.3-2026 规范模型（**677 triples**）：

| 类别 | 数量 | 内容 |
|------|------|------|
| **18 核心实体**（附录 B.1–B.18） | 18 | Standard / StandardizationObject / Stakeholder / Organization / Individual / DomainCategory / InternationalClassificationofStandard / ChineseClassificationofStandard / ContentElement / StructuralElement / InformationUnit / InformationForm / Object / Property / Constraint / ActionClass / ExternalResource / StandardizationProcess |
| **26 规范性派生类**（§6.2.4/6.2.5/7.3.2/附录 C 引用） | 26 | Clause / TitledClause / Example / Note / List / NormativeElement / InformativeElement / TextForm / FigureForm / TableForm / FormulaForm / CodeForm / DescriptiveProperty / CapabilityProperty / ConstraintProperty / Determination / LawRegulation / Patent / ReferenceDocument / Level / Section / Paragraph / Item / Term / Version / DocumentNumber |
| **36 对象属性** | 36 | 34 核心对象属性（§7.3.2）+ 2 可选扩展（§6.3）：adopts / replaces / cites / references / hasPart / issuedBy / proposedBy / administeredBy / draftedBy（→ Stakeholder） / publishedBy / classifiedUnder / standardizes / hasNormativeElement / hasStructuralElement / hasClause / hasSubClause（transitive） / defines / usesTerm / hasRepresentationForm / hasExample / hasNote / citesStandard / referencesClause / involvesObject / specifiesCharacteristic / hasCharacteristic / imposesConstraint / constrainsObject / constrainsCharacteristic / describesAction / referencesExternalResource / isRelatedToPatent / hasDevelopmentStage / includesStandard / hasVersion / hasDocumentNumber |
| **45 数据属性**（47 C 条目，constraintType 跨 Standard/Clause/Constraint 三域） | 45 | purpose / languageVersion / status / constraintType / documentName / standardNumber / issuedDate / effectiveDate / subjectName / industrialSector / orgName / creditCode / orgLocation / personName / affiliation / phone / address / ICS_code / ICS_name / CCS_code / CCS_name / elementStatus / scopeOfEffect / sectionNumber / sectionTitle / clauseNumber / clauseTitle / uniqueldentifier / contentDescription / clauseType / objectName / objectCategory / propertyName / propertyValue / propertyType / maxValue / minValue / thresholdRange / unit / fileType / effectiveTime / responsibleParty / stageCode / startDate / endDate |
| **8.2 核心公理** | — | 14 Standard ⊥ 不相交 / NormativeElement ⊥ InformativeElement / Organization ⊥ Individual / 子类层级（Level / InformationUnit / ContentElement / Property / DomainCategory / Stakeholder / ExternalResource / ActionClass / InformationForm 等）/ 功能性约束（issuedBy / administeredBy / creditCode / stageCode）/ hasKey（Standard → standardNumber） |

### 3.3 JSON-LD 1.1 Framing

`JsonLdFramer` 委托 dotNetRDF 的 `JsonLdProcessor.Frame()` 实现 W3C JSON-LD 1.1 Framing。

| 指令 | 说明 |
|------|------|
| `@type` | 按 rdf:type 过滤节点 |
| `@id` | 按 @id 精确匹配 |
| `@embed` | @always / @once / @never / @first / @last / @link |
| `@explicit` | 仅输出帧中列出的属性 |
| `@requireAll` | 要求节点拥有全部指定属性 |
| `@omitDefault` | 缺失属性时不输出空数组 |
| 嵌套帧 | 属性值中的对象作为子帧递归应用 |

管线：`GraphSlicerEngine` → SPARQL CONSTRUCT → 合并图 → JsonLdWriter → JsonLdFramer 帧化 → 输出文件

### 3.4 SHACL 约束验证

`ShaclValidator` 包装 dotNetRDF `ShapesGraph.Validate()`：

```csharp
var report = validator.Validate(dataGraph, shapesGraph);
// report.Conforms: bool
// report.Results: IEnumerable<ValidationResult>
```

`StandardShapes` 提供 GB/T 48000.3-2026 默认 SHACL shapes（6 个 NodeShape）：

| Shape | 目标类 | 约束 |
|-------|--------|------|
| StandardShape | `std:Standard` | standardNumber: 1..1 string; documentName: ≥1; issuedDate: ≥1; effectiveDate: ≥1; status: ≥1; languageVersion: ≥1; purpose: ≥1; constraintType: ≥1 |
| OrganizationShape | `std:Organization` | orgName: ≥1 |
| IndividualShape | `std:Individual` | personName: ≥1 |
| ClauseShape | `std:Clause` | clauseNumber: ≥1 |
| ExternalResourceShape | `std:ExternalResource` | fileType: ≥1 |
| StandardizationProcessShape | `std:StandardizationProcess` | stageCode: 1..1 |

验证示例（`ontology validate --profile standard`，本体自身验证）：

```
Conforms: True
Results: 0   ← 标准本体自身满足全部 shapes，无违规
```

### 3.5 版本追溯

`VersionTracer` 提供三个核心能力：

| 方法 | 功能 |
|------|------|
| `TraceReplacesChain(graph, iri)` | 沿 `std:replaces` 链从新到旧追溯版本谱系 |
| `GetVersions(graph, iri)` | 通过 `std:hasVersion` 获取所有版本 |
| `Diff(graph, oldIri, newIri)` | 两个版本间的属性级差异对比（added/removed/changed） |

### 3.6 图切片器（Graph Slicer）

`GraphSlicerEngine` 从多种数据源提取知识图谱切片：

```text
数据源                              SPARQL CONSTRUCT
────────────────────────────────────────────────────
RemoteEndpointSource ─┐
（Fuseki / Stardog /   │         ┌──────────┐
 GraphDB / Ontop）    ├────────→│  Merge   │→ JSON-LD → Frame → .jsonld
LocalFilesSource ─────┘         └──────────┘
（.ttl / .rdf / .jsonld / .nt）
```

**数据源适配器**：

- **RemoteEndpointSource**：HTTP POST SPARQL 查询至远程端点，解析 N-Triples 响应，支持 Basic Auth
- **LocalFilesSource**：加载本地 RDF 文件至 `TripleStore`，使用 `LeviathanQueryProcessor` 执行 CONSTRUCT

---

## 4. CLI 命令参考

### 4.1 本体构建

```bash
# 构建标准本体（Turtle 格式，默认输出 docs/zh-CN/ontology/standard/standard-ontology.ttl）
openclaw ontology build --profile standard

# 指定输出格式和路径
openclaw ontology build --profile standard --format turtle   --output ./ontology.ttl
openclaw ontology build --profile standard --format jsonld   --output ./ontology.jsonld
openclaw ontology build --profile standard --format rdfxml  --output ./ontology.rdf
```

### 4.2 SHACL 验证

```bash
# 验证标准本体自身（Conforms: True，0 violations）
openclaw ontology validate --profile standard

# 验证实例数据（使用自定义 shapes）
openclaw ontology validate --data ./instances.ttl --shapes ./shapes.ttl
```

### 4.3 版本追溯

```bash
# 追溯 replaces 版本链
openclaw ontology versions --data ./instances.ttl --standard std:GB-T-12345-v3

# 版本差异对比
openclaw ontology versions --data ./instances.ttl \
  --diff-old std:GB-T-12345-v1 --diff-new std:GB-T-12345-v3
```

---

## 5. MetaSkill DAG 集成

### 5.1 验证步骤

在 MetaSkill DAG 中可加入 `ontology_validate` 步骤：

```yaml
- id: validate_ontology
  kind: tool_call
  tool: ontology_validate
  with:
    data: "./tmp/my-instances.ttl"
    shapes: "./tmp/standard-shapes.ttl"
```

### 5.2 全链路示例

```yaml
kind: meta
name: quality-root-cause-assistant

composition:
  steps:
    # Step 1: 加载图切片
    - id: load_graph
      kind: tool_call
      tool: load_temporary_graph
      with:
        path: "./tmp/quality-slice.jsonld"
        format: "jsonld"

    # Step 2: SHACL 验证（自动在 LLM 推理前校验本体合规性）
    - id: validate
      kind: tool_call
      tool: ontology_validate
      depends_on: [load_graph]
      with:
        data: "./tmp/quality-slice.jsonld"
        shapes: "./tmp/standard-shapes.ttl"

    # Step 3: LLM 推理
    - id: reason
      kind: llm_chat
      depends_on: [validate]
      with:
        input: "{{ outputs.load_graph }}"

    # Step 4: 行动执行
    - id: execute
      kind: tool_call
      depends_on: [reason]
      tool: action_execute
      tool_allowlist: [action_execute]
      with:
        proposal: "{{ outputs.reason }}"
```

---

## 6. 本体可视化

`tools/ResourceOntology` 是基于 Cytoscape.js 的交互式本体可视化工具，已迁移至 openclaw.net 仓库（`bc0e400`）。

**能力**：

- 4 种图布局：力导向（CoSE）、层级（dagre）、同心圆、广度优先
- 8 种边类型颜色编码：subClassOf / restriction / disjoint / domainRange / typeOf / assertion / property / inverse
- 左侧层次树 + 右侧详情面板（类 / 属性 / 实例检查器）
- 拖放上传 `.owl` / `.rdf` / `.xml` / `.jsonld` 文件
- JSON-LD 导出（compacted / expanded）
- 中英文双语界面
- .NET 10 (ASP.NET Core) + Svelte 5 + TypeScript + Tailwind CSS v4

```powershell
cd tools/ResourceOntology
./run.ps1    # 生产模式
./dev.ps1    # 开发模式（Vite HMR）
```

---

## 7. 命名空间

| 前缀 | IRI | 说明 |
|------|-----|------|
| `std` | `http://openclaw.net/ontology/standard#` | GB/T 48000.3 标准本体命名空间 |
| `owl` | `http://www.w3.org/2002/07/owl#` | OWL 2 |
| `rdf` | `http://www.w3.org/1999/02/22-rdf-syntax-ns#` | RDF |
| `rdfs` | `http://www.w3.org/2000/01/rdf-schema#` | RDF Schema |
| `xsd` | `http://www.w3.org/2001/XMLSchema#` | XML Schema |
| `sh` | `http://www.w3.org/ns/shacl#` | SHACL |

命名空间文档页面：[docs/zh-CN/ontology/standard/index.html](docs/zh-CN/ontology/standard/index.html)

可下载格式：

- [Turtle](docs/zh-CN/ontology/standard/standard-ontology.ttl)
- [JSON-LD](docs/zh-CN/ontology/standard/standard-ontology.jsonld)
- [RDF/XML](docs/zh-CN/ontology/standard/standard-ontology.rdf)

---

## 8. 代码索引

| 组件 | 路径 |
|------|------|
| 图切片引擎 | [GraphSlicerEngine.cs](src/OpenClaw.GraphSlicer/GraphSlicerEngine.cs) |
| JSON-LD Framing | [JsonLdFramer.cs](src/OpenClaw.GraphSlicer/JsonLdFramer.cs) |
| SPARQL 远程端点 | [RemoteEndpointSource.cs](src/OpenClaw.GraphSlicer/RemoteEndpointSource.cs) |
| SPARQL 本地文件 | [LocalFilesSource.cs](src/OpenClaw.GraphSlicer/LocalFilesSource.cs) |
| OWL 本体构建器 | [OntologyBuilder.cs](src/OpenClaw.Ontology/OntologyBuilder.cs) |
| SHACL 验证器 | [ShaclValidator.cs](src/OpenClaw.Ontology/ShaclValidator.cs) |
| DAG 验证工具 | [OntologyValidateTool.cs](src/OpenClaw.Ontology/OntologyValidateTool.cs) |
| 标准本体 | [StandardOntology.cs](src/OpenClaw.StandardOntology/StandardOntology.cs) |
| SHACL Shapes | [StandardShapes.cs](src/OpenClaw.StandardOntology/StandardShapes.cs) |
| 版本追溯 | [VersionTracer.cs](src/OpenClaw.StandardOntology/VersionTracer.cs) |
| 本体 CLI | [OntologyCommands.cs](src/OpenClaw.Cli/OntologyCommands.cs) |
| 运行时图加载 | [TemporaryGraphTool.cs](src/OpenClaw.Agent/Tools/TemporaryGraphTool.cs) |
| 评估文档 | [gbt48000-3-ontology-evaluation.md](docs/zh-CN/gbt48000-3-ontology-evaluation.md) |
| 命名空间文档 | [index.html](docs/zh-CN/ontology/standard/index.html) |
| 全链路文档 | [graph-slicer-metaskill-pipeline.md](docs/zh-CN/graph-slicer-metaskill-pipeline.md) |

---

## 9. 参考标准

| 标准 | 名称 |
|------|------|
| GB/T 48000.3-2026 | 标准数字化 第3部分：本体建模要求 |
| GB/T 42131-2022 | 人工智能 知识图谱技术框架 |
| GB/T 1.1-2020 | 标准化工作导则 第1部分 |
| GB/T 18391.3 | 信息技术 元数据注册系统(MDR) |
| ISO/IEC 21838 | Top-level ontologies (BFO) |
| W3C OWL 2 | Web Ontology Language |
| W3C SHACL | Shapes Constraint Language |
| W3C JSON-LD 1.1 | JSON-LD 1.1 Framing |

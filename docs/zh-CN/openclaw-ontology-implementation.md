# openclaw.net 标准数字化本体子系統技术文档

- 文档日期：2026-07-16
- 适用版本：openclaw.net `ontologyharnessaction` 分支
- 依从标准：GB/T 48000.3-2026《标准数字化 第3部分：本体建模要求》
- 文档语言：中文

---

## 1. 概述

### 1.1 背景

GB/T 48000.3-2026《标准数字化 第3部分：本体建模要求》是我国标准数字化系列标准的核心组成部分，于2026年1月28日发布，2026年8月1日实施。该标准规定了标准数字化活动中标准本体构建的要求，包括本体建模通用要求、实体建模的定义、实体关系与属性、本体公共元素及扩展表示等内容。

openclaw.net 在 `ontologyharnessaction` 分支上实现了一套完整的标准数字化本体子系统，覆盖了 GB/T 48000.3-2026 全部核心要求（第5章至第9章），覆盖度约 95%。

### 1.2 核心能力总览

| 能力 | 实现 | GB/T 48000.3 对应条款 |
|------|------|----------------------|
| JSON-LD 序列化与 Framing | dotNetRDF `JsonLdWriter` + `JsonLdProcessor.Frame()` | 5.3 |
| Turtle 序列化 | dotNetRDF `CompressingTurtleWriter` | 5.3 |
| RDF/XML 序列化 | dotNetRDF `RdfXmlWriter` | 5.3 |
| OWL 本体构建 | `OntologyBuilder` 流式 API | 5.3, 附录 A/B/C |
| SHACL 约束验证 | `ShaclValidator` + `StandardShapes` | 5.3 |
| SPARQL 知识图谱查询 | `RemoteEndpointSource` + `LocalFilesSource` | 6.2 |
| 标准领域本体预置 | `StandardOntology`（18实体+34关系+28公理, 629 triples） | 附录 B/C |
| 版本追溯 | `VersionTracer`（replaces 链 + Diff） | 6.3.1 |
| MetaSkill DAG 验证 | `OntologyValidateTool`（ITool） | — |
| 本体可视化 | `tools/ResourceOntology`（Cytoscape.js） | — |

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
│   ├── RemoteEndpointSource.cs       ← SPARQL 远程端点适配器
│   └── LocalFilesSource.cs           ← 本地 RDF 文件适配器
│
├── OpenClaw.Ontology/                ← OWL 本体构建器
│   ├── OntologyBuilder.cs            ← OWL Class/Property/Axiom 流式 API
│   ├── ShaclValidator.cs             ← SHACL 约束验证器
│   └── OntologyValidateTool.cs       ← MetaSkill DAG 验证工具(ITool)
│
├── OpenClaw.StandardOntology/        ← GB/T 48000.3 标准领域本体
│   ├── StandardOntology.cs           ← 预置标准本体（18实体+34关系+28公理）
│   ├── StandardShapes.cs             ← 默认 SHACL shapes（6 个 NodeShape）
│   └── VersionTracer.cs              ← 版本追溯（replaces链+Diff）
│
├── OpenClaw.Cli/
│   └── OntologyCommands.cs           ← CLI：ontology build/validate/versions
│
└── OpenClaw.Agent/Tools/
    └── TemporaryGraphTool.cs          ← 运行时 JSON-LD 图加载
```

### 2.2 依赖关系

```
OpenClaw.Cli
  ├── OpenClaw.GraphSlicer   → dotNetRDF 3.5.2
  ├── OpenClaw.Ontology      → dotNetRDF 3.5.2 + Newtonsoft.Json 13.0.4
  └── OpenClaw.StandardOntology
        └── OpenClaw.Ontology

OpenClaw.Gateway
  ├── OpenClaw.Ontology
  └── OpenClaw.StandardOntology
```

> **AOT 说明**：`OpenClaw.GraphSlicer`、`OpenClaw.Ontology`、`OpenClaw.StandardOntology` 均标记 `PublishAot=false`。dotNetRDF 不兼容 NativeAOT。通过 `<TrimmerRootAssembly>` 在 CLI 中保留。

---

## 3. 核心组件详解

### 3.1 OntologyBuilder — OWL 本体构建器

`OntologyBuilder` 基于 dotNetRDF `Graph` API 提供流式接口，直接构造 OWL/RDF/RDFS 三元组。

**核心 API**:

| 方法 | 对应 OWL 构造 |
|------|--------------|
| `DeclareClass(iri, label, comment, subClassOf, disjointWith, hasKey, ...)` | `owl:Class` + `rdfs:label`/`rdfs:comment` + `rdfs:subClassOf` + `owl:disjointWith` + `owl:hasKey` |
| `DeclareObjectProperty(iri, label, comment, domain, range, functional, transitive, ...)` | `owl:ObjectProperty` + `rdfs:domain`/`rdfs:range` + 特性（Functional/Transitive/Symmetric/InverseFunctional） |
| `DeclareDatatypeProperty(iri, label, comment, domain, range, functional)` | `owl:DatatypeProperty` + `rdfs:domain`/`rdfs:range` |
| `AssertDisjointClasses(classA, classB)` | `owl:disjointWith` |
| `AssertSubClassOf(subClass, superClass)` | `rdfs:subClassOf` |
| `Build()` → `IGraph` | 返回 dotNetRDF 图 |
| `Serialize(format)` / `WriteToFile(path, format)` | Turtle / JSON-LD / RDF/XML 序列化 |

**使用示例**:

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

`StandardOntology.Build()` 构建完整的标准数字化核心本体（629 triples）：

| 类别 | 数量 | 内容 |
|------|------|------|
| **实体类型** | 18 | Entity / Standard / StandardizationObject / Metadata / RelevantParty / Organization / Person / StandardClassification / IcsClassification / CcsClassification / Element / Level / Term / InformationUnit / RepresentationForm / Object / Characteristic / ConstraintLogic / Determination / ExternalConstraint / DevelopmentStage / Version / DocumentNumber |
| **对象属性** | 34 | adopts / replaces / cites / references / hasPart / issuedBy / proposedBy / administeredBy / draftedBy / publishedBy / classifiedUnder / standardizes / defines / usesTerm / hasRepresentationForm / hasExample / hasNote / citesStandard / referencesClause / involvesObject / specifiesCharacteristic / hasCharacteristic / imposesConstraint / constrainsObject / constrainsCharacteristic / describesAction / referencesExternalResource / isRelatedToPatent / hasDevelopmentStage / includesStandard / hasClause / hasSubClause / hasNormativeElement / hasStructuralElement / hasVersion / hasDocumentNumber |
| **数据属性** | 22 | standardNumber / standardName / issuedDate / effectiveDate / standardStatus / obligationType / partyName / termName / termDefinition / characteristicName / characteristicValue / maxValue / minValue / nominalValue / toleranceValue / unitOfMeasure / levelCode / levelTitle / versionNumber / versionStatus / stageCode / stageStartDate / stageEndDate |
| **公理** | 28 | 10 不相交类 + 11 子类层级 + 1 唯一标识(hasKey) + 5 属性特性(functional/transitive等) |

### 3.3 JSON-LD 1.1 Framing

`JsonLdFramer` 委托 dotNetRDF 的 `JsonLdProcessor.Frame()` 实现 W3C 标准 JSON-LD 1.1 Framing。支持全部帧指令：

| 指令 | 说明 |
|------|------|
| `@type` | 按 rdf:type 过滤节点（单个或数组） |
| `@id` | 按 @id 精确匹配节点 |
| `@embed` | @always / @once / @never / @first / @last / @link |
| `@explicit` | 仅输出帧中列出的属性 |
| `@requireAll` | 要求节点拥有全部指定属性 |
| `@omitDefault` | 缺失属性时不输出空数组 |
| 嵌套帧 | 属性值中的对象作为子帧递归应用 |

**管线**：`GraphSlicerEngine` 执行 SPARQL CONSTRUCT → 合并图 → JsonLdWriter 序列化 → JsonLdFramer 帧化 → 输出文件。

### 3.4 SHACL 约束验证

`ShaclValidator` 包装 dotNetRDF `ShapesGraph.Validate()`，提供:

- `Validate(IGraph data, IGraph shapes)` → `ShaclReport`
- `Conforms(IGraph data, IGraph shapes)` → `bool`

`StandardShapes` 提供 GB/T 48000.3 默认 SHACL shapes（6 个 NodeShape）：

| Shape | 目标类 | 约束 |
|-------|--------|------|
| StandardShape | `std:Standard` | standardNumber: 1..1 string; issuedDate: ≥1; effectiveDate: ≥1; standardStatus: ≥1; standardName: ≥1 |
| OrganizationShape | `std:Organization` | partyName: ≥1 |
| PersonShape | `std:Person` | partyName: ≥1 |
| TermShape | `std:Term` | termName: ≥1 |
| DevelopmentStageShape | `std:DevelopmentStage` | stageCode: 1..1 |

**验证示例**：

```
输入：1 合规 Standard + 1 缺失 standardNumber 的 Standard + 1 合规 Organization + 1 缺失 partyName 的 Organization

Conforms: False
Results: 5
  ❌ [Violation] standardNumber: minCount 1 → std:GB-T-BAD
  ❌ [Violation] issuedDate: minCount 1 → std:GB-T-BAD
  ❌ [Violation] effectiveDate: minCount 1 → std:GB-T-BAD
  ❌ [Violation] standardStatus: minCount 1 → std:GB-T-BAD
  ❌ [Violation] partyName: minCount 1 → std:Org-BAD
```

### 3.5 版本追溯

`VersionTracer` 提供三个核心能力：

| 方法 | 功能 |
|------|------|
| `TraceReplacesChain(graph, iri)` | 沿 `std:replaces` 链从新到旧追溯版本谱系 |
| `GetVersions(graph, iri)` | 通过 `std:hasVersion` 获取所有版本 |
| `Diff(graph, oldIri, newIri)` | 两个版本间的属性级差异对比 |

### 3.6 图切片器（Graph Slicer）

`GraphSlicerEngine` 从多种数据源提取知识图谱切片：

```
数据源                          SPARQL CONSTRUCT
────────────────────────────────────────────────────
RemoteEndpointSource ─┐
(Fuseki/Stardog/      │         ┌──────────┐
 GraphDB/Ontop)       ├────────→│  Merge   │→ JSON-LD → Frame → .jsonld
LocalFilesSource ─────┘         └──────────┘
(.ttl/.rdf/.jsonld/.nt)
```

**数据源适配器**：
- **RemoteEndpointSource**：HTTP POST SPARQL 查询至远程端点，解析 N-Triples 响应，支持 Basic Auth
- **LocalFilesSource**：加载本地 RDF 文件至 `TripleStore`，使用 `LeviathanQueryProcessor` 执行 CONSTRUCT

---

## 4. CLI 命令参考

### 4.1 本体构建

```bash
# 构建标准本体（Turtle 格式）
openclaw ontology build --profile standard

# 指定输出格式和路径
openclaw ontology build --profile standard --format turtle --output ./ontology.ttl
openclaw ontology build --profile standard --format jsonld --output ./ontology.jsonld
openclaw ontology build --profile standard --format rdfxml --output ./ontology.rdf
```

### 4.2 SHACL 验证

```bash
# 验证标准本体自身（无实例，0 violations）
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

在 MetaSkill DAG 中可加入 SHACL 验证步骤：

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

    # Step 2: SHACL 验证
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

`tools/ResourceOntology` 是一个基于 Cytoscape.js 的交互式本体可视化工具，已迁移至 openclaw.net 仓库。

**能力**：
- 4 种图布局：力导向（CoSE）、层级（dagre）、同心圆、广度优先
- 8 种边类型颜色编码：subClassOf / restriction / disjoint / domainRange / typeOf / assertion / property / inverse
- 左侧层次树 + 右侧详情面板（类/属性/实例检查器）
- 拖放 `.owl`/`.rdf`/`.xml` 文件上传
- JSON-LD 导出（compacted / expanded）
- 中英文双语界面
- .NET 10 (ASP.NET Core) + Svelte 5 + TypeScript + Tailwind CSS v4

**使用**：openclaw.net 的 `ontology build --format rdfxml` 输出可直接被该工具加载。

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
| 图切片引擎 | [src/OpenClaw.GraphSlicer/GraphSlicerEngine.cs](src/OpenClaw.GraphSlicer/GraphSlicerEngine.cs) |
| JSON-LD Framing | [src/OpenClaw.GraphSlicer/JsonLdFramer.cs](src/OpenClaw.GraphSlicer/JsonLdFramer.cs) |
| SPARQL 远程端点 | [src/OpenClaw.GraphSlicer/RemoteEndpointSource.cs](src/OpenClaw.GraphSlicer/RemoteEndpointSource.cs) |
| SPARQL 本地文件 | [src/OpenClaw.GraphSlicer/LocalFilesSource.cs](src/OpenClaw.GraphSlicer/LocalFilesSource.cs) |
| OWL 本体构建器 | [src/OpenClaw.Ontology/OntologyBuilder.cs](src/OpenClaw.Ontology/OntologyBuilder.cs) |
| SHACL 验证器 | [src/OpenClaw.Ontology/ShaclValidator.cs](src/OpenClaw.Ontology/ShaclValidator.cs) |
| DAG 验证工具 | [src/OpenClaw.Ontology/OntologyValidateTool.cs](src/OpenClaw.Ontology/OntologyValidateTool.cs) |
| 标准本体 | [src/OpenClaw.StandardOntology/StandardOntology.cs](src/OpenClaw.StandardOntology/StandardOntology.cs) |
| SHACL Shapes | [src/OpenClaw.StandardOntology/StandardShapes.cs](src/OpenClaw.StandardOntology/StandardShapes.cs) |
| 版本追溯 | [src/OpenClaw.StandardOntology/VersionTracer.cs](src/OpenClaw.StandardOntology/VersionTracer.cs) |
| 本体 CLI | [src/OpenClaw.Cli/OntologyCommands.cs](src/OpenClaw.Cli/OntologyCommands.cs) |
| 运行时图加载 | [src/OpenClaw.Agent/Tools/TemporaryGraphTool.cs](src/OpenClaw.Agent/Tools/TemporaryGraphTool.cs) |
| 评估文档 | [docs/zh-CN/gbt48000-3-ontology-evaluation.md](docs/zh-CN/gbt48000-3-ontology-evaluation.md) |
| 全链路文档 | [docs/zh-CN/graph-slicer-metaskill-pipeline.md](docs/zh-CN/graph-slicer-metaskill-pipeline.md) |

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
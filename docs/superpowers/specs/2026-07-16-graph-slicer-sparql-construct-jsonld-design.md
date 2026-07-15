# 图切片器（Graph Slicer）设计说明

- 文档日期：2026-07-16
- 设计状态：待评审
- 适用范围：OpenClaw.NET，外部切片器——SPARQL CONSTRUCT + JSON-LD Framing → MetaSkill 消费
- 文档语言：中文

## 1. 背景与问题定义

MetaSkill DAG 的 `load_temporary_graph` 步骤消费 JSON-LD 临时图文件进行推理。当前临时图由外部
手动生成，缺少一个标准化、可配置、可自动化的切片工具。

需要一个独立的图切片器，支持从多种数据源（SPARQL 端点、本地 RDF 文件、关系数据库 + R2RML）
执行 SPARQL CONSTRUCT 查询，对结果应用 JSON-LD Framing 规范化输出，生成 MetaSkill 可直接消费的
`.jsonld` 文件。

## 2. 设计目标与非目标

### 2.1 目标

1. 支持三种数据源：远程 SPARQL 端点、本地 RDF 文件、关系数据库 + R2RML 映射
2. SPARQL CONSTRUCT → JSON-LD 序列化 → JSON-LD Framing → 文件输出的完整管线
3. 基于 YAML/JSON profile 的声明式配置
4. 独立 CLI 工具，与 Gateway 解耦
5. 可被 Automation 调用实现定时自动切片
6. 基于 dotNetRDF 3.3.0，不引入其他 RDF 库

### 2.2 非目标

1. 不实现 RDF 推理或 OWL 本体推理
2. 不实现 R2RML/Ontop 引擎本身（用户自行部署 Ontop，CLI 通过 SPARQL 端点连接）
3. 不替代 dotNetRDF 中的 SPARQL 处理器
4. 不改变 MetaSkill DAG 或 `load_temporary_graph` 的行为

## 3. 方案选择

**采用方案：独立 CLI + dotNetRDF**

| 候选方案 | 评价 |
|---------|------|
| A: Gateway 内置服务 | 拒绝：dotNetRDF 依赖污染核心，边界模糊 |
| B: 独立 CLI + dotNetRDF | **采用**：依赖隔离、边界清晰、对接已有 Automation 机制 |
| C: MetaSkill DAG 步骤 | 拒绝：增加 DAG 引擎复杂度，不适合需要外部认证的数据源 |

## 4. 总体架构

```
┌─ Graph Slice Profile (YAML/JSON) ───────────────────────────────────┐
│                                                                      │
│  sources: [RemoteEndpointSource | LocalFilesSource]                  │
│  constructQuery: SPARQL CONSTRUCT                                    │
│  frame: JSON-LD Frame object                                         │
│  output: path + limits + compaction                                  │
│                                                                      │
└──────────────────────────────────────────────────────────────────────┘
        │
        ▼
┌─ Slice Pipeline ────────────────────────────────────────────────────┐
│                                                                      │
│  1. Execute CONSTRUCT across all sources                             │
│     ┌──────────────────────────────────────────┐                    │
│     │ foreach source:                           │                    │
│     │   if RemoteEndpoint:                      │                    │
│     │     SparqlRemoteEndpoint.QueryWithResultGraph(constructQuery)  │
│     │   if LocalFiles:                          │                    │
│     │     FileLoader.Load(path) → IGraph        │                    │
│     │     LeviathanQueryProcessor.ProcessQuery(constructQuery)       │
│     │   merge into mergedGraph                  │                    │
│     └──────────────────────────────────────────┘                    │
│                                                                      │
│  2. Convert to JSON-LD document                                      │
│     JsonLdProcessor.FromRDF(mergedGraph) → JObject                  │
│                                                                      │
│  3. Apply JSON-LD frame                                              │
│     JsonLdProcessor.Frame(document, frameObject) → JObject          │
│                                                                      │
│  4. Serialize and write                                              │
│     JsonLdProcessor.ToFlatString(framed) → .jsonld file             │
│                                                                      │
└──────────────────────────────────────────────────────────────────────┘
        │
        ▼
  ./tmp/quality-slice.jsonld  ←  被 MetaSkill DAG load_temporary_graph 消费
```

### 4.1 数据源适配策略

| 数据源 | dotNetRDF API | 适配器 |
|--------|---------------|--------|
| A: SPARQL 端点（Fuseki/Stardog/GraphDB） | `SparqlRemoteEndpoint` | `RemoteEndpointSource` |
| B: 本地 RDF 文件（.ttl/.rdf/.jsonld/.nt） | `FileLoader` + `LeviathanQueryProcessor` | `LocalFilesSource` |
| C: 关系 DB + R2RML（Ontop 等） | 同 A — Ontop 暴露虚拟 SPARQL 端点 | `RemoteEndpointSource` |

A 和 C 收敛到同一个适配器。用户需要为 C 单独部署 Ontop，CLI 不管理 Ontop 生命周期。

## 5. CLI 设计

### 5.1 命令

```bash
# 基本用法——按 profile 执行切片
openclaw graph slice --profile production

# 覆盖输出路径
openclaw graph slice --profile production --output ./today.jsonld

# dry run（执行 CONSTRUCT 但不写文件，打印统计）
openclaw graph slice --profile production --dry-run

# 列出 profile 的源、查询摘要、预期输出路径
openclaw graph slice --profile production --info
```

### 5.2 项目结构

新建独立类库项目 `src/OpenClaw.GraphSlicer/`。CLI 项目引用此类库，在 `Program.cs` 注册 `graph` 命令。

```
src/OpenClaw.GraphSlicer/              ← 新独立类库
  OpenClaw.GraphSlicer.csproj           （仅引用 dotNetRDF + OpenClaw.Core）
  ISparqlSource.cs                      # 数据源接口
  RemoteEndpointSource.cs               # 远程 SPARQL 端点适配器
  LocalFilesSource.cs                   # 本地 RDF 文件适配器
  GraphSlicerEngine.cs                  # 编排引擎

src/OpenClaw.Cli/
  GraphSliceCommands.cs                 # CLI 命令入口（引用 GraphSlicer 引擎）

src/OpenClaw.Core/Models/
  GraphSliceProfile.cs                  # 配置模型
```

**依赖链：**

```
OpenClaw.Cli
  ├─ OpenClaw.GraphSlicer  ← dotNetRDF 3.3.0 隔离在此
  │     └─ OpenClaw.Core
  └─ OpenClaw.Core
```

dotNetRDF 的依赖封闭在 `OpenClaw.GraphSlicer` 类库内。CLI 只通过 `GraphSlicerEngine` 的
公共 API 调用，不直接接触 RDF 类型。Gateway 完全不触及这个依赖。

### 5.3 核心类型签名

```csharp
// 数据源接口
internal interface ISparqlSource
{
    Task<IGraph> ExecuteConstructAsync(string constructQuery, CancellationToken ct);
}

// 远程端点实现
internal sealed class RemoteEndpointSource : ISparqlSource
{
    public RemoteEndpointSource(
        Uri endpoint,
        NetworkCredential? credentials,
        int timeoutSeconds,
        string? defaultGraphUri);
}

// 本地文件实现
internal sealed class LocalFilesSource : ISparqlSource
{
    public LocalFilesSource(
        IReadOnlyList<string> paths,
        string? namedGraphUri);
}

// 编排引擎
internal sealed class GraphSlicerEngine
{
    public Task<SliceResult> ExecuteAsync(
        GraphSliceProfile profile,
        CancellationToken ct);
}
```

## 6. 配置模型

### 6.1 Profile 结构

```csharp
public sealed class GraphSliceProfile
{
    public List<SliceSourceConfig> Sources { get; init; } = [];
    public string Construct { get; init; } = "";
    public JsonElement? Frame { get; init; }
    public SliceOutputConfig Output { get; init; } = new();
}

public sealed class SliceSourceConfig
{
    // "remote-endpoint" | "local-files"
    public string Kind { get; init; } = "remote-endpoint";

    // remote-endpoint 字段
    public string? Endpoint { get; init; }
    public SliceAuthConfig? Auth { get; init; }
    public int TimeoutSeconds { get; init; } = 60;
    public string? DefaultGraphUri { get; init; }

    // local-files 字段
    public List<string>? Paths { get; init; }
    public string? NamedGraphUri { get; init; }
}

public sealed class SliceAuthConfig
{
    // "none" | "basic" | "digest"
    public string Type { get; init; } = "none";
    public string? UsernameEnv { get; init; }
    public string? PasswordEnv { get; init; }
}

public sealed class SliceOutputConfig
{
    public string Path { get; init; } = "./tmp/graph-slice.jsonld";
    public int MaxTriples { get; init; } = 50000;
    public bool Compaction { get; init; } = true;
}
```

### 6.2 配置文件示例

```yaml
# graph-slice.yaml —— 放在 OpenClaw 配置目录下

profiles:
  production:
    sources:
      - kind: remote-endpoint
        endpoint: https://fuseki.example.com/production/query
        auth:
          type: basic
          usernameEnv: FUSEKI_USER
          passwordEnv: FUSEKI_PASS
        timeoutSeconds: 60
        defaultGraphUri: "https://data.example.com/graph/production"

      - kind: local-files
        paths:
          - ./data/reference-data.ttl
          - ./data/quality-taxonomy.jsonld

      - kind: remote-endpoint
        endpoint: http://localhost:8080/sparql    # Ontop 虚拟端点
        auth:
          type: none
        timeoutSeconds: 30

    construct: |
      PREFIX ex: <http://openclaw.net/ontology/industrial#>
      CONSTRUCT {
        ?batch ex:defectRate ?rate ;
               ex:hasMaterial ?material .
        ?material ex:supplierQuality ?sq ;
                  ex:name ?materialName .
      }
      WHERE {
        ?batch a ex:ProductBatch ;
               ex:hasMeasurement ?m .
        ?m ex:value ?rate ;
           ex:type "defect_rate" .
        OPTIONAL {
          ?batch ex:usesMaterial ?material .
          ?material ex:qualityScore ?sq ;
                    rdfs:label ?materialName .
        }
      }

    frame:
      "@context": "http://openclaw.net/ontology/industrial.jsonld"
      "@type": "ex:QualitySlice"
      "ex:batches":
        "@type": "ex:ProductBatch"
        "ex:id": {}
        "ex:defectRate": {}
        "ex:materials":
          "@type": "ex:Material"
          "ex:name": {}
          "ex:supplierQuality": {}

    output:
      path: "./tmp/quality-slice.jsonld"
      maxTriples: 50000
      compaction: true

  staging:
    sources:
      - kind: local-files
        paths:
          - ./test-data/sample-batch.ttl
          - ./test-data/sample-materials.jsonld

    construct: |
      PREFIX ex: <http://openclaw.net/ontology/industrial#>
      CONSTRUCT { ?s ?p ?o }
      WHERE  { ?s ?p ?o }

    frame:
      "@context": "http://openclaw.net/ontology/industrial.jsonld"

    output:
      path: "./tmp/staging-slice.jsonld"
      maxTriples: 1000
      compaction: true
```

## 7. 执行语义

### 7.1 多源合并

1. 每个 source 独立执行 CONSTRUCT，产生各自的 `IGraph`
2. 各图合并到一个结果图：`mergedGraph.Assert(triplesFromSource)`
3. 合并后执行 JSON-LD 转换

合并策略：Plain UNION。不处理 blank node 冲突（如有需要，用户应在 SPARQL 中规范化为 IRI）。

### 7.2 错误处理

| 场景 | 行为 |
|------|------|
| 远程端点超时 | 返回错误，退出码 2 |
| 远程端点 HTTP 5xx | 返回错误，退出码 2（不重试） |
| 本地文件不存在 | 返回错误，退出码 2 |
| CONSTRUCT 导致超量 triple | 截断并写入 "truncated" 告警到 stderr，退出码 0 |
| 合并图为空 | 返回错误，退出码 3（空结果） |
| 所有源成功且结果非空 | 写入文件，退出码 0 |

### 7.3 性能限制

- `MaxTriples` 默认 50000，防止大图撑满 MetaSkill DAG 上下文
- `TimeOutSeconds` 每个源独立超时，不累积
- 文件加载到内存 `TripleStore`，不流式处理（dotNetRDF 当前 API 限制，10万 triple 以内可接受）

### 7.4 与现有全链路的对接

```
openclaw graph slice --profile production
      ↓ 输出
./tmp/quality-slice.jsonld
      ↓ 被 MetaSkill DAG 第一步消费
- id: load_graph
  kind: tool_call
  tool: load_temporary_graph
  with:
    path: "./tmp/quality-slice.jsonld"
    format: "jsonld"
      ↓
下一步 llm_chat 推理 → action_execute 回写
```

Automation 集成（定时切片）：

```yaml
# Automation 模板配置
- id: daily-quality-slice
  kind: shell
  cron: "0 6 * * *"
  command: openclaw graph slice --profile production
```

## 8. dotNetRDF 能力评估

| 需求 | API | 状态 |
|------|-----|------|
| SPARQL CONSTRUCT（远程） | `SparqlRemoteEndpoint.QueryWithResultGraph()` | ✅ 支持 Basic/Digest 认证 |
| SPARQL CONSTRUCT（本地） | `LeviathanQueryProcessor.ProcessQuery()` → `TripleStore` | ✅ |
| JSON-LD 1.1 序列化 | `JsonLdProcessor.FromRDF()` / `ToFlatString()` | ✅ W3C 兼容 |
| JSON-LD Framing | `JsonLdProcessor.Frame()` | ✅ 支持 `@type`/`@embed`/`@explicit` |
| 多格式加载 | `FileLoader.Load()` | ✅ .ttl/.rdf/.jsonld/.nt/.n3 |
| 图合并 | `IGraph.Merge()` / `Assert()` | ✅ |
| .NET 8/9 | NuGet 3.3.0 | ✅ |
| AOT | 不要求 | CLI 项目不需要 AOT，不影响 Gateway AOT |

**不需要的 dotNetRDF 部分：** RDF 推理引擎、OWL 推理、Stardog/AllegroGraph 专用连接器（用通用 HTTP SPARQL 端点）。

## 9. 依赖

dotNetRDF 封闭在 `OpenClaw.GraphSlicer` 类库内，CLI 通过公共 API 间接调用，Gateway 完全不触及：

| 包 | 位置 | 用途 |
|----|------|------|
| dotNetRDF 3.3.0 | `OpenClaw.GraphSlicer.csproj` | RDF 解析、SPARQL 查询、JSON-LD 序列化与 Framing |
| OpenClaw.Core | `OpenClaw.GraphSlicer.csproj` | 配置模型复用 |
| OpenClaw.GraphSlicer | `OpenClaw.Cli.csproj` | CLI 通过项目引用调用引擎 |

不引入额外的 SPARQL 或 JSON-LD 库。

## 10. 测试策略

| 测试类型 | 内容 |
|---------|------|
| 单元测试 | `RemoteEndpointSource` 用 Mock HTTP Server 模拟 SPARQL 端点 |
| 单元测试 | `LocalFilesSource` 用本地 .ttl/.jsonld 测试文件 |
| 单元测试 | `GraphSlicerEngine` 多源合并 + Framing + 输出 |
| 集成测试 | 完整 profile → 输出文件 → `load_temporary_graph` 可正确读取（E2E 与现有全链路衔接） |
| 负向测试 | 超时、文件不存在、空结果、超量 triple |

## 11. 项目文件变更汇总

| 操作 | 文件 | 说明 |
|------|------|------|
| 新建 | `src/OpenClaw.GraphSlicer/OpenClaw.GraphSlicer.csproj` | 独立类库，依赖 dotNetRDF 3.3.0 + OpenClaw.Core |
| 新建 | `src/OpenClaw.GraphSlicer/ISparqlSource.cs` | 数据源接口 |
| 新建 | `src/OpenClaw.GraphSlicer/RemoteEndpointSource.cs` | 远程 SPARQL 端点适配器 |
| 新建 | `src/OpenClaw.GraphSlicer/LocalFilesSource.cs` | 本地 RDF 文件适配器 |
| 新建 | `src/OpenClaw.GraphSlicer/GraphSlicerEngine.cs` | 编排引擎 |
| 新建 | `src/OpenClaw.Core/Models/GraphSliceProfile.cs` | 配置模型 |
| 修改 | `src/OpenClaw.Core/Models/Session.cs` | 新增 JsonSerializable 声明 |
| 修改 | `src/OpenClaw.Cli/OpenClaw.Cli.csproj` | 新增 OpenClaw.GraphSlicer 项目引用 |
| 新建 | `src/OpenClaw.Cli/GraphSliceCommands.cs` | CLI 命令入口 |
| 修改 | `src/OpenClaw.Cli/Program.cs` | 注册 `graph` 顶层命令 |
| 新建 | `src/OpenClaw.Tests/GraphSliceCommandsTests.cs` | 单元测试 + 集成测试 |
| 修改 | `docs/zh-CN/meta-skill-harness-action-writeback-pipeline.md` | 补充外部切片器章节引用 |
| 修改 | `docs/meta-skill-harness-action-writeback-pipeline.md` | 同上（英文版） |

## 12. 风险与缓解

1. **风险：dotNetRDF JSON-LD Framing 实现与 W3C 规范有偏差**
   - 缓解：测试用标准的 JSON-LD Playground 结果作为 expected frame 输出
2. **风险：本地大文件（>10万 triple）内存压力**
   - 缓解：`MaxTriples` 截断 + 文档建议外部工具预过滤
3. **风险：Ontop 版本与 SPARQL 端点兼容性**
   - 缓解：使用标准 SPARQL HTTP 协议，不依赖 Ontop 专有扩展
4. **风险：dotNetRDF 反射路径与 AOT 不兼容**
   - 缓解：dotNetRDF 只在 CLI 项目引用，CLI 不需要 AOT；不影响 Gateway AOT

## 13. 结论

本设计以 dotNetRDF 3.3.0 为核心，构建了一个独立 CLI 图切片器，满足从 SPARQL 端点、本地
RDF 文件、关系数据库（+R2RML）三种数据源执行 SPARQL CONSTRUCT → JSON-LD Framing → 输出
.jsonld 文件的完整需求。切片器与 MetaSkill DAG 通过 `load_temporary_graph` 以文件形式松耦合
对接，可被 Automation 调度实现自动化。

dotNetRDF 完全覆盖所需能力，无需额外 RDF 库。

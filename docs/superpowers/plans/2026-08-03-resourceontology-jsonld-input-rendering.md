# ResourceOntology JSON-LD 输入渲染 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 `tools/ResourceOntology` 能把 **OWL 的 JSON-LD 序列化**（形态 A）作为渲染输入，解析结果与现有 RDF/XML 路径对等；为后续形态 C / O2 对齐预留边界说明，但不实现形态 B 或 O3。

**Architecture:** 服务端在 `OntologyParser` 按扩展名与轻量内容嗅探选择 `RdfXmlParser` 或 `JsonLdParser`，统一进入现有 `BuildModel` → `OntologyDto`。导出路径复用同一「加载为 IGraph」辅助，去掉重复硬编码 RDF/XML。前端只放宽文件选择、Content-Type 与文案。独立 xUnit 项目覆盖 round-trip 与非法输入。

**Tech Stack:** .NET 10、C#、dotNetRDF 3.3.1（默认不升 3.5.2）、xUnit、Svelte 5、ASP.NET Core Minimal API

**Spec:** [docs/superpowers/specs/2026-08-03-resourceontology-jsonld-rendering-feasibility-design.md](../specs/2026-08-03-resourceontology-jsonld-rendering-feasibility-design.md)

## Global Constraints

- **v1 范围 = 形态 A（OWL-as-JSON-LD）**。形态 B 完整 OWL 体验、O3 强复用 GraphSlicer **不做**。
- **不破坏** 现有 `.owl` / `.rdf` / `.xml` 路径与 `OntologyDto` JSON 契约（字段名/形状）。
- **UI 只消费 `OntologyDto`**；不按序列化格式重做 Cytoscape / 树 / 详情。
- **远程 `@context`：v1 不承诺**。fixture 与文档要求自包含 context；解析失败 → 可读 400。
- **不承诺** JSON-LD bit-exact / 排版稳定 round-trip。
- **dotNetRDF：** A0/A1 默认保持 `3.3.1`。仅当 A0 证明 3.3.1 无法读回自导出 JSON-LD，且升级是最小修复时，才改 `ResourceOntology.Api.csproj` 并在提交说明中记录。
- **不添加** 对 `src/OpenClaw.GraphSlicer` 的项目引用（O1 手法）。
- **Trimmed publish：** Task 6 为 smoke；失败可记 follow-up，**不阻塞** `dotnet run` 开发路径的 A1 验收（除非用户当场要求 publish 必过）。
- 路径均相对仓库根 `E:\GitHub\openclaw.net`（或当前 clone 根）。
- Commit message：英文 conventional（`test:` / `feat:` / `docs:`）。

---

## Scope Check

单一子系统（输入解析 + API 列表 + 薄 UI/文档），可独立验收。形态 C fixture 矩阵、通用图模式另开计划。

| 档 | 内容 | 本计划 |
| --- | --- | --- |
| A0 | JSON-LD 读回 + DTO 关键计数对等 | 是 |
| A1 | API/UX/文档/RDFXML 回归 | 是 |
| Publish smoke | trimmed 可选 | 是（可记债） |
| C1 / B / O3 | 主仓深度兼容、通用图、强复用 | **否** |

## File Structure

- Create: `tools/ResourceOntology/server/ResourceOntology.Api.Tests/ResourceOntology.Api.Tests.csproj` — xUnit 工程
- Create: `tools/ResourceOntology/server/ResourceOntology.Api.Tests/OntologyParserJsonLdTests.cs` — parser 测试
- Modify: `tools/ResourceOntology/server/Services/OntologyParser.cs` — 格式分派
- Modify: `tools/ResourceOntology/server/Program.cs` — files glob、共享 LoadGraph、export 复用
- Modify: `tools/ResourceOntology/client/src/lib/api.ts` — 上传 Content-Type
- Modify: `tools/ResourceOntology/client/src/App.svelte` — accept / 拖放
- Modify: `tools/ResourceOntology/client/src/lib/i18n.svelte.ts` — 英文文案
- Modify: `tools/ResourceOntology/client/src/lib/locales/zh.json` — 中文文案
- Modify: `tools/ResourceOntology/README.md` — 输入边界 + API 表纠偏

**不改：** `BuildModel` 业务语义（除非暴露明确 bug）、`graph.ts` / GraphView、`src/OpenClaw.*`。

---

### Task 1: 测试工程 + 失败用例（A0 TDD）

**Files:**
- Create: `tools/ResourceOntology/server/ResourceOntology.Api.Tests/ResourceOntology.Api.Tests.csproj`
- Create: `tools/ResourceOntology/server/ResourceOntology.Api.Tests/OntologyParserJsonLdTests.cs`

**Interfaces:**
- Consumes: `OntologyParser.Parse(TextReader, string)`、`ParseFile(string)`、`OntologyDto.Stats`
- Produces: 红灯测试，锁定 JSON-LD 源名 `.jsonld` 的 round-trip 行为

- [ ] **Step 1: 创建测试 csproj**

创建 `tools/ResourceOntology/server/ResourceOntology.Api.Tests/ResourceOntology.Api.Tests.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <RootNamespace>ResourceOntology.Api.Tests</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="dotNetRDF" Version="3.3.1" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\ResourceOntology.Api.csproj" />
  </ItemGroup>

</Project>
```

若还原失败：打开 `src/OpenClaw.Tests/OpenClaw.Tests.csproj`，把 xUnit / Test.Sdk 版本改成与仓库一致。

- [ ] **Step 2: 编写测试文件**

创建 `tools/ResourceOntology/server/ResourceOntology.Api.Tests/OntologyParserJsonLdTests.cs`：

```csharp
using ResourceOntology.Api.Services;
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Writing;
using Xunit;

namespace ResourceOntology.Api.Tests;

public class OntologyParserJsonLdTests
{
    private static string FindResourceOwlPath()
    {
        var start = new DirectoryInfo(AppContext.BaseDirectory);
        for (var dir = start; dir != null; dir = dir.Parent)
        {
            var direct = Path.Combine(dir.FullName, "ontology", "Resource.owl");
            if (File.Exists(direct))
                return direct;

            // From server/ResourceOntology.Api.Tests/bin/{config}/net10.0 → tools/ResourceOntology/ontology
            var relative = Path.GetFullPath(Path.Combine(dir.FullName, "ontology", "Resource.owl"));
            if (File.Exists(relative))
                return relative;
        }

        // Explicit fallback from test project location (…/Api.Tests/bin/… → ../../../../ontology)
        var fromBase = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "ontology", "Resource.owl"));
        if (File.Exists(fromBase))
            return fromBase;

        throw new FileNotFoundException("Could not locate tools/ResourceOntology/ontology/Resource.owl");
    }

    private static string ExportOwlFileToJsonLd(string owlPath)
    {
        var graph = new Graph();
        new RdfXmlParser().Load(graph, owlPath);
        var store = new TripleStore();
        store.Add(graph);
        var writer = new JsonLdWriter(new JsonLdWriterOptions { UseNativeTypes = true });
        using var sw = new StringWriter();
        writer.Save(store, sw);
        return sw.ToString();
    }

    [Fact]
    public void Parse_JsonLdRoundTrip_MatchesOwlCriticalStats()
    {
        var parser = new OntologyParser();
        var owlPath = FindResourceOwlPath();
        var fromOwl = parser.ParseFile(owlPath);

        var jsonLd = ExportOwlFileToJsonLd(owlPath);
        using var reader = new StringReader(jsonLd);
        var fromJsonLd = parser.Parse(reader, "Resource.jsonld");

        Assert.True(fromOwl.Stats.Classes > 0, "fixture OWL should have classes");
        Assert.True(
            fromOwl.Stats.Classes == fromJsonLd.Stats.Classes,
            $"Classes owl={fromOwl.Stats.Classes} jsonld={fromJsonLd.Stats.Classes}");
        Assert.True(
            fromOwl.Stats.Individuals == fromJsonLd.Stats.Individuals,
            $"Individuals owl={fromOwl.Stats.Individuals} jsonld={fromJsonLd.Stats.Individuals}");
        Assert.True(
            fromOwl.Stats.ObjectProperties == fromJsonLd.Stats.ObjectProperties,
            $"ObjectProperties owl={fromOwl.Stats.ObjectProperties} jsonld={fromJsonLd.Stats.ObjectProperties}");
        Assert.True(
            fromOwl.Stats.DatatypeProperties == fromJsonLd.Stats.DatatypeProperties,
            $"DatatypeProperties owl={fromOwl.Stats.DatatypeProperties} jsonld={fromJsonLd.Stats.DatatypeProperties}");
        Assert.True(
            fromOwl.Stats.SubClassAxioms == fromJsonLd.Stats.SubClassAxioms,
            $"SubClassAxioms owl={fromOwl.Stats.SubClassAxioms} jsonld={fromJsonLd.Stats.SubClassAxioms}");
    }

    [Fact]
    public void Parse_InvalidJsonLd_Throws()
    {
        var parser = new OntologyParser();
        using var reader = new StringReader("{ this is not json-ld");
        Assert.ThrowsAny<Exception>(() => parser.Parse(reader, "bad.jsonld"));
    }

    [Fact]
    public void ParseFile_RdfXml_StillWorks()
    {
        var parser = new OntologyParser();
        var dto = parser.ParseFile(FindResourceOwlPath());
        Assert.True(dto.Stats.Classes > 0);
        Assert.True(dto.Stats.Individuals > 0);
    }
}
```

- [ ] **Step 3: 运行测试，确认 JSON-LD round-trip 失败**

```powershell
dotnet test tools/ResourceOntology/server/ResourceOntology.Api.Tests/ResourceOntology.Api.Tests.csproj --nologo
```

**Expected:**
- `ParseFile_RdfXml_StillWorks`：**PASS**（现有 RDF/XML 仍可用）
- `Parse_JsonLdRoundTrip_MatchesOwlCriticalStats`：**FAIL**（当前 `Parse` 一律走 `RdfXmlParser`，JSON-LD 文本会抛解析异常，或无法得到对等 Stats）
- `Parse_InvalidJsonLd_Throws`：可能 PASS（抛异常）或行为未定义；以「实现后必须 ThrowsAny」为准，若当前已 throw 可保持红/绿均可，但 round-trip 必须先红

- [ ] **Step 4: Commit**

```powershell
git add tools/ResourceOntology/server/ResourceOntology.Api.Tests/ResourceOntology.Api.Tests.csproj tools/ResourceOntology/server/ResourceOntology.Api.Tests/OntologyParserJsonLdTests.cs
git commit -m "test: add ResourceOntology JSON-LD parser failing tests"
```

---

### Task 2: OntologyParser 格式分派（A0 实现）

**Files:**
- Modify: `tools/ResourceOntology/server/Services/OntologyParser.cs`

**Interfaces:**
- Consumes: `VDS.RDF.Parsing.JsonLdParser`、`RdfXmlParser`、`IGraph`
- Produces:
  - `OntologyParser.Parse(TextReader reader, string sourceName)` — 按 `sourceName` 与内容嗅探选 parser
  - `OntologyParser.ParseFile(string path)` — 按扩展名选 parser
  - 内部建议：`static void LoadGraph(IGraph g, TextReader reader, string sourceName)`、`static bool LooksLikeJsonLd(string sourceName, string? peek)`

- [ ] **Step 1: 实现分派逻辑（最小改动）**

修改 `OntologyParser.cs`：保留 `BuildModel` 主体不动；替换 `Parse` / `ParseFile` 入口。目标形态：

```csharp
public OntologyDto Parse(TextReader reader, string sourceName)
{
    // TextReader may not seek; buffer once.
    var text = reader.ReadToEnd();
    IGraph g = new Graph();
    LoadGraphFromText(g, text, sourceName);
    return BuildModel(g, sourceName);
}

public OntologyDto ParseFile(string path)
{
    IGraph g = new Graph();
    LoadGraphFromFile(g, path);
    return BuildModel(g, Path.GetFileName(path));
}

internal static void LoadGraphFromFile(IGraph g, string path)
{
    var ext = Path.GetExtension(path).ToLowerInvariant();
    if (ext is ".jsonld" or ".json")
    {
        new JsonLdParser().Load(g, path);
        return;
    }

    if (ext is ".owl" or ".rdf" or ".xml" or "")
    {
        new RdfXmlParser().Load(g, path);
        return;
    }

    // Unknown extension: sniff file head
    var head = File.ReadAllText(path);
    LoadGraphFromText(g, head, path);
}

internal static void LoadGraphFromText(IGraph g, string text, string sourceName)
{
    var ext = Path.GetExtension(sourceName).ToLowerInvariant();
    var trimmed = text.TrimStart();
    var preferJsonLd =
        ext is ".jsonld" or ".json"
        || trimmed.StartsWith('{')
        || trimmed.StartsWith('[');

    if (preferJsonLd)
    {
        using var sr = new StringReader(text);
        new JsonLdParser().Load(g, sr);
        return;
    }

    using var srXml = new StringReader(text);
    new RdfXmlParser().Load(g, srXml);
}
```

实现要求：
- `using VDS.RDF.Parsing;` 已足够覆盖 `JsonLdParser`（与现有 `RdfXmlParser` 同命名空间）。
- 若 `JsonLdParser().Load(IGraph, string path)` 签名在 3.3.1 不可用，改为 `using var fs = File.OpenText(path); new JsonLdParser().Load(g, fs);`。
- **不要**在 v1 配置自定义远程 context loader；用库默认行为。
- XML 声明/`<rdf:RDF` 仍走 RDF/XML。
- 扩展名为 `.json` 但内容是普通 JSON 非 JSON-LD：允许抛解析异常（测试 `Parse_InvalidJsonLd_Throws` 覆盖非法文本）。

可选：把 `LoadGraphFromFile` / `LoadGraphFromText` 做成 `public` 或 `internal`，供 `Program.cs` export 复用（Task 3）。若保持 private，Task 3 可复制同一规则到本地 static 函数，但 **优先 internal + `InternalsVisibleTo`**。

若测试项目需访问 internal：

在 `ResourceOntology.Api.csproj` 增加：

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="ResourceOntology.Api.Tests" />
  </ItemGroup>
```

- [ ] **Step 2: 跑测试至全绿**

```powershell
dotnet test tools/ResourceOntology/server/ResourceOntology.Api.Tests/ResourceOntology.Api.Tests.csproj --nologo
```

**Expected:** 3 个测试全部 **PASS**。

若 round-trip Stats 不对等：
1. 先打印两侧 `Stats` 与 `Classes.Count`，确认是否只是 blank node / restriction 计数差。
2. **允许** `Restrictions` / `DisjointAxioms` 有小幅差异时，先放宽断言为 Classes/Individuals/ObjectProperties/DatatypeProperties 四项（与 spec「关键计数」一致），但必须在测试注释写明原因。
3. 若 Classes 都对不上：检查 JSON-LD 是否未正确 load（图 triple 数为 0）——修 parser 而非改期望为 0。

- [ ] **Step 3: Commit**

```powershell
git add tools/ResourceOntology/server/Services/OntologyParser.cs tools/ResourceOntology/server/ResourceOntology.Api.csproj tools/ResourceOntology/server/ResourceOntology.Api.Tests
git commit -m "feat: parse OWL JSON-LD inputs in ResourceOntology OntologyParser"
```

---

### Task 3: API 列表 / 加载 / 导出共用图加载（A1 后端）

**Files:**
- Modify: `tools/ResourceOntology/server/Program.cs`
- Modify: `tools/ResourceOntology/server/Services/OntologyParser.cs`（若需暴露 LoadGraph helpers）

**Interfaces:**
- Consumes: `OntologyParser.Parse` / `ParseFile` / 共享 `LoadGraph*`
- Produces:
  - `GET /api/ontology/files` — 返回 `.owl` **与** `.jsonld`（可选 `.json`，但列表默认 **仅** `.owl` + `.jsonld`，避免误列无关 json）
  - `GET /api/ontology/load?file=` — 已有；依赖 `ParseFile` 即支持 `.jsonld`
  - `POST /api/ontology/parse` — 已有；依赖 `Parse` + `name` 扩展名
  - `GET|POST /api/ontology/export-jsonld` — 用共享加载替代硬编码 `RdfXmlParser`，使 **已是 JSON-LD 的文件** 也能导出/再序列化（不要求 bit-exact）

- [ ] **Step 1: 扩展 files 列表**

将 `Program.cs` 中：

```csharp
list.Files = Directory.GetFiles(dir, "*.owl")
```

替换为合并多扩展名并去重排序，例如：

```csharp
static IEnumerable<string> EnumerateOntologyFiles(string dir)
{
    foreach (var pattern in new[] { "*.owl", "*.jsonld" })
    {
        foreach (var f in Directory.GetFiles(dir, pattern))
            yield return f;
    }
}

// ...
list.Files = EnumerateOntologyFiles(dir)
    .Select(f => new OntologyFileEntry
    {
        Name = Path.GetFileName(f),
        DisplayName = Path.GetFileNameWithoutExtension(f)
    })
    .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
    .Select(g => g.First())
    .OrderBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase)
    .ToList();
```

- [ ] **Step 2: export-jsonld 共用加载**

在 `OntologyParser` 增加 public/internal 方法（推荐）：

```csharp
public IGraph LoadGraph(string path)
{
    var g = new Graph();
    LoadGraphFromFile(g, path);
    return g;
}

public IGraph LoadGraph(TextReader reader, string sourceName)
{
    var text = reader.ReadToEnd();
    var g = new Graph();
    LoadGraphFromText(g, text, sourceName);
    return g;
}
```

`GET /api/ontology/export-jsonld` 中删除：

```csharp
var graph = new VDS.RDF.Graph();
var parser = new RdfXmlParser();
using (var reader = new StreamReader(File.OpenRead(path)))
{
    parser.Load(graph, reader);
}
```

改为使用单例 `OntologyParser`（文件顶部已有 `var parser = ...`，注意与局部变量名冲突——export handler 内不要 `var parser = new RdfXmlParser()`，改用外层 parser 服务）：

```csharp
var graph = parser.LoadGraph(path);
```

`POST /api/ontology/export-jsonld` 中同样把 `RdfXmlParser` 换成：

```csharp
using var sr = new StringReader(owlText);
var graph = parser.LoadGraph(sr, fileName);
```

变量名 `owlText` 可保留或改名为 `ontologyText`（可选，非必须大范围重命名）。

- [ ] **Step 3: 增加 API 级测试（可选但推荐）**

在同一测试项目新增 `OntologyParserDispatchTests` 或扩展现有文件：

```csharp
[Fact]
public void LoadGraph_JsonLdExtension_LoadsTriples()
{
    var p = new OntologyParser();
    var owlPath = /* FindResourceOwlPath() */;
    var json = /* ExportOwlFileToJsonLd(owlPath) */;
    var tmp = Path.Combine(Path.GetTempPath(), "resource-ontology-a1-" + Guid.NewGuid().ToString("n") + ".jsonld");
    try
    {
        File.WriteAllText(tmp, json);
        var g = p.LoadGraph(tmp);
        Assert.True(g.Triples.Count > 0);
    }
    finally
    {
        if (File.Exists(tmp)) File.Delete(tmp);
    }
}
```

（需 Task 2 已暴露 `LoadGraph`。）

- [ ] **Step 4: 运行测试**

```powershell
dotnet test tools/ResourceOntology/server/ResourceOntology.Api.Tests/ResourceOntology.Api.Tests.csproj --nologo
dotnet build tools/ResourceOntology/server/ResourceOntology.Api.csproj --nologo
```

**Expected:** 测试 PASS；API 项目 build 成功。

- [ ] **Step 5: Commit**

```powershell
git add tools/ResourceOntology/server/Program.cs tools/ResourceOntology/server/Services/OntologyParser.cs tools/ResourceOntology/server/ResourceOntology.Api.Tests
git commit -m "feat: list and load JSON-LD ontologies in ResourceOntology API"
```

---

### Task 4: 前端上传 / 文案（A1 UI）

**Files:**
- Modify: `tools/ResourceOntology/client/src/lib/api.ts`
- Modify: `tools/ResourceOntology/client/src/App.svelte`
- Modify: `tools/ResourceOntology/client/src/lib/i18n.svelte.ts`
- Modify: `tools/ResourceOntology/client/src/lib/locales/zh.json`

**Interfaces:**
- Consumes: `POST /api/ontology/parse?name=`
- Produces: 用户可选择 `.jsonld` / `.json`；请求 Content-Type 与扩展名一致

- [ ] **Step 1: api.ts 按扩展名设置 Content-Type**

将 `parseOntologyFile` 改为：

```typescript
function contentTypeForOntologyFile(file: File): string {
  const name = file.name.toLowerCase()
  if (name.endsWith('.jsonld') || name.endsWith('.json')) return 'application/ld+json'
  if (name.endsWith('.ttl') || name.endsWith('.n3')) return 'text/turtle'
  return 'application/rdf+xml'
}

/** Parse an ontology file (RDF/XML or OWL-as-JSON-LD) chosen by the user. */
export function parseOntologyFile(file: File): Promise<Ontology> {
  const url = `/api/ontology/parse?name=${encodeURIComponent(file.name)}`
  return fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': contentTypeForOntologyFile(file) },
    body: file,
  }).then((r) => asJson<Ontology>(r))
}
```

说明：v1 **不实现** Turtle 解析；`.ttl` Content-Type 仅为将来扩展，若用户上传 ttl，服务端应 400。不要在 UI accept 中加入 `.ttl`。

- [ ] **Step 2: App.svelte accept**

把：

```svelte
<input bind:this={fileInput} type="file" accept=".owl,.rdf,.xml" class="hidden" onchange={onPick} />
```

改为：

```svelte
<input bind:this={fileInput} type="file" accept=".owl,.rdf,.xml,.jsonld,.json" class="hidden" onchange={onPick} />
```

检查拖放逻辑（`ondrop` / `onPick`）：若有扩展名白名单，同步加入 `jsonld`/`json`。在 `App.svelte` 搜索 `owl` / `endsWith` / `accept` 并一并更新。

- [ ] **Step 3: i18n**

`i18n.svelte.ts` fallback：

```typescript
'app.openFile': 'Open ontology file…',
'app.dropHint': 'Drop an .owl or .jsonld file to visualise',
```

`locales/zh.json`：

```json
"app.openFile": "打开本体文件…",
"app.dropHint": "拖拽 .owl 或 .jsonld 文件到此处",
```

- [ ] **Step 4: 本地快速验证（手工）**

```powershell
cd tools/ResourceOntology
./dev.ps1
```

或：

```powershell
./run.ps1
```

手工：
1. 打开默认 `Resource.owl`，确认图仍在。
2. 点 Export JSON-LD，保存为临时 `Resource.roundtrip.jsonld`。
3. Open file 选该 jsonld，确认 class/instance 数量与步骤 1 同量级、图可点。
4. 上传非法 `.jsonld`，UI 显示错误而非白屏。

- [ ] **Step 5: Commit**

```powershell
git add tools/ResourceOntology/client/src/lib/api.ts tools/ResourceOntology/client/src/App.svelte tools/ResourceOntology/client/src/lib/i18n.svelte.ts tools/ResourceOntology/client/src/lib/locales/zh.json
git commit -m "feat: accept JSON-LD ontology uploads in ResourceOntology UI"
```

---

### Task 5: README 边界与 API 表纠偏（A1 文档）

**Files:**
- Modify: `tools/ResourceOntology/README.md`

**Interfaces:**
- Produces: 与实现一致的用户文档；路线图安全表述

- [ ] **Step 1: 更新「Loading other ontologies」**

将仅 RDF/XML 的描述改为：

```markdown
## Loading other ontologies

The app can visualise OWL ontologies serialised as:

- **RDF/XML** — `.owl`, `.rdf`, `.xml`
- **JSON-LD** — `.jsonld` (OWL-as-JSON-LD; same axioms, different serialisation)

Use **Open ontology file…** or drag-and-drop. Parsing is server-side; files stay on your machine.

### JSON-LD scope (v1)

**Supported:** OWL ontologies encoded as JSON-LD (shape A), including files produced by this app’s **Export JSON-LD**.

**Not supported in v1:**

- Arbitrary JSON-LD knowledge graphs without OWL vocabulary (no full OWL-browser fidelity)
- Guaranteed bit-exact JSON-LD round-trip formatting
- Remote `@context` resolution as a supported offline/CI feature
- Turtle / N-Triples as first-class upload formats
```

- [ ] **Step 2: 修正 API reference 表**

与 `Program.cs` **实际路由** 对齐（当前实现已移除 default/source 时不要再写它们）。表应包含：

| Method | Route | Description |
| --- | --- | --- |
| `POST` | `/api/ontology/parse?name=<file>` | 上传 RDF/XML 或 JSON-LD → `OntologyDto` |
| `GET` | `/api/ontology/files` | 列出 `ontology/` 下 `.owl` 与 `.jsonld` |
| `GET` | `/api/ontology/load?file=` | 按文件名加载并解析 |
| `GET` | `/api/ontology/export-jsonld?file=&format=` | 导出 JSON-LD |
| `POST` | `/api/ontology/export-jsonld?fileName=&format=` | 上传后导出 JSON-LD |
| `GET` | `/api/health` | 健康检查 |

- [ ] **Step 3: Architecture 一句**

将 backend 描述中的 `RDF/XML → model` 改为 `RDF/XML or JSON-LD → model`。

- [ ] **Step 4: Commit**

```powershell
git add tools/ResourceOntology/README.md
git commit -m "docs: document ResourceOntology JSON-LD input boundaries"
```

---

### Task 6: Trimmed publish smoke（风险 R2，可记债）

**Files:**
- 通常无代码；若失败可能 Modify: `tools/ResourceOntology/server/ResourceOntology.Api.csproj`（`TrimmerRootAssembly` 等）

**Interfaces:**
- Produces: 是否在 single-file trimmed 下仍能 `JsonLdParser` 读回的结论

- [ ] **Step 1: Publish**

```powershell
dotnet publish tools/ResourceOntology/server/ResourceOntology.Api.csproj -c Release -o artifacts/resourceontology-publish --nologo
```

- [ ] **Step 2: 针对 publish 输出做最小验证**

优先：用测试在 Debug 已覆盖 parser 的前提下，对 publish 目录做 **启动 health**（需本机端口空闲）：

```powershell
$env:ASPNETCORE_URLS = "http://127.0.0.1:5179"
Start-Process -FilePath "artifacts/resourceontology-publish/ResourceOntology.Api.exe" -PassThru
# wait ~3s
Invoke-RestMethod http://127.0.0.1:5179/api/health
# stop the process when done
```

若 exe 名不同，以 `artifacts/resourceontology-publish` 目录实际文件名为准。

更强验证（推荐）：写临时控制台不必须；可手动 POST 一小段 JSON-LD 到 `/api/ontology/parse`。

- [ ] **Step 3: 结果处理**

- **PASS：** 在 PR/提交说明写一句 `trimmed publish smoke: ok`。
- **FAIL（MissingMethod / 裁剪相关）：** 不要扩大范围重写架构。尝试在 csproj 增加：

```xml
  <ItemGroup>
    <TrimmerRootAssembly Include="dotNetRDF" />
  </ItemGroup>
```

若仍失败：在 `tools/ResourceOntology/README.md` 的 JSON-LD 节增加 **Known limitation: trimmed single-file publish may require follow-up trim roots**，并开 follow-up 笔记；**A1 功能仍以 `dotnet run` + unit tests 验收**。

- [ ] **Step 4: Commit（仅当有 csproj/README 变更）**

```powershell
git add tools/ResourceOntology/server/ResourceOntology.Api.csproj tools/ResourceOntology/README.md
git commit -m "fix: preserve JSON-LD parsing under trimmed ResourceOntology publish"
```

无变更则跳过 commit。

---

### Task 7: 总验收清单

- [ ] **Step 1: 自动化**

```powershell
dotnet test tools/ResourceOntology/server/ResourceOntology.Api.Tests/ResourceOntology.Api.Tests.csproj --nologo
dotnet build tools/ResourceOntology/server/ResourceOntology.Api.csproj --nologo
```

**Expected:** 全绿。

- [ ] **Step 2: 手工对照 spec 验收**

| Spec 项 | 验证 |
| --- | --- |
| 形态 A Go | export-jsonld → 再打开，Stats 关键项对等 |
| RDF/XML 回归 | Resource.owl 与中文 `.owl` 仍可加载 |
| 非法 JSON-LD | 400 / UI 错误 |
| 非目标 B | README 写明不做任意 KG OWL 级体验 |
| 非目标 O3 | 无 GraphSlicer 项目引用 |
| 远程 context | README 不承诺 |
| files 列表 | 若放入 `ontology/*.jsonld` 应出现在下拉框 |

- [ ] **Step 3: 最终说明（给 reviewer）**

在 PR 描述粘贴：

```markdown
## Summary
- ResourceOntology accepts OWL-as-JSON-LD (.jsonld) for graph rendering via OntologyParser dispatch.
- Unit tests: RDF/XML regression + JSON-LD round-trip stats + invalid input.
- Docs: v1 boundaries (no arbitrary JSON-LD / no O3 / no bit-exact round-trip).

## Test plan
- [x] dotnet test tools/ResourceOntology/server/ResourceOntology.Api.Tests
- [x] Manual: export JSON-LD and re-open
- [ ] Trimmed publish smoke (pass / documented debt)
```

---

## Self-Review（计划作者已执行）

| Spec 要求 | 对应 Task |
| --- | --- |
| A0 spike round-trip | Task 1–2 |
| A1 分派 + UX + 测试 + README | Task 2–5 |
| 错误处理 400 | Task 2 invalid test + 现有 parse catch |
| 不实现 B/O3 | Global Constraints + 无相关 task |
| 远程 context 策略 | Constraints + README |
| R1 版本 | 默认 3.3.1；失败再升 |
| R2 trimmed | Task 6 |
| O2 薄适配 | 本计划 O1 落地；README 边界；不引 GraphSlicer |
| C1 | 明确 out of scope |
| 导出路径不再写死 RDF/XML | Task 3 |
| API 文档漂移 | Task 5 |

**Placeholder scan：** 无 TBD；测试与代码块已给出可粘贴内容。  
**类型一致性：** `Parse` / `ParseFile` / `LoadGraph` / `OntologyDto.Stats` 命名在各 Task 一致。

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-08-03-resourceontology-jsonld-input-rendering.md`.

**Two execution options:**

1. **Subagent-Driven (recommended)** — 每个 Task 新开 subagent，Task 间 review，迭代快  
2. **Inline Execution** — 本会话按 executing-plans 连续执行并设检查点  

**Which approach?**

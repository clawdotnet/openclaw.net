# openclaw.net Standard Digitalization Ontology Subsystem

- Date: 2026-07-16
- Branch: openclaw.net `ontologyharnessaction`
- Standard: GB/T 48000.3-2026 Part 3: Requirement for Ontology Modeling
- Language: English

---

## 1. Overview

### 1.1 Background

GB/T 48000.3-2026 "Standard digitalization — Part 3: Requirement for ontology modeling" is a core component of China's standard digitalization framework, issued on January 28, 2026 and effective August 1, 2026. It specifies requirements for constructing standard ontologies in digitalization activities, covering modeling principles, entity definitions, relationships, attributes, axioms, and extension principles.

openclaw.net implements a complete standard digitalization ontology subsystem on the `ontologyharnessaction` branch, covering all core requirements (Chapters 5–9) of GB/T 48000.3-2026 at approximately 95% coverage.

### 1.2 Capability Summary

| Capability | Implementation | GB/T 48000.3 Reference |
|------|------|----------------------|
| JSON-LD Serialization & Framing | dotNetRDF `JsonLdWriter` + `JsonLdProcessor.Frame()` | 5.3 |
| Turtle Serialization | dotNetRDF `CompressingTurtleWriter` | 5.3 |
| RDF/XML Serialization | dotNetRDF `RdfXmlWriter` | 5.3 |
| OWL Ontology Construction | `OntologyBuilder` fluent API | 5.3, Annex A/B/C |
| SHACL Validation | `ShaclValidator` + `StandardShapes` | 5.3 |
| SPARQL Querying | `RemoteEndpointSource` + `LocalFilesSource` | 6.2 |
| Pre-built Standard Ontology | `StandardOntology` (18 entities + 34 properties + 28 axioms, 629 triples) | Annex B/C |
| Version Tracing | `VersionTracer` (replaces chain + Diff) | 6.3.1 |
| MetaSkill DAG Validation | `OntologyValidateTool` (ITool) | — |
| Ontology Visualization | `tools/ResourceOntology` (Cytoscape.js) | — |

---

## 2. System Architecture

### 2.1 Module Overview

```
src/
├── OpenClaw.Core/Models/
│   ├── GraphSliceProfile.cs          ← Graph slice config
│   └── OntologyProfile.cs            ← Ontology build config
│
├── OpenClaw.GraphSlicer/             ← RDF knowledge graph slicer
│   ├── GraphSlicerEngine.cs          ← CONSTRUCT→merge→frame pipeline
│   ├── JsonLdFramer.cs               ← JSON-LD 1.1 Framing (dotNetRDF)
│   ├── ISparqlSource.cs              ← Data source interface
│   ├── RemoteEndpointSource.cs       ← SPARQL remote endpoint adapter
│   └── LocalFilesSource.cs           ← Local RDF file adapter
│
├── OpenClaw.Ontology/                ← OWL ontology builder
│   ├── OntologyBuilder.cs            ← OWL Class/Property/Axiom fluent API
│   ├── ShaclValidator.cs             ← SHACL constraint validator
│   └── OntologyValidateTool.cs       ← MetaSkill DAG validation tool (ITool)
│
├── OpenClaw.StandardOntology/        ← GB/T 48000.3 domain ontology
│   ├── StandardOntology.cs           ← Pre-built ontology (18E+34P+28A)
│   ├── StandardShapes.cs             ← Default SHACL shapes (6 NodeShapes)
│   └── VersionTracer.cs              ← Version tracing (replaces chain+Diff)
│
├── OpenClaw.Cli/
│   └── OntologyCommands.cs           ← CLI: ontology build/validate/versions
│
└── OpenClaw.Agent/Tools/
    └── TemporaryGraphTool.cs          ← Runtime JSON-LD graph loader
```

### 2.2 Dependencies

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

> **AOT note**: `OpenClaw.GraphSlicer`, `OpenClaw.Ontology`, and `OpenClaw.StandardOntology` are marked `PublishAot=false`. dotNetRDF is not AOT-compatible. Preserved via `<TrimmerRootAssembly>` in CLI builds.

---

## 3. Core Components

### 3.1 OntologyBuilder — OWL Ontology Builder

`OntologyBuilder` provides a fluent interface over dotNetRDF's `Graph` API for constructing OWL/RDF/RDFS triples directly.

**Core API**:

| Method | OWL Construct |
|------|--------------|
| `DeclareClass(iri, label, comment, subClassOf, disjointWith, hasKey, ...)` | `owl:Class` + labels, comments, hierarchy, disjointness, keys |
| `DeclareObjectProperty(iri, label, comment, domain, range, functional, transitive, ...)` | `owl:ObjectProperty` + domain/range + characteristics (Functional/Transitive/Symmetric/InverseFunctional) |
| `DeclareDatatypeProperty(iri, label, comment, domain, range, functional)` | `owl:DatatypeProperty` + domain/range |
| `AssertDisjointClasses(classA, classB)` | `owl:disjointWith` |
| `AssertSubClassOf(subClass, superClass)` | `rdfs:subClassOf` |
| `Build()` → `IGraph` | Returns dotNetRDF graph |
| `Serialize(format)` / `WriteToFile(path, format)` | Turtle / JSON-LD / RDF/XML output |

**Example**:

```csharp
var ob = new OntologyBuilder("http://openclaw.net/ontology/standard#");
ob.WithPrefix("std", "http://openclaw.net/ontology/standard#")
  .DeclareClass("std:Standard", "Standard Entity", "Core node for standard documents",
      hasKey: ["std:standardNumber"])
  .DeclareObjectProperty("std:replaces", "Replaces", "New version replaces old version",
      "std:Standard", "std:Standard")
  .DeclareDatatypeProperty("std:standardNumber", "Standard Number",
      "Per GB/T 1.1 format", "std:Standard", "xsd:string",
      functional: true)
  .AssertDisjointClasses("std:Standard", "std:Term")
  .WriteToFile("./ontology.ttl", OntologyOutputFormat.Turtle);
```

### 3.2 StandardOntology — GB/T 48000.3 Pre-built Ontology

`StandardOntology.Build()` constructs the complete standard digitalization core ontology (629 triples):

| Category | Count | Details |
|------|------|------|
| **Entity Types** | 18 | Entity / Standard / StandardizationObject / Metadata / RelevantParty / Organization / Person / StandardClassification / IcsClassification / CcsClassification / Element / Level / Term / InformationUnit / RepresentationForm / Object / Characteristic / ConstraintLogic / Determination / ExternalConstraint / DevelopmentStage / Version / DocumentNumber |
| **Object Properties** | 34 | adopts / replaces / cites / references / hasPart / issuedBy / proposedBy / administeredBy / draftedBy / publishedBy / classifiedUnder / standardizes / defines / usesTerm / hasRepresentationForm / hasExample / hasNote / citesStandard / referencesClause / involvesObject / specifiesCharacteristic / hasCharacteristic / imposesConstraint / constrainsObject / constrainsCharacteristic / describesAction / referencesExternalResource / isRelatedToPatent / hasDevelopmentStage / includesStandard / hasClause / hasSubClause / hasNormativeElement / hasStructuralElement / hasVersion / hasDocumentNumber |
| **Datatype Properties** | 22 | standardNumber / standardName / issuedDate / effectiveDate / standardStatus / obligationType / partyName / termName / termDefinition / characteristicName / characteristicValue / maxValue / minValue / nominalValue / toleranceValue / unitOfMeasure / levelCode / levelTitle / versionNumber / versionStatus / stageCode / stageStartDate / stageEndDate |
| **Axioms** | 28 | 10 disjointness + 11 subclass hierarchies + 1 hasKey + 5 property characteristics (functional/transitive/etc.) |

### 3.3 JSON-LD 1.1 Framing

`JsonLdFramer` delegates to dotNetRDF's `JsonLdProcessor.Frame()` for W3C-compliant JSON-LD 1.1 Framing. Supports all frame directives:

| Directive | Description |
|------|------|
| `@type` | Filter nodes by rdf:type (single or array) |
| `@id` | Exact node matching by @id |
| `@embed` | @always / @once / @never / @first / @last / @link |
| `@explicit` | Only include properties listed in frame |
| `@requireAll` | Node must have all specified properties |
| `@omitDefault` | Omit empty arrays for missing properties |
| Nested frames | Object-valued properties as sub-frames |

**Pipeline**: `GraphSlicerEngine` → SPARQL CONSTRUCT → merge graphs → JsonLdWriter → JsonLdFramer → output file.

### 3.4 SHACL Validation

`ShaclValidator` wraps dotNetRDF's `ShapesGraph.Validate()`:

- `Validate(IGraph data, IGraph shapes)` → `ShaclReport`
- `Conforms(IGraph data, IGraph shapes)` → `bool`

`StandardShapes` provides GB/T 48000.3 default SHACL shapes (6 NodeShapes):

| Shape | Target Class | Constraints |
|-------|-------------|-------------|
| StandardShape | `std:Standard` | standardNumber: 1..1 string; issuedDate: ≥1; effectiveDate: ≥1; standardStatus: ≥1; standardName: ≥1 |
| OrganizationShape | `std:Organization` | partyName: ≥1 |
| PersonShape | `std:Person` | partyName: ≥1 |
| TermShape | `std:Term` | termName: ≥1 |
| DevelopmentStageShape | `std:DevelopmentStage` | stageCode: 1..1 |

**Validation Example**:

```
Input: 1 valid Standard + 1 Standard missing standardNumber +
       1 valid Organization + 1 Organization missing partyName

Conforms: False
Results: 5
  ❌ [Violation] standardNumber: minCount 1 → std:GB-T-BAD
  ❌ [Violation] issuedDate: minCount 1 → std:GB-T-BAD
  ❌ [Violation] effectiveDate: minCount 1 → std:GB-T-BAD
  ❌ [Violation] standardStatus: minCount 1 → std:GB-T-BAD
  ❌ [Violation] partyName: minCount 1 → std:Org-BAD
```

### 3.5 Version Tracing

`VersionTracer` provides three core capabilities:

| Method | Function |
|------|------|
| `TraceReplacesChain(graph, iri)` | Follow `std:replaces` chain from newest to oldest |
| `GetVersions(graph, iri)` | Get all versions via `std:hasVersion` |
| `Diff(graph, oldIri, newIri)` | Property-level diff between two versions |

### 3.6 Graph Slicer

`GraphSlicerEngine` extracts knowledge graph slices from multiple data sources:

```
Data Sources                    SPARQL CONSTRUCT
────────────────────────────────────────────────────
RemoteEndpointSource ─┐
(Fuseki/Stardog/      │         ┌──────────┐
 GraphDB/Ontop)       ├────────→│  Merge   │→ JSON-LD → Frame → .jsonld
LocalFilesSource ─────┘         └──────────┘
(.ttl/.rdf/.jsonld/.nt)
```

**Source adapters**:
- **RemoteEndpointSource**: HTTP POST SPARQL, N-Triples response, Basic Auth support
- **LocalFilesSource**: Load local RDF into `TripleStore`, execute CONSTRUCT via `LeviathanQueryProcessor`

---

## 4. CLI Reference

### 4.1 Ontology Build

```bash
# Build standard ontology (Turtle)
openclaw ontology build --profile standard

# Specify format and output
openclaw ontology build --profile standard --format turtle --output ./ontology.ttl
openclaw ontology build --profile standard --format jsonld --output ./ontology.jsonld
openclaw ontology build --profile standard --format rdfxml --output ./ontology.rdf
```

### 4.2 SHACL Validation

```bash
# Validate the standard ontology itself (no instances, 0 violations)
openclaw ontology validate --profile standard

# Validate instance data with custom shapes
openclaw ontology validate --data ./instances.ttl --shapes ./shapes.ttl
```

### 4.3 Version Tracing

```bash
# Trace replaces chain
openclaw ontology versions --data ./instances.ttl --standard std:GB-T-12345-v3

# Diff between two versions
openclaw ontology versions --data ./instances.ttl \
  --diff-old std:GB-T-12345-v1 --diff-new std:GB-T-12345-v3
```

---

## 5. MetaSkill DAG Integration

### 5.1 Validation Step

SHACL validation as a DAG step:

```yaml
- id: validate_ontology
  kind: tool_call
  tool: ontology_validate
  with:
    data: "./tmp/my-instances.ttl"
    shapes: "./tmp/standard-shapes.ttl"
```

### 5.2 Full Pipeline Example

```yaml
kind: meta
name: quality-root-cause-assistant

composition:
  steps:
    # Step 1: Load graph slice
    - id: load_graph
      kind: tool_call
      tool: load_temporary_graph
      with:
        path: "./tmp/quality-slice.jsonld"
        format: "jsonld"

    # Step 2: SHACL validation
    - id: validate
      kind: tool_call
      tool: ontology_validate
      depends_on: [load_graph]
      with:
        data: "./tmp/quality-slice.jsonld"
        shapes: "./tmp/standard-shapes.ttl"

    # Step 3: LLM reasoning
    - id: reason
      kind: llm_chat
      depends_on: [validate]
      with:
        input: "{{ outputs.load_graph }}"

    # Step 4: Action execution
    - id: execute
      kind: tool_call
      depends_on: [reason]
      tool: action_execute
      tool_allowlist: [action_execute]
      with:
        proposal: "{{ outputs.reason }}"
```

---

## 6. Ontology Visualization

`tools/ResourceOntology` is an interactive ontology visualization tool based on Cytoscape.js, migrated into the openclaw.net repository.

**Capabilities**:
- 4 graph layouts: Force-directed (CoSE), Hierarchical (dagre), Concentric, Breadth-first
- 8 edge type color codes: subClassOf / restriction / disjoint / domainRange / typeOf / assertion / property / inverse
- Left sidebar hierarchy tree + right sidebar detail inspector (class/property/individual)
- Drag-and-drop `.owl`/`.rdf`/`.xml` file upload
- JSON-LD export (compacted / expanded)
- Bilingual UI (English / Chinese)
- .NET 10 (ASP.NET Core) + Svelte 5 + TypeScript + Tailwind CSS v4

**Usage**: openclaw.net `ontology build --format rdfxml` output can be directly loaded.

```powershell
cd tools/ResourceOntology
./run.ps1    # Production mode
./dev.ps1    # Development mode (Vite HMR)
```

---

## 7. Namespaces

| Prefix | IRI | Description |
|------|-----|------|
| `std` | `http://openclaw.net/ontology/standard#` | GB/T 48000.3 standard ontology |
| `owl` | `http://www.w3.org/2002/07/owl#` | OWL 2 |
| `rdf` | `http://www.w3.org/1999/02/22-rdf-syntax-ns#` | RDF |
| `rdfs` | `http://www.w3.org/2000/01/rdf-schema#` | RDF Schema |
| `xsd` | `http://www.w3.org/2001/XMLSchema#` | XML Schema |
| `sh` | `http://www.w3.org/ns/shacl#` | SHACL |

Namespace documentation: [docs/zh-CN/ontology/standard/index.html](docs/zh-CN/ontology/standard/index.html)

Downloadable formats:
- [Turtle](docs/zh-CN/ontology/standard/standard-ontology.ttl)
- [JSON-LD](docs/zh-CN/ontology/standard/standard-ontology.jsonld)
- [RDF/XML](docs/zh-CN/ontology/standard/standard-ontology.rdf)

---

## 8. Code Index

| Component | Path |
|------|------|
| Graph Slicer Engine | [src/OpenClaw.GraphSlicer/GraphSlicerEngine.cs](src/OpenClaw.GraphSlicer/GraphSlicerEngine.cs) |
| JSON-LD Framing | [src/OpenClaw.GraphSlicer/JsonLdFramer.cs](src/OpenClaw.GraphSlicer/JsonLdFramer.cs) |
| SPARQL Remote Endpoint | [src/OpenClaw.GraphSlicer/RemoteEndpointSource.cs](src/OpenClaw.GraphSlicer/RemoteEndpointSource.cs) |
| SPARQL Local Files | [src/OpenClaw.GraphSlicer/LocalFilesSource.cs](src/OpenClaw.GraphSlicer/LocalFilesSource.cs) |
| OWL Ontology Builder | [src/OpenClaw.Ontology/OntologyBuilder.cs](src/OpenClaw.Ontology/OntologyBuilder.cs) |
| SHACL Validator | [src/OpenClaw.Ontology/ShaclValidator.cs](src/OpenClaw.Ontology/ShaclValidator.cs) |
| DAG Validation Tool | [src/OpenClaw.Ontology/OntologyValidateTool.cs](src/OpenClaw.Ontology/OntologyValidateTool.cs) |
| Standard Ontology | [src/OpenClaw.StandardOntology/StandardOntology.cs](src/OpenClaw.StandardOntology/StandardOntology.cs) |
| SHACL Shapes | [src/OpenClaw.StandardOntology/StandardShapes.cs](src/OpenClaw.StandardOntology/StandardShapes.cs) |
| Version Tracer | [src/OpenClaw.StandardOntology/VersionTracer.cs](src/OpenClaw.StandardOntology/VersionTracer.cs) |
| Ontology CLI | [src/OpenClaw.Cli/OntologyCommands.cs](src/OpenClaw.Cli/OntologyCommands.cs) |
| Runtime Graph Loader | [src/OpenClaw.Agent/Tools/TemporaryGraphTool.cs](src/OpenClaw.Agent/Tools/TemporaryGraphTool.cs) |
| Evaluation Document | [docs/zh-CN/gbt48000-3-ontology-evaluation.md](docs/zh-CN/gbt48000-3-ontology-evaluation.md) |
| Full Pipeline Guide | [docs/zh-CN/graph-slicer-metaskill-pipeline.md](docs/zh-CN/graph-slicer-metaskill-pipeline.md) |

---

## 9. References

| Standard | Title |
|------|------|
| GB/T 48000.3-2026 | Standard digitalization — Part 3: Requirement for ontology modeling |
| GB/T 42131-2022 | Artificial intelligence — Technical framework of knowledge graph |
| GB/T 1.1-2020 | Directives for standardization — Part 1 |
| GB/T 18391.3 | Information technology — Metadata registries (MDR) |
| ISO/IEC 21838 | Top-level ontologies (BFO) |
| W3C OWL 2 | Web Ontology Language |
| W3C SHACL | Shapes Constraint Language |
| W3C JSON-LD 1.1 | JSON-LD 1.1 Framing |
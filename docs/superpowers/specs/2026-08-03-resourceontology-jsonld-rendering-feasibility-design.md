# ResourceOntology JSON-LD Input Rendering — Feasibility Design

**Date:** 2026-08-03  
**Status:** Approved for documentation (evaluation only; no implementation in this cycle)  
**Scope path:** `tools/ResourceOntology`  
**Audiences:** engineering go/no-go, product/roadmap wording, alignment with main-repo ontology stack  

## 1. Purpose and non-goals

### 1.1 Purpose

Assess whether `tools/ResourceOntology` can accept **JSON-LD ontology files as render inputs** (not only export JSON-LD), and produce a **decision-ready** write-up that covers:

1. **Engineering go/no-go** — effort, risk, minimal path  
2. **Roadmap-safe claims** — what may be promised vs explicitly out of scope  
3. **Main-repo alignment** — independent tool path vs thin adapter vs hard reuse of GraphSlicer  

### 1.2 Non-goals (this document)

- No feature code, API changes, dependency bumps, or release commits as part of this design cycle  
- No compliance certification narrative  
- No commitment to calendar ship dates  
- No requirement that v1 support arbitrary knowledge graphs at full OWL-browser fidelity  

### 1.3 Current baseline (facts)

| Layer | Baseline |
| --- | --- |
| Input | RDF/XML only (`.owl` / `.rdf` / `.xml`); `RdfXmlParser` hardcoded in `OntologyParser` |
| Lift | `IGraph` → `BuildModel` → `OntologyDto` (OWL/RDFS-centric) |
| UI | Svelte + Cytoscape; consumes `OntologyDto` only (format-agnostic) |
| JSON-LD today | **Export** via `JsonLdWriter` (+ optional expand); **not** load/render input |
| Main repo | `OpenClaw.GraphSlicer` / related tools already read/write JSON-LD; `FileLoader.Load` multi-format; dotNetRDF **3.5.2** |
| Tool package | dotNetRDF **3.3.1**; trimmed/single-file publish enabled on the API project |

Primary references:

- `tools/ResourceOntology/README.md`  
- `tools/ResourceOntology/server/Services/OntologyParser.cs`  
- `tools/ResourceOntology/server/Program.cs`  
- `src/OpenClaw.GraphSlicer/`  
- `docs/openclaw-ontology-implementation.md`  

## 2. Decision rubrics

| Verdict | Meaning |
| --- | --- |
| **Go** | Clear minimal path; risks known and mitigable; must not break existing RDF/XML path |
| **Go with constraints** | Feasible only with explicit format/OWL/context boundaries; roadmap must not say “any JSON-LD” |
| **No-Go (this tool / this phase)** | Goal is a general KG browser or hard-wired full GraphSlicer pipeline → separate project |
| **Defer** | Blocked on missing spike evidence (e.g. trimmed publish + remote `@context`) |

**Overall verdict:** **Go with constraints**

## 3. JSON-LD input shapes (tiered)

### 3.1 Definitions

| Shape | Definition | Typical sources |
| --- | --- | --- |
| **A. OWL-as-JSON-LD** | OWL axioms (Class, properties, individuals, restrictions, …) serialized as JSON-LD | Protégé export, this tool’s `export-jsonld` re-load, standard OWL JSON-LD |
| **B. Generic JSON-LD KG** | Arbitrary `@graph` / node documents + `@context`; OWL vocabulary not guaranteed | Business KGs, schema.org-style graphs, mixed vocabularies |
| **C. Main-repo JSON-LD artifacts** | Outputs from GraphSlicer framing, TemporaryGraphTool, ontology slices, etc. | `OpenClaw.GraphSlicer`, agent temp graphs, slice exports |

### 3.2 Fit to current pipeline

```text
bytes → (parser) → IGraph → BuildModel(OWL lift) → OntologyDto → graph / tree / details
```

| Shape | Parse to `IGraph` | `BuildModel` quality | Full OWL-browser UX | Feasibility |
| --- | --- | --- | --- | --- |
| **A** | High (`JsonLdParser` / `FileLoader`) | High (vocabulary aligned) | **High** — parity with RDF/XML | **Go** |
| **B** | Medium–high (syntax OK; remote context risky) | **Low–medium** (often no `owl:Class`, …) | **Low** — empty/partial hierarchy & restrictions | **Go with constraints** (entity/graph-level only; not OWL-level) |
| **C** | High (main repo already exercises JSON-LD) | **Depends on frame** | **Medium** — good under documented context/frame; else ≈ B | **Go with constraints** (supported artifact profile required) |

### 3.3 Roadmap-safe wording

**Allowed (recommended):**

- ResourceOntology can take **OWL ontologies serialized as JSON-LD** as visualization input, with render parity to the existing RDF/XML path (shape A).  
- **Main-repo agreed JSON-LD outputs** (shape C) are browsable under a documented context/frame profile; gaps listed in a compatibility matrix.

**Not allowed for v1 capability claims:**

- “Supports arbitrary JSON-LD / any knowledge graph at full ontology-browser fidelity” (full shape B).  
- “Bit-exact / cosmetically stable JSON-LD round-trip” (export exists; full round-trip fidelity is a separate item).  
- “Browser-side JSON-LD framing/reasoning” (parsing remains server-side).

### 3.4 Shape B downgrade paths (explicitly non-v1)

If shape B is pursued later (separate decision):

1. **Strict OWL lift (current logic)** — non-OWL types → near-empty UI (worst UX)  
2. **Heuristic lift** — treat `rdf:type` targets as classes, other nodes as individuals/assertions — “has a graph,” not formal OWL  
3. **Generic RDF graph mode** — new DTO/UI (node = IRI, edge = predicate) — essentially a new product surface  

Shape B must not share a single roadmap checkbox with shape A.

### 3.5 Shape C acceptance probes (before calling C “supported”)

| Probe | Why |
| --- | --- |
| Main-repo sample JSON-LD → upload/parse in ResourceOntology | End-to-end DTO production |
| Same ontology as RDF/XML vs main-repo JSON-LD: class/individual counts | Lift loss detection |
| Embedded/local `@context` vs HTTP-resolved context | Offline/CI reproducibility |
| Framed vs compact vs expanded | Declare supported input profile(s) |
| Tool dotNetRDF 3.3.1 vs main-repo 3.5.2 | Parse/behavior drift |

### 3.6 Effort bands (planning magnitudes only)

| Band | Work | Magnitude | Depends on |
| --- | --- | --- | --- |
| **A0 Spike** | Re-load `export-jsonld` output; compare `OntologyDto` | 0.5–1 day | — |
| **A1 MVP** | Extension/content-type dispatch; `.jsonld` list/upload UX; tests; README boundaries | 1–2 days | A0 pass |
| **C1** | 1–2 main-repo fixtures + compatibility notes; optional package align | +1–2 days | A1 |
| **B\*** | Heuristic or generic graph mode | 3–8+ days | Separate go |
| **This feasibility doc** | Spec only | ~0.5 day | — |

## 4. Implementation strategy options (main-repo relationship)

| Dimension | **O1 Tool-local** | **O2 Thin adapter (recommended target)** | **O3 Hard reuse GraphSlicer** |
| --- | --- | --- | --- |
| Approach | Local `JsonLdParser` dispatch; keep DTO/UI | Align parse options, package version, fixtures, context policy with main repo; optional small shared helper; UI still on `OntologyDto` | Ingest via GraphSlicer source/frame/slice, then map to DTO or change UI |
| Shape A | Excellent | Excellent | Excellent but heavy |
| Shape C | Drifts easily | **Best balance** | Highest ceiling |
| Shape B | Unsolved | Unsolved | May still need new UI |
| Coupling | None | Low–medium | High (`tools` ↔ `src`, APIs, release) |
| Tech debt | Dual JSON-LD stacks | Controlled | Architectural bind |
| Roadmap voice | “Tool supports OWL-JSON-LD” | **“Consistent with main-repo JSON-LD habits”** | “Unified ontology pipeline visualizer” |
| Magnitude to A1-class | 1–2 days | 3–5 days (incl. align/fixtures) | 5–10+ days |
| Choose when | Prove “can view” | Eng + roadmap + alignment | Product becomes platform KG browser |

**Recommended stance:** default **O2** as the target state; allow **A0/A1 with O1 techniques**, then fold version/fixtures into O2. **O3 is No-Go for v1** under this evaluation.

```text
A0 spike (O1 technique) → A1 MVP → C1 fixtures / alignment (enter O2) → B / O3 as separate decisions
```

## 5. Risks

| ID | Risk | Impact | Likelihood | Mitigation | Blocking? |
| --- | --- | --- | --- | --- | --- |
| R1 | Tool dotNetRDF **3.3.1** vs main **3.5.2** | Shape C inconsistency | Medium | A0 compare; consider bump before/with A1 | No (measure first) |
| R2 | **Trimmed/single-file** drops JSON-LD dependencies | Published binary fails parse | Medium | Run A0 against publish output; trimmer roots if needed | **May block publish channel** |
| R3 | Remote `@context` HTTP/offline/SSRF | CI/intranet failure | High if remote allowed | v1: embedded/local context only; document policy | Policy, not hard tech veto |
| R4 | Marketing shape B as shape A | Empty graph / trust damage | High if wording loose | Tiered claims; optional UI “non-OWL” hint | Product risk |
| R5 | `BuildModel` OWL vocabulary assumptions | Sparse lift for B / some C | Certain | Tiered expectations; C profile | Expectation management |
| R6 | Full-file in-memory + large graph layout | OOM / UI jank | Medium | Same limits as OWL today; quotas later | Known debt |
| R7 | O3 scope creep | Delay | Medium if O3 chosen | Keep O3 out of v1 | Scope |
| R8 | README/API drift vs code | Mis-implementation | Already present | Fix docs when implementing | No |
| R9 | Round-trip cosmetic/bit equality | Source tab disappointment | Medium | v1 does not promise bit-exact round-trip | No |

## 6. Go / No-Go decision table

| Item | Verdict | Conditions |
| --- | --- | --- |
| JSON-LD **render input** in ResourceOntology | **Go with constraints** | Tier by shape; not “any JSON-LD” |
| Shape **A** as v1 goal | **Go** | A0: re-load export-jsonld; critical DTO counts/structure parity (blank-node label drift allowed) |
| Shape **C** as v1.x | **Go with constraints** | ≥1 main-repo fixture + supported context/frame note; C failure must not block A |
| Shape **B** full OWL UX | **No-Go (v1)** | Explore later or “generic graph view” project |
| Strategy **O1** | **Go (spike/MVP technique)** | Local parser dispatch OK |
| Strategy **O2** | **Go (default target)** | Version/fixtures/docs aligned |
| Strategy **O3** | **No-Go (v1 of this eval)** | Unless product upgrades to platform KG browser |
| Implement in this cycle | **No** | Spec-only delivery |
| Bump dotNetRDF / trim fixes | **Defer to A0/A1** | Evidence-driven |
| Public roadmap one-liner | **Go (use text below)** | Must keep boundaries |

### 6.1 Suggested roadmap one-liner

> ResourceOntology will accept **OWL ontologies serialized as JSON-LD** as visualization input, with parity to the RDF/XML path, and will browse **main-repo profiled JSON-LD outputs** under a documented context/frame. It does **not** claim full OWL-level visualization for arbitrary JSON-LD knowledge graphs.

### 6.2 Phrases to avoid

- “Full JSON-LD ontology support” (unqualified)  
- “Same visualization pipeline as GraphSlicer” (implies O3 unless separately funded)  

## 7. Phased plan (now / next / later / won’t)

| Phase | Action | Output | Gate |
| --- | --- | --- | --- |
| **Now** | Land this feasibility design; no feature code | This spec under `docs/superpowers/specs/` | Maintainer review of spec |
| **Next (if implementation approved)** | A0 spike (re-load + optional trimmed publish) | Pass/fail log for R1/R2 | Fail → fix stack or defer publish channel |
| **MVP** | A1: dispatch, `.jsonld` UX, tests, README boundaries | Demoable OWL-JSON-LD | Pass → may mark v1 capability |
| **Later** | C1 fixtures + O2 alignment; capability matrix row | “Main-repo artifact usable” | Expand fixture by fixture |
| **Won’t (tool v1)** | Arbitrary JSON-LD OWL-level UX; O3 hard reuse; bit-exact round-trip | — | Separate initiatives |

## 8. Future implementation sketch (not authorized work)

Informational only — for a later `writing-plans` cycle if approved.

### 8.1 Likely touch points

- `tools/ResourceOntology/server/Services/OntologyParser.cs` — parser selection by extension / content-type / sniff  
- `tools/ResourceOntology/server/Program.cs` — file list globs, upload validation, shared load helper with export path  
- `tools/ResourceOntology/client/src/App.svelte` (+ i18n) — accept `.jsonld` / `.json` where appropriate  
- Tests under tool or repo test project — round-trip and regression on `.owl`  
- `tools/ResourceOntology/README.md` — input formats and non-goals  

### 8.2 Error-handling principles

- Illegal JSON / JSON-LD parse failure → **400** with readable message (same family as OWL failures)  
- Parse OK but zero classes and likely non-OWL → **200** with optional warning on DTO/meta (do not hard-fail shape B experiments)  
- Remote context failure → explicit error; v1 may disable remote contexts  

### 8.3 Minimum test set (when implementing)

1. Self `export-jsonld` round-trip ≈ original OWL `OntologyDto`  
2. Invalid JSON-LD → 400  
3. Existing `.owl` path unchanged  
4. (C1) One main-repo fixture snapshot or count assertions  

### 8.4 Architecture preference

Keep **gateway of serialization** on the server; keep **OWL lift** in one `BuildModel`; keep **UI on `OntologyDto`**. Do not re-implement visualization per serialization format.

## 9. Summary verdict

| Question | Answer |
| --- | --- |
| Is JSON-LD render input feasible? | **Yes, tiered by shape** |
| Conflict with current architecture? | **A: no; B: semantic model mismatch; C: depends on artifact profile** |
| Minimum valuable tier | **A (via A0 spike)** |
| Default alignment strategy | **O2 thin adapter; O1 OK for spike/MVP** |
| Overall | **Go with constraints** |
| This cycle deliverable | **Design/feasibility only — no implementation** |

## 10. Approval record

| Item | Status |
| --- | --- |
| Brainstorm scope (eval only; audiences A+B+C; shapes A/B/C tiered; compare O1/O2/O3; decision skeleton E) | Captured in session |
| Design sections §1–§3 (as discussed) | User advanced with “继续” / “ok” |
| Written spec | This file |

Implementation planning (`writing-plans`) starts only after explicit approval of **this file** and a separate request to implement or plan implementation.

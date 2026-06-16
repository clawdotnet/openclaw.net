# Meta Skill Creator Dependency Parity Completed Plan

> **Status:** Historical completed plan. PR #152 delivered this dependency-parity work, including creator tools, built-in registration, the history-explorer child skill, and AgentRuntime/MAF parity tests. The checked boxes below reflect completed implementation slices, not remaining work.

**Goal:** Make src/OpenClaw.Gateway/skills/meta-skill-creator/SKILL.md executable in OpenClaw by migrating missing dependencies from OpenSquilla (or equivalent ports) and validating PREVIEW_ONLY + FULL_GATED paths.

**Architecture:** Kept the migrated meta skill definition as source of truth and closed runtime gaps by adding first-party creator tools as OpenClaw ITool implementations, wiring them into Gateway built-in tools, and migrating history-explorer as a bundled skill_exec child skill under Gateway skills. AgentRuntime and MafAgentRuntime parity tests prove end-to-end tool/skill resolution, and dependency docs now reflect closure.

**Tech Stack:** .NET 10, C#, OpenClaw ITool abstraction, SkillLoader bundled skills, xUnit, System.Text.Json, existing AgentRuntime/MAF meta execution paths

---

## Scope Check

This was one subsystem: enabling meta-skill-creator runtime dependencies. No split was required.

## File Structure

- Create: src/OpenClaw.Agent/Tools/MetaSkillCreatorTools.cs
  Responsibility: implement emit_text, meta_skill_fill_slots, meta_skill_assemble, meta_skill_lint_run, meta_skill_smoke_run, meta_skill_runtime_e2e_run, meta_skill_persist_proposal.

- Create: src/OpenClaw.Agent/Tools/MetaSkillCreatorModels.cs
  Responsibility: strongly typed request/response DTOs for slot filling, lint/smoke summaries, runtime_e2e result, and persisted proposal envelope.

- Create: src/OpenClaw.Agent/Tools/MetaSkillCreatorTemplateCatalog.cs
  Responsibility: map pattern_id to template payload and render SKILL.md output deterministically.

- Modify: src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.RuntimeFactories.cs
  Responsibility: register new creator tools in CreateBuiltInTools so meta tool_call steps can resolve.

- Create: src/OpenClaw.Gateway/skills/history-explorer/SKILL.md
  Responsibility: migrate bundled history-explorer skill definition (entrypoint parse json timeout semantics).

- Create: src/OpenClaw.Gateway/skills/history-explorer/scripts/explore.py
  Responsibility: migrate/port explore script used by history-explorer skill_exec step.

- Modify: src/OpenClaw.Gateway/skills/meta-skill-creator/DEPENDENCY_GAPS.md
  Responsibility: mark dependency closure status and list any intentionally deferred gaps.

- Create: src/OpenClaw.Tests/MetaSkillCreatorToolsTests.cs
  Responsibility: unit tests for each migrated tool contract and JSON schema behavior.

- Modify: src/OpenClaw.Tests/AgentRuntimeTests.cs
  Responsibility: add meta-skill-creator route execution tests (PREVIEW_ONLY and FULL_GATED minimal path).

- Modify: src/OpenClaw.Tests/MafAdapterTests.cs
  Responsibility: add parity tests for MAF runtime execution of same scenarios.

---

### Task 1: Add Red Tests for Missing Creator Tools

**Files:**
- Create: src/OpenClaw.Tests/MetaSkillCreatorToolsTests.cs
- Test: src/OpenClaw.Tests/MetaSkillCreatorToolsTests.cs

- [x] **Step 1: Write failing test for emit_text contract**

```csharp
[Fact]
public async Task EmitTextTool_ReturnsProvidedText()
{
    var tool = new EmitTextTool();
    var result = await tool.ExecuteAsync("{""text"":""hello""}", CancellationToken.None);
    Assert.Equal("hello", result);
}
```

- [x] **Step 2: Write failing tests for assemble/fill_slots/lint/smoke/persist JSON contracts**

```csharp
[Fact]
public async Task MetaSkillAssembleTool_UnknownPattern_ReturnsErrorJson()
{
    var tool = new MetaSkillAssembleTool();
    var result = await tool.ExecuteAsync("{""pattern_id"":""unknown"",""slots_json"":""{}""}", CancellationToken.None);
    using var doc = JsonDocument.Parse(result);
    Assert.Equal("error", doc.RootElement.GetProperty("status").GetString());
    Assert.Equal("unknown_pattern_id", doc.RootElement.GetProperty("errorCode").GetString());
}
```

- [x] **Step 3: Run failing slice**

Run: dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter FullyQualifiedName~MetaSkillCreatorToolsTests
Expected: FAIL with missing types/tools.

- [x] **Step 4: Commit red tests**

```bash
git add src/OpenClaw.Tests/MetaSkillCreatorToolsTests.cs
git commit -m "test: add red tests for meta-skill-creator tool contracts"
```

### Task 2: Implement Creator Tool Surface in OpenClaw.Agent

**Files:**
- Create: src/OpenClaw.Agent/Tools/MetaSkillCreatorModels.cs
- Create: src/OpenClaw.Agent/Tools/MetaSkillCreatorTemplateCatalog.cs
- Create: src/OpenClaw.Agent/Tools/MetaSkillCreatorTools.cs
- Test: src/OpenClaw.Tests/MetaSkillCreatorToolsTests.cs

- [x] **Step 1: Add model DTOs used by tool implementations**

```csharp
internal sealed record CreatorToolError(string Status, string ErrorCode, string Message);
internal sealed record CreatorLintResult(string Status, bool Passed, string[] FailedGates, string Summary);
internal sealed record CreatorSmokeResult(string Status, bool Passed, bool Degraded, string Summary);
internal sealed record CreatorPersistResult(string Status, string ProposalId, string Path);
```

- [x] **Step 2: Port template catalog and assembler from OpenSquilla proposer patterns**

```csharp
internal static class MetaSkillCreatorTemplateCatalog
{
    public static bool TryGetTemplate(string patternId, out string template)
    {
        return _templates.TryGetValue(patternId, out template!);
    }

    private static readonly Dictionary<string, string> _templates = new(StringComparer.Ordinal)
    {
        ["p1_sequential"] = "---\nname: {{name}}\nkind: meta\n...",
        ["p2_fan_out_merge"] = "---\nname: {{name}}\nkind: meta\n...",
        ["p3_condition_gated"] = "---\nname: {{name}}\nkind: meta\n..."
    };
}
```

- [x] **Step 3: Implement emit_text, fill_slots, assemble, lint, smoke, runtime_e2e, persist tools as ITool classes**

```csharp
public sealed class EmitTextTool : ITool
{
    public string Name => "emit_text";
    public string Description => "Emit fixed text output for meta workflows.";
    public string ParameterSchema => "{""type"":""object"",""properties"":{""text"":{""type"":""string""}},""required"": [""text""]}";

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(argumentsJson);
        var text = doc.RootElement.GetProperty("text").GetString() ?? string.Empty;
        return ValueTask.FromResult(text);
    }
}
```

- [x] **Step 4: Run tests to green the tool contract slice**

Run: dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter FullyQualifiedName~MetaSkillCreatorToolsTests
Expected: PASS.

- [x] **Step 5: Commit tool implementation**

```bash
git add src/OpenClaw.Agent/Tools/MetaSkillCreatorModels.cs src/OpenClaw.Agent/Tools/MetaSkillCreatorTemplateCatalog.cs src/OpenClaw.Agent/Tools/MetaSkillCreatorTools.cs src/OpenClaw.Tests/MetaSkillCreatorToolsTests.cs
git commit -m "feat: port meta-skill-creator tools into OpenClaw runtime"
```

### Task 3: Register Creator Tools in Gateway Built-ins

**Files:**
- Modify: src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.RuntimeFactories.cs
- Test: src/OpenClaw.Tests/AgentRuntimeTests.cs

- [x] **Step 1: Write failing runtime test for missing tool resolution**

```csharp
[Fact]
public async Task ExecuteMetaSkillAsync_MetaSkillCreatorPreview_PathResolvesCreatorTools()
{
    var tool = new EmitTextTool();
    var agent = new AgentRuntime(_chatClient, [tool], _memory, _config, maxHistoryTurns: 5, skills: [LoadMetaSkillCreatorDefinition()]);
    var session = new Session { Id = "sess", SenderId = "u", ChannelId = "c" };
    var result = await InvokeMetaSkillAsync(agent, session, "meta-skill-creator", "create a meta-skill", CancellationToken.None);
    Assert.DoesNotContain("Tool 'emit_text' not found", result, StringComparison.OrdinalIgnoreCase);
}
```

- [x] **Step 2: Register creator tools in CreateBuiltInTools list**

```csharp
tools.Add(new EmitTextTool());
tools.Add(new MetaSkillFillSlotsTool());
tools.Add(new MetaSkillAssembleTool());
tools.Add(new MetaSkillLintRunTool());
tools.Add(new MetaSkillSmokeRunTool());
tools.Add(new MetaSkillRuntimeE2ERunTool());
tools.Add(new MetaSkillPersistProposalTool());
```

- [x] **Step 3: Run focused runtime slice**

Run: dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter FullyQualifiedName~ExecuteMetaSkillAsync_MetaSkillCreatorPreview_PathResolvesCreatorTools
Expected: PASS.

- [x] **Step 4: Commit registration change**

```bash
git add src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.RuntimeFactories.cs src/OpenClaw.Tests/AgentRuntimeTests.cs
git commit -m "feat: register meta-skill-creator built-in toolchain"
```

### Task 4: Migrate history-explorer Skill and Script

**Files:**
- Create: src/OpenClaw.Gateway/skills/history-explorer/SKILL.md
- Create: src/OpenClaw.Gateway/skills/history-explorer/scripts/explore.py
- Test: src/OpenClaw.Tests/SkillTests.cs

- [x] **Step 1: Write failing skill-load test for history-explorer presence**

```csharp
[Fact]
public void LoadAll_BundledHistoryExplorer_IsDiscovered()
{
    var config = new SkillsConfig { Enabled = true, Load = new SkillLoadConfig { IncludeBundled = true, IncludeManaged = false, IncludeWorkspace = false } };
    var logger = new TestLogger();
    var skills = SkillLoader.LoadAll(config, null, logger);
    Assert.Contains(skills, s => string.Equals(s.Name, "history-explorer", StringComparison.OrdinalIgnoreCase));
}
```

- [x] **Step 2: Add migrated SKILL.md and explore.py from OpenSquilla with OpenClaw path adjustments**

```yaml
entrypoint:
  command: python {baseDir}/scripts/explore.py
  args:
    - --query
    - "{{ with.query | truncate(512) }}"
  parse: json
  timeout: 30
```

- [x] **Step 3: Run skill loader tests**

Run: dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter FullyQualifiedName~LoadAll_BundledHistoryExplorer_IsDiscovered
Expected: PASS.

- [x] **Step 4: Commit migrated child skill**

```bash
git add src/OpenClaw.Gateway/skills/history-explorer/SKILL.md src/OpenClaw.Gateway/skills/history-explorer/scripts/explore.py src/OpenClaw.Tests/SkillTests.cs
git commit -m "feat: migrate history-explorer bundled skill for meta-skill-creator"
```

### Task 5: End-to-End MetaSkillCreator Runtime Parity (Agent + MAF)

**Files:**
- Modify: src/OpenClaw.Tests/AgentRuntimeTests.cs
- Modify: src/OpenClaw.Tests/MafAdapterTests.cs
- Modify: src/OpenClaw.Gateway/skills/meta-skill-creator/DEPENDENCY_GAPS.md

- [x] **Step 1: Add AgentRuntime PREVIEW_ONLY flow test**

```csharp
[Fact]
public async Task ExecuteMetaSkillAsync_MetaSkillCreator_PreviewOnly_Completes()
{
    var tools = BuildCreatorToolSetForTests();
    var agent = new AgentRuntime(_chatClient, tools, _memory, _config, maxHistoryTurns: 5, skills: [LoadMetaSkillCreatorDefinition()]);
    var session = new Session { Id = "preview", SenderId = "u", ChannelId = "c" };
    var result = await InvokeMetaSkillAsync(agent, session, "meta-skill-creator", "create a meta-skill preview only", CancellationToken.None);
    Assert.DoesNotContain("not found", result, StringComparison.OrdinalIgnoreCase);
}
```

- [x] **Step 2: Add MAF PREVIEW_ONLY parity test**

```csharp
[Fact]
public async Task MafAgentRuntime_ExecuteMetaSkillAsync_MetaSkillCreator_PreviewOnly_Completes()
{
    var runtime = CreateRuntime(storagePath, new TestLlmExecutionService(), new MafOptions(), tools: BuildCreatorMafTools(), skills: [LoadMetaSkillCreatorDefinition()]);
    var session = CreateSession("maf-meta-creator-preview");
    var result = await InvokeMafMetaSkillAsync(runtime, session, "meta-skill-creator", "create a meta-skill preview", CancellationToken.None);
    Assert.DoesNotContain("not found", result, StringComparison.OrdinalIgnoreCase);
}
```

- [x] **Step 3: Add FULL_GATED minimal test with deterministic stubs for lint/smoke/runtime_e2e/persist**

```csharp
[Fact]
public async Task ExecuteMetaSkillAsync_MetaSkillCreator_FullGated_ProducesPersistencePayload()
{
    var tools = BuildCreatorToolSetForTests();
    var agent = new AgentRuntime(_chatClient, tools, _memory, _config, maxHistoryTurns: 5, skills: [LoadMetaSkillCreatorDefinition()]);
    var session = new Session { Id = "full", SenderId = "u", ChannelId = "c" };
    var result = await InvokeMetaSkillAsync(agent, session, "meta-skill-creator", "create production-ready fully gated meta-skill", CancellationToken.None);
    Assert.Contains("proposal", result, StringComparison.OrdinalIgnoreCase);
}
```

- [x] **Step 4: Run parity slices**

Run: dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter FullyQualifiedName~MetaSkillCreator
Expected: PASS for Agent + MAF tests.

- [x] **Step 5: Update dependency gap doc to closure state**

```md
## Migration Status

- Skill file migration: complete
- Runtime dependency parity: complete
- Current operating mode: executable in PREVIEW_ONLY and FULL_GATED test slices
```

- [x] **Step 6: Commit parity and docs**

```bash
git add src/OpenClaw.Tests/AgentRuntimeTests.cs src/OpenClaw.Tests/MafAdapterTests.cs src/OpenClaw.Gateway/skills/meta-skill-creator/DEPENDENCY_GAPS.md
git commit -m "test: validate meta-skill-creator dependency parity across runtimes"
```

## Self-Review

- Spec coverage: covers all missing dependencies listed in src/OpenClaw.Gateway/skills/meta-skill-creator/DEPENDENCY_GAPS.md (tools + history-explorer) and requires parity tests for Agent + MAF.
- Placeholder scan: no TBD/TODO placeholders; each task has explicit files, code snippets, test commands, and commit steps.
- Type consistency: tool names are consistent with SKILL.md references (emit_text, meta_skill_fill_slots, meta_skill_assemble, meta_skill_lint_run, meta_skill_smoke_run, meta_skill_runtime_e2e_run, meta_skill_persist_proposal).

## Completion Notes

- Implementation status: complete in PR #152.
- Dependency closure: creator tools, built-in registration, history-explorer, and AgentRuntime/MAF parity tests are delivered.
- Validation status: covered by the PR's MetaSkillCreator, AgentRuntime, MafAdapter, SkillLoader, full standard, and sandbox-enabled test runs.

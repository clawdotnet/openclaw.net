using System.Diagnostics;
using System.Text.Json;
using OpenClaw.Cli;
using OpenClaw.Core.Features;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class SkillCommandsTests : IDisposable
{
    private readonly string? _previousOperatorId;

    public SkillCommandsTests()
    {
        _previousOperatorId = Environment.GetEnvironmentVariable("OPENCLAW_OPERATOR_ID");
        if (string.IsNullOrWhiteSpace(_previousOperatorId))
            Environment.SetEnvironmentVariable("OPENCLAW_OPERATOR_ID", "test-operator");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("OPENCLAW_OPERATOR_ID", _previousOperatorId);
    }

    [Fact]
    public async Task RunAsync_InstallDryRun_DoesNotCopySkill()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        try
        {
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            var sourceDir = CreateSkill(root, "Release Notes", "Summarize release notes.");
            var exitCode = await SkillCommands.RunAsync(["install", sourceDir, "--dry-run"]);

            Assert.Equal(0, exitCode);
            Assert.False(Directory.Exists(Path.Combine(workspace, "skills", "release-notes")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Install_CopiesSkillIntoWorkspaceSkills()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        try
        {
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            var sourceDir = CreateSkill(root, "Inbox Triage", "Triage an inbox carefully.");
            var exitCode = await SkillCommands.RunAsync(["install", sourceDir]);

            Assert.Equal(0, exitCode);
            var installedSkillPath = Path.Combine(workspace, "skills", "inbox-triage", "SKILL.md");
            Assert.True(File.Exists(installedSkillPath));
            var installedContents = await File.ReadAllTextAsync(installedSkillPath);
            Assert.Contains("name: Inbox Triage", installedContents, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Install_FromTarballWithSpecialPath_Succeeds()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        try
        {
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            var sourceDir = CreateSkill(root, "Tarball Skill", "Install from a tarball.");
            var tarballPath = Path.Combine(root, "-tarball skill.tgz");
            await CreateTarballAsync(root, Path.GetFileName(sourceDir), tarballPath);

            var exitCode = await SkillCommands.RunAsync(["install", tarballPath]);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(workspace, "skills", "tarball-skill", "SKILL.md")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Install_RejectsSymlinkEntries()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        try
        {
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            var sourceDir = CreateSkill(root, "Symlink Skill", "Should reject symlink content.");
            var outsideFile = Path.Combine(root, "outside.txt");
            await File.WriteAllTextAsync(outsideFile, "secret");
            File.CreateSymbolicLink(Path.Combine(sourceDir, "escape.txt"), outsideFile);

            var exitCode = await SkillCommands.RunAsync(["install", sourceDir]);

            Assert.Equal(1, exitCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Catalog_Json_KindMeta_FiltersMetaSkills()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            var toolSkill = CreateSkill(root, "Tool Skill", "Tool workflow.");
            var metaSkill = CreateSkill(root, "Meta Skill", "Meta workflow.", kind: "meta");

            Assert.Equal(0, await SkillCommands.RunAsync(["install", toolSkill]));
            Assert.Equal(0, await SkillCommands.RunAsync(["install", metaSkill]));

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["catalog", "--kind", "meta", "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            Assert.Equal(1, document.RootElement.GetProperty("count").GetInt32());
            var skill = document.RootElement.GetProperty("skills")[0];
            Assert.Equal("Meta Skill", skill.GetProperty("name").GetString());
            Assert.Equal("meta", skill.GetProperty("kind").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Catalog_InvalidKind_Json_ReturnsErrorSchema()
    {
        var previousError = Console.Error;
        using var error = new StringWriter();

        try
        {
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["catalog", "--kind", "tool", "--json"]);

            Assert.Equal(2, exitCode);
            using var document = JsonDocument.Parse(error.ToString());
            Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
            Assert.Equal("skills catalog", document.RootElement.GetProperty("command").GetString());
            Assert.Equal("invalid_kind", document.RootElement.GetProperty("errorCode").GetString());
        }
        finally
        {
            Console.SetError(previousError);
        }
    }

    [Fact]
    public async Task RunAsync_Inspect_MissingSource_Json_ReturnsErrorSchema()
    {
        var previousError = Console.Error;
        using var error = new StringWriter();

        try
        {
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["inspect", "--json"]);

            Assert.Equal(2, exitCode);
            using var document = JsonDocument.Parse(error.ToString());
            Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
            Assert.Equal("skills inspect", document.RootElement.GetProperty("command").GetString());
            Assert.Equal("missing_source_path", document.RootElement.GetProperty("errorCode").GetString());
        }
        finally
        {
            Console.SetError(previousError);
        }
    }

    [Fact]
    public async Task RunAsync_Install_MissingSource_Json_ReturnsErrorSchema()
    {
        var previousError = Console.Error;
        using var error = new StringWriter();

        try
        {
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["install", "--json"]);

            Assert.Equal(2, exitCode);
            using var document = JsonDocument.Parse(error.ToString());
            Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
            Assert.Equal("skills install", document.RootElement.GetProperty("command").GetString());
            Assert.Equal("missing_source_path", document.RootElement.GetProperty("errorCode").GetString());
        }
        finally
        {
            Console.SetError(previousError);
        }
    }

    [Fact]
    public async Task RunAsync_Inspect_SourceNotFound_Json_ReturnsErrorSchema()
    {
        var previousError = Console.Error;
        using var error = new StringWriter();

        try
        {
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["inspect", "path-that-does-not-exist", "--json"]);

            Assert.Equal(1, exitCode);
            using var document = JsonDocument.Parse(error.ToString());
            Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
            Assert.Equal("skills inspect", document.RootElement.GetProperty("command").GetString());
            Assert.Equal("inspect_failed", document.RootElement.GetProperty("errorCode").GetString());
            Assert.Contains("Skill path not found:", document.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previousError);
        }
    }

    [Fact]
    public async Task RunAsync_Install_SourceNotFound_Json_ReturnsErrorSchema()
    {
        var previousError = Console.Error;
        using var error = new StringWriter();

        try
        {
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["install", "path-that-does-not-exist", "--json"]);

            Assert.Equal(1, exitCode);
            using var document = JsonDocument.Parse(error.ToString());
            Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
            Assert.Equal("skills install", document.RootElement.GetProperty("command").GetString());
            Assert.Equal("inspect_failed", document.RootElement.GetProperty("errorCode").GetString());
            Assert.Contains("Skill path not found:", document.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previousError);
        }
    }

    [Fact]
    public async Task RunAsync_Install_MissingWorkspace_Json_ReturnsErrorSchema()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousError = Console.Error;

        try
        {
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", null);
            var sourceDir = CreateSkill(root, "Install Fails", "Requires workspace to resolve install target.");

            using var error = new StringWriter();
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["install", sourceDir, "--json"]);

            Assert.Equal(1, exitCode);
            using var document = JsonDocument.Parse(error.ToString());
            Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
            Assert.Equal("skills install", document.RootElement.GetProperty("command").GetString());
            Assert.Equal("install_failed", document.RootElement.GetProperty("errorCode").GetString());
            Assert.Contains("Missing OPENCLAW_WORKSPACE", document.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Accept_Json_WithoutOperatorId_ReturnsPermissionDenied()
    {
        var previousError = Console.Error;
        var previousOperator = Environment.GetEnvironmentVariable("OPENCLAW_OPERATOR_ID");
        using var error = new StringWriter();

        try
        {
            Environment.SetEnvironmentVariable("OPENCLAW_OPERATOR_ID", null);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "accept", "sess-any", "--proposal", "meta-run:run-001:paused", "--json"]);

            Assert.Equal(1, exitCode);
            using var document = JsonDocument.Parse(error.ToString());
            Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
            Assert.Equal("skills meta-runs proposals accept", document.RootElement.GetProperty("command").GetString());
            Assert.Equal("permission_denied", document.RootElement.GetProperty("errorCode").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_OPERATOR_ID", previousOperator);
            Console.SetError(previousError);
        }
    }

    [Fact]
    public async Task RunAsync_Proposals_ReadOnlyAlias_ListsDerivedProposalsJson()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-proposals-alias-json",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused"
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user"
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["proposals", "sess-proposals-alias-json", "--storage", memoryPath, "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            Assert.Equal("sess-proposals-alias-json", document.RootElement.GetProperty("sessionId").GetString());
            Assert.Equal(MetaRunProposalEntrypoints.ReadOnlyAlias, document.RootElement.GetProperty("entrypoint").GetString());
            Assert.True(document.RootElement.GetProperty("readOnlyAlias").GetBoolean());
            Assert.Equal(1, document.RootElement.GetProperty("count").GetInt32());
            Assert.Equal("meta-run:run-paused-001:paused", document.RootElement.GetProperty("proposals")[0].GetProperty("id").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Proposals_ReadOnlyAlias_RejectsLifecycleActions()
    {
        var previousError = Console.Error;
        using var error = new StringWriter();

        try
        {
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["proposals", "accept", "sess-any", "--proposal", "meta-run:run-001:paused"]);

            Assert.Equal(2, exitCode);
            Assert.Contains("read-only entry", error.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Console.SetError(previousError);
        }
    }

    [Fact]
    public async Task RunAsync_Proposals_ReadOnlyAlias_RejectsLifecycleActions_Json_ReturnsErrorSchema()
    {
        var previousError = Console.Error;
        using var error = new StringWriter();

        try
        {
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["proposals", "accept", "sess-any", "--proposal", "meta-run:run-001:paused", "--json"]);

            Assert.Equal(2, exitCode);
            using var document = JsonDocument.Parse(error.ToString());
            Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
            Assert.Equal("skills proposals", document.RootElement.GetProperty("command").GetString());
            Assert.Equal("read_only_alias_lifecycle_action", document.RootElement.GetProperty("errorCode").GetString());
        }
        finally
        {
            Console.SetError(previousError);
        }
    }

    [Fact]
    public async Task RunAsync_Create_Json_CreatesMetaSkillScaffold()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["create", "Meta Skill Creator", "--kind", "meta", "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var rootElement = document.RootElement;
            Assert.Equal("Meta Skill Creator", rootElement.GetProperty("name").GetString());
            Assert.Equal("meta-skill-creator", rootElement.GetProperty("slug").GetString());
            Assert.Equal("meta", rootElement.GetProperty("kind").GetString());
            Assert.True(rootElement.GetProperty("created").GetBoolean());
            var skillPath = rootElement.GetProperty("path").GetString();
            Assert.False(string.IsNullOrWhiteSpace(skillPath));
            Assert.True(File.Exists(Path.Combine(skillPath!, "SKILL.md")));

            var skillContents = await File.ReadAllTextAsync(Path.Combine(skillPath!, "SKILL.md"));
            Assert.Contains("kind: meta", skillContents, StringComparison.Ordinal);
            Assert.Contains("composition:", skillContents, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Create_ExistingSkillWithoutForce_ReturnsConflict()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            Assert.Equal(0, await SkillCommands.RunAsync(["create", "meta-skill-creator", "--kind", "meta"]));

            using var error = new StringWriter();
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["create", "meta-skill-creator", "--kind", "meta"]);

            Assert.Equal(1, exitCode);
            Assert.Contains("already exists", error.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Create_Json_WithProposalDraft_IncludesDraftSection()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["create", "Meta Proposal Seed", "--kind", "meta", "--proposal-draft", "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var rootElement = document.RootElement;
            Assert.Equal("meta", rootElement.GetProperty("kind").GetString());
            var proposalDraft = rootElement.GetProperty("proposalDraft");
            Assert.True(proposalDraft.GetProperty("available").GetBoolean());
            Assert.Equal("draft", proposalDraft.GetProperty("status").GetString());
            Assert.Equal("meta_skill_creator_draft", proposalDraft.GetProperty("kind").GetString());
            Assert.Equal("meta-proposal-seed", proposalDraft.GetProperty("id").GetString());
            Assert.Contains("Meta Proposal Seed", proposalDraft.GetProperty("title").GetString(), StringComparison.Ordinal);
            var quality = proposalDraft.GetProperty("quality");
            Assert.Equal(3, quality.GetProperty("checksPassed").GetInt32());
            Assert.Equal(3, quality.GetProperty("checksTotal").GetInt32());
            Assert.Equal(0, quality.GetProperty("warnings").GetArrayLength());
            var checks = quality.GetProperty("checks");
            Assert.Equal(3, checks.GetArrayLength());
            Assert.Equal("name_present", checks[0].GetProperty("id").GetString());
            Assert.Equal("pass", checks[0].GetProperty("status").GetString());
            Assert.Equal("description_present", checks[1].GetProperty("id").GetString());
            Assert.Equal("pass", checks[1].GetProperty("status").GetString());
            Assert.Equal("meta_composition_seeded", checks[2].GetProperty("id").GetString());
            Assert.Equal("pass", checks[2].GetProperty("status").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Create_Text_WithProposalDraft_PrintsDraftSummary()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["create", "Meta Proposal Seed", "--kind", "meta", "--proposal-draft"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            Assert.Contains("Proposal draft: draft", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Proposal kind: meta_skill_creator_draft", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Create_ProposalDraft_WithStandardKind_ReturnsUsage()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            using var error = new StringWriter();
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["create", "Standard Skill", "--kind", "standard", "--proposal-draft"]);

            Assert.Equal(2, exitCode);
            Assert.Contains("--proposal-draft is only supported", error.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Create_ProposalDraft_WithStandardKind_Json_ReturnsErrorCode()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            using var error = new StringWriter();
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["create", "Standard Skill", "--kind", "standard", "--proposal-draft", "--json"]);

            Assert.Equal(2, exitCode);
            using var document = JsonDocument.Parse(error.ToString());
            Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
            Assert.Equal("skills create", document.RootElement.GetProperty("command").GetString());
            Assert.Equal("invalid_proposal_draft_kind", document.RootElement.GetProperty("errorCode").GetString());
        }
        finally
        {
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Create_Json_WithProposalDraft_ShortDescription_TripsQualityGate()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["create", "Meta Proposal Seed", "--kind", "meta", "--description", "short", "--proposal-draft", "--json"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, output.ToString());

            using var document = JsonDocument.Parse(error.ToString());
            Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
            Assert.Equal("skills create", document.RootElement.GetProperty("command").GetString());
            Assert.Equal("proposal_draft_quality_gate_failed", document.RootElement.GetProperty("errorCode").GetString());
            var quality = document.RootElement.GetProperty("quality");
            Assert.Equal(2, quality.GetProperty("checksPassed").GetInt32());
            Assert.Equal(3, quality.GetProperty("checksTotal").GetInt32());
            Assert.Equal(3, quality.GetProperty("minimumChecksPassed").GetInt32());
            Assert.Equal(1, quality.GetProperty("warnings").GetArrayLength());
            Assert.Equal("description_too_short", quality.GetProperty("warnings")[0].GetString());
            var blockingChecks = quality.GetProperty("blockingChecks");
            Assert.Equal(1, blockingChecks.GetArrayLength());
            Assert.Equal("description_present", blockingChecks[0].GetProperty("id").GetString());
            Assert.Equal("warn", blockingChecks[0].GetProperty("status").GetString());
            Assert.Equal("Expand description to include expected behavior and boundaries.", blockingChecks[0].GetProperty("recommendation").GetString());

            Assert.False(Directory.Exists(Path.Combine(workspace, "skills", "meta-proposal-seed")));
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Create_MissingName_Json_ReturnsErrorSchema()
    {
        var previousError = Console.Error;
        using var error = new StringWriter();

        try
        {
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["create", "--json"]);

            Assert.Equal(2, exitCode);
            using var document = JsonDocument.Parse(error.ToString());
            Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
            Assert.Equal("skills create", document.RootElement.GetProperty("command").GetString());
            Assert.Equal("invalid_create_usage", document.RootElement.GetProperty("errorCode").GetString());
            Assert.Contains("Usage: openclaw skills create", document.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previousError);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_PrintsPersistedMetaRunSummary()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                var session = new Session
                {
                    Id = "sess-meta-1",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-001",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "ok",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:00:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:00:02Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 42
                                }
                            }
                        }
                    }
                };

                await store.SaveSessionAsync(session, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "sess-meta-1", "--storage", memoryPath]);

            Assert.Equal(0, exitCode);
            Assert.Contains("Session: sess-meta-1", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Run: run-001", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Skill: meta-flow", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Status: completed", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Steps: 1", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("- Step: draft | kind=llm_chat | status=completed | duration_ms=42", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Unknown skills subcommand", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_UnknownSubcommand_Json_ReturnsErrorSchema()
    {
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["not-a-command", "--json"]);

            Assert.Equal(2, exitCode);
            Assert.Equal(string.Empty, output.ToString());
            using var document = JsonDocument.Parse(error.ToString());
            Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
            Assert.Equal("skills", document.RootElement.GetProperty("command").GetString());
            Assert.Equal("unknown_subcommand", document.RootElement.GetProperty("errorCode").GetString());
            Assert.Contains("Unknown skills subcommand: not-a-command", document.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

    [Fact]
    public async Task RunAsync_UnknownSubcommand_Text_PrintsHelpAndMessage()
    {
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["not-a-command"]);

            Assert.Equal(2, exitCode);
            Assert.Contains("Unknown skills subcommand: not-a-command", error.ToString(), StringComparison.Ordinal);
            Assert.Contains("openclaw skills — Inspect and install local OpenClaw skill packages", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_PrintsStepFailureDetails()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                var session = new Session
                {
                    Id = "sess-meta-failed",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-err-001",
                            SkillName = "meta-flow",
                            Status = "failed",
                            ErrorCode = "tool_failed",
                            Error = "search step failed",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:05:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:05:03Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "search",
                                    Kind = "tool_call",
                                    Status = "failed",
                                    FailureCode = "tool_failed",
                                    DurationMs = 18.5
                                }
                            }
                        }
                    }
                };

                await store.SaveSessionAsync(session, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "sess-meta-failed", "--storage", memoryPath, "--verbose"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("Run: run-err-001", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Error code: tool_failed", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("- Step: search | kind=tool_call | status=failed | duration_ms=18.5 | failure_code=tool_failed", output.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_PrintsContinuedStepFlag()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                var session = new Session
                {
                    Id = "sess-meta-continued",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-cont-001",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "fallback ok",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:10:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:10:04Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "primary",
                                    Kind = "tool_call",
                                    Status = "failed",
                                    FailureCode = "tool_failed",
                                    DurationMs = 12,
                                    Continued = true
                                },
                                new SessionMetaStepResult
                                {
                                    Id = "fallback",
                                    Kind = "tool_call",
                                    Status = "completed",
                                    DurationMs = 7
                                }
                            }
                        }
                    }
                };

                await store.SaveSessionAsync(session, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "sess-meta-continued", "--storage", memoryPath, "--verbose"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("- Step: primary | kind=tool_call | status=failed | duration_ms=12 | failure_code=tool_failed | continued=true", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("- Step: fallback | kind=tool_call | status=completed | duration_ms=7", output.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_WithNoHistory_PrintsZeroRuns()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-empty",
                    ChannelId = "cli",
                    SenderId = "tester"
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "sess-meta-empty", "--storage", memoryPath]);

            Assert.Equal(0, exitCode);
            Assert.Contains("Session: sess-meta-empty", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Meta runs: 0", output.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Verbose_PrintsStepSummaries()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                var session = new Session
                {
                    Id = "sess-meta-verbose",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-verbose-001",
                            SkillName = "meta-flow",
                            Status = "completed",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:12:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:12:03Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "classify",
                                    Kind = "llm_classify",
                                    Status = "completed",
                                    DurationMs = 9
                                }
                            }
                        }
                    }
                };

                await store.SaveSessionAsync(session, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "sess-meta-verbose", "--storage", memoryPath, "--verbose"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("- Step: classify | kind=llm_classify | status=completed | duration_ms=9", output.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_RunFilter_PrintsOnlyRequestedRun()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                var session = new Session
                {
                    Id = "sess-meta-filter",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-older",
                            SkillName = "meta-flow",
                            Status = "failed",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:15:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:15:02Z")
                        },
                        new SessionMetaRunRecord
                        {
                            RunId = "run-target",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "selected",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:16:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:16:02Z")
                        }
                    }
                };

                await store.SaveSessionAsync(session, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "sess-meta-filter", "--storage", memoryPath, "--run", "run-target"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("Session: sess-meta-filter", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Meta runs: 2 total, showing 1", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Run: run-target", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Run: run-older", output.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_RunFilter_WhenRunMissing_PrintsError()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-missing-run",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-present",
                            SkillName = "meta-flow",
                            Status = "completed",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:20:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:20:01Z")
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "sess-meta-missing-run", "--storage", memoryPath, "--run", "run-absent"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, output.ToString());
            Assert.Contains("Run 'run-absent' not found in session 'sess-meta-missing-run'.", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Json_PrintsFilteredRunPayload()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-json",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-json-older",
                            SkillName = "meta-flow",
                            Status = "failed",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:25:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:25:01Z")
                        },
                        new SessionMetaRunRecord
                        {
                            RunId = "run-json-target",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "json ok",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:26:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:26:02Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 21
                                }
                            }
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "sess-meta-json", "--storage", memoryPath, "--run", "run-json-target", "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var rootElement = document.RootElement;
            Assert.Equal("sess-meta-json", rootElement.GetProperty("sessionId").GetString());
            Assert.Equal(2, rootElement.GetProperty("totalCount").GetInt32());
            Assert.Equal(1, rootElement.GetProperty("shownCount").GetInt32());

            var runs = rootElement.GetProperty("runs");
            Assert.Equal(1, runs.GetArrayLength());
            Assert.Equal("run-json-target", runs[0].GetProperty("runId").GetString());
            Assert.Equal("json ok", runs[0].GetProperty("finalText").GetString());
            Assert.Equal(1, runs[0].GetProperty("stepResults").GetArrayLength());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Json_WithNoHistory_PrintsEmptyPayload()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-json-empty",
                    ChannelId = "cli",
                    SenderId = "tester"
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "sess-meta-json-empty", "--storage", memoryPath, "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var rootElement = document.RootElement;
            Assert.Equal("sess-meta-json-empty", rootElement.GetProperty("sessionId").GetString());
            Assert.Equal(0, rootElement.GetProperty("totalCount").GetInt32());
            Assert.Equal(0, rootElement.GetProperty("shownCount").GetInt32());
            Assert.Equal(0, rootElement.GetProperty("runs").GetArrayLength());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_JsonVerbose_PrintsSameStructuredPayload()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-json-verbose",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-json-verbose",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "json verbose ok",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:28:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:28:02Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "fallback",
                                    Kind = "tool_call",
                                    Status = "completed",
                                    DurationMs = 5
                                }
                            }
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "sess-meta-json-verbose", "--storage", memoryPath, "--json", "--verbose"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var run = document.RootElement.GetProperty("runs")[0];
            Assert.Equal("run-json-verbose", run.GetProperty("runId").GetString());
            Assert.Equal(1, run.GetProperty("stepResults").GetArrayLength());
            Assert.Equal("fallback", run.GetProperty("stepResults")[0].GetProperty("id").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_ReplayPreview_Json_PrintsUnavailableSummary()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-replay-preview",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-preview-001",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "done",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:30:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:30:02Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 8
                                }
                            }
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "replay", "sess-meta-replay-preview", "--storage", memoryPath, "--run", "run-preview-001", "--json"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var rootElement = document.RootElement;
            Assert.Equal("sess-meta-replay-preview", rootElement.GetProperty("sessionId").GetString());
            Assert.Equal("run-preview-001", rootElement.GetProperty("runId").GetString());
            Assert.False(rootElement.GetProperty("replayAvailable").GetBoolean());
            Assert.Contains(MetaRunReplayReasons.NotEnoughInputsForExecutableReplay, rootElement.GetProperty("reason").GetString(), StringComparison.Ordinal);
            var availableArtifacts = rootElement.GetProperty("availableArtifacts");
            Assert.Equal(2, availableArtifacts.GetArrayLength());
            Assert.Equal(MetaRunReplayArtifactNames.FinalText, availableArtifacts[0].GetString());
            Assert.Equal(MetaRunReplayArtifactNames.StepResults, availableArtifacts[1].GetString());
            var retainedSteps = rootElement.GetProperty("retainedSteps");
            Assert.Single(retainedSteps.EnumerateArray());
            Assert.Equal("draft", retainedSteps[0].GetProperty("id").GetString());
            Assert.Equal("llm_chat", retainedSteps[0].GetProperty("kind").GetString());
            Assert.Equal("completed", retainedSteps[0].GetProperty("status").GetString());
            Assert.Equal(8, retainedSteps[0].GetProperty("durationMs").GetDouble());
            Assert.False(retainedSteps[0].GetProperty("continued").GetBoolean());
            var plan = rootElement.GetProperty("plan");
            Assert.Equal(MetaRunReplayPlanSummaries.AuditableNotReplayable, plan.GetProperty("summary").GetString());
            Assert.Equal(MetaRunReplayModes.PreviewOnly, plan.GetProperty("mode").GetString());
            Assert.False(plan.GetProperty("executable").GetBoolean());
            var replayableSteps = plan.GetProperty("replayableSteps");
            Assert.Single(replayableSteps.EnumerateArray());
            Assert.Equal("draft", replayableSteps[0].GetProperty("id").GetString());
            Assert.Equal(MetaRunReplayStepReadinessKinds.TraceOnly, replayableSteps[0].GetProperty("readiness").GetString());
            Assert.Equal(MetaRunReplayStepReadinessReasons.TraceOnly, replayableSteps[0].GetProperty("reason").GetString());
            var blockedBy = plan.GetProperty("blockedByRequirements");
            Assert.Equal(3, blockedBy.GetArrayLength());
            Assert.Equal(MetaRunReplayRequirementNames.PromptContext, blockedBy[0].GetProperty("name").GetString());
            Assert.Equal(MetaRunReplayRequirementKinds.NotPersisted, blockedBy[0].GetProperty("kind").GetString());
            Assert.Equal(MetaRunReplayRequirementNames.StepInputs, blockedBy[1].GetProperty("name").GetString());
            Assert.Equal(MetaRunReplayRequirementKinds.NotPersisted, blockedBy[1].GetProperty("kind").GetString());
            Assert.Equal(MetaRunReplayRequirementNames.ToolArguments, blockedBy[2].GetProperty("name").GetString());
            Assert.Equal(MetaRunReplayRequirementKinds.NotPersisted, blockedBy[2].GetProperty("kind").GetString());
            var missingRequirements = rootElement.GetProperty("missingRequirements");
            Assert.Equal(3, missingRequirements.GetArrayLength());
            Assert.Equal(MetaRunReplayRequirementNames.PromptContext, missingRequirements[0].GetProperty("name").GetString());
            Assert.Equal(MetaRunReplayRequirementKinds.NotPersisted, missingRequirements[0].GetProperty("kind").GetString());
            Assert.Equal(MetaRunReplayRequirementReasons.PromptContextNotPersisted, missingRequirements[0].GetProperty("reason").GetString());
            Assert.Equal(MetaRunReplayRequirementNames.StepInputs, missingRequirements[1].GetProperty("name").GetString());
            Assert.Equal(MetaRunReplayRequirementKinds.NotPersisted, missingRequirements[1].GetProperty("kind").GetString());
            Assert.Equal(MetaRunReplayRequirementReasons.StepInputsNotPersisted, missingRequirements[1].GetProperty("reason").GetString());
            Assert.Equal(MetaRunReplayRequirementNames.ToolArguments, missingRequirements[2].GetProperty("name").GetString());
            Assert.Equal(MetaRunReplayRequirementKinds.NotPersisted, missingRequirements[2].GetProperty("kind").GetString());
            Assert.Equal(MetaRunReplayRequirementReasons.ToolArgumentsNotPersisted, missingRequirements[2].GetProperty("reason").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_ReplayPreview_Json_FailedContinuedStep_PrintsDerivedReadiness()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-replay-json-derived-readiness",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-preview-json-derived-readiness",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "done",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:33:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:33:03Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "primary",
                                    Kind = "tool_call",
                                    Status = "failed",
                                    FailureCode = "tool_failed",
                                    DurationMs = 12,
                                    Continued = true
                                }
                            }
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "replay", "sess-meta-replay-json-derived-readiness", "--storage", memoryPath, "--run", "run-preview-json-derived-readiness", "--json"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var replayableSteps = document.RootElement.GetProperty("plan").GetProperty("replayableSteps");
            Assert.Single(replayableSteps.EnumerateArray());
            Assert.Equal("primary", replayableSteps[0].GetProperty("id").GetString());
            Assert.Equal(MetaRunReplayStepReadinessKinds.FailureTraceContinued, replayableSteps[0].GetProperty("readiness").GetString());
            Assert.Equal(MetaRunReplayStepReadinessReasons.FailureTraceContinued, replayableSteps[0].GetProperty("reason").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_ReplayPreview_Json_SkillExecMissingInputs_ReportsMachineReadableRequirement()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-replay-skill-exec-missing",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-replay-skill-exec-missing-001",
                            SkillName = "meta-flow",
                            Status = "completed",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-13T10:20:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-13T10:20:02Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "exec",
                                    Kind = "skill_exec",
                                    Status = "completed",
                                    DurationMs = 7
                                }
                            }
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "replay", "sess-meta-replay-skill-exec-missing", "--storage", memoryPath, "--run", "run-replay-skill-exec-missing-001", "--json"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var missingRequirements = document.RootElement.GetProperty("missingRequirements");
            Assert.Contains(missingRequirements.EnumerateArray(), item =>
                string.Equals(item.GetProperty("name").GetString(), MetaRunReplayRequirementNames.SkillExecInputs, StringComparison.Ordinal)
                && string.Equals(item.GetProperty("kind").GetString(), MetaRunReplayRequirementKinds.NotPersisted, StringComparison.Ordinal)
                && string.Equals(item.GetProperty("reason").GetString(), MetaRunReplayRequirementReasons.SkillExecInputsNotPersisted, StringComparison.Ordinal));

            var operatorSummary = document.RootElement.GetProperty("operatorSummary");
            Assert.Equal(1, operatorSummary.GetProperty("skillExecSteps").GetInt32());
            Assert.Equal(1, operatorSummary.GetProperty("skillExecStepsWithoutEvidence").GetInt32());

            var triageHints = document.RootElement.GetProperty("triageHints");
            Assert.Contains(triageHints.EnumerateArray(), item =>
                string.Equals(item.GetProperty("code").GetString(), MetaRunReplayTriageHintCodes.SkillExecInputsNotPersisted, StringComparison.Ordinal)
                && item.GetProperty("priority").GetInt32() == 1);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_ReplayPreview_Json_FailedAndContinuedOnlySteps_PrintDistinctDerivedReadiness()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-replay-json-derived-readiness-extra",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-preview-json-derived-readiness-extra",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "done",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:35:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:35:03Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "failed",
                                    Kind = "tool_call",
                                    Status = "failed",
                                    FailureCode = "tool_failed",
                                    DurationMs = 7
                                },
                                new SessionMetaStepResult
                                {
                                    Id = "continued",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 5,
                                    Continued = true
                                }
                            }
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "replay", "sess-meta-replay-json-derived-readiness-extra", "--storage", memoryPath, "--run", "run-preview-json-derived-readiness-extra", "--json"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var replayableSteps = document.RootElement.GetProperty("plan").GetProperty("replayableSteps");
            Assert.Equal(2, replayableSteps.GetArrayLength());
            Assert.Equal("failed", replayableSteps[0].GetProperty("id").GetString());
            Assert.Equal(MetaRunReplayStepReadinessKinds.FailureTraceOnly, replayableSteps[0].GetProperty("readiness").GetString());
            Assert.Equal(MetaRunReplayStepReadinessReasons.FailureTraceOnly, replayableSteps[0].GetProperty("reason").GetString());
            Assert.Equal("continued", replayableSteps[1].GetProperty("id").GetString());
            Assert.Equal(MetaRunReplayStepReadinessKinds.ContinuationTraceOnly, replayableSteps[1].GetProperty("readiness").GetString());
            Assert.Equal(MetaRunReplayStepReadinessReasons.ContinuationTraceOnly, replayableSteps[1].GetProperty("reason").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_ReplayPreview_Text_PrintsUnavailableSummary()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-replay-text",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-preview-text",
                            SkillName = "meta-flow",
                            Status = "failed",
                            ErrorCode = "tool_failed",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:31:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:31:03Z")
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "replay", "sess-meta-replay-text", "--storage", memoryPath, "--run", "run-preview-text"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            Assert.Contains("Replay preview for run: run-preview-text", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Replay available: no", output.ToString(), StringComparison.Ordinal);
            Assert.Contains(MetaRunReplayReasons.NotEnoughInputsForExecutableReplay, output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Replay plan:", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Operator summary:", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"Summary: {MetaRunReplayPlanSummaries.MetadataOnlyNotReplayable}", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"Mode: {MetaRunReplayModes.PreviewOnly}", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Executable: no", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"Blocked by requirements: {MetaRunReplayRequirementNames.PromptContext} | kind={MetaRunReplayRequirementKinds.NotPersisted} | reason=", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"{MetaRunReplayRequirementNames.StepInputs} | kind={MetaRunReplayRequirementKinds.NotPersisted} | reason=", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"{MetaRunReplayRequirementNames.ToolArguments} | kind={MetaRunReplayRequirementKinds.NotPersisted} | reason=", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"{MetaRunReplayRequirementNames.StepResults} | kind={MetaRunReplayRequirementKinds.NotRetained} | reason=", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Available artifacts:", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"- {MetaRunReplayArtifactNames.ErrorCode}", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Retained steps:", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Missing replay inputs:", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"- {MetaRunReplayRequirementNames.PromptContext} | kind={MetaRunReplayRequirementKinds.NotPersisted} | reason=", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"- {MetaRunReplayRequirementNames.StepInputs} | kind={MetaRunReplayRequirementKinds.NotPersisted} | reason=", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"- {MetaRunReplayRequirementNames.ToolArguments} | kind={MetaRunReplayRequirementKinds.NotPersisted} | reason=", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"- {MetaRunReplayRequirementNames.StepResults} | kind={MetaRunReplayRequirementKinds.NotRetained} | reason=", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_ReplayPreview_Text_WithRetainedSteps_PrintsStepReadiness()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-replay-text-steps",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-preview-text-steps",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "done",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:32:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:32:03Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 8
                                }
                            }
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "replay", "sess-meta-replay-text-steps", "--storage", memoryPath, "--run", "run-preview-text-steps"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            Assert.Contains($"Summary: {MetaRunReplayPlanSummaries.AuditableNotReplayable}", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"Replayable steps: draft | readiness={MetaRunReplayStepReadinessKinds.TraceOnly} | reason={MetaRunReplayStepReadinessReasons.TraceOnly}", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"Blocked by requirements: {MetaRunReplayRequirementNames.PromptContext} | kind={MetaRunReplayRequirementKinds.NotPersisted} | reason=", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"{MetaRunReplayRequirementNames.StepInputs} | kind={MetaRunReplayRequirementKinds.NotPersisted} | reason=", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"{MetaRunReplayRequirementNames.ToolArguments} | kind={MetaRunReplayRequirementKinds.NotPersisted} | reason=", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Retained steps:", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_ReplayPreview_Text_FailedContinuedStep_PrintsDerivedReadiness()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-replay-text-derived-readiness",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-preview-text-derived-readiness",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "done",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:34:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:34:03Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "primary",
                                    Kind = "tool_call",
                                    Status = "failed",
                                    FailureCode = "tool_failed",
                                    DurationMs = 12,
                                    Continued = true
                                }
                            }
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "replay", "sess-meta-replay-text-derived-readiness", "--storage", memoryPath, "--run", "run-preview-text-derived-readiness"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            Assert.Contains($"Replayable steps: primary | readiness={MetaRunReplayStepReadinessKinds.FailureTraceContinued} | reason={MetaRunReplayStepReadinessReasons.FailureTraceContinued}", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_ReplayPreview_Text_FailedAndContinuedOnlySteps_PrintDistinctDerivedReadiness()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-replay-text-derived-readiness-extra",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-preview-text-derived-readiness-extra",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "done",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-12T13:36:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-12T13:36:03Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "failed",
                                    Kind = "tool_call",
                                    Status = "failed",
                                    FailureCode = "tool_failed",
                                    DurationMs = 7
                                },
                                new SessionMetaStepResult
                                {
                                    Id = "continued",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 5,
                                    Continued = true
                                }
                            }
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "replay", "sess-meta-replay-text-derived-readiness-extra", "--storage", memoryPath, "--run", "run-preview-text-derived-readiness-extra"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            Assert.Contains($"failed | readiness={MetaRunReplayStepReadinessKinds.FailureTraceOnly} | reason={MetaRunReplayStepReadinessReasons.FailureTraceOnly}", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"continued | readiness={MetaRunReplayStepReadinessKinds.ContinuationTraceOnly} | reason={MetaRunReplayStepReadinessReasons.ContinuationTraceOnly}", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Reconstruct_Json_PrintsHistoryOnlyCompletedRun()
    {
        var root = CreateTempRoot();

        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-reconstruct-json",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-reconstruct-001",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "done",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-13T10:00:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-13T10:00:02Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 8
                                }
                            }
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "reconstruct", "sess-meta-reconstruct-json", "--storage", memoryPath, "--run", "run-reconstruct-001", "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var rootElement = document.RootElement;
            Assert.Equal("sess-meta-reconstruct-json", rootElement.GetProperty("sessionId").GetString());
            Assert.Equal("run-reconstruct-001", rootElement.GetProperty("runId").GetString());
            Assert.Equal(MetaRunReplayExecutionModes.AuditReconstruction, rootElement.GetProperty("mode").GetString());
            Assert.Equal(MetaRunReplayExecutionSources.HistoryOnly, rootElement.GetProperty("source").GetString());
            Assert.Equal("completed", rootElement.GetProperty("status").GetString());
            Assert.Equal("done", rootElement.GetProperty("finalText").GetString());
            Assert.Single(rootElement.GetProperty("timeline").EnumerateArray());
            Assert.False(rootElement.GetProperty("proposalSummary").GetProperty("available").GetBoolean());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Reconstruct_Json_SkillExecStep_IncludesExecutionEvidenceInNotes()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-reconstruct-skill-exec-json",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-reconstruct-skill-exec-001",
                            SkillName = "meta-flow",
                            Status = "completed",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-13T10:15:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-13T10:15:03Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "exec",
                                    Kind = "skill_exec",
                                    Status = "completed",
                                    DurationMs = 9,
                                    ExecutionEvidence = new SessionMetaStepExecutionEvidence
                                    {
                                        CommandPreview = "echo-stdin.ps1 --flag value",
                                        InputMode = "stdin",
                                        StdinBytes = 14,
                                        ParseMode = "xml"
                                    }
                                }
                            }
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "reconstruct", "sess-meta-reconstruct-skill-exec-json", "--storage", memoryPath, "--run", "run-reconstruct-skill-exec-001", "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var timeline = document.RootElement.GetProperty("timeline");
            Assert.Single(timeline.EnumerateArray());
            Assert.Equal("skill_exec", timeline[0].GetProperty("kind").GetString());
            var notes = timeline[0].GetProperty("notes").GetString();
            Assert.NotNull(notes);
            Assert.Contains("input_mode=stdin", notes, StringComparison.Ordinal);
            Assert.Contains("stdin_bytes=14", notes, StringComparison.Ordinal);
            Assert.Contains("parse_mode=xml", notes, StringComparison.Ordinal);

            var triageHints = document.RootElement.GetProperty("triageHints");
            Assert.Contains(triageHints.EnumerateArray(), item =>
                string.Equals(item.GetProperty("code").GetString(), MetaRunReplayTriageHintCodes.SkillExecParseModeAnomaly, StringComparison.Ordinal)
                && item.GetProperty("priority").GetInt32() == 2);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Reconstruct_Json_PausedRun_UsesCheckpointAugmentation()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-reconstruct-paused",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord

                        {
                            RunId = "run-reconstruct-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-13T10:05:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-13T10:05:02Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 4
                                }
                            }
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user",
                        Prompt = "Need more detail",
                        PendingStepIds = ["ask_user"],
                        BlockedStepIds = ["finalize"],
                        Outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["draft"] = "draft output"
                        },
                        FailureAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["primary"] = "fallback"
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "reconstruct", "sess-meta-reconstruct-paused", "--storage", memoryPath, "--run", "run-reconstruct-paused-001", "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var rootElement = document.RootElement;
            Assert.Equal(MetaRunReplayExecutionSources.HistoryPlusCheckpoint, rootElement.GetProperty("source").GetString());
            Assert.Equal("ask_user", rootElement.GetProperty("checkpoint").GetProperty("pendingStepId").GetString());
            Assert.True(rootElement.GetProperty("checkpoint").GetProperty("promptPresent").GetBoolean());
            Assert.Equal("finalize", rootElement.GetProperty("checkpoint").GetProperty("blockedStepIds")[0].GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Reconstruct_Text_PrintsTimelineAndCheckpointSections()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-reconstruct-text",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-reconstruct-text-001",
                            SkillName = "meta-flow",
                            Status = "paused",
                            StartedAtUtc = DateTimeOffset.Parse("2026-06-13T10:10:00Z"),
                            CompletedAtUtc = DateTimeOffset.Parse("2026-06-13T10:10:03Z"),
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 4
                                }
                            }
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user",
                        Prompt = "Need more detail",
                        PendingStepIds = ["ask_user"],
                        BlockedStepIds = ["finalize"]
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "reconstruct", "sess-meta-reconstruct-text", "--storage", memoryPath, "--run", "run-reconstruct-text-001"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            Assert.Contains("Replay reconstruction for run: run-reconstruct-text-001", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"Mode: {MetaRunReplayExecutionModes.AuditReconstruction}", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"Source: {MetaRunReplayExecutionSources.HistoryPlusCheckpoint}", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Operator summary:", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Timeline:", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("- 1 | step=draft | kind=llm_chat | status=completed | duration_ms=4", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Checkpoint:", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Pending step: ask_user", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Replay available:", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Missing replay inputs:", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Reconstruct_MissingRun_ReturnsUsage()
    {
        var previousError = Console.Error;
        using var error = new StringWriter();

        try
        {
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "reconstruct", "sess-meta-reconstruct-json"]);

            Assert.Equal(2, exitCode);
            Assert.Contains("--run <run-id> is required for meta-runs reconstruct.", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previousError);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Reconstruct_MissingRun_Json_ReturnsErrorSchema()
    {
        var previousError = Console.Error;
        using var error = new StringWriter();

        try
        {
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "reconstruct", "sess-meta-reconstruct-json", "--json"]);

            Assert.Equal(2, exitCode);
            using var document = JsonDocument.Parse(error.ToString());
            Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
            Assert.Equal("skills meta-runs reconstruct", document.RootElement.GetProperty("command").GetString());
            Assert.Equal("missing_run_id", document.RootElement.GetProperty("errorCode").GetString());
        }
        finally
        {
            Console.SetError(previousError);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Replay_MissingRun_Json_ReturnsErrorSchema()
    {
        var previousError = Console.Error;
        using var error = new StringWriter();

        try
        {
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "replay", "sess-meta-replay-json", "--json"]);

            Assert.Equal(2, exitCode);
            using var document = JsonDocument.Parse(error.ToString());
            Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
            Assert.Equal("skills meta-runs replay", document.RootElement.GetProperty("command").GetString());
            Assert.Equal("missing_run_id", document.RootElement.GetProperty("errorCode").GetString());
        }
        finally
        {
            Console.SetError(previousError);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Reconstruct_SessionMissing_PrintsError()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "reconstruct", "sess-meta-missing", "--storage", memoryPath, "--run", "run-001"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, output.ToString());
            Assert.Contains("Session 'sess-meta-missing' not found.", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Reconstruct_SessionMissing_Json_ReturnsErrorSchema()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "reconstruct", "sess-meta-missing", "--storage", memoryPath, "--run", "run-001", "--json"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, output.ToString());
            using var document = JsonDocument.Parse(error.ToString());
            Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
            Assert.Equal("skills meta-runs reconstruct", document.RootElement.GetProperty("command").GetString());
            Assert.Equal("session_not_found", document.RootElement.GetProperty("errorCode").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Reconstruct_RunMissing_PrintsError()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-reconstruct-missing-run",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-present",
                            SkillName = "meta-flow",
                            Status = "completed"
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "reconstruct", "sess-meta-reconstruct-missing-run", "--storage", memoryPath, "--run", "run-absent"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, output.ToString());
            Assert.Contains("Run 'run-absent' not found in session 'sess-meta-reconstruct-missing-run'.", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Reconstruct_RunMissing_Json_ReturnsErrorSchema()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-reconstruct-missing-run",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-present",
                            SkillName = "meta-flow",
                            Status = "completed"
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "reconstruct", "sess-meta-reconstruct-missing-run", "--storage", memoryPath, "--run", "run-absent", "--json"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, output.ToString());
            using var document = JsonDocument.Parse(error.ToString());
            Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
            Assert.Equal("skills meta-runs reconstruct", document.RootElement.GetProperty("command").GetString());
            Assert.Equal("run_not_found", document.RootElement.GetProperty("errorCode").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Json_PrintsDerivedPausedAndFailedSummaries()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-json",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused",
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 4
                                }
                            }
                        },
                        new SessionMetaRunRecord
                        {
                            RunId = "run-failed-001",
                            SkillName = "meta-flow",
                            Status = "failed",
                            ErrorCode = "tool_failed"
                        },
                        new SessionMetaRunRecord
                        {
                            RunId = "run-completed-001",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "done"
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user",
                        Prompt = "Need more detail",
                        PendingStepIds = ["ask_user"],
                        BlockedStepIds = ["finalize"],
                        Outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["draft"] = "draft output"
                        },
                        FailureAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["tool_call"] = "tool_failed"
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "proposals", "sess-meta-proposals-json", "--storage", memoryPath, "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var rootElement = document.RootElement;
            Assert.Equal("sess-meta-proposals-json", rootElement.GetProperty("sessionId").GetString());
            Assert.Equal(MetaRunProposalEntrypoints.MetaRuns, rootElement.GetProperty("entrypoint").GetString());
            Assert.False(rootElement.GetProperty("readOnlyAlias").GetBoolean());
            Assert.Equal(2, rootElement.GetProperty("count").GetInt32());
            var proposals = rootElement.GetProperty("proposals");
            Assert.Equal("meta-run:run-paused-001:paused", proposals[0].GetProperty("id").GetString());
            Assert.Equal("paused_run_followup", proposals[0].GetProperty("kind").GetString());
            Assert.Equal("derived_meta_run_evidence", proposals[0].GetProperty("source").GetString());
            Assert.Equal("show", proposals[0].GetProperty("availableActions")[0].GetString());
            Assert.Equal("meta-run:run-failed-001:failed", proposals[1].GetProperty("id").GetString());
            Assert.Equal("failed_run_review", proposals[1].GetProperty("kind").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Json_PreservesMetaRunHistoryOrder()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-json-order",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-failed-001",
                            SkillName = "meta-flow",
                            Status = "failed",
                            ErrorCode = "tool_failed"
                        },
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused"
                        },
                        new SessionMetaRunRecord
                        {
                            RunId = "run-failed-002",
                            SkillName = "meta-flow",
                            Status = "failed",
                            ErrorCode = "model_timeout"
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user"
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "proposals", "sess-meta-proposals-json-order", "--storage", memoryPath, "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var proposals = document.RootElement.GetProperty("proposals");
            Assert.Equal(3, proposals.GetArrayLength());
            Assert.Equal("meta-run:run-failed-001:failed", proposals[0].GetProperty("id").GetString());
            Assert.Equal("meta-run:run-paused-001:paused", proposals[1].GetProperty("id").GetString());
            Assert.Equal("meta-run:run-failed-002:failed", proposals[2].GetProperty("id").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Json_WithRunFilter_PrintsRequestedProposalOnly()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-json-run-filter",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused"
                        },
                        new SessionMetaRunRecord
                        {
                            RunId = "run-failed-001",
                            SkillName = "meta-flow",
                            Status = "failed",
                            ErrorCode = "tool_failed"
                        },
                        new SessionMetaRunRecord
                        {
                            RunId = "run-completed-001",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "done"
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user"
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "proposals", "sess-meta-proposals-json-run-filter", "--storage", memoryPath, "--run", "run-failed-001", "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var rootElement = document.RootElement;
            Assert.Equal(1, rootElement.GetProperty("count").GetInt32());
            var proposals = rootElement.GetProperty("proposals");
            Assert.Single(proposals.EnumerateArray());
            Assert.Equal("meta-run:run-failed-001:failed", proposals[0].GetProperty("id").GetString());
            Assert.Equal("failed_run_review", proposals[0].GetProperty("kind").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Show_Json_PrintsDerivedPausedDetail()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-show",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused",
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 4
                                },
                                new SessionMetaStepResult
                                {
                                    Id = "tool_call",
                                    Kind = "tool_call",
                                    Status = "failed",
                                    FailureCode = "tool_failed",
                                    DurationMs = 9,
                                    Continued = true
                                }
                            }
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user",
                        Prompt = "Need more detail",
                        PendingStepIds = ["ask_user"],
                        BlockedStepIds = ["finalize"],
                        Outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["draft"] = "draft output"
                        },
                        FailureAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["tool_call"] = "tool_failed"
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "proposals", "show", "sess-meta-proposals-show", "--storage", memoryPath, "--proposal", "meta-run:run-paused-001:paused", "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var rootElement = document.RootElement;
            Assert.Equal("sess-meta-proposals-show", rootElement.GetProperty("sessionId").GetString());
            Assert.Equal(MetaRunProposalEntrypoints.MetaRuns, rootElement.GetProperty("entrypoint").GetString());
            Assert.False(rootElement.GetProperty("readOnlyAlias").GetBoolean());
            var proposal = rootElement.GetProperty("proposal");
            Assert.Equal("meta-run:run-paused-001:paused", proposal.GetProperty("id").GetString());
            Assert.Equal("paused_run_followup", proposal.GetProperty("kind").GetString());
            Assert.Equal("ask_user", proposal.GetProperty("pendingStepId").GetString());
            Assert.Equal("draft", proposal.GetProperty("timelineStepIds")[0].GetString());
            var checkpoint = proposal.GetProperty("checkpoint");
            Assert.Equal("ask_user", checkpoint.GetProperty("pendingStepId").GetString());
            Assert.True(checkpoint.GetProperty("promptPresent").GetBoolean());
            Assert.Equal("draft", checkpoint.GetProperty("outputStepIds")[0].GetString());
            Assert.Equal("tool_call", checkpoint.GetProperty("failureAliasStepIds")[0].GetString());
            var steps = proposal.GetProperty("steps");
            Assert.Equal(2, steps.GetArrayLength());
            Assert.Equal("draft", steps[0].GetProperty("id").GetString());
            Assert.Equal("llm_chat", steps[0].GetProperty("kind").GetString());
            Assert.Equal("completed", steps[0].GetProperty("status").GetString());
            Assert.Equal(4, steps[0].GetProperty("durationMs").GetDouble());
            Assert.Equal("tool_call", steps[1].GetProperty("id").GetString());
            Assert.Equal("tool_call", steps[1].GetProperty("kind").GetString());
            Assert.Equal("failed", steps[1].GetProperty("status").GetString());
            Assert.Equal("tool_failed", steps[1].GetProperty("failureCode").GetString());
            Assert.Equal(9, steps[1].GetProperty("durationMs").GetDouble());
            Assert.True(steps[1].GetProperty("continued").GetBoolean());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Proposals_ReadOnlyAlias_Show_Json_PrintsDerivedPausedDetail()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-proposals-alias-show",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused"
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user"
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["proposals", "show", "sess-proposals-alias-show", "--storage", memoryPath, "--proposal", "meta-run:run-paused-001:paused", "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var rootElement = document.RootElement;
            Assert.Equal("sess-proposals-alias-show", rootElement.GetProperty("sessionId").GetString());
            Assert.Equal(MetaRunProposalEntrypoints.ReadOnlyAlias, rootElement.GetProperty("entrypoint").GetString());
            Assert.True(rootElement.GetProperty("readOnlyAlias").GetBoolean());
            Assert.Equal("meta-run:run-paused-001:paused", rootElement.GetProperty("proposal").GetProperty("id").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Text_PrintsDerivedList()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-text",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused"
                        },
                        new SessionMetaRunRecord
                        {
                            RunId = "run-failed-001",
                            SkillName = "meta-flow",
                            Status = "failed",
                            ErrorCode = "tool_failed"
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user"
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "proposals", "sess-meta-proposals-text", "--storage", memoryPath]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            Assert.Contains("Session: sess-meta-proposals-text", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Derived proposals: 2", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Proposal: meta-run:run-paused-001:paused", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Source: derived_meta_run_evidence", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Available actions: show", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Accept:", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Proposal lifecycle:", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Show_Text_PrintsStepDetails()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-show-text",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-failed-001",
                            SkillName = "meta-flow",
                            Status = "failed",
                            ErrorCode = "tool_failed",
                            Error = "Tool call crashed.",
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 3
                                },
                                new SessionMetaStepResult
                                {
                                    Id = "tool_call",
                                    Kind = "tool_call",
                                    Status = "failed",
                                    FailureCode = "tool_failed",
                                    DurationMs = 8,
                                    Continued = false
                                }
                            }
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "proposals", "show", "sess-meta-proposals-show-text", "--storage", memoryPath, "--proposal", "meta-run:run-failed-001:failed"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            Assert.Contains("Proposal: meta-run:run-failed-001:failed", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Timeline steps: draft, tool_call", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Evidence:", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Evidence timeline steps: draft, tool_call", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Evidence error code: tool_failed", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Evidence error: Tool call crashed.", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Steps:", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("- draft | kind=llm_chat | status=completed | durationMs=3", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("- tool_call | kind=tool_call | status=failed | failureCode=tool_failed | durationMs=8 | continued=false", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Show_Text_PrintsCheckpointSection()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-show-checkpoint-text",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused",
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 4
                                }
                            }
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user",
                        Prompt = "Need more detail",
                        PendingStepIds = ["ask_user"],
                        BlockedStepIds = ["finalize"],
                        Outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["draft"] = "draft output"
                        },
                        FailureAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["tool_call"] = "tool_failed"
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "proposals", "show", "sess-meta-proposals-show-checkpoint-text", "--storage", memoryPath, "--proposal", "meta-run:run-paused-001:paused"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            Assert.Contains("Checkpoint:", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Pending step: ask_user", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Prompt present: yes", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Output steps: draft", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Failure alias steps: tool_call", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Show_Json_PrintsEvidenceSection()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-show-evidence-json",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-failed-001",
                            SkillName = "meta-flow",
                            Status = "failed",
                            ErrorCode = "tool_failed",
                            Error = "Tool call crashed.",
                            FinalText = "partial output",
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 3
                                },
                                new SessionMetaStepResult
                                {
                                    Id = "tool_call",
                                    Kind = "tool_call",
                                    Status = "failed",
                                    FailureCode = "tool_failed",
                                    DurationMs = 8
                                }
                            }
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "proposals", "show", "sess-meta-proposals-show-evidence-json", "--storage", memoryPath, "--proposal", "meta-run:run-failed-001:failed", "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var proposal = document.RootElement.GetProperty("proposal");
            Assert.Equal("tool_failed", proposal.GetProperty("errorCode").GetString());
            Assert.Equal("Tool call crashed.", proposal.GetProperty("error").GetString());
            Assert.Equal("partial output", proposal.GetProperty("finalText").GetString());
            var evidence = proposal.GetProperty("evidence");
            Assert.Equal("draft", evidence.GetProperty("timelineStepIds")[0].GetString());
            Assert.Equal("tool_call", evidence.GetProperty("timelineStepIds")[1].GetString());
            Assert.Equal("tool_failed", evidence.GetProperty("errorCode").GetString());
            Assert.Equal("Tool call crashed.", evidence.GetProperty("error").GetString());
            Assert.Equal("partial output", evidence.GetProperty("finalText").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Show_Json_KeepsLegacyMirrorsAlongsideGroupedDetail()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-show-legacy-json",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused",
                            ErrorCode = "tool_failed",
                            Error = "Tool call crashed.",
                            FinalText = "partial output",
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 3
                                }
                            }
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user",
                        Prompt = "Need more detail",
                        PendingStepIds = ["ask_user"],
                        BlockedStepIds = ["finalize"]
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "proposals", "show", "sess-meta-proposals-show-legacy-json", "--storage", memoryPath, "--proposal", "meta-run:run-paused-001:paused", "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var proposal = document.RootElement.GetProperty("proposal");

            Assert.Equal("ask_user", proposal.GetProperty("pendingStepId").GetString());
            Assert.Equal("draft", proposal.GetProperty("timelineStepIds")[0].GetString());
            Assert.Equal("tool_failed", proposal.GetProperty("errorCode").GetString());
            Assert.Equal("Tool call crashed.", proposal.GetProperty("error").GetString());
            Assert.Equal("partial output", proposal.GetProperty("finalText").GetString());

            Assert.Equal("ask_user", proposal.GetProperty("checkpoint").GetProperty("pendingStepId").GetString());
            Assert.Equal("draft", proposal.GetProperty("evidence").GetProperty("timelineStepIds")[0].GetString());
            Assert.Equal("tool_failed", proposal.GetProperty("evidence").GetProperty("errorCode").GetString());
            Assert.Equal("partial output", proposal.GetProperty("evidence").GetProperty("finalText").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Accept_Json_PrintsAppliedReview()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-accept-json",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused"
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user"
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "accept", "sess-meta-proposals-accept-json",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var response = document.RootElement;
            Assert.Equal("sess-meta-proposals-accept-json", response.GetProperty("sessionId").GetString());
            Assert.Equal("meta-run:run-paused-001:paused", response.GetProperty("proposalId").GetString());
            Assert.Equal("accepted", response.GetProperty("reviewStatus").GetString());
            Assert.False(response.GetProperty("alreadyReviewed").GetBoolean());
            Assert.True(response.TryGetProperty("reviewedAtUtc", out _));
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Accept_StoresDurableMetaRunProposalLifecycle()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var memory = new FileMemoryStore(memoryPath))
            {
                await memory.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposal-durable",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused",
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 2
                                }
                            }
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user"
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "accept", "sess-meta-proposal-durable",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var response = JsonDocument.Parse(output.ToString());
            var reviewedAtUtcRaw = response.RootElement.GetProperty("reviewedAtUtc").GetString();
            Assert.False(string.IsNullOrWhiteSpace(reviewedAtUtcRaw));

            var featureStore = new FileFeatureStore(memoryPath);
            var durable = await featureStore.GetProposalAsync(
                "meta-run-proposal:sess-meta-proposal-durable:meta-run:run-paused-001:paused",
                CancellationToken.None);

            Assert.NotNull(durable);
            Assert.Equal(LearningProposalKind.MetaRunProposal, durable!.Kind);
            Assert.Equal(LearningProposalStatus.Approved, durable.Status);
            Assert.Equal("sess-meta-proposal-durable", durable.Metadata["meta_run_proposal_session_id"]);
            Assert.Equal("meta-run:run-paused-001:paused", durable.Metadata["meta_run_proposal_id"]);
            Assert.Equal("run-paused-001", durable.Metadata["meta_run_proposal_run_id"]);
            Assert.Equal("paused", durable.Metadata["meta_run_proposal_status"]);
            Assert.Equal(MetaRunProposalKinds.PausedRunFollowup, durable.Metadata["meta_run_proposal_kind"]);
            Assert.Equal(MetaRunProposalSources.DerivedMetaRunEvidence, durable.Metadata["meta_run_proposal_source"]);
            Assert.Equal("v1", durable.Metadata["meta_run_proposal_provenance_snapshot_version"]);
            Assert.Equal("paused", durable.Metadata["meta_run_proposal_provenance_run_status"]);
            Assert.Equal("1", durable.Metadata["meta_run_proposal_provenance_step_count"]);
            Assert.Equal("draft", durable.Metadata["meta_run_proposal_provenance_step_ids"]);
            Assert.Equal("ask_user", durable.Metadata["meta_run_proposal_provenance_checkpoint_pending_step_id"]);
            Assert.Equal("false", durable.Metadata["meta_run_proposal_provenance_checkpoint_prompt_present"]);
            Assert.Equal("opensquilla-authoring-v1", durable.Metadata["meta_run_proposal_accept_gate_profile"]);
            Assert.Equal("true", durable.Metadata["meta_run_proposal_accept_gate_passed"]);
            Assert.Equal(string.Empty, durable.Metadata["meta_run_proposal_accept_gate_failed_checks"]);
            Assert.Equal(
                DateTimeOffset.Parse(reviewedAtUtcRaw!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)
                    .ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                durable.Metadata["meta_run_proposal_accept_gate_checked_at_utc"]);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Show_Json_AfterAccept_IncludesProvenanceSnapshot()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-show-provenance-json",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused",
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 2
                                }
                            }
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user",
                        Prompt = "Need more detail"
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var acceptExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "accept", "sess-meta-proposals-show-provenance-json",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--json"]);

            Assert.Equal(0, acceptExitCode);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var showExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "show", "sess-meta-proposals-show-provenance-json",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--json"]);

            Assert.Equal(0, showExitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var proposal = document.RootElement.GetProperty("proposal");
            var provenance = proposal.GetProperty("provenance");
            var audit = proposal.GetProperty("audit");
            Assert.Equal("v1", provenance.GetProperty("snapshotVersion").GetString());
            Assert.Equal("paused", provenance.GetProperty("runStatus").GetString());
            Assert.Equal(1, provenance.GetProperty("stepCount").GetInt32());
            Assert.Equal("draft", provenance.GetProperty("stepIds")[0].GetString());
            Assert.Equal("ask_user", provenance.GetProperty("checkpointPendingStepId").GetString());
            Assert.True(provenance.GetProperty("checkpointPromptPresent").GetBoolean());
            Assert.Equal("v1", audit.GetProperty("schemaVersion").GetString());
            Assert.Equal("test-operator", audit.GetProperty("actorId").GetString());
            Assert.Equal("accept", audit.GetProperty("transitionAction").GetString());
            Assert.True(audit.TryGetProperty("changedAtUtc", out var changedAt));
            Assert.Equal(JsonValueKind.String, changedAt.ValueKind);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Show_Json_AfterAccept_IncludesWorkflowSection()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-show-workflow-json",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused"
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user"
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var acceptExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "accept", "sess-meta-proposals-show-workflow-json",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--json"]);

            Assert.Equal(0, acceptExitCode);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var showExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "show", "sess-meta-proposals-show-workflow-json",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--json"]);

            Assert.Equal(0, showExitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var proposal = document.RootElement.GetProperty("proposal");
            var workflow = proposal.GetProperty("workflow");
            Assert.False(string.IsNullOrWhiteSpace(workflow.GetProperty("workflowId").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(workflow.GetProperty("stage").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(workflow.GetProperty("lastAction").GetString()));
            Assert.True(workflow.GetProperty("transitionCount").GetInt32() >= 1);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Dismiss_Json_WithReason_PrintsAppliedReview()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-dismiss-json",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-failed-001",
                            SkillName = "meta-flow",
                            Status = "failed",
                            ErrorCode = "tool_failed"
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "dismiss", "sess-meta-proposals-dismiss-json",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-failed-001:failed",
                "--reason", "operator reviewed",
                "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var response = document.RootElement;
            Assert.Equal("dismissed", response.GetProperty("reviewStatus").GetString());
            Assert.Equal("operator reviewed", response.GetProperty("reason").GetString());
            Assert.False(response.GetProperty("alreadyReviewed").GetBoolean());
            var audit = response.GetProperty("audit");
            Assert.Equal("v1", audit.GetProperty("schemaVersion").GetString());
            Assert.Equal("test-operator", audit.GetProperty("actorId").GetString());
            Assert.Equal("dismiss", audit.GetProperty("transitionAction").GetString());
            Assert.True(audit.TryGetProperty("changedAtUtc", out var changedAt));
            Assert.Equal(JsonValueKind.String, changedAt.ValueKind);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Rollback_Json_AfterAccept_TransitionsToRolledBack()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-rollback-after-accept-json",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused"
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user"
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var acceptExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "accept", "sess-meta-proposals-rollback-after-accept-json",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--json"]);

            Assert.Equal(0, acceptExitCode);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var rollbackExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "rollback", "sess-meta-proposals-rollback-after-accept-json",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--json"]);

            Assert.Equal(0, rollbackExitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var response = document.RootElement;
            Assert.Equal("rolled_back", response.GetProperty("lifecycleStatus").GetString());
            Assert.False(response.GetProperty("alreadyReviewed").GetBoolean());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Change_Json_AfterRollback_AllowsTargetReviewStatus()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-change-after-rollback-json",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-failed-001",
                            SkillName = "meta-flow",
                            Status = "failed",
                            ErrorCode = "tool_failed"
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var dismissExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "dismiss", "sess-meta-proposals-change-after-rollback-json",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-failed-001:failed",
                "--reason", "operator reviewed",
                "--json"]);

            Assert.Equal(0, dismissExitCode);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var rollbackExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "rollback", "sess-meta-proposals-change-after-rollback-json",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-failed-001:failed",
                "--json"]);

            Assert.Equal(0, rollbackExitCode);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var changeExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "change", "sess-meta-proposals-change-after-rollback-json",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-failed-001:failed",
                "--to", "accept",
                "--json"]);

            Assert.Equal(0, changeExitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var response = document.RootElement;
            Assert.Equal("accepted", response.GetProperty("reviewStatus").GetString());
            Assert.Equal("approved", response.GetProperty("lifecycleStatus").GetString());

            var featureStore = new FileFeatureStore(memoryPath);
            var durable = await featureStore.GetProposalAsync(
                "meta-run-proposal:sess-meta-proposals-change-after-rollback-json:meta-run:run-failed-001:failed",
                CancellationToken.None);

            Assert.NotNull(durable);
            Assert.Equal("opensquilla-authoring-v1", durable!.Metadata["meta_run_proposal_accept_gate_profile"]);
            Assert.Equal("true", durable.Metadata["meta_run_proposal_accept_gate_passed"]);
            Assert.Equal(string.Empty, durable.Metadata["meta_run_proposal_accept_gate_failed_checks"]);
            Assert.Equal(
                DateTimeOffset.Parse(response.GetProperty("reviewedAtUtc").GetString()!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)
                    .ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                durable.Metadata["meta_run_proposal_accept_gate_checked_at_utc"]);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Change_Json_AfterRollback_IncludesWorkflowSection()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-change-workflow-json",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-failed-001",
                            SkillName = "meta-flow",
                            Status = "failed",
                            ErrorCode = "tool_failed"
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var dismissExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "dismiss", "sess-meta-proposals-change-workflow-json",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-failed-001:failed",
                "--reason", "operator reviewed",
                "--json"]);

            Assert.Equal(0, dismissExitCode);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var rollbackExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "rollback", "sess-meta-proposals-change-workflow-json",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-failed-001:failed",
                "--json"]);

            Assert.Equal(0, rollbackExitCode);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var changeExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "change", "sess-meta-proposals-change-workflow-json",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-failed-001:failed",
                "--to", "accept",
                "--json"]);

            Assert.Equal(0, changeExitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var workflow = document.RootElement.GetProperty("workflow");
            Assert.False(string.IsNullOrWhiteSpace(workflow.GetProperty("workflowId").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(workflow.GetProperty("stage").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(workflow.GetProperty("lastAction").GetString()));
            Assert.True(workflow.GetProperty("transitionCount").GetInt32() >= 1);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Change_Json_NonRolledBackTargetMatch_RejectsWithNoPartialJson()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-change-non-rolled-back-json",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused"
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user"
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var acceptExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "accept", "sess-meta-proposals-change-non-rolled-back-json",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--json"]);

            Assert.Equal(0, acceptExitCode);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var changeExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "change", "sess-meta-proposals-change-non-rolled-back-json",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--to", "accept",
                "--json"]);

            Assert.Equal(1, changeExitCode);
            Assert.Equal(string.Empty, output.ToString());
            using var document = JsonDocument.Parse(error.ToString());
            Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
            Assert.Equal("skills meta-runs proposals change", document.RootElement.GetProperty("command").GetString());
            Assert.Equal("invalid_lifecycle_transition", document.RootElement.GetProperty("errorCode").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Change_InvalidTarget_Json_ReturnsErrorSchema()
    {
        var previousError = Console.Error;
        using var error = new StringWriter();

        try
        {
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "change", "sess-meta-proposals-change-invalid-target", "--proposal", "meta-run:run-001:paused", "--to", "invalid", "--json"]);

            Assert.Equal(2, exitCode);
            using var document = JsonDocument.Parse(error.ToString());
            Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
            Assert.Equal("skills meta-runs proposals change", document.RootElement.GetProperty("command").GetString());
            Assert.Equal("invalid_change_target", document.RootElement.GetProperty("errorCode").GetString());
        }
        finally
        {
            Console.SetError(previousError);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Rollback_MissingProposal_Json_ReturnsErrorSchema()
    {
        var previousError = Console.Error;
        using var error = new StringWriter();

        try
        {
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "proposals", "rollback", "sess-meta-proposals-rollback", "--json"]);

            Assert.Equal(2, exitCode);
            using var document = JsonDocument.Parse(error.ToString());
            Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
            Assert.Equal("skills meta-runs proposals rollback", document.RootElement.GetProperty("command").GetString());
            Assert.Equal("missing_proposal_id", document.RootElement.GetProperty("errorCode").GetString());
        }
        finally
        {
            Console.SetError(previousError);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Show_Json_AfterLifecycleChanges_IncludesLifecycleAndHistory()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-show-lifecycle-history-json",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused",
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 2
                                }
                            }
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user"
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var acceptExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "accept", "sess-meta-proposals-show-lifecycle-history-json",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--json"]);

            Assert.Equal(0, acceptExitCode);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var rollbackExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "rollback", "sess-meta-proposals-show-lifecycle-history-json",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--reason", "operator rollback",
                "--json"]);

            Assert.Equal(0, rollbackExitCode);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var changeExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "change", "sess-meta-proposals-show-lifecycle-history-json",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--to", "dismiss",
                "--reason", "second review",
                "--json"]);

            Assert.Equal(0, changeExitCode);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var showExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "show", "sess-meta-proposals-show-lifecycle-history-json",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--json"]);

            Assert.Equal(0, showExitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var proposal = document.RootElement.GetProperty("proposal");

            var lifecycle = proposal.GetProperty("lifecycle");
            Assert.Equal("rejected", lifecycle.GetProperty("status").GetString());
            Assert.False(lifecycle.GetProperty("rolledBack").GetBoolean());
            Assert.Equal("second review", lifecycle.GetProperty("reviewNotes").GetString());
            Assert.True(lifecycle.TryGetProperty("reviewedAtUtc", out _));

            var provenanceHistory = proposal.GetProperty("provenanceHistory");
            Assert.Equal(3, provenanceHistory.GetArrayLength());

            Assert.Equal("accept", provenanceHistory[0].GetProperty("action").GetString());
            Assert.Equal("pending", provenanceHistory[0].GetProperty("fromStatus").GetString());
            Assert.Equal("approved", provenanceHistory[0].GetProperty("toStatus").GetString());

            Assert.Equal("rollback", provenanceHistory[1].GetProperty("action").GetString());
            Assert.Equal("approved", provenanceHistory[1].GetProperty("fromStatus").GetString());
            Assert.Equal("rolled_back", provenanceHistory[1].GetProperty("toStatus").GetString());
            Assert.Equal("operator rollback", provenanceHistory[1].GetProperty("reason").GetString());

            Assert.Equal("change", provenanceHistory[2].GetProperty("action").GetString());
            Assert.Equal("rolled_back", provenanceHistory[2].GetProperty("fromStatus").GetString());
            Assert.Equal("rejected", provenanceHistory[2].GetProperty("toStatus").GetString());
            Assert.Equal("second review", provenanceHistory[2].GetProperty("reason").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Phase3_E2E_CreateToLifecycleToAudit_ReachesConsistentState()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOperator = Environment.GetEnvironmentVariable("OPENCLAW_OPERATOR_ID");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_OPERATOR_ID", "operator-phase3");

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-phase3-e2e",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused",
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 2
                                }
                            }
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user"
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var createExitCode = await SkillCommands.RunAsync([
                "create", "Phase3 E2E Meta Skill",
                "--kind", "meta",
                "--proposal-draft",
                "--json"]);

            Assert.Equal(0, createExitCode);
            Assert.Equal(string.Empty, error.ToString());
            using (var createDocument = JsonDocument.Parse(output.ToString()))
            {
                var createRoot = createDocument.RootElement;
                Assert.Equal("phase3-e2e-meta-skill", createRoot.GetProperty("slug").GetString());
                Assert.True(createRoot.GetProperty("proposalDraft").GetProperty("available").GetBoolean());
            }

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var dismissExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "dismiss", "sess-phase3-e2e",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--reason", "first review",
                "--json"]);

            Assert.Equal(0, dismissExitCode);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var rollbackExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "rollback", "sess-phase3-e2e",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--reason", "operator rollback",
                "--json"]);

            Assert.Equal(0, rollbackExitCode);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var previousOperatorAfterDismiss = Environment.GetEnvironmentVariable("OPENCLAW_OPERATOR_ID");
            Environment.SetEnvironmentVariable("OPENCLAW_OPERATOR_ID", null);

            var deniedExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "change", "sess-phase3-e2e",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--to", "accept",
                "--reason", "authorization check",
                "--json"]);

            Assert.Equal(1, deniedExitCode);
            Assert.Equal(string.Empty, output.ToString());
            using (var deniedDocument = JsonDocument.Parse(error.ToString()))
            {
                Assert.Equal("permission_denied", deniedDocument.RootElement.GetProperty("errorCode").GetString());
                Assert.Equal("skills meta-runs proposals change", deniedDocument.RootElement.GetProperty("command").GetString());
            }

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var showAfterDeniedExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "show", "sess-phase3-e2e",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--json"]);

            Assert.Equal(0, showAfterDeniedExitCode);
            Assert.Equal(string.Empty, error.ToString());
            using (var showAfterDenied = JsonDocument.Parse(output.ToString()))
            {
                var proposalAfterDenied = showAfterDenied.RootElement.GetProperty("proposal");
                var lifecycleAfterDenied = proposalAfterDenied.GetProperty("lifecycle");
                Assert.Equal("rolled_back", lifecycleAfterDenied.GetProperty("status").GetString());
                Assert.True(lifecycleAfterDenied.GetProperty("rolledBack").GetBoolean());

                var auditAfterDenied = proposalAfterDenied.GetProperty("audit");
                Assert.Equal("rollback", auditAfterDenied.GetProperty("transitionAction").GetString());
                Assert.Equal("operator-phase3", auditAfterDenied.GetProperty("actorId").GetString());

                var provenanceAfterDenied = proposalAfterDenied.GetProperty("provenanceHistory");
                Assert.Equal(2, provenanceAfterDenied.GetArrayLength());
                Assert.Equal("dismiss", provenanceAfterDenied[0].GetProperty("action").GetString());
                Assert.Equal("rollback", provenanceAfterDenied[1].GetProperty("action").GetString());
            }

            Environment.SetEnvironmentVariable("OPENCLAW_OPERATOR_ID", previousOperatorAfterDismiss);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var changeExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "change", "sess-phase3-e2e",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--to", "accept",
                "--reason", "second review",
                "--json"]);

            Assert.Equal(0, changeExitCode);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var showExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "show", "sess-phase3-e2e",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--json"]);

            Assert.Equal(0, showExitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var showDocument = JsonDocument.Parse(output.ToString());
            var proposal = showDocument.RootElement.GetProperty("proposal");

            var lifecycle = proposal.GetProperty("lifecycle");
            Assert.Equal("approved", lifecycle.GetProperty("status").GetString());
            Assert.False(lifecycle.GetProperty("rolledBack").GetBoolean());
            Assert.Equal("second review", lifecycle.GetProperty("reviewNotes").GetString());

            var audit = proposal.GetProperty("audit");
            Assert.Equal("v1", audit.GetProperty("schemaVersion").GetString());
            Assert.Equal("operator-phase3", audit.GetProperty("actorId").GetString());
            Assert.Equal("change", audit.GetProperty("transitionAction").GetString());

            var provenanceHistory = proposal.GetProperty("provenanceHistory");
            Assert.Equal(3, provenanceHistory.GetArrayLength());
            Assert.Equal("dismiss", provenanceHistory[0].GetProperty("action").GetString());
            Assert.Equal("rollback", provenanceHistory[1].GetProperty("action").GetString());
            Assert.Equal("change", provenanceHistory[2].GetProperty("action").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_OPERATOR_ID", previousOperator);
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Phase3_E2E_DismissThenAcceptConflict_PreservesDismissedState()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOperator = Environment.GetEnvironmentVariable("OPENCLAW_OPERATOR_ID");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_OPERATOR_ID", "operator-phase3-conflict");

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-phase3-e2e-conflict",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-failed-001",
                            SkillName = "meta-flow",
                            Status = "failed",
                            ErrorCode = "tool_failed"
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var createExitCode = await SkillCommands.RunAsync([
                "create", "Phase3 E2E Conflict Meta Skill",
                "--kind", "meta",
                "--proposal-draft",
                "--json"]);

            Assert.Equal(0, createExitCode);
            Assert.Equal(string.Empty, error.ToString());

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var dismissExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "dismiss", "sess-phase3-e2e-conflict",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-failed-001:failed",
                "--reason", "first review",
                "--json"]);

            Assert.Equal(0, dismissExitCode);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var acceptExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "accept", "sess-phase3-e2e-conflict",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-failed-001:failed",
                "--json"]);

            Assert.Equal(1, acceptExitCode);
            Assert.Equal(string.Empty, output.ToString());
            using (var conflictDocument = JsonDocument.Parse(error.ToString()))
            {
                Assert.Equal("error", conflictDocument.RootElement.GetProperty("status").GetString());
                Assert.Equal("skills meta-runs proposals accept", conflictDocument.RootElement.GetProperty("command").GetString());
                Assert.Equal("proposal_already_reviewed", conflictDocument.RootElement.GetProperty("errorCode").GetString());
                Assert.Contains("already reviewed as dismissed", conflictDocument.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
            }

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var showExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "show", "sess-phase3-e2e-conflict",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-failed-001:failed",
                "--json"]);

            Assert.Equal(0, showExitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var showDocument = JsonDocument.Parse(output.ToString());
            var proposal = showDocument.RootElement.GetProperty("proposal");
            var lifecycle = proposal.GetProperty("lifecycle");
            Assert.Equal("rejected", lifecycle.GetProperty("status").GetString());
            Assert.Equal("first review", lifecycle.GetProperty("reviewNotes").GetString());

            var audit = proposal.GetProperty("audit");
            Assert.Equal("dismiss", audit.GetProperty("transitionAction").GetString());
            Assert.Equal("operator-phase3-conflict", audit.GetProperty("actorId").GetString());

            var provenanceHistory = proposal.GetProperty("provenanceHistory");
            Assert.Equal(1, provenanceHistory.GetArrayLength());
            Assert.Equal("dismiss", provenanceHistory[0].GetProperty("action").GetString());
            Assert.Equal("pending", provenanceHistory[0].GetProperty("fromStatus").GetString());
            Assert.Equal("rejected", provenanceHistory[0].GetProperty("toStatus").GetString());
            Assert.Equal("first review", provenanceHistory[0].GetProperty("reason").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_OPERATOR_ID", previousOperator);
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Phase3_E2E_ChangeDenied_DoesNotAdvanceWorkflowTransitionCount()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOperator = Environment.GetEnvironmentVariable("OPENCLAW_OPERATOR_ID");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_OPERATOR_ID", "operator-phase3-change-denied");

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-phase3-e2e-change-denied",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused"
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user"
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var acceptExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "accept", "sess-phase3-e2e-change-denied",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--json"]);

            Assert.Equal(0, acceptExitCode);
            Assert.Equal(string.Empty, error.ToString());

            var featureStore = new FileFeatureStore(memoryPath);
            const string workflowDurableId = "meta-run-workflow:sess-phase3-e2e-change-denied:meta-run:run-paused-001:paused";
            var workflowBeforeDenied = await featureStore.GetProposalAsync(workflowDurableId, CancellationToken.None);

            Assert.NotNull(workflowBeforeDenied);
            var transitionCountBeforeDenied = workflowBeforeDenied!.Metadata["meta_run_workflow_transition_count"];

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();
            Environment.SetEnvironmentVariable("OPENCLAW_OPERATOR_ID", null);

            var deniedExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "change", "sess-phase3-e2e-change-denied",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--to", "accept",
                "--json"]);

            Assert.Equal(1, deniedExitCode);
            Assert.Equal(string.Empty, output.ToString());
            using (var deniedDocument = JsonDocument.Parse(error.ToString()))
            {
                Assert.Equal("error", deniedDocument.RootElement.GetProperty("status").GetString());
                Assert.Equal("skills meta-runs proposals change", deniedDocument.RootElement.GetProperty("command").GetString());
                Assert.Equal("permission_denied", deniedDocument.RootElement.GetProperty("errorCode").GetString());
            }

            var workflowAfterDenied = await featureStore.GetProposalAsync(workflowDurableId, CancellationToken.None);
            Assert.NotNull(workflowAfterDenied);
            Assert.Equal(transitionCountBeforeDenied, workflowAfterDenied!.Metadata["meta_run_workflow_transition_count"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_OPERATOR_ID", previousOperator);
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Phase3_E2E_ConflictAcceptAfterDismiss_DoesNotAdvanceWorkflowTransitionCount()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOperator = Environment.GetEnvironmentVariable("OPENCLAW_OPERATOR_ID");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_OPERATOR_ID", "operator-phase3-conflict-nondrift");

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-phase3-e2e-conflict-nondrift",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-failed-001",
                            SkillName = "meta-flow",
                            Status = "failed",
                            ErrorCode = "tool_failed"
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var dismissExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "dismiss", "sess-phase3-e2e-conflict-nondrift",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-failed-001:failed",
                "--reason", "first review",
                "--json"]);

            Assert.Equal(0, dismissExitCode);
            Assert.Equal(string.Empty, error.ToString());

            var featureStore = new FileFeatureStore(memoryPath);
            const string workflowDurableId = "meta-run-workflow:sess-phase3-e2e-conflict-nondrift:meta-run:run-failed-001:failed";
            var workflowBeforeConflict = await featureStore.GetProposalAsync(workflowDurableId, CancellationToken.None);

            Assert.NotNull(workflowBeforeConflict);
            var transitionCountBeforeConflict = workflowBeforeConflict!.Metadata["meta_run_workflow_transition_count"];

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var acceptExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "accept", "sess-phase3-e2e-conflict-nondrift",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-failed-001:failed",
                "--json"]);

            Assert.Equal(1, acceptExitCode);
            Assert.Equal(string.Empty, output.ToString());
            using (var conflictDocument = JsonDocument.Parse(error.ToString()))
            {
                Assert.Equal("error", conflictDocument.RootElement.GetProperty("status").GetString());
                Assert.Equal("skills meta-runs proposals accept", conflictDocument.RootElement.GetProperty("command").GetString());
                Assert.Equal("proposal_already_reviewed", conflictDocument.RootElement.GetProperty("errorCode").GetString());
            }

            var workflowAfterConflict = await featureStore.GetProposalAsync(workflowDurableId, CancellationToken.None);
            Assert.NotNull(workflowAfterConflict);
            Assert.Equal(transitionCountBeforeConflict, workflowAfterConflict!.Metadata["meta_run_workflow_transition_count"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_OPERATOR_ID", previousOperator);
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Phase3_E2E_AcceptThenDismissConflict_PreservesApprovedState()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOperator = Environment.GetEnvironmentVariable("OPENCLAW_OPERATOR_ID");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_OPERATOR_ID", "operator-phase3-conflict-reverse");

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-phase3-e2e-conflict-reverse",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused"
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user"
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var createExitCode = await SkillCommands.RunAsync([
                "create", "Phase3 E2E Reverse Conflict Meta Skill",
                "--kind", "meta",
                "--proposal-draft",
                "--json"]);

            Assert.Equal(0, createExitCode);
            Assert.Equal(string.Empty, error.ToString());

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var acceptExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "accept", "sess-phase3-e2e-conflict-reverse",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--json"]);

            Assert.Equal(0, acceptExitCode);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var dismissExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "dismiss", "sess-phase3-e2e-conflict-reverse",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--json"]);

            Assert.Equal(1, dismissExitCode);
            Assert.Equal(string.Empty, output.ToString());
            using (var conflictDocument = JsonDocument.Parse(error.ToString()))
            {
                Assert.Equal("error", conflictDocument.RootElement.GetProperty("status").GetString());
                Assert.Equal("skills meta-runs proposals dismiss", conflictDocument.RootElement.GetProperty("command").GetString());
                Assert.Equal("proposal_already_reviewed", conflictDocument.RootElement.GetProperty("errorCode").GetString());
                Assert.Contains("already reviewed as accepted", conflictDocument.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
            }

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var showExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "show", "sess-phase3-e2e-conflict-reverse",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--json"]);

            Assert.Equal(0, showExitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var showDocument = JsonDocument.Parse(output.ToString());
            var proposal = showDocument.RootElement.GetProperty("proposal");
            var lifecycle = proposal.GetProperty("lifecycle");
            Assert.Equal("approved", lifecycle.GetProperty("status").GetString());
            if (lifecycle.TryGetProperty("reviewNotes", out var reviewNotes))
                Assert.True(string.IsNullOrEmpty(reviewNotes.GetString()));

            var audit = proposal.GetProperty("audit");
            Assert.Equal("accept", audit.GetProperty("transitionAction").GetString());
            Assert.Equal("operator-phase3-conflict-reverse", audit.GetProperty("actorId").GetString());

            var provenanceHistory = proposal.GetProperty("provenanceHistory");
            Assert.Equal(1, provenanceHistory.GetArrayLength());
            Assert.Equal("accept", provenanceHistory[0].GetProperty("action").GetString());
            Assert.Equal("pending", provenanceHistory[0].GetProperty("fromStatus").GetString());
            Assert.Equal("approved", provenanceHistory[0].GetProperty("toStatus").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_OPERATOR_ID", previousOperator);
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Phase3_E2E_AcceptRollbackChange_PersistsWorkflowObjectHistory()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOperator = Environment.GetEnvironmentVariable("OPENCLAW_OPERATOR_ID");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_OPERATOR_ID", "operator-phase3-workflow-history");

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-phase3-e2e-workflow-history",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused"
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user"
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var acceptExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "accept", "sess-phase3-e2e-workflow-history",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--json"]);

            Assert.Equal(0, acceptExitCode);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var rollbackExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "rollback", "sess-phase3-e2e-workflow-history",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--json"]);

            Assert.Equal(0, rollbackExitCode);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var changeExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "change", "sess-phase3-e2e-workflow-history",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--to", "accept",
                "--json"]);

            Assert.Equal(0, changeExitCode);
            Assert.Equal(string.Empty, error.ToString());

            var featureStore = new FileFeatureStore(memoryPath);
            var workflow = await featureStore.GetProposalAsync(
                "meta-run-workflow:sess-phase3-e2e-workflow-history:meta-run:run-paused-001:paused",
                CancellationToken.None);

            Assert.NotNull(workflow);
            Assert.Equal(LearningProposalKind.MetaRunReviewWorkflow, workflow!.Kind);
            Assert.Equal("3", workflow.Metadata["meta_run_workflow_transition_count"]);
            Assert.Equal("change", workflow.Metadata["meta_run_workflow_last_action"]);
            Assert.Equal("decision_recorded", workflow.Metadata["meta_run_workflow_stage"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_OPERATOR_ID", previousOperator);
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_Phase3_E2E_InvalidTransitionAfterAccept_PreservesApprovedState()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOperator = Environment.GetEnvironmentVariable("OPENCLAW_OPERATOR_ID");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_OPERATOR_ID", "operator-phase3-invalid-transition");

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-phase3-e2e-invalid-transition",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused"
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user"
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var createExitCode = await SkillCommands.RunAsync([
                "create", "Phase3 E2E Invalid Transition Meta Skill",
                "--kind", "meta",
                "--proposal-draft",
                "--json"]);

            Assert.Equal(0, createExitCode);
            Assert.Equal(string.Empty, error.ToString());

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var acceptExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "accept", "sess-phase3-e2e-invalid-transition",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--json"]);

            Assert.Equal(0, acceptExitCode);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var invalidTransitionExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "change", "sess-phase3-e2e-invalid-transition",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--to", "accept",
                "--json"]);

            Assert.Equal(1, invalidTransitionExitCode);
            Assert.Equal(string.Empty, output.ToString());
            using (var invalidTransitionDocument = JsonDocument.Parse(error.ToString()))
            {
                Assert.Equal("error", invalidTransitionDocument.RootElement.GetProperty("status").GetString());
                Assert.Equal("skills meta-runs proposals change", invalidTransitionDocument.RootElement.GetProperty("command").GetString());
                Assert.Equal("invalid_lifecycle_transition", invalidTransitionDocument.RootElement.GetProperty("errorCode").GetString());
            }

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var showExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "show", "sess-phase3-e2e-invalid-transition",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--json"]);

            Assert.Equal(0, showExitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var showDocument = JsonDocument.Parse(output.ToString());
            var proposal = showDocument.RootElement.GetProperty("proposal");

            var lifecycle = proposal.GetProperty("lifecycle");
            Assert.Equal("approved", lifecycle.GetProperty("status").GetString());

            var audit = proposal.GetProperty("audit");
            Assert.Equal("accept", audit.GetProperty("transitionAction").GetString());
            Assert.Equal("operator-phase3-invalid-transition", audit.GetProperty("actorId").GetString());

            var provenanceHistory = proposal.GetProperty("provenanceHistory");
            Assert.Equal(1, provenanceHistory.GetArrayLength());
            Assert.Equal("accept", provenanceHistory[0].GetProperty("action").GetString());
            Assert.Equal("pending", provenanceHistory[0].GetProperty("fromStatus").GetString());
            Assert.Equal("approved", provenanceHistory[0].GetProperty("toStatus").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_OPERATOR_ID", previousOperator);
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Show_Text_AfterLifecycleChanges_PrintsLifecycleAndHistory()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-show-lifecycle-history-text",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused",
                            StepResults =
                            {
                                new SessionMetaStepResult
                                {
                                    Id = "draft",
                                    Kind = "llm_chat",
                                    Status = "completed",
                                    DurationMs = 2
                                }
                            }
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user"
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var acceptExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "accept", "sess-meta-proposals-show-lifecycle-history-text",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--json"]);

            Assert.Equal(0, acceptExitCode);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var rollbackExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "rollback", "sess-meta-proposals-show-lifecycle-history-text",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--reason", "operator rollback",
                "--json"]);

            Assert.Equal(0, rollbackExitCode);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var changeExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "change", "sess-meta-proposals-show-lifecycle-history-text",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--to", "dismiss",
                "--reason", "second review",
                "--json"]);

            Assert.Equal(0, changeExitCode);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var showExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "show", "sess-meta-proposals-show-lifecycle-history-text",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused"]);

            Assert.Equal(0, showExitCode);
            Assert.Equal(string.Empty, error.ToString());
            Assert.Contains("Lifecycle:", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Status: rejected", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Rolled back: no", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Review notes: second review", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Provenance history:", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("- accept | pending -> approved", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("- rollback | approved -> rolled_back", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("reason=operator rollback", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("- change | rolled_back -> rejected", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("reason=second review", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Accept_Json_SecondCall_IsIdempotentSuccess()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-accept-idempotent",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused"
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user"
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var firstExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "accept", "sess-meta-proposals-accept-idempotent",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--json"]);

            Assert.Equal(0, firstExitCode);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var secondExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "accept", "sess-meta-proposals-accept-idempotent",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused",
                "--json"]);

            Assert.Equal(0, secondExitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            Assert.True(document.RootElement.GetProperty("alreadyReviewed").GetBoolean());
            Assert.Equal("accepted", document.RootElement.GetProperty("reviewStatus").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Accept_Json_Conflict_WritesNoPartialJson()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-conflict-json",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-failed-001",
                            SkillName = "meta-flow",
                            Status = "failed",
                            ErrorCode = "tool_failed"
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var dismissExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "dismiss", "sess-meta-proposals-conflict-json",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-failed-001:failed",
                "--reason", "operator reviewed",
                "--json"]);

            Assert.Equal(0, dismissExitCode);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var acceptExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "accept", "sess-meta-proposals-conflict-json",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-failed-001:failed",
                "--json"]);

            Assert.Equal(1, acceptExitCode);
            Assert.Equal(string.Empty, output.ToString());
            Assert.Contains("already reviewed as dismissed", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Json_IncludesReviewStatus()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-list-review-json",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused"
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user"
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var acceptExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "accept", "sess-meta-proposals-list-review-json",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-paused-001:paused"]);

            Assert.Equal(0, acceptExitCode);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var listExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "sess-meta-proposals-list-review-json",
                "--storage", memoryPath,
                "--json"]);

            Assert.Equal(0, listExitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var proposal = document.RootElement.GetProperty("proposals")[0];
            Assert.Equal("accepted", proposal.GetProperty("reviewStatus").GetString());
            Assert.True(proposal.TryGetProperty("reviewedAtUtc", out _));
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Show_Json_IncludesReviewSection()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-show-review-json",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-failed-001",
                            SkillName = "meta-flow",
                            Status = "failed",
                            ErrorCode = "tool_failed"
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var dismissExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "dismiss", "sess-meta-proposals-show-review-json",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-failed-001:failed",
                "--reason", "operator reviewed"]);

            Assert.Equal(0, dismissExitCode);

            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();

            var showExitCode = await SkillCommands.RunAsync([
                "meta-runs", "proposals", "show", "sess-meta-proposals-show-review-json",
                "--storage", memoryPath,
                "--proposal", "meta-run:run-failed-001:failed",
                "--json"]);

            Assert.Equal(0, showExitCode);
            Assert.Equal(string.Empty, error.ToString());

            using var document = JsonDocument.Parse(output.ToString());
            var review = document.RootElement.GetProperty("proposal").GetProperty("review");
            Assert.Equal("dismissed", review.GetProperty("status").GetString());
            Assert.Equal("operator reviewed", review.GetProperty("reason").GetString());
            Assert.True(review.TryGetProperty("reviewedAtUtc", out _));
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Show_MissingProposal_ReturnsUsage()
    {
        var previousError = Console.Error;
        using var error = new StringWriter();

        try
        {
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "proposals", "show", "sess-meta-proposals-show"]);

            Assert.Equal(2, exitCode);
            Assert.Contains("--proposal <id> is required for meta-runs proposals show.", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previousError);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Show_MissingProposal_Json_ReturnsErrorSchema()
    {
        var previousError = Console.Error;
        using var error = new StringWriter();

        try
        {
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "proposals", "show", "sess-meta-proposals-show", "--json"]);

            Assert.Equal(2, exitCode);
            using var document = JsonDocument.Parse(error.ToString());
            Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
            Assert.Equal("skills meta-runs proposals show", document.RootElement.GetProperty("command").GetString());
            Assert.Equal("missing_proposal_id", document.RootElement.GetProperty("errorCode").GetString());
        }
        finally
        {
            Console.SetError(previousError);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Show_Json_MissingProposal_WritesNoPartialJson()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-show-missing-json",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused"
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user"
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "proposals", "show", "sess-meta-proposals-show-missing-json", "--storage", memoryPath, "--proposal", "meta-run:run-missing-001:paused", "--json"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, output.ToString());
            using var document = JsonDocument.Parse(error.ToString());
            Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
            Assert.Equal("skills meta-runs proposals show", document.RootElement.GetProperty("command").GetString());
            Assert.Equal("proposal_not_found", document.RootElement.GetProperty("errorCode").GetString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Show_InvalidProposalKindSuffix_ReturnsNotFound()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-show-invalid-kind",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-paused-001",
                            SkillName = "meta-flow",
                            Status = "paused"
                        }
                    },
                    MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                    {
                        SkillName = "meta-flow",
                        PendingStepId = "ask_user"
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "proposals", "show", "sess-meta-proposals-show-invalid-kind", "--storage", memoryPath, "--proposal", "meta-run:run-paused-001:failed"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, output.ToString());
            Assert.Contains("Proposal 'meta-run:run-paused-001:failed' not found in session 'sess-meta-proposals-show-invalid-kind'.", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MetaRuns_Proposals_Text_WithCompletedRunsOnly_PrintsZero()
    {
        var root = CreateTempRoot();
        var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
        var previousOut = Console.Out;
        var previousError = Console.Error;

        try
        {
            var workspace = Path.Combine(root, "workspace");
            var memoryPath = Path.Combine(root, "memory");
            Directory.CreateDirectory(workspace);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

            await using (var store = new FileMemoryStore(memoryPath))
            {
                await store.SaveSessionAsync(new Session
                {
                    Id = "sess-meta-proposals-empty",
                    ChannelId = "cli",
                    SenderId = "tester",
                    MetaRunHistory =
                    {
                        new SessionMetaRunRecord
                        {
                            RunId = "run-completed-001",
                            SkillName = "meta-flow",
                            Status = "completed",
                            FinalText = "done"
                        }
                    }
                }, CancellationToken.None);
            }

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await SkillCommands.RunAsync(["meta-runs", "proposals", "sess-meta-proposals-empty", "--storage", memoryPath]);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            Assert.Contains("Session: sess-meta-proposals-empty", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Derived proposals: 0", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Proposal:", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-skill-command-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateSkill(string root, string name, string description, string? kind = null)
    {
        var slug = name.ToLowerInvariant().Replace(' ', '-');
        var skillDir = Path.Combine(root, slug);
        Directory.CreateDirectory(skillDir);
        var kindLine = string.IsNullOrWhiteSpace(kind) ? string.Empty : $"kind: {kind}{Environment.NewLine}";
        var compositionLine = string.Equals(kind, "meta", StringComparison.OrdinalIgnoreCase)
            ? $"composition: {{\"steps\":[{{\"id\":\"s1\",\"kind\":\"llm_chat\",\"with\":{{\"prompt\":\"hello\"}}}}]}}{Environment.NewLine}"
            : string.Empty;
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            $"---{Environment.NewLine}" +
            $"name: {name}{Environment.NewLine}" +
            kindLine +
            $"description: {description}{Environment.NewLine}" +
            compositionLine +
            $"metadata: {{\"openclaw\":{{\"homepage\":\"https://example.com/{slug}\",\"requires\":{{\"env\":[\"OPENAI_API_KEY\"]}}}}}}{Environment.NewLine}" +
            $"---{Environment.NewLine}{Environment.NewLine}" +
            "Follow the documented process." +
            Environment.NewLine);
        return skillDir;
    }

    private static async Task CreateTarballAsync(string workingDirectory, string sourceDirectoryName, string tarballPath)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "tar",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--create");
        process.StartInfo.ArgumentList.Add("--gzip");
        process.StartInfo.ArgumentList.Add("--file");
        process.StartInfo.ArgumentList.Add(tarballPath);
        process.StartInfo.ArgumentList.Add(sourceDirectoryName);

        process.Start();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"tar create failed: {stderr}");
    }
}

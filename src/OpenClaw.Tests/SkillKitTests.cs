using System.IO.Compression;
using OpenClaw.Cli;
using OpenClaw.SkillKit;
using OpenClaw.SkillKit.Abstractions;
using Xunit;

namespace OpenClaw.Tests;

public sealed class SkillKitTests
{
    [Theory]
    [InlineData("Community Research Insight Extractor", "community.research_insight")]
    [InlineData("Donor Proposal Builder", "donor.proposal_builder")]
    [InlineData("ASP.NET Feature Builder", "aspnet.feature_builder")]
    [InlineData("  Donor@@ Proposal!! Builder  ", "donor.proposal_builder")]
    public void SkillIdGenerator_NormalizesNames(string name, string expected)
    {
        Assert.Equal(expected, SkillIdGenerator.Generate(name));
    }

    [Fact]
    public async Task PackageCreation_CreatesExpectedFilesAndProtectsExistingPackage()
    {
        var root = CreateTempRoot();
        try
        {
            var service = SkillPackageService.CreateDefault();
            var skillsRoot = Path.Join(root, "skills");

            var package = await service.CreateNewAsync("Community Research Insight Extractor", "research", "research", skillsRoot, force: false);

            foreach (var file in SkillTemplateRenderer.RequiredFiles)
                Assert.True(File.Exists(Path.Join(package.RootPath, file)), file);

            await Assert.ThrowsAsync<IOException>(() =>
                service.CreateNewAsync("Community Research Insight Extractor", "research", "research", skillsRoot, force: false));

            var forced = await service.CreateNewAsync("Community Research Insight Extractor", "research", "research", skillsRoot, force: true);
            Assert.True(File.Exists(Path.Join(forced.RootPath, "skill.yaml")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolvePackageFilePath_EnforcesPackageRootContainment()
    {
        var root = CreateTempRoot();
        try
        {
            var resolved = SkillPackageReader.ResolvePackageFilePath(root, "intent.md");

            Assert.Equal(Path.GetFullPath(Path.Join(root, "intent.md")), resolved);
            Assert.Throws<InvalidDataException>(() => SkillPackageReader.ResolvePackageFilePath(root, Path.GetFullPath(Path.Join(root, "escape.md"))));
            Assert.Throws<InvalidDataException>(() => SkillPackageReader.ResolvePackageFilePath(root, "../escape.md"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Generate_RestoresMissingFilesWithoutOverwritingUnlessForced()
    {
        var root = CreateTempRoot();
        try
        {
            var service = SkillPackageService.CreateDefault();
            var skillsRoot = Path.Join(root, "skills");
            var package = await service.CreateNewAsync("Donor Proposal Builder", "proposal", "proposal", skillsRoot, force: false);
            var examplesPath = Path.Join(package.RootPath, "examples.md");
            var guardrailsPath = Path.Join(package.RootPath, "guardrails.md");
            File.Delete(examplesPath);
            await File.WriteAllTextAsync(guardrailsPath, "custom guardrails");

            await service.GenerateAsync("donor.proposal_builder", skillsRoot, force: false);

            Assert.True(File.Exists(examplesPath));
            Assert.Equal("custom guardrails", await File.ReadAllTextAsync(guardrailsPath));

            await service.GenerateAsync("donor.proposal_builder", skillsRoot, force: true);
            Assert.Contains("# Guardrails", await File.ReadAllTextAsync(guardrailsPath), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ManifestSerialization_RoundTripsKeyFields()
    {
        var manifest = new SkillTemplateRenderer().CreateManifest("Donor Proposal Builder", "proposal", "proposal");
        var path = Path.Join(CreateTempRoot(), "skill.yaml");
        try
        {
            await SkillManifestSerializer.WriteAsync(path, manifest);
            var read = await SkillManifestSerializer.ReadAsync(path);

            Assert.Equal("donor.proposal_builder", read.Id);
            Assert.Equal("Donor Proposal Builder", read.Name);
            Assert.Equal("proposal", read.Category);
            Assert.Contains("project_brief", read.Inputs.Required);
            Assert.Contains("external_submit", read.Tools.Forbidden);
            Assert.NotEmpty(read.Workflow.Steps);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public async Task ManifestSerialization_PreservesAliasesAndBackslashes()
    {
        var manifest = new SkillManifest
        {
            Id = "example.backslash_skill",
            Name = "Backslash Skill",
            Version = "0.1.0",
            Category = "test",
            Aliases = ["example-backslash-skill"],
            Intent = new SkillIntent { Outcome = "Check path handling." },
            Inputs = new SkillInputs { Required = [@"docs\notes"] },
            Outputs = new SkillOutputs { Required = ["summary"] },
            Tools = new SkillToolPolicy { Allowed = ["file.read"], Forbidden = ["email.send"] },
            Guardrails = new SkillGuardrails { MustNot = ["Invent facts."] },
            Validation = new SkillValidationPolicy { Checks = ["Ground claims."] },
            Workflow = new SkillWorkflow
            {
                Steps = [new SkillWorkflowStep { Id = "validate", Name = "Validate", Type = SkillWorkflowStepType.Validation, Description = "Validate output." }]
            }
        };
        var path = Path.Join(CreateTempRoot(), "skill.yaml");
        try
        {
            await SkillManifestSerializer.WriteAsync(path, manifest);
            var read = await SkillManifestSerializer.ReadAsync(path);

            Assert.Contains("example-backslash-skill", read.Aliases);
            Assert.Contains(@"docs\notes", read.Inputs.Required);
            Assert.Equal(SkillWorkflowStepType.Validation, read.Workflow.Steps[0].Type);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public async Task Validation_PassesValidSkillAndFailsMissingManifest()
    {
        var root = CreateTempRoot();
        try
        {
            var service = SkillPackageService.CreateDefault();
            var skillsRoot = Path.Join(root, "skills");
            var package = await service.CreateNewAsync("Community Research Insight Extractor", "research", "research", skillsRoot, force: false);

            var valid = await service.ValidateAsync(package.Manifest.Id, skillsRoot);
            Assert.True(valid.Passed);

            var missing = await service.ValidateAsync(Path.Join(root, "missing"), skillsRoot);
            Assert.False(missing.Passed);
            Assert.Contains(missing.Issues, static issue => issue.FileName == "skill.yaml" && issue.Severity == SkillValidationSeverity.Error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Validation_FailsToolOverlapAndWorkflowWithoutSteps()
    {
        var root = CreateTempRoot();
        try
        {
            var skillsRoot = Path.Join(root, "skills");
            var skillRoot = Path.Join(skillsRoot, "test.bad_skill");
            Directory.CreateDirectory(skillRoot);
            var manifest = new SkillManifest
            {
                Id = "test.bad_skill",
                Name = "Bad Skill",
                Version = "0.1.0",
                Category = "test",
                Intent = new SkillIntent { Outcome = "Produce a grounded output for testing." },
                Inputs = new SkillInputs { Required = ["source"] },
                Outputs = new SkillOutputs { Required = ["summary"] },
                Tools = new SkillToolPolicy { Allowed = ["file.read"], Forbidden = ["file.read"], ApprovalRequired = [] },
                Guardrails = new SkillGuardrails { MustNot = ["Invent facts."] },
                Validation = new SkillValidationPolicy { Checks = [] },
                Workflow = new SkillWorkflow { Steps = [] }
            };
            await SkillManifestSerializer.WriteAsync(Path.Join(skillRoot, "skill.yaml"), manifest);
            foreach (var file in SkillTemplateRenderer.RequiredFiles.Where(static file => file != "skill.yaml"))
                await File.WriteAllTextAsync(Path.Join(skillRoot, file), "# Test");

            var result = await new SkillValidator().ValidateAsync("test.bad_skill", skillsRoot);

            Assert.False(result.Passed);
            Assert.Contains(result.Issues, static issue => issue.Message.Contains("overlap", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.Issues, static issue => issue.Message.Contains("Workflow must contain", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.Issues, static issue => issue.Message.Contains("Validation checks are empty", StringComparison.OrdinalIgnoreCase) && issue.Severity == SkillValidationSeverity.Warning);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PackageZip_CreatesPortableArchiveWithExpectedFiles()
    {
        var root = CreateTempRoot();
        try
        {
            var service = SkillPackageService.CreateDefault();
            var skillsRoot = Path.Join(root, "skills");
            var packagesRoot = Path.Join(root, "packages");
            await service.CreateNewAsync("Donor Proposal Builder", "proposal", "proposal", skillsRoot, force: false);

            var zipPath = await service.PackageAsync("donor.proposal_builder", skillsRoot, packagesRoot, force: false);

            Assert.True(File.Exists(zipPath));
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var file in SkillTemplateRenderer.RequiredFiles)
                Assert.Contains(archive.Entries, entry => string.Equals(entry.FullName, file, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CliRun_DryRunPrintsPlanAndDoesNotExecuteTools()
    {
        var root = CreateTempRoot();
        try
        {
            var skillsRoot = Path.Join(root, "skills");
            var inputPath = Path.Join(root, "transcript.md");
            await File.WriteAllTextAsync(inputPath, "Meeting notes");
            await SkillPackageService.CreateDefault().CreateNewAsync("Community Research Insight Extractor", "research", "research", skillsRoot, force: false);

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exitCode = await SkillKitCommands.RunAsync(
                ["run", "community.research_insight", "--input", "transcript.md", "--dry-run", "--output", skillsRoot],
                stdout,
                stderr,
                root);

            Assert.Equal(0, exitCode);
            var output = stdout.ToString();
            Assert.Contains("Execution Plan:", output, StringComparison.Ordinal);
            Assert.Contains("collect_inputs", output, StringComparison.Ordinal);
            Assert.Contains("Dry run complete. No model calls or tool calls were executed.", output, StringComparison.Ordinal);
            Assert.DoesNotContain("tool call executed", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CliRun_DryRunRequiresInput()
    {
        var root = CreateTempRoot();
        try
        {
            var skillsRoot = Path.Join(root, "skills");
            await SkillPackageService.CreateDefault().CreateNewAsync("Community Research Insight Extractor", "research", "research", skillsRoot, force: false);

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exitCode = await SkillKitCommands.RunAsync(
                ["run", "community.research_insight", "--dry-run", "--output", skillsRoot],
                stdout,
                stderr,
                root);

            Assert.Equal(2, exitCode);
            Assert.Contains("At least one --input", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TraceUpdater_SanitizesMultilineMessages()
    {
        var root = CreateTempRoot();
        try
        {
            var skillsRoot = Path.Join(root, "skills");
            var package = await SkillPackageService.CreateDefault().CreateNewAsync("Donor Proposal Builder", "proposal", "proposal", skillsRoot, force: false);

            await new SkillTraceUpdater().AppendAsync(package, "packaged\n- forged entry");

            var trace = await File.ReadAllTextAsync(Path.Join(package.RootPath, "trace.md"));
            Assert.Contains("packaged - forged entry", trace, StringComparison.Ordinal);
            Assert.DoesNotContain("- forged entry", trace.Replace("packaged - forged entry", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CliList_ShowsLocalSkills()
    {
        var root = CreateTempRoot();
        try
        {
            var skillsRoot = Path.Join(root, "skills");
            await SkillPackageService.CreateDefault().CreateNewAsync("Donor Proposal Builder", "proposal", "proposal", skillsRoot, force: false);

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exitCode = await SkillKitCommands.RunAsync(["list", "--output", skillsRoot], stdout, stderr, root);

            Assert.Equal(0, exitCode);
            var output = stdout.ToString();
            Assert.Contains("donor.proposal_builder", output, StringComparison.Ordinal);
            Assert.Contains("Donor Proposal Builder", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Critique_WritesDeterministicCritique()
    {
        var root = CreateTempRoot();
        try
        {
            var skillsRoot = Path.Join(root, "skills");
            await SkillPackageService.CreateDefault().CreateNewAsync("Community Research Insight Extractor", "research", "research", skillsRoot, force: false);

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exitCode = await SkillKitCommands.RunAsync(["critique", "community.research_insight", "--output", skillsRoot], stdout, stderr, root);

            Assert.Equal(0, exitCode);
            var critiquePath = Path.Join(skillsRoot, "community.research_insight", "critique.md");
            Assert.True(File.Exists(critiquePath));
            Assert.Contains("deterministic", await File.ReadAllTextAsync(critiquePath), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Join(Path.GetTempPath(), "openclaw-skillkit-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }
}

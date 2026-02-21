using OpenClaw.Core.Skills;
using Xunit;

namespace OpenClaw.Tests;

public class SkillLoaderTests
{
    [Fact]
    public void ParseSkillContent_ValidFrontmatter_ReturnsSkill()
    {
        var content = """
            ---
            name: test-skill
            description: A test skill for unit testing
            ---
            Use the test tool to run tests.
            Always validate output before returning.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/test-skill", SkillSource.Workspace);

        Assert.NotNull(skill);
        Assert.Equal("test-skill", skill!.Name);
        Assert.Equal("A test skill for unit testing", skill.Description);
        Assert.Contains("test tool", skill.Instructions);
        Assert.Equal("/skills/test-skill", skill.Location);
        Assert.Equal(SkillSource.Workspace, skill.Source);
    }

    [Fact]
    public void ParseSkillContent_MissingFrontmatter_ReturnsNull()
    {
        var content = "Just some markdown without frontmatter.";

        var skill = SkillLoader.ParseSkillContent(content, "/skills/bad", SkillSource.Workspace);

        Assert.Null(skill);
    }

    [Fact]
    public void ParseSkillContent_MissingName_ReturnsNull()
    {
        var content = """
            ---
            description: No name here
            ---
            Instructions body.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/noname", SkillSource.Workspace);

        Assert.Null(skill);
    }

    [Fact]
    public void ParseSkillContent_WithMetadata_ParsesRequirements()
    {
        var content = """
            ---
            name: gemini-skill
            description: Use Gemini for coding
            metadata: {"openclaw": {"requires": {"bins": ["gemini"], "env": ["GEMINI_API_KEY"]}, "primaryEnv": "GEMINI_API_KEY", "emoji": "♊️"}}
            ---
            Use the gemini CLI tool.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/gemini", SkillSource.Managed);

        Assert.NotNull(skill);
        Assert.Equal("gemini-skill", skill!.Name);
        Assert.Single(skill.Metadata.RequireBins);
        Assert.Equal("gemini", skill.Metadata.RequireBins[0]);
        Assert.Single(skill.Metadata.RequireEnv);
        Assert.Equal("GEMINI_API_KEY", skill.Metadata.RequireEnv[0]);
        Assert.Equal("GEMINI_API_KEY", skill.Metadata.PrimaryEnv);
        Assert.Equal("♊️", skill.Metadata.Emoji);
    }

    [Fact]
    public void ParseSkillContent_UserInvocableFalse_SetsProperly()
    {
        var content = """
            ---
            name: internal-skill
            description: Not user-invocable
            user-invocable: false
            ---
            Internal instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/internal", SkillSource.Bundled);

        Assert.NotNull(skill);
        Assert.False(skill!.UserInvocable);
    }

    [Fact]
    public void ParseSkillContent_DisableModelInvocation_SetsProperly()
    {
        var content = """
            ---
            name: slash-only
            description: Slash command only
            disable-model-invocation: true
            ---
            Only via slash command.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/slash", SkillSource.Bundled);

        Assert.NotNull(skill);
        Assert.True(skill!.DisableModelInvocation);
    }

    [Fact]
    public void ParseSkillContent_CommandDispatch_SetsProperly()
    {
        var content = """
            ---
            name: summarize
            description: Summarize content
            command-dispatch: tool
            command-tool: summarize_tool
            command-arg-mode: raw
            ---
            Summarization instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/summarize", SkillSource.Workspace);

        Assert.NotNull(skill);
        Assert.Equal("tool", skill!.CommandDispatch);
        Assert.Equal("summarize_tool", skill.CommandTool);
        Assert.Equal("raw", skill.CommandArgMode);
    }

    [Fact]
    public void ParseSkillContent_ReplacesBaseDir()
    {
        var content = """
            ---
            name: my-skill
            description: Uses baseDir
            ---
            Run the script at {baseDir}/run.sh
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/home/user/skills/my-skill", SkillSource.Workspace);

        Assert.NotNull(skill);
        Assert.Contains("/home/user/skills/my-skill/run.sh", skill!.Instructions);
        Assert.DoesNotContain("{baseDir}", skill.Instructions);
    }

    [Fact]
    public void ParseSkillContent_WithOsGate_ParsesOsList()
    {
        var content = """
            ---
            name: mac-only
            description: macOS only skill
            metadata: {"openclaw": {"os": ["darwin"]}}
            ---
            macOS instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/mac", SkillSource.Bundled);

        Assert.NotNull(skill);
        Assert.Single(skill!.Metadata.Os);
        Assert.Equal("darwin", skill.Metadata.Os[0]);
    }

    [Fact]
    public void ParseSkillContent_AlwaysTrue_SetsFlag()
    {
        var content = """
            ---
            name: core-skill
            description: Always loaded
            metadata: {"openclaw": {"always": true}}
            ---
            Core instructions.
            """;

        var skill = SkillLoader.ParseSkillContent(content, "/skills/core", SkillSource.Bundled);

        Assert.NotNull(skill);
        Assert.True(skill!.Metadata.Always);
    }

    [Fact]
    public void ParseMetadata_Null_ReturnsDefaults()
    {
        var meta = SkillLoader.ParseMetadata(null);
        Assert.False(meta.Always);
        Assert.Empty(meta.Os);
        Assert.Empty(meta.RequireBins);
        Assert.Empty(meta.RequireEnv);
    }

    [Fact]
    public void ParseMetadata_InvalidJson_ReturnsDefaults()
    {
        var meta = SkillLoader.ParseMetadata("not json at all");
        Assert.False(meta.Always);
    }

    [Fact]
    public void ParseMetadata_NoOpenclawKey_ReturnsDefaults()
    {
        var meta = SkillLoader.ParseMetadata("""{"other": true}""");
        Assert.False(meta.Always);
    }

    [Fact]
    public void LoadAll_Disabled_ReturnsEmpty()
    {
        var config = new SkillsConfig { Enabled = false };
        var logger = new TestLogger();

        var skills = SkillLoader.LoadAll(config, null, logger);

        Assert.Empty(skills);
    }

    [Fact]
    public void LoadAll_NoDirectories_ReturnsEmpty()
    {
        var config = new SkillsConfig { Enabled = true };
        var logger = new TestLogger();

        var skills = SkillLoader.LoadAll(config, "/nonexistent/workspace", logger);

        Assert.Empty(skills);
    }

    [Fact]
    public void LoadAll_WithSkillFiles_LoadsAndFilters()
    {
        // Create temp skill structure: <workspace>/skills/<skill-name>/SKILL.md
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-test-skills-{Guid.NewGuid():N}");
        var skillDir = Path.Combine(tempDir, "skills", "test-skill");
        Directory.CreateDirectory(skillDir);

        try
        {
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), """
                ---
                name: test-skill
                description: A test skill
                ---
                Test instructions here.
                """);

            var config = new SkillsConfig { Enabled = true };
            var logger = new TestLogger();

            // Use tempDir as workspace skills
            var skills = SkillLoader.LoadAll(config, tempDir, logger);

            Assert.Single(skills);
            Assert.Equal("test-skill", skills[0].Name);
            Assert.Equal(SkillSource.Workspace, skills[0].Source);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void LoadAll_DisabledByEntry_Excluded()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-test-skills-{Guid.NewGuid():N}");
        var skillDir = Path.Combine(tempDir, "skills", "disabled-skill");
        Directory.CreateDirectory(skillDir);

        try
        {
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), """
                ---
                name: disabled-skill
                description: Should be filtered out
                ---
                Instructions.
                """);

            var config = new SkillsConfig
            {
                Enabled = true,
                Entries = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["disabled-skill"] = new SkillEntryConfig { Enabled = false }
                }
            };
            var logger = new TestLogger();

            var skills = SkillLoader.LoadAll(config, tempDir, logger);

            Assert.Empty(skills);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void LoadAll_WorkspaceOverridesManaged_HigherPrecedenceWins()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"openclaw-test-skills-{Guid.NewGuid():N}");
        var extraDir = Path.Combine(tempDir, "extra");
        var wsDir = Path.Combine(tempDir, "workspace");

        var extraSkillDir = Path.Combine(extraDir, "my-skill");
        var wsSkillDir = Path.Combine(wsDir, "skills", "my-skill");
        Directory.CreateDirectory(extraSkillDir);
        Directory.CreateDirectory(wsSkillDir);

        try
        {
            File.WriteAllText(Path.Combine(extraSkillDir, "SKILL.md"), """
                ---
                name: my-skill
                description: Extra version
                ---
                Extra instructions.
                """);

            File.WriteAllText(Path.Combine(wsSkillDir, "SKILL.md"), """
                ---
                name: my-skill
                description: Workspace version
                ---
                Workspace instructions.
                """);

            var config = new SkillsConfig
            {
                Enabled = true,
                Load = new SkillLoadConfig { ExtraDirs = [extraDir] }
            };
            var logger = new TestLogger();

            var skills = SkillLoader.LoadAll(config, wsDir, logger);

            Assert.Single(skills);
            Assert.Equal("my-skill", skills[0].Name);
            Assert.Equal("Workspace version", skills[0].Description);
            Assert.Equal(SkillSource.Workspace, skills[0].Source);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}

public class SkillPromptBuilderTests
{
    [Fact]
    public void Build_NoSkills_ReturnsEmpty()
    {
        var result = SkillPromptBuilder.Build([]);
        Assert.Equal("", result);
    }

    [Fact]
    public void Build_WithSkills_GeneratesXml()
    {
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "web-search",
                Description = "Search the web",
                Instructions = "Use the web_search tool to find information.",
                Location = "/skills/web-search"
            }
        };

        var result = SkillPromptBuilder.Build(skills);

        Assert.Contains("<available-skills>", result);
        Assert.Contains("<name>web-search</name>", result);
        Assert.Contains("<description>Search the web</description>", result);
        Assert.Contains("<location>/skills/web-search</location>", result);
        Assert.Contains("</available-skills>", result);
        Assert.Contains("<skill-instructions>", result);
        Assert.Contains("## Skill: web-search", result);
        Assert.Contains("Use the web_search tool", result);
    }

    [Fact]
    public void Build_DisableModelInvocation_ExcludesSkill()
    {
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "visible",
                Description = "Visible skill",
                Instructions = "Visible instructions.",
                Location = "/skills/visible"
            },
            new()
            {
                Name = "hidden",
                Description = "Hidden skill",
                Instructions = "Hidden instructions.",
                Location = "/skills/hidden",
                DisableModelInvocation = true
            }
        };

        var result = SkillPromptBuilder.Build(skills);

        Assert.Contains("visible", result);
        Assert.DoesNotContain("<name>hidden</name>", result);
    }

    [Fact]
    public void Build_EscapesXmlChars()
    {
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "test & <demo>",
                Description = "A \"test\" skill",
                Instructions = "Instructions here.",
                Location = "/skills/test"
            }
        };

        var result = SkillPromptBuilder.Build(skills);

        Assert.Contains("test &amp; &lt;demo&gt;", result);
        Assert.Contains("A &quot;test&quot; skill", result);
    }

    [Fact]
    public void BuildSummary_NoSkills_ReturnsMessage()
    {
        var result = SkillPromptBuilder.BuildSummary([]);
        Assert.Equal("No skills loaded.", result);
    }

    [Fact]
    public void BuildSummary_WithSkills_ListsThem()
    {
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "search",
                Description = "Web search",
                Instructions = "...",
                Location = "/skills/search",
                Source = SkillSource.Workspace
            },
            new()
            {
                Name = "internal",
                Description = "Internal only",
                Instructions = "...",
                Location = "/skills/internal",
                Source = SkillSource.Bundled,
                DisableModelInvocation = true
            }
        };

        var result = SkillPromptBuilder.BuildSummary(skills);

        Assert.Contains("Loaded skills (2)", result);
        Assert.Contains("search: Web search", result);
        Assert.Contains("(Workspace)", result);
        Assert.Contains("internal: Internal only", result);
        Assert.Contains("[no-model]", result);
        Assert.Contains("(Bundled)", result);
    }

    [Fact]
    public void EstimateCharacterCost_NoSkills_ReturnsZero()
    {
        Assert.Equal(0, SkillPromptBuilder.EstimateCharacterCost([]));
    }

    [Fact]
    public void EstimateCharacterCost_WithSkills_ReturnsPositive()
    {
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "test",
                Description = "Test skill",
                Instructions = "Do the thing.",
                Location = "/skills/test"
            }
        };

        var cost = SkillPromptBuilder.EstimateCharacterCost(skills);
        Assert.True(cost > 195); // base + per-skill
    }

    [Fact]
    public void EstimateCharacterCost_ExcludesDisabledModelSkills()
    {
        var skills = new List<SkillDefinition>
        {
            new()
            {
                Name = "hidden",
                Description = "Hidden",
                Instructions = "...",
                Location = "/skills/hidden",
                DisableModelInvocation = true
            }
        };

        Assert.Equal(0, SkillPromptBuilder.EstimateCharacterCost(skills));
    }
}

/// <summary>Minimal ILogger for tests.</summary>
file sealed class TestLogger : Microsoft.Extensions.Logging.ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) { }
}

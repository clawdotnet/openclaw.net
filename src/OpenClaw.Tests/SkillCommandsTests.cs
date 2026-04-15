using OpenClaw.Cli;
using Xunit;

namespace OpenClaw.Tests;

public sealed class SkillCommandsTests
{
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

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-skill-command-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateSkill(string root, string name, string description)
    {
        var slug = name.ToLowerInvariant().Replace(' ', '-');
        var skillDir = Path.Combine(root, slug);
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            $"---{Environment.NewLine}" +
            $"name: {name}{Environment.NewLine}" +
            $"description: {description}{Environment.NewLine}" +
            $"metadata: {{\"openclaw\":{{\"homepage\":\"https://example.com/{slug}\",\"requires\":{{\"env\":[\"OPENAI_API_KEY\"]}}}}}}{Environment.NewLine}" +
            $"---{Environment.NewLine}{Environment.NewLine}" +
            "Follow the documented process." +
            Environment.NewLine);
        return skillDir;
    }
}

using System.Text.Json;
using OpenClaw.Core.Skills;

namespace OpenClaw.Core.Plugins;

public static class AgentPluginSkillLoader
{
    private const string SkillFileName = "SKILL.md";

    public static List<string> GetSkillDirectories(List<AgentPluginPackage> packages)
    {
        var dirs = new List<string>();
        foreach (var pkg in packages)
        {
            if (!string.IsNullOrEmpty(pkg.SkillsPath) && Directory.Exists(pkg.SkillsPath))
            {
                // 只扫描直接子目录
                foreach (var skillDir in Directory.EnumerateDirectories(pkg.SkillsPath))
                {
                    var skillFile = Path.Combine(skillDir, SkillFileName);
                    if (File.Exists(skillFile))
                    {
                        dirs.Add(skillDir);
                    }
                }
            }
        }
        return dirs;
    }

    public static List<PluginCompatibilityDiagnostic> ValidateSkills(
        AgentPluginPackage package,
        out List<string> validSkillNames)
    {
        validSkillNames = [];
        var diagnostics = new List<PluginCompatibilityDiagnostic>();

        if (string.IsNullOrEmpty(package.SkillsPath) || !Directory.Exists(package.SkillsPath))
            return diagnostics;

        // 只检查直接子目录
        foreach (var skillDir in Directory.EnumerateDirectories(package.SkillsPath))
        {
            var skillName = Path.GetFileName(skillDir);
            var skillFile = Path.Combine(skillDir, SkillFileName);

            if (!File.Exists(skillFile))
            {
                diagnostics.Add(new PluginCompatibilityDiagnostic
                {
                    Code = "invalid_skill",
                    Message = $"Skill directory '{skillName}' does not contain {SkillFileName}. Skipping.",
                    Surface = "skill",
                    Path = skillDir
                });
                continue;
            }

            // 验证 SKILL.md 可以解析
            try
            {
                var content = File.ReadAllText(skillFile);
                if (string.IsNullOrWhiteSpace(content))
                {
                    diagnostics.Add(new PluginCompatibilityDiagnostic
                    {
                        Code = "empty_skill",
                        Message = $"Skill '{skillName}' has empty SKILL.md. Skipping.",
                        Surface = "skill",
                        Path = skillFile
                    });
                    continue;
                }

                validSkillNames.Add(skillName);
            }
            catch (Exception ex)
            {
                diagnostics.Add(new PluginCompatibilityDiagnostic
                {
                    Code = "skill_read_error",
                    Message = $"Failed to read skill '{skillName}': {ex.Message}",
                    Surface = "skill",
                    Path = skillFile
                });
            }
        }

        return diagnostics;
    }
}

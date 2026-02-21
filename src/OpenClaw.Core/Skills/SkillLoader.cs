using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpenClaw.Core.Skills;

/// <summary>
/// Scans skill directories, parses SKILL.md files, filters by requirements, and returns
/// eligible <see cref="SkillDefinition"/> instances. Compatible with the OpenClaw AgentSkills spec.
/// </summary>
public static class SkillLoader
{
    /// <summary>
    /// Load and filter all eligible skills from the standard locations.
    /// Precedence: workspace > managed > bundled > extra dirs.
    /// </summary>
    public static List<SkillDefinition> LoadAll(
        SkillsConfig config,
        string? workspacePath,
        ILogger logger)
    {
        if (!config.Enabled)
        {
            logger.LogInformation("Skills system is disabled");
            return [];
        }

        var allSkills = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);

        // 1. Extra dirs (lowest precedence — added first, overwritten by higher)
        foreach (var dir in config.Load.ExtraDirs)
        {
            if (Directory.Exists(dir))
                ScanDirectory(dir, SkillSource.Extra, allSkills, logger);
        }

        // 2. Bundled skills
        var bundledDir = Path.Combine(AppContext.BaseDirectory, "skills");
        if (Directory.Exists(bundledDir))
            ScanDirectory(bundledDir, SkillSource.Bundled, allSkills, logger);

        // 3. Managed/local skills
        var managedDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".openclaw", "skills");
        if (Directory.Exists(managedDir))
            ScanDirectory(managedDir, SkillSource.Managed, allSkills, logger);

        // 4. Workspace skills (highest precedence)
        if (!string.IsNullOrWhiteSpace(workspacePath))
        {
            var wsSkillsDir = Path.Combine(workspacePath, "skills");
            if (Directory.Exists(wsSkillsDir))
                ScanDirectory(wsSkillsDir, SkillSource.Workspace, allSkills, logger);
        }

        // Filter by config and requirements
        var eligible = new List<SkillDefinition>();
        foreach (var (name, skill) in allSkills)
        {
            // AllowBundled filter
            if (skill.Source == SkillSource.Bundled &&
                config.AllowBundled.Length > 0 &&
                !config.AllowBundled.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                logger.LogDebug("Skill '{Name}' skipped (not in allowBundled)", name);
                continue;
            }

            // Per-skill entry disable
            var configKey = skill.Metadata.SkillKey ?? name;
            if (config.Entries.TryGetValue(configKey, out var entry) && !entry.Enabled)
            {
                logger.LogDebug("Skill '{Name}' disabled by config", name);
                continue;
            }

            // Requirement gating (unless always=true)
            if (!skill.Metadata.Always && !CheckRequirements(skill, config, logger))
            {
                logger.LogDebug("Skill '{Name}' skipped (requirements not met)", name);
                continue;
            }

            eligible.Add(skill);
        }

        logger.LogInformation("Loaded {Count} eligible skills from {Total} discovered",
            eligible.Count, allSkills.Count);

        return eligible;
    }

    /// <summary>
    /// Scan a directory for subdirectories containing SKILL.md.
    /// </summary>
    private static void ScanDirectory(
        string rootDir,
        SkillSource source,
        Dictionary<string, SkillDefinition> results,
        ILogger logger)
    {
        try
        {
            foreach (var skillDir in Directory.GetDirectories(rootDir))
            {
                var skillFile = Path.Combine(skillDir, "SKILL.md");
                if (!File.Exists(skillFile))
                    continue;

                try
                {
                    var skill = ParseSkillFile(skillFile, skillDir, source);
                    if (skill is not null)
                    {
                        // Higher precedence sources overwrite lower ones
                        results[skill.Name] = skill;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to parse skill at {Path}", skillFile);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to scan skill directory {Dir}", rootDir);
        }
    }

    /// <summary>
    /// Parse a SKILL.md file with YAML-like frontmatter.
    /// </summary>
    internal static SkillDefinition? ParseSkillFile(string filePath, string skillDir, SkillSource source)
    {
        var content = File.ReadAllText(filePath);
        return ParseSkillContent(content, skillDir, source);
    }

    /// <summary>
    /// Parse SKILL.md content. Separated from file I/O for testing.
    /// </summary>
    internal static SkillDefinition? ParseSkillContent(string content, string skillDir, SkillSource source)
    {
        // Split frontmatter from body
        if (!content.StartsWith("---"))
            return null;

        var endIndex = content.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (endIndex < 0)
            return null;

        var frontmatter = content[3..endIndex].Trim();
        var body = content[(endIndex + 4)..].Trim();

        // Parse frontmatter lines
        string? name = null;
        string? description = null;
        string? metadataJson = null;
        var userInvocable = true;
        var disableModelInvocation = false;
        string? commandDispatch = null;
        string? commandTool = null;
        string? commandArgMode = null;
        string? homepage = null;

        foreach (var rawLine in frontmatter.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
                continue;

            var colonIdx = line.IndexOf(':');
            if (colonIdx < 0)
                continue;

            var key = line[..colonIdx].Trim().ToLowerInvariant();
            var value = line[(colonIdx + 1)..].Trim();

            switch (key)
            {
                case "name":
                    name = value;
                    break;
                case "description":
                    description = value;
                    break;
                case "metadata":
                    metadataJson = value;
                    break;
                case "user-invocable":
                    userInvocable = !value.Equals("false", StringComparison.OrdinalIgnoreCase);
                    break;
                case "disable-model-invocation":
                    disableModelInvocation = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    break;
                case "command-dispatch":
                    commandDispatch = value;
                    break;
                case "command-tool":
                    commandTool = value;
                    break;
                case "command-arg-mode":
                    commandArgMode = value;
                    break;
                case "homepage":
                    homepage = value;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(name))
            return null;

        description ??= "";

        var metadata = ParseMetadata(metadataJson);
        if (homepage is not null && metadata.Homepage is null)
            metadata.Homepage = homepage;

        // Replace {baseDir} placeholder in instructions
        body = body.Replace("{baseDir}", skillDir);

        return new SkillDefinition
        {
            Name = name,
            Description = description,
            Instructions = body,
            Location = skillDir,
            Source = source,
            Metadata = metadata,
            UserInvocable = userInvocable,
            DisableModelInvocation = disableModelInvocation,
            CommandDispatch = commandDispatch,
            CommandTool = commandTool,
            CommandArgMode = commandArgMode
        };
    }

    /// <summary>
    /// Parse the metadata JSON from the frontmatter.
    /// Expected format: { "openclaw": { "requires": { ... }, "primaryEnv": "..." } }
    /// </summary>
    internal static SkillMetadata ParseMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new SkillMetadata();

        try
        {
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("openclaw", out var oc))
                return new SkillMetadata();

            var meta = new SkillMetadata();

            if (oc.TryGetProperty("always", out var always))
                meta.Always = always.GetBoolean();
            if (oc.TryGetProperty("emoji", out var emoji))
                meta.Emoji = emoji.GetString();
            if (oc.TryGetProperty("homepage", out var hp))
                meta.Homepage = hp.GetString();
            if (oc.TryGetProperty("primaryEnv", out var pe))
                meta.PrimaryEnv = pe.GetString();
            if (oc.TryGetProperty("skillKey", out var sk))
                meta.SkillKey = sk.GetString();

            if (oc.TryGetProperty("os", out var os))
                meta.Os = ReadStringArray(os);

            if (oc.TryGetProperty("requires", out var req))
            {
                if (req.TryGetProperty("bins", out var bins))
                    meta.RequireBins = ReadStringArray(bins);
                if (req.TryGetProperty("anyBins", out var anyBins))
                    meta.RequireAnyBins = ReadStringArray(anyBins);
                if (req.TryGetProperty("env", out var env))
                    meta.RequireEnv = ReadStringArray(env);
                if (req.TryGetProperty("config", out var cfg))
                    meta.RequireConfig = ReadStringArray(cfg);
            }

            return meta;
        }
        catch
        {
            return new SkillMetadata();
        }
    }

    /// <summary>
    /// Check if a skill's requirements are met on this host.
    /// </summary>
    private static bool CheckRequirements(SkillDefinition skill, SkillsConfig config, ILogger logger)
    {
        var meta = skill.Metadata;

        // OS gate
        if (meta.Os.Length > 0)
        {
            var currentOs = GetCurrentOs();
            if (!meta.Os.Contains(currentOs, StringComparer.OrdinalIgnoreCase))
            {
                logger.LogDebug("Skill '{Name}' skipped (OS {Current} not in [{Required}])",
                    skill.Name, currentOs, string.Join(", ", meta.Os));
                return false;
            }
        }

        // Required binaries
        foreach (var bin in meta.RequireBins)
        {
            if (!IsBinaryOnPath(bin))
            {
                logger.LogDebug("Skill '{Name}' skipped (binary '{Bin}' not found)", skill.Name, bin);
                return false;
            }
        }

        // Any-of binaries
        if (meta.RequireAnyBins.Length > 0 && !meta.RequireAnyBins.Any(IsBinaryOnPath))
        {
            logger.LogDebug("Skill '{Name}' skipped (none of [{Bins}] found)",
                skill.Name, string.Join(", ", meta.RequireAnyBins));
            return false;
        }

        // Required env vars (check config entry env injection too)
        var configKey = meta.SkillKey ?? skill.Name;
        config.Entries.TryGetValue(configKey, out var entry);

        foreach (var envVar in meta.RequireEnv)
        {
            var hasEnv = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envVar));
            var hasInConfig = entry?.Env.ContainsKey(envVar) == true;
            var hasApiKey = meta.PrimaryEnv == envVar && !string.IsNullOrWhiteSpace(entry?.ApiKey);

            if (!hasEnv && !hasInConfig && !hasApiKey)
            {
                logger.LogDebug("Skill '{Name}' skipped (env var '{Var}' not set)", skill.Name, envVar);
                return false;
            }
        }

        return true;
    }

    private static string GetCurrentOs()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "darwin";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "linux";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "win32";
        return "unknown";
    }

    // Cache for binary-on-PATH lookups to avoid redundant filesystem scans
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _binaryOnPathCache = new(StringComparer.Ordinal);

    private static bool IsBinaryOnPath(string binaryName)
    {
        return _binaryOnPathCache.GetOrAdd(binaryName, static name =>
        {
            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            var separator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';

            foreach (var dir in pathVar.Split(separator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var fullPath = Path.Combine(dir, name);
                    if (File.Exists(fullPath))
                        return true;

                    // Windows: check with common extensions
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        if (File.Exists(fullPath + ".exe") || File.Exists(fullPath + ".cmd") || File.Exists(fullPath + ".bat"))
                            return true;
                    }
                }
                catch
                {
                    // Skip inaccessible directories
                }
            }

            return false;
        });
    }

    private static string[] ReadStringArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            return [];

        var result = new string[element.GetArrayLength()];
        var i = 0;
        foreach (var item in element.EnumerateArray())
        {
            result[i++] = item.GetString() ?? "";
        }
        return result;
    }
}

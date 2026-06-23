using System.Runtime.InteropServices;
using System.Text;
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
        ILogger logger,
        IReadOnlyList<string>? pluginSkillDirs = null)
    {
        if (!config.Enabled)
        {
            logger.LogInformation("Skills system is disabled");
            return [];
        }

        var allSkills = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);

        // 1. Extra dirs (lowest precedence — added first, overwritten by higher)
        var scanSubdirectories = config.Load.ScanSubdirectories;
        foreach (var dir in config.Load.ExtraDirs)
        {
            if (Directory.Exists(dir))
                ScanDirectory(dir, SkillSource.Extra, allSkills, logger, scanSubdirectories);
        }

        // 2. Bundled skills
        if (config.Load.IncludeBundled)
        {
            var bundledDir = Path.Combine(AppContext.BaseDirectory, "skills");
            if (Directory.Exists(bundledDir))
                ScanDirectory(bundledDir, SkillSource.Bundled, allSkills, logger, scanSubdirectories);
        }

        // 3. Managed/local skills
        if (config.Load.IncludeManaged)
        {
            var managedDir = string.IsNullOrWhiteSpace(config.Load.ManagedRoot)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openclaw", "skills")
                : NormalizeManagedRootPath(config.Load.ManagedRoot, logger);
            if (managedDir is not null && TryDirectoryExists(managedDir))
                ScanDirectory(managedDir, SkillSource.Managed, allSkills, logger, scanSubdirectories);
        }

        // 4. Plugin-packaged skills
        if (pluginSkillDirs is not null)
        {
            foreach (var pluginDir in pluginSkillDirs)
            {
                if (Directory.Exists(pluginDir))
                    ScanDirectory(pluginDir, SkillSource.Plugin, allSkills, logger, scanSubdirectories);
            }
        }

        // 5. Workspace skills (highest precedence)
        if (config.Load.IncludeWorkspace && !string.IsNullOrWhiteSpace(workspacePath))
        {
            var wsSkillsDir = Path.Combine(workspacePath, "skills");
            if (Directory.Exists(wsSkillsDir))
                ScanDirectory(wsSkillsDir, SkillSource.Workspace, allSkills, logger, scanSubdirectories);
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

    private static string? NormalizeManagedRootPath(string managedRoot, ILogger logger)
    {
        try
        {
            var normalized = managedRoot.Trim();

            if (normalized.StartsWith("~", StringComparison.Ordinal))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(home))
                {
                    var suffix = normalized[1..].TrimStart('/', '\\');
                    normalized = suffix.Length == 0 ? home : Path.Combine(home, suffix);
                }
            }

            if (!Path.IsPathRooted(normalized))
                normalized = Path.GetFullPath(normalized);

            return normalized;
        }
        catch (Exception ex) when (IsPathException(ex))
        {
            logger.LogWarning(ex, "Skipping invalid managed skills root '{ManagedRoot}'.", managedRoot);
            return null;
        }
    }

    private static bool TryDirectoryExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex) when (IsPathException(ex))
        {
            return false;
        }
    }

    private static bool IsPathException(Exception ex) =>
        ex is ArgumentException or IOException or NotSupportedException or PathTooLongException or UnauthorizedAccessException;

    /// <summary>
    /// Scan a directory for subdirectories containing SKILL.md.
    /// When <paramref name="scanSubdirectories"/> is true, descends into nested
    /// subdirectories (e.g. subskills/docx/SKILL.md) so that parent skill
    /// directories can bundle sub-skills.
    /// </summary>
    private static void ScanDirectory(
        string rootDir,
        SkillSource source,
        Dictionary<string, SkillDefinition> results,
        ILogger logger,
        bool scanSubdirectories = false)
    {
        try
        {
            var rootSkillFile = Path.Combine(rootDir, "SKILL.md");
            if (File.Exists(rootSkillFile))
            {
                try
                {
                    if (TryParseSkillFile(rootSkillFile, rootDir, source, out var skill, out var errorCode) && skill is not null)
                    {
                        results[skill.Name] = skill;
                    }
                    else
                    {
                        logger.LogWarning("Failed to parse skill at {Path} (error_code={ErrorCode})", rootSkillFile, errorCode ?? "parse_failed");
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to parse skill at {Path}", rootSkillFile);
                }
            }

            var searchOption = scanSubdirectories
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;

            foreach (var skillDir in Directory.GetDirectories(rootDir, "*", searchOption))
            {
                var skillFile = Path.Combine(skillDir, "SKILL.md");
                if (!File.Exists(skillFile))
                    continue;

                try
                {
                    if (TryParseSkillFile(skillFile, skillDir, source, out var skill, out var errorCode) && skill is not null)
                    {
                        // Higher precedence sources overwrite lower ones
                        results[skill.Name] = skill;
                    }
                    else
                    {
                        logger.LogWarning("Failed to parse skill at {Path} (error_code={ErrorCode})", skillFile, errorCode ?? "parse_failed");
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

    internal static bool TryParseSkillFile(
        string filePath,
        string skillDir,
        SkillSource source,
        out SkillDefinition? skill,
        out string? errorCode)
    {
        var content = File.ReadAllText(filePath);
        return TryParseSkillContent(content, skillDir, source, out skill, out errorCode);
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
        var kind = SkillKind.Standard;
        List<string>? triggers = null;
        int? metaPriority = null;
        string? finalTextMode = null;
        string? compositionJson = null;
        string? commandDispatch = null;
        string? commandTool = null;
        string? commandArgMode = null;
        string? homepage = null;

        var frontmatterLines = frontmatter.Split('\n');
        for (var lineIndex = 0; lineIndex < frontmatterLines.Length; lineIndex++)
        {
            var rawLine = frontmatterLines[lineIndex].TrimEnd('\r');
            if (!string.IsNullOrWhiteSpace(rawLine) && GetIndent(rawLine) != 0)
                continue;

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
                    name = NormalizeFrontmatterScalar(value);
                    break;
                case "description":
                    description = NormalizeFrontmatterScalar(value);
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
                case "kind":
                    if (!TryParseSkillKind(NormalizeFrontmatterScalar(value), out kind))
                        return null;
                    break;
                case "triggers":
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        var triggerBlock = CollectIndentedBlock(frontmatterLines, lineIndex + 1, out var consumedLines);
                        lineIndex += consumedLines;
                        if (!TryConvertYamlBlockToJson(triggerBlock, out var triggerJson) || triggerJson is null)
                            return null;

                        value = triggerJson;
                    }

                    if (!TryParseStringList(value, out triggers))
                        return null;
                    break;
                case "meta-priority":
                case "meta_priority":
                    if (!int.TryParse(value, out var parsedMetaPriority))
                        return null;
                    metaPriority = parsedMetaPriority;
                    break;
                case "final-text-mode":
                case "final_text_mode":
                    finalTextMode = NormalizeFrontmatterScalar(value);
                    break;
                case "composition":
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        var compositionBlock = CollectIndentedBlock(frontmatterLines, lineIndex + 1, out var consumedLines);
                        lineIndex += consumedLines;
                        if (!TryConvertYamlBlockToJson(compositionBlock, out compositionJson))
                            return null;
                    }
                    else
                    {
                        compositionJson = value;
                    }
                    break;
                case "command-dispatch":
                    commandDispatch = NormalizeFrontmatterScalar(value);
                    break;
                case "command-tool":
                    commandTool = NormalizeFrontmatterScalar(value);
                    break;
                case "command-arg-mode":
                    commandArgMode = NormalizeFrontmatterScalar(value);
                    break;
                case "homepage":
                    homepage = NormalizeFrontmatterScalar(value);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(name))
            return null;

        description ??= "";

        var metadata = ParseMetadata(metadataJson);
        if (homepage is not null && metadata.Homepage is null)
            metadata.Homepage = homepage;

        MetaSkillComposition? composition = null;
        if (kind == SkillKind.Meta)
        {
            if (string.IsNullOrWhiteSpace(compositionJson))
                return null;

            composition = ParseComposition(compositionJson);
            if (composition is null || composition.Steps.Count == 0)
                return null;

            if (!ValidateFinalTextMode(finalTextMode, composition.Steps))
                return null;
        }

        // Replace {baseDir} placeholder in instructions
        body = body.Replace("{baseDir}", skillDir);

        // Progressive disclosure (level 3): surface auxiliary files under
        // references/ and scripts/ in the manifest. The full file content is
        // *not* loaded here — only the listing — so the model can fetch on demand.
        var resources = ScanSkillResources(skillDir);

        return new SkillDefinition
        {
            Name = name,
            Description = description,
            Instructions = body,
            Location = skillDir,
            Source = source,
            Metadata = metadata,
            Kind = kind,
            Triggers = triggers ?? [],
            MetaPriority = metaPriority,
            FinalTextMode = finalTextMode,
            Composition = composition,
            UserInvocable = userInvocable,
            DisableModelInvocation = disableModelInvocation,
            CommandDispatch = commandDispatch,
            CommandTool = commandTool,
            CommandArgMode = commandArgMode,
            Resources = resources
        };
    }

    internal static bool TryParseSkillContent(
        string content,
        string skillDir,
        SkillSource source,
        out SkillDefinition? skill,
        out string? errorCode)
    {
        skill = ParseSkillContent(content, skillDir, source);
        if (skill is not null)
        {
            errorCode = null;
            return true;
        }

        errorCode = DiagnoseSkillParseFailure(content);
        return false;
    }

    private static string DiagnoseSkillParseFailure(string content)
    {
        if (!content.StartsWith("---", StringComparison.Ordinal))
            return "invalid_frontmatter";

        var endIndex = content.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (endIndex < 0)
            return "invalid_frontmatter";

        var frontmatter = content[3..endIndex].Trim();
        string? name = null;
        var kind = SkillKind.Standard;
        string? compositionJson = null;
        string? finalTextMode = null;

        var frontmatterLines = frontmatter.Split('\n');
        for (var lineIndex = 0; lineIndex < frontmatterLines.Length; lineIndex++)
        {
            var rawLine = frontmatterLines[lineIndex].TrimEnd('\r');
            if (!string.IsNullOrWhiteSpace(rawLine) && GetIndent(rawLine) != 0)
                continue;

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
                    name = NormalizeFrontmatterScalar(value);
                    break;
                case "kind":
                    if (!TryParseSkillKind(NormalizeFrontmatterScalar(value), out kind))
                        return "invalid_kind";
                    break;
                case "composition":
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        var compositionBlock = CollectIndentedBlock(frontmatterLines, lineIndex + 1, out var consumedLines);
                        lineIndex += consumedLines;
                        if (!TryConvertYamlBlockToJson(compositionBlock, out compositionJson))
                            return "invalid_meta_composition";
                    }
                    else
                    {
                        compositionJson = value;
                    }
                    break;
                case "final-text-mode":
                case "final_text_mode":
                    finalTextMode = NormalizeFrontmatterScalar(value);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(name))
            return "missing_name";

        if (kind != SkillKind.Meta)
            return "parse_failed";

        if (string.IsNullOrWhiteSpace(compositionJson))
            return "missing_meta_composition";

        var composition = ParseComposition(compositionJson, out var compositionErrorCode);
        if (composition is null)
            return compositionErrorCode ?? "invalid_meta_composition";

        if (!ValidateFinalTextMode(finalTextMode, composition.Steps))
            return "invalid_final_text_mode";

        return "parse_failed";
    }

    private static bool TryParseSkillKind(string rawValue, out SkillKind kind)
    {
        kind = SkillKind.Standard;

        if (string.IsNullOrWhiteSpace(rawValue))
            return false;

        switch (rawValue.Trim().ToLowerInvariant())
        {
            case "standard":
                return true;
            case "meta":
                kind = SkillKind.Meta;
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseStringList(string rawValue, out List<string>? values)
    {
        values = null;

        if (string.IsNullOrWhiteSpace(rawValue))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(rawValue);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return false;

            values = [];
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    return false;

                var value = item.GetString();
                if (string.IsNullOrWhiteSpace(value))
                    return false;

                values.Add(value);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeFrontmatterScalar(string rawValue)
    {
        var value = rawValue.Trim();
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return UnquoteYamlScalar(value);
        }

        return value;
    }

    private static string CollectIndentedBlock(string[] lines, int startIndex, out int consumedLines)
    {
        consumedLines = 0;
        var builder = new StringBuilder();
        for (var index = startIndex; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            if (!string.IsNullOrWhiteSpace(line) && GetIndent(line) == 0)
                break;

            builder.AppendLine(line);
            consumedLines++;
        }

        return builder.ToString();
    }

    private static bool TryConvertYamlBlockToJson(string yaml, out string? json)
    {
        json = null;
        if (string.IsNullOrWhiteSpace(yaml))
            return false;

        var lines = yaml.Split('\n')
            .Select(static line => line.TrimEnd('\r'))
            .ToList();

        var index = 0;
        SkipYamlBlankLines(lines, ref index);
        if (index >= lines.Count)
            return false;

        var indent = GetIndent(lines[index]);
        if (!TryParseYamlNode(lines, ref index, indent, out var node) || node is null)
            return false;

        SkipYamlBlankLines(lines, ref index);
        if (index < lines.Count)
            return false;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            node.WriteTo(writer);
        }

        json = Encoding.UTF8.GetString(stream.ToArray());
        return true;
    }

    private static bool TryParseYamlNode(IReadOnlyList<string> lines, ref int index, int indent, out SkillYamlNode? node)
    {
        node = null;
        SkipYamlBlankLines(lines, ref index);
        if (index >= lines.Count)
            return false;

        var line = lines[index];
        var lineIndent = GetIndent(line);
        if (lineIndent < indent)
            return false;

        var text = line[lineIndent..].TrimEnd();
        return text.StartsWith("- ", StringComparison.Ordinal)
            ? TryParseYamlArray(lines, ref index, lineIndent, out node)
            : TryParseYamlMapping(lines, ref index, lineIndent, out node);
    }

    private static bool TryParseYamlMapping(IReadOnlyList<string> lines, ref int index, int indent, out SkillYamlNode? node)
    {
        var properties = new List<KeyValuePair<string, SkillYamlNode>>();
        node = null;

        while (index < lines.Count)
        {
            SkipYamlBlankLines(lines, ref index);
            if (index >= lines.Count)
                break;

            var line = lines[index];
            var lineIndent = GetIndent(line);
            if (lineIndent < indent)
                break;
            if (lineIndent > indent)
                return false;

            var text = line[lineIndent..].TrimEnd();
            if (text.StartsWith("- ", StringComparison.Ordinal))
                break;

            if (!TrySplitYamlKeyValue(text, out var key, out var value))
                return false;

            if (!TryParseYamlValue(lines, ref index, indent, value, out var valueNode) || valueNode is null)
                return false;

            properties.Add(new KeyValuePair<string, SkillYamlNode>(NormalizeYamlPropertyName(key), valueNode));
        }

        node = new SkillYamlObjectNode(properties);
        return true;
    }

    private static bool TryParseYamlArray(IReadOnlyList<string> lines, ref int index, int indent, out SkillYamlNode? node)
    {
        var items = new List<SkillYamlNode>();
        node = null;

        while (index < lines.Count)
        {
            SkipYamlBlankLines(lines, ref index);
            if (index >= lines.Count)
                break;

            var line = lines[index];
            var lineIndent = GetIndent(line);
            if (lineIndent < indent)
                break;
            if (lineIndent > indent)
                return false;

            var text = line[lineIndent..].TrimEnd();
            if (!text.StartsWith("- ", StringComparison.Ordinal))
                break;

            var itemText = text[2..].Trim();
            if (string.IsNullOrWhiteSpace(itemText))
            {
                index++;
                var childIndex = FindNextYamlContentLine(lines, index);
                if (childIndex < 0 || GetIndent(lines[childIndex]) <= indent)
                {
                    items.Add(new SkillYamlScalarNode(string.Empty));
                    continue;
                }

                var childIndent = GetIndent(lines[childIndex]);
                if (!TryParseYamlNode(lines, ref index, childIndent, out var childNode) || childNode is null)
                    return false;

                items.Add(childNode);
                continue;
            }

            if (TrySplitYamlKeyValue(itemText, out var key, out var value))
            {
                var properties = new List<KeyValuePair<string, SkillYamlNode>>();
                if (!TryParseYamlInlineValueOrLiteral(lines, ref index, indent, value, out var firstValue) || firstValue is null)
                    return false;

                properties.Add(new KeyValuePair<string, SkillYamlNode>(NormalizeYamlPropertyName(key), firstValue));

                var childIndex = FindNextYamlContentLine(lines, index);
                if (childIndex >= 0 && GetIndent(lines[childIndex]) > indent)
                {
                    var childIndent = GetIndent(lines[childIndex]);
                    if (!TryParseYamlMapping(lines, ref index, childIndent, out var continuationNode) ||
                        continuationNode is not SkillYamlObjectNode continuationObject)
                    {
                        return false;
                    }

                    properties.AddRange(continuationObject.Properties);
                }

                items.Add(new SkillYamlObjectNode(properties));
                continue;
            }

            items.Add(ParseYamlScalar(itemText));
            index++;

            var nextIndex = FindNextYamlContentLine(lines, index);
            if (nextIndex >= 0 && GetIndent(lines[nextIndex]) > indent)
                return false;
        }

        node = new SkillYamlArrayNode(items);
        return true;
    }

    private static bool TryParseYamlValue(
        IReadOnlyList<string> lines,
        ref int index,
        int parentIndent,
        string rawValue,
        out SkillYamlNode? node)
    {
        if (TryParseYamlInlineValueOrLiteral(lines, ref index, parentIndent, rawValue, out node))
            return true;

        node = null;
        return false;
    }

    private static bool TryParseYamlInlineValueOrLiteral(
        IReadOnlyList<string> lines,
        ref int index,
        int parentIndent,
        string rawValue,
        out SkillYamlNode? node)
    {
        node = null;
        var value = rawValue.Trim();

        if (IsYamlLiteralIndicator(value))
        {
            index++;
            node = new SkillYamlScalarNode(ParseYamlLiteralBlock(lines, ref index, parentIndent));
            return true;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            index++;
            var childIndex = FindNextYamlContentLine(lines, index);
            if (childIndex < 0 || GetIndent(lines[childIndex]) <= parentIndent)
            {
                node = new SkillYamlScalarNode(string.Empty);
                return true;
            }

            var childIndent = GetIndent(lines[childIndex]);
            return TryParseYamlNode(lines, ref index, childIndent, out node);
        }

        node = ParseYamlScalar(value);
        index++;
        return true;
    }

    private static SkillYamlNode ParseYamlScalar(string rawValue)
    {
        var value = rawValue.Trim();
        if (value.StartsWith("[", StringComparison.Ordinal) && value.EndsWith("]", StringComparison.Ordinal))
        {
            return ParseYamlInlineArray(value);
        }

        if (value.Equals("true", StringComparison.OrdinalIgnoreCase))
            return new SkillYamlScalarNode(true);

        if (value.Equals("false", StringComparison.OrdinalIgnoreCase))
            return new SkillYamlScalarNode(false);

        if (value.Equals("null", StringComparison.OrdinalIgnoreCase) || value.Equals("~", StringComparison.Ordinal))
            return new SkillYamlScalarNode(null);

        if (long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var longValue))
            return new SkillYamlScalarNode(longValue);

        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var doubleValue))
            return new SkillYamlScalarNode(doubleValue);

        return new SkillYamlScalarNode(UnquoteYamlScalar(value));
    }

    private static SkillYamlArrayNode ParseYamlInlineArray(string rawValue)
    {
        var content = rawValue[1..^1].Trim();
        if (string.IsNullOrWhiteSpace(content))
            return new SkillYamlArrayNode([]);

        var items = SplitYamlInlineArray(content)
            .Select(ParseYamlScalar)
            .ToList();
        return new SkillYamlArrayNode(items);
    }

    private static IEnumerable<string> SplitYamlInlineArray(string content)
    {
        var start = 0;
        var quote = '\0';
        var escaped = false;

        for (var index = 0; index < content.Length; index++)
        {
            var character = content[index];
            if (quote != '\0')
            {
                if (quote == '"' && character == '\\' && !escaped)
                {
                    escaped = true;
                    continue;
                }

                if (character == quote && !escaped)
                    quote = '\0';

                escaped = false;
                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                continue;
            }

            if (character != ',')
                continue;

            yield return content[start..index].Trim();
            start = index + 1;
        }

        yield return content[start..].Trim();
    }

    private static string ParseYamlLiteralBlock(IReadOnlyList<string> lines, ref int index, int parentIndent)
    {
        var literalLines = new List<string>();
        var contentIndent = -1;

        while (index < lines.Count)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                literalLines.Add(string.Empty);
                index++;
                continue;
            }

            var lineIndent = GetIndent(line);
            if (lineIndent <= parentIndent)
                break;

            if (contentIndent < 0)
                contentIndent = lineIndent;

            var remove = Math.Min(contentIndent, line.Length);
            literalLines.Add(line[remove..].TrimEnd());
            index++;
        }

        return string.Join('\n', literalLines);
    }

    private static bool TrySplitYamlKeyValue(string text, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;

        var colonIndex = text.IndexOf(':');
        if (colonIndex <= 0)
            return false;

        key = text[..colonIndex].Trim();
        value = text[(colonIndex + 1)..].Trim();
        return key.Length > 0;
    }

    private static string NormalizeYamlPropertyName(string key)
        => key switch
        {
            "skill_exec_entrypoint" => "entrypoint",
            "skill_exec_args" => "args",
            "skill_exec_stdin" => "stdin",
            "skill_exec_cwd" => "cwd",
            "skill_exec_parse_mode" => "parse_mode",
            _ => key
        };

    private static bool IsYamlLiteralIndicator(string value)
        => value.Equals("|", StringComparison.Ordinal) ||
           value.Equals("|-", StringComparison.Ordinal) ||
           value.Equals("|+", StringComparison.Ordinal) ||
           value.StartsWith("|", StringComparison.Ordinal);

    private static string UnquoteYamlScalar(string value)
    {
        if (value.Length < 2)
            return value;

        if (value[0] == '\'' && value[^1] == '\'')
            return value[1..^1].Replace("''", "'", StringComparison.Ordinal);

        if (value[0] != '"' || value[^1] != '"')
            return value;

        var builder = new StringBuilder(value.Length - 2);
        for (var index = 1; index < value.Length - 1; index++)
        {
            var character = value[index];
            if (character != '\\' || index + 1 >= value.Length - 1)
            {
                builder.Append(character);
                continue;
            }

            var escaped = value[++index];
            builder.Append(escaped switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '"' => '"',
                '\\' => '\\',
                _ => escaped
            });
        }

        return builder.ToString();
    }

    private static int FindNextYamlContentLine(IReadOnlyList<string> lines, int startIndex)
    {
        for (var index = startIndex; index < lines.Count; index++)
        {
            if (!IsYamlIgnorableLine(lines[index]))
                return index;
        }

        return -1;
    }

    private static void SkipYamlBlankLines(IReadOnlyList<string> lines, ref int index)
    {
        while (index < lines.Count && IsYamlIgnorableLine(lines[index]))
            index++;
    }

    private static bool IsYamlIgnorableLine(string line)
        => string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#", StringComparison.Ordinal);

    private static int GetIndent(string line)
    {
        var indent = 0;
        while (indent < line.Length && line[indent] == ' ')
            indent++;

        return indent;
    }

    private abstract class SkillYamlNode
    {
        internal abstract void WriteTo(Utf8JsonWriter writer);
    }

    private sealed class SkillYamlObjectNode : SkillYamlNode
    {
        internal SkillYamlObjectNode(IReadOnlyList<KeyValuePair<string, SkillYamlNode>> properties)
        {
            Properties = properties;
        }

        internal IReadOnlyList<KeyValuePair<string, SkillYamlNode>> Properties { get; }

        internal override void WriteTo(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            foreach (var (key, value) in Properties)
            {
                writer.WritePropertyName(key);
                value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }
    }

    private sealed class SkillYamlArrayNode : SkillYamlNode
    {
        internal SkillYamlArrayNode(IReadOnlyList<SkillYamlNode> items)
        {
            Items = items;
        }

        private IReadOnlyList<SkillYamlNode> Items { get; }

        internal override void WriteTo(Utf8JsonWriter writer)
        {
            writer.WriteStartArray();
            foreach (var item in Items)
                item.WriteTo(writer);

            writer.WriteEndArray();
        }
    }

    private sealed class SkillYamlScalarNode : SkillYamlNode
    {
        internal SkillYamlScalarNode(object? value)
        {
            Value = value;
        }

        private object? Value { get; }

        internal override void WriteTo(Utf8JsonWriter writer)
        {
            switch (Value)
            {
                case null:
                    writer.WriteNullValue();
                    break;
                case bool boolValue:
                    writer.WriteBooleanValue(boolValue);
                    break;
                case long longValue:
                    writer.WriteNumberValue(longValue);
                    break;
                case double doubleValue:
                    writer.WriteNumberValue(doubleValue);
                    break;
                case string stringValue:
                    writer.WriteStringValue(stringValue);
                    break;
                default:
                    writer.WriteStringValue(Value.ToString());
                    break;
            }
        }
    }

    internal static MetaSkillComposition? ParseComposition(string json)
    {
        return ParseComposition(json, out _);
    }

    internal static MetaSkillComposition? ParseComposition(string json, out string? errorCode)
    {
        errorCode = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            errorCode = "invalid_meta_composition";
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                errorCode = "invalid_meta_composition";
                return null;
            }

            if (!doc.RootElement.TryGetProperty("steps", out var stepsElement) ||
                stepsElement.ValueKind != JsonValueKind.Array)
            {
                errorCode = "invalid_meta_composition";
                return null;
            }

            string? toolArgsJson = null;
            if (doc.RootElement.TryGetProperty("tool_args", out var compositionToolArgsElement))
            {
                if (compositionToolArgsElement.ValueKind != JsonValueKind.Object)
                {
                    errorCode = "invalid_tool_args";
                    return null;
                }

                toolArgsJson = compositionToolArgsElement.GetRawText();
            }

            var steps = new List<MetaSkillStepDefinition>();
            foreach (var stepElement in stepsElement.EnumerateArray())
            {
                if (stepElement.ValueKind != JsonValueKind.Object)
                {
                    errorCode = "invalid_meta_composition";
                    return null;
                }

                if (!stepElement.TryGetProperty("id", out var idElement) ||
                    idElement.ValueKind != JsonValueKind.String)
                {
                    errorCode = "invalid_meta_composition";
                    return null;
                }

                string? kind = null;
                if (stepElement.TryGetProperty("kind", out var kindElement) && kindElement.ValueKind == JsonValueKind.String)
                    kind = kindElement.GetString();
                else if (stepElement.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String)
                    kind = typeElement.GetString();

                if (string.IsNullOrWhiteSpace(kind))
                {
                    errorCode = "invalid_meta_composition";
                    return null;
                }

                var dependsOn = new List<string>();
                if (stepElement.TryGetProperty("depends_on", out var dependsOnElement))
                {
                    if (dependsOnElement.ValueKind != JsonValueKind.Array)
                    {
                        errorCode = "invalid_meta_composition";
                        return null;
                    }

                    foreach (var dependsOnItem in dependsOnElement.EnumerateArray())
                    {
                        if (dependsOnItem.ValueKind != JsonValueKind.String)
                        {
                            errorCode = "invalid_meta_composition";
                            return null;
                        }

                        var dep = dependsOnItem.GetString();
                        if (string.IsNullOrWhiteSpace(dep))
                        {
                            errorCode = "invalid_meta_composition";
                            return null;
                        }

                        dependsOn.Add(dep);
                    }
                }

                var skill = stepElement.TryGetProperty("skill", out var skillElement) && skillElement.ValueKind == JsonValueKind.String
                    ? skillElement.GetString()
                    : null;
                var tool = stepElement.TryGetProperty("tool", out var toolElement) && toolElement.ValueKind == JsonValueKind.String
                    ? toolElement.GetString()
                    : null;
                string? withJson = null;
                if (stepElement.TryGetProperty("with", out var withElement))
                {
                    if (withElement.ValueKind != JsonValueKind.Object)
                    {
                        errorCode = "invalid_with_payload";
                        return null;
                    }

                    withJson = withElement.GetRawText();
                }

                string? when = null;
                if (stepElement.TryGetProperty("when", out var whenElement))
                {
                    if (whenElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(whenElement.GetString()))
                    {
                        errorCode = "invalid_when_expression";
                        return null;
                    }

                    when = whenElement.GetString();
                }

                if (!TryParseStepToolArgs(stepElement, kind, out var stepToolArgsJson, out errorCode))
                    return null;

                if (!TryParseToolAllowlist(stepElement, kind, out var toolAllowlist, out errorCode))
                    return null;

                if (!TryParseOutputChoices(stepElement, kind, out var outputChoices, out errorCode))
                    return null;

                if (!TryNormalizeClassifyOptions(kind, withJson, outputChoices, out withJson, out errorCode))
                    return null;

                if (!TryParseClarify(stepElement, withJson, kind, out var clarify, out errorCode))
                    return null;

                if (!TryParseRouteArray(stepElement, out var routes, out var hasRouteArray, out errorCode))
                    return null;

                if (hasRouteArray && HasLegacyRouteObject(withJson))
                {
                    errorCode = "invalid_route";
                    return null;
                }

                if (!TryParseOnFailure(stepElement, out var onFailure, out errorCode))
                    return null;

                if (!TryParseTimeoutSeconds(stepElement, out var timeoutSeconds, out errorCode))
                    return null;

                if (!TryParseRetryPolicy(stepElement, out var retry, out errorCode))
                    return null;

                if (!TryParseOutputContract(stepElement, out var outputContract, out errorCode))
                    return null;

                if (!TryParseSkillExecOptions(stepElement, kind, out var skillExecEntrypoint, out var skillExecArgs, out var skillExecStdin, out var skillExecCwd, out var skillExecParseMode, out errorCode))
                    return null;

                steps.Add(new MetaSkillStepDefinition
                {
                    Id = idElement.GetString()!,
                    Kind = kind,
                    Skill = skill,
                    Tool = tool,
                    SkillExecEntrypoint = skillExecEntrypoint,
                    SkillExecArgs = skillExecArgs,
                    SkillExecStdin = skillExecStdin,
                    SkillExecCwd = skillExecCwd,
                    SkillExecParseMode = skillExecParseMode,
                    WithJson = withJson,
                    When = when,
                    ToolArgsJson = stepToolArgsJson,
                    ToolAllowlist = toolAllowlist,
                    OutputChoices = outputChoices,
                    Clarify = clarify,
                    Routes = routes,
                    DependsOn = dependsOn,
                    OnFailure = onFailure,
                    TimeoutSeconds = timeoutSeconds,
                    Retry = retry,
                    OutputContract = outputContract
                });
            }

            if (!ValidateComposition(steps, out errorCode))
                return null;

            return new MetaSkillComposition
            {
                ToolArgsJson = toolArgsJson,
                Steps = steps
            };
        }
        catch
        {
            errorCode = "invalid_meta_composition";
            return null;
        }
    }

    private static bool ValidateComposition(IReadOnlyList<MetaSkillStepDefinition> steps)
        => ValidateComposition(steps, out _);

    private static bool ValidateComposition(IReadOnlyList<MetaSkillStepDefinition> steps, out string? errorCode)
    {
        errorCode = null;

        if (steps.Count == 0)
        {
            errorCode = "invalid_meta_composition";
            return false;
        }

        var supportedKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "agent",
            "skill_exec",
            "tool_call",
            "llm_chat",
            "llm_classify",
            "user_input"
        };

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in steps)
        {
            if (!ids.Add(step.Id))
            {
                errorCode = "duplicate_step_id";
                return false;
            }

            if (!supportedKinds.Contains(step.Kind))
            {
                errorCode = "unsupported_step_kind";
                return false;
            }

            if (step.Kind.Equals("tool_call", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(step.Tool))
            {
                errorCode = "invalid_step_kind_fields";
                return false;
            }

            if (step.Kind.Equals("tool_call", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(step.Skill))
            {
                errorCode = "invalid_step_kind_fields";
                return false;
            }

            if (step.Kind.Equals("skill_exec", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(step.Skill))
            {
                errorCode = "invalid_step_kind_fields";
                return false;
            }

            if (step.Kind.Equals("skill_exec", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(step.SkillExecEntrypoint))
            {
                errorCode = "invalid_step_kind_fields";
                return false;
            }

            if (step.Kind.Equals("skill_exec", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(step.Tool))
            {
                errorCode = "invalid_step_kind_fields";
                return false;
            }

            if (step.Kind.Equals("agent", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(step.Tool))
            {
                errorCode = "invalid_step_kind_fields";
                return false;
            }

            if (step.Kind.Equals("llm_chat", StringComparison.OrdinalIgnoreCase) &&
                (!string.IsNullOrWhiteSpace(step.Skill) || !string.IsNullOrWhiteSpace(step.Tool)))
            {
                errorCode = "invalid_step_kind_fields";
                return false;
            }

            if (step.Kind.Equals("llm_classify", StringComparison.OrdinalIgnoreCase) &&
                (!string.IsNullOrWhiteSpace(step.Skill) || !string.IsNullOrWhiteSpace(step.Tool)))
            {
                errorCode = "invalid_step_kind_fields";
                return false;
            }

            if (step.Kind.Equals("user_input", StringComparison.OrdinalIgnoreCase) &&
                (!string.IsNullOrWhiteSpace(step.Skill) || !string.IsNullOrWhiteSpace(step.Tool)))
            {
                errorCode = "invalid_step_kind_fields";
                return false;
            }
        }

        foreach (var step in steps)
        {
            if (step.Kind.Equals("llm_classify", StringComparison.OrdinalIgnoreCase) && !ValidateClassifyStep(step.WithJson, ids))
            {
                errorCode = "invalid_classify_step";
                return false;
            }

            foreach (var dependency in step.DependsOn)
            {
                if (!ids.Contains(dependency))
                {
                    errorCode = "invalid_dependency";
                    return false;
                }

                if (string.Equals(step.Id, dependency, StringComparison.OrdinalIgnoreCase))
                {
                    errorCode = "self_dependency";
                    return false;
                }
            }
        }

        if (HasDependencyCycle(steps))
        {
            errorCode = "dependency_cycle";
            return false;
        }

        if (!ValidateRouteArrays(steps, out errorCode))
            return false;

        if (!ValidateFailureBranches(steps, ids, out errorCode))
            return false;

        return true;
    }

    private static bool TryParseOnFailure(JsonElement stepElement, out string? onFailure, out string? errorCode)
    {
        onFailure = null;
        errorCode = null;

        if (!stepElement.TryGetProperty("on_failure", out var onFailureElement) ||
            onFailureElement.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (onFailureElement.ValueKind != JsonValueKind.String)
        {
            errorCode = "invalid_on_failure";
            return false;
        }

        var value = onFailureElement.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            errorCode = "invalid_on_failure";
            return false;
        }

        onFailure = value.Trim();
        return true;
    }

    private static bool TryParseTimeoutSeconds(JsonElement stepElement, out int? timeoutSeconds, out string? errorCode)
    {
        timeoutSeconds = null;
        errorCode = null;

        if (!stepElement.TryGetProperty("timeout_seconds", out var timeoutElement) ||
            timeoutElement.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (timeoutElement.ValueKind != JsonValueKind.Number || !timeoutElement.TryGetInt32(out var parsed) || parsed <= 0)
        {
            errorCode = "invalid_step_timeout";
            return false;
        }

        timeoutSeconds = parsed;
        return true;
    }

    private static bool TryParseRetryPolicy(JsonElement stepElement, out MetaStepRetryPolicy retry, out string? errorCode)
    {
        retry = new MetaStepRetryPolicy();
        errorCode = null;

        if (!stepElement.TryGetProperty("retry", out var retryElement) ||
            retryElement.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (retryElement.ValueKind != JsonValueKind.Object)
        {
            errorCode = "invalid_step_retry";
            return false;
        }

        foreach (var property in retryElement.EnumerateObject())
        {
            if (!IsSupportedRetryProperty(property.Name))
            {
                errorCode = "invalid_step_retry";
                return false;
            }
        }

        var maxAttempts = 1;
        if (retryElement.TryGetProperty("max_attempts", out var maxAttemptsElement))
        {
            if (maxAttemptsElement.ValueKind != JsonValueKind.Number ||
                !maxAttemptsElement.TryGetInt32(out maxAttempts) ||
                maxAttempts is < 1 or > 10)
            {
                errorCode = "invalid_step_retry";
                return false;
            }
        }

        var backoffMs = 0;
        if (retryElement.TryGetProperty("backoff_ms", out var backoffElement))
        {
            if (backoffElement.ValueKind != JsonValueKind.Number ||
                !backoffElement.TryGetInt32(out backoffMs) ||
                backoffMs is < 0 or > 600000)
            {
                errorCode = "invalid_step_retry";
                return false;
            }
        }

        retry = new MetaStepRetryPolicy
        {
            MaxAttempts = maxAttempts,
            BackoffMs = backoffMs
        };
        return true;
    }

    private static bool TryParseOutputContract(JsonElement stepElement, out MetaStepOutputContract outputContract, out string? errorCode)
    {
        outputContract = new MetaStepOutputContract();
        errorCode = null;

        var hasContract = stepElement.TryGetProperty("output_contract", out var contractElement);
        if (!hasContract)
            hasContract = stepElement.TryGetProperty("output_schema", out contractElement);

        if (!hasContract || contractElement.ValueKind == JsonValueKind.Null)
            return true;

        if (contractElement.ValueKind != JsonValueKind.Object)
        {
            errorCode = "invalid_output_contract";
            return false;
        }

        foreach (var property in contractElement.EnumerateObject())
        {
            if (!IsSupportedOutputContractProperty(property.Name))
            {
                errorCode = "invalid_output_contract";
                return false;
            }
        }

        var format = "text";
        if (contractElement.TryGetProperty("format", out var formatElement))
        {
            if (formatElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(formatElement.GetString()))
            {
                errorCode = "invalid_output_contract";
                return false;
            }

            format = formatElement.GetString()!.Trim().ToLowerInvariant();
        }

        if (format is not ("text" or "json"))
        {
            errorCode = "invalid_output_contract";
            return false;
        }

        var requiredProperties = new List<string>();
        if (contractElement.TryGetProperty("required_properties", out var requiredElement))
        {
            if (requiredElement.ValueKind != JsonValueKind.Array)
            {
                errorCode = "invalid_output_contract";
                return false;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in requiredElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    errorCode = "invalid_output_contract";
                    return false;
                }

                var propertyName = item.GetString();
                if (string.IsNullOrWhiteSpace(propertyName) || !seen.Add(propertyName.Trim()))
                {
                    errorCode = "invalid_output_contract";
                    return false;
                }

                requiredProperties.Add(propertyName.Trim());
            }
        }

        if (requiredProperties.Count > 0 && format != "json")
        {
            errorCode = "invalid_output_contract";
            return false;
        }

        outputContract = new MetaStepOutputContract
        {
            Format = format,
            RequiredProperties = requiredProperties
        };
        return true;
    }

    private static bool ValidateFailureBranches(
        IReadOnlyList<MetaSkillStepDefinition> steps,
        ISet<string> knownStepIds,
        out string? errorCode)
    {
        errorCode = null;
        var stepById = steps.ToDictionary(static step => step.Id, StringComparer.OrdinalIgnoreCase);
        var designatedBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fallbackTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.OnFailure))
                continue;

            if (string.Equals(step.Id, step.OnFailure, StringComparison.OrdinalIgnoreCase) ||
                !knownStepIds.Contains(step.OnFailure))
            {
                errorCode = "invalid_on_failure";
                return false;
            }

            var substitute = stepById[step.OnFailure!];
            if (!string.IsNullOrWhiteSpace(substitute.OnFailure) || substitute.DependsOn.Count > 0)
            {
                errorCode = "invalid_on_failure";
                return false;
            }

            if (designatedBy.TryGetValue(step.OnFailure!, out _))
            {
                errorCode = "invalid_on_failure";
                return false;
            }

            designatedBy[step.OnFailure!] = step.Id;
            fallbackTargets.Add(step.OnFailure!);
        }

        foreach (var step in steps)
        {
            foreach (var dependency in step.DependsOn)
            {
                if (!fallbackTargets.Contains(dependency))
                    continue;

                errorCode = "invalid_on_failure";
                return false;
            }

            foreach (var route in step.Routes)
            {
                if (!fallbackTargets.Contains(route.To))
                    continue;

                errorCode = "invalid_on_failure";
                return false;
            }

            if (ContainsFallbackTargetInLegacyClassifyRoutes(step.WithJson, fallbackTargets))
            {
                errorCode = "invalid_on_failure";
                return false;
            }
        }

        return true;
    }

    private static bool ContainsFallbackTargetInLegacyClassifyRoutes(string? withJson, ISet<string> fallbackTargets)
    {
        if (string.IsNullOrWhiteSpace(withJson))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(withJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("route", out var routeElement) ||
                routeElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var routeProperty in routeElement.EnumerateObject())
            {
                if (routeProperty.Value.ValueKind == JsonValueKind.String)
                {
                    var targetStep = routeProperty.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(targetStep) && fallbackTargets.Contains(targetStep))
                        return true;

                    continue;
                }

                if (routeProperty.Value.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var target in routeProperty.Value.EnumerateArray())
                {
                    if (target.ValueKind != JsonValueKind.String)
                        continue;

                    var targetStep = target.GetString();
                    if (!string.IsNullOrWhiteSpace(targetStep) && fallbackTargets.Contains(targetStep))
                        return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool TryParseStringArrayProperty(
        JsonElement parentElement,
        string propertyName,
        out IReadOnlyList<string> values,
        out string? errorCode)
    {
        errorCode = null;
        values = [];

        if (!parentElement.TryGetProperty(propertyName, out var propertyElement))
            return true;

        if (propertyElement.ValueKind != JsonValueKind.Array)
        {
            errorCode = "invalid_meta_composition";
            return false;
        }

        var items = new List<string>();
        foreach (var itemElement in propertyElement.EnumerateArray())
        {
            if (itemElement.ValueKind != JsonValueKind.String)
            {
                errorCode = "invalid_meta_composition";
                return false;
            }

            var item = itemElement.GetString();
            if (string.IsNullOrWhiteSpace(item))
            {
                errorCode = "invalid_meta_composition";
                return false;
            }

            items.Add(item);
        }

        values = items;
        return true;
    }

    private static bool TryParseToolAllowlist(
        JsonElement stepElement,
        string? stepKind,
        out IReadOnlyList<string> toolAllowlist,
        out string? errorCode)
    {
        toolAllowlist = [];
        errorCode = null;

        if (!stepElement.TryGetProperty("tool_allowlist", out var toolAllowlistElement))
            return true;

        if (!IsToolStepKind(stepKind))
        {
            errorCode = "invalid_tool_allowlist";
            return false;
        }

        if (!TryParseStringArrayProperty(stepElement, "tool_allowlist", out var values, out _))
        {
            errorCode = "invalid_tool_allowlist";
            return false;
        }

        if (values.Count == 0)
        {
            errorCode = "invalid_tool_allowlist";
            return false;
        }

        toolAllowlist = values;
        return true;
    }

    private static bool TryParseStepToolArgs(
        JsonElement stepElement,
        string? stepKind,
        out string? toolArgsJson,
        out string? errorCode)
    {
        toolArgsJson = null;
        errorCode = null;

        if (!stepElement.TryGetProperty("tool_args", out var stepToolArgsElement))
            return true;

        if (!IsToolStepKind(stepKind) || stepToolArgsElement.ValueKind != JsonValueKind.Object)
        {
            errorCode = "invalid_tool_args";
            return false;
        }

        toolArgsJson = stepToolArgsElement.GetRawText();
        return true;
    }

    private static bool TryParseOutputChoices(
        JsonElement stepElement,
        string? stepKind,
        out IReadOnlyList<string> outputChoices,
        out string? errorCode)
    {
        outputChoices = [];
        errorCode = null;

        if (!stepElement.TryGetProperty("output_choices", out _))
            return true;

        if (!IsOutputChoicesStepKind(stepKind))
        {
            errorCode = "invalid_output_choices";
            return false;
        }

        if (!TryParseStringArrayProperty(stepElement, "output_choices", out var values, out _))
        {
            errorCode = "invalid_output_choices";
            return false;
        }

        if (values.Count == 0)
        {
            errorCode = "invalid_output_choices";
            return false;
        }

        outputChoices = values;
        return true;
    }

    private static bool TryNormalizeClassifyOptions(
        string? stepKind,
        string? withJson,
        IReadOnlyList<string> outputChoices,
        out string? normalizedWithJson,
        out string? errorCode)
    {
        normalizedWithJson = withJson;
        errorCode = null;

        if (!string.Equals(stepKind, "llm_classify", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.IsNullOrWhiteSpace(withJson))
        {
            if (outputChoices.Count == 0)
                return true;

            normalizedWithJson = BuildClassifyOptionsWithJson(outputChoices);
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(withJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                errorCode = "invalid_meta_composition";
                return false;
            }

            if (doc.RootElement.TryGetProperty("options", out _))
                return true;

            if (outputChoices.Count == 0)
                return true;

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in doc.RootElement.EnumerateObject())
                    property.WriteTo(writer);

                writer.WritePropertyName("options");
                writer.WriteStartArray();
                foreach (var option in outputChoices)
                    writer.WriteStringValue(option);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            normalizedWithJson = Encoding.UTF8.GetString(stream.ToArray());
            return true;
        }
        catch (JsonException)
        {
            errorCode = "invalid_meta_composition";
            return false;
        }
    }

    private static string BuildClassifyOptionsWithJson(IReadOnlyList<string> outputChoices)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("options");
            writer.WriteStartArray();
            foreach (var option in outputChoices)
                writer.WriteStringValue(option);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool TryParseSkillExecOptions(
        JsonElement stepElement,
        string? stepKind,
        out string? entrypoint,
        out IReadOnlyList<string> args,
        out string? stdin,
        out string? cwd,
        out string? parseMode,
        out string? errorCode)
    {
        entrypoint = null;
        args = [];
        stdin = null;
        cwd = null;
        parseMode = null;
        errorCode = null;

        var isSkillExec = string.Equals(stepKind, "skill_exec", StringComparison.OrdinalIgnoreCase);

        if (stepElement.TryGetProperty("entrypoint", out var entrypointElement))
        {
            if (!isSkillExec || entrypointElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(entrypointElement.GetString()))
            {
                errorCode = "invalid_skill_exec";
                return false;
            }

            entrypoint = entrypointElement.GetString()!.Trim();
        }

        if (stepElement.TryGetProperty("args", out _))
        {
            if (!isSkillExec || !TryParseStringArrayProperty(stepElement, "args", out args, out _))
            {
                errorCode = "invalid_skill_exec";
                return false;
            }
        }

        if (stepElement.TryGetProperty("stdin", out var stdinElement))
        {
            if (!isSkillExec || stdinElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(stdinElement.GetString()))
            {
                errorCode = "invalid_skill_exec";
                return false;
            }

            stdin = stdinElement.GetString();
        }

        if (stepElement.TryGetProperty("cwd", out var cwdElement))
        {
            if (!isSkillExec || cwdElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(cwdElement.GetString()))
            {
                errorCode = "invalid_skill_exec";
                return false;
            }

            cwd = cwdElement.GetString()!.Trim();
            if (Path.IsPathRooted(cwd) || cwd.Contains("..", StringComparison.Ordinal))
            {
                errorCode = "invalid_skill_exec";
                return false;
            }
        }

        if (stepElement.TryGetProperty("parse_mode", out var parseModeElement))
        {
            if (!isSkillExec || parseModeElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(parseModeElement.GetString()))
            {
                errorCode = "invalid_skill_exec";
                return false;
            }

            parseMode = parseModeElement.GetString()!.Trim().ToLowerInvariant();
            if (parseMode is not ("text" or "json"))
            {
                errorCode = "invalid_skill_exec";
                return false;
            }
        }

        return true;
    }

    private static bool TryParseRouteArray(
        JsonElement stepElement,
        out IReadOnlyList<MetaRouteDefinition> routes,
        out bool hasRouteArray,
        out string? errorCode)
    {
        errorCode = null;
        routes = [];
        hasRouteArray = false;

        if (!stepElement.TryGetProperty("route", out var routesElement))
            return true;

        hasRouteArray = true;

        if (routesElement.ValueKind != JsonValueKind.Array)
        {
            errorCode = "invalid_route";
            return false;
        }

        if (routesElement.GetArrayLength() == 0)
        {
            errorCode = "invalid_route";
            return false;
        }

        var parsedRoutes = new List<MetaRouteDefinition>();
        foreach (var routeElement in routesElement.EnumerateArray())
        {
            if (routeElement.ValueKind != JsonValueKind.Object)
            {
                errorCode = "invalid_route";
                return false;
            }

            foreach (var property in routeElement.EnumerateObject())
            {
                if (!IsSupportedRouteProperty(property.Name))
                {
                    errorCode = "invalid_route";
                    return false;
                }
            }

            string? when = null;
            if (routeElement.TryGetProperty("when", out var whenElement))
            {
                if (whenElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(whenElement.GetString()))
                {
                    errorCode = "invalid_when_expression";
                    return false;
                }

                when = whenElement.GetString();
            }

            if (!routeElement.TryGetProperty("to", out var toElement) || toElement.ValueKind != JsonValueKind.String)
            {
                errorCode = "invalid_route";
                return false;
            }

            var target = toElement.GetString();
            if (string.IsNullOrWhiteSpace(target))
            {
                errorCode = "invalid_route";
                return false;
            }

            parsedRoutes.Add(new MetaRouteDefinition
            {
                When = when,
                To = target
            });
        }

        routes = parsedRoutes;
        return true;
    }

    private static bool TryParseClarify(
        JsonElement stepElement,
        string? withJson,
        string? stepKind,
        out MetaClarifySchema? clarify,
        out string? errorCode)
    {
        errorCode = null;
        clarify = null;

        if (!TryGetClarifyElement(stepElement, withJson, out var clarifyElement, out errorCode))
            return false;

        if (clarifyElement is null)
            return true;

        if (!string.Equals(stepKind, "user_input", StringComparison.OrdinalIgnoreCase))
        {
            errorCode = "invalid_clarify_schema";
            return false;
        }

        if (clarifyElement.Value.ValueKind != JsonValueKind.Object)
        {
            errorCode = "invalid_clarify_schema";
            return false;
        }

        foreach (var property in clarifyElement.Value.EnumerateObject())
        {
            if (!IsSupportedClarifyProperty(property.Name))
            {
                errorCode = "invalid_clarify_schema";
                return false;
            }
        }

        var mode = "chat";
        if (clarifyElement.Value.TryGetProperty("mode", out var modeElement))
        {
            if (modeElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(modeElement.GetString()))
            {
                errorCode = "invalid_clarify_schema";
                return false;
            }

            mode = modeElement.GetString() ?? "chat";
            if (!string.Equals(mode, "chat", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(mode, "form", StringComparison.OrdinalIgnoreCase))
            {
                errorCode = "invalid_clarify_schema";
                return false;
            }
        }

        var extractNaturalLanguage = false;
        if (clarifyElement.Value.TryGetProperty("extract_natural_language", out var extractElement))
        {
            if (extractElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                errorCode = "invalid_clarify_schema";
                return false;
            }

            extractNaturalLanguage = extractElement.ValueKind == JsonValueKind.True;
        }

        if (!TryParseStringArrayProperty(clarifyElement.Value, "cancel_words", out var cancelWords, out _))
        {
            errorCode = "invalid_clarify_schema";
            return false;
        }

        int? timeoutSeconds = null;
        if (clarifyElement.Value.TryGetProperty("timeout_seconds", out var timeoutElement))
        {
            if (timeoutElement.ValueKind != JsonValueKind.Number || !timeoutElement.TryGetInt32(out var parsedTimeout) || parsedTimeout <= 0)
            {
                errorCode = "invalid_clarify_schema";
                return false;
            }

            timeoutSeconds = parsedTimeout;
        }

        string? skipIf = null;
        if (clarifyElement.Value.TryGetProperty("skip_if", out var skipIfElement))
        {
            if (skipIfElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(skipIfElement.GetString()))
            {
                errorCode = "invalid_clarify_schema";
                return false;
            }

            skipIf = skipIfElement.GetString();
        }

        var fields = new List<MetaClarifyField>();
        var fieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (clarifyElement.Value.TryGetProperty("fields", out var fieldsElement))
        {
            if (fieldsElement.ValueKind != JsonValueKind.Array)
            {
                errorCode = "invalid_clarify_schema";
                return false;
            }

            foreach (var fieldElement in fieldsElement.EnumerateArray())
            {
                if (fieldElement.ValueKind != JsonValueKind.Object)
                {
                    errorCode = "invalid_clarify_schema";
                    return false;
                }

                foreach (var property in fieldElement.EnumerateObject())
                {
                    if (!IsSupportedClarifyFieldProperty(property.Name))
                    {
                        errorCode = "invalid_clarify_schema";
                        return false;
                    }
                }

                if (!fieldElement.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
                {
                    errorCode = "invalid_clarify_schema";
                    return false;
                }

                if (!fieldElement.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
                {
                    errorCode = "invalid_clarify_schema";
                    return false;
                }

                if (!TryParseStringArrayProperty(fieldElement, "options", out var options, out _))
                {
                    errorCode = "invalid_clarify_schema";
                    return false;
                }

                int? minLength = null;
                if (fieldElement.TryGetProperty("min_length", out var minLengthElement))
                {
                    if (minLengthElement.ValueKind != JsonValueKind.Number || !minLengthElement.TryGetInt32(out var parsedMinLength) || parsedMinLength < 0)
                    {
                        errorCode = "invalid_clarify_schema";
                        return false;
                    }

                    minLength = parsedMinLength;
                }

                int? maxLength = null;
                if (fieldElement.TryGetProperty("max_length", out var maxLengthElement))
                {
                    if (maxLengthElement.ValueKind != JsonValueKind.Number || !maxLengthElement.TryGetInt32(out var parsedMaxLength) || parsedMaxLength < 0)
                    {
                        errorCode = "invalid_clarify_schema";
                        return false;
                    }

                    maxLength = parsedMaxLength;
                }

                double? min = null;
                if (fieldElement.TryGetProperty("min", out var minElement))
                {
                    if (minElement.ValueKind != JsonValueKind.Number || !minElement.TryGetDouble(out var parsedMin))
                    {
                        errorCode = "invalid_clarify_schema";
                        return false;
                    }

                    min = parsedMin;
                }

                double? max = null;
                if (fieldElement.TryGetProperty("max", out var maxElement))
                {
                    if (maxElement.ValueKind != JsonValueKind.Number || !maxElement.TryGetDouble(out var parsedMax))
                    {
                        errorCode = "invalid_clarify_schema";
                        return false;
                    }

                    max = parsedMax;
                }

                var fieldName = nameElement.GetString();
                var fieldType = typeElement.GetString();
                if (string.IsNullOrWhiteSpace(fieldName) || string.IsNullOrWhiteSpace(fieldType))
                {
                    errorCode = "invalid_clarify_schema";
                    return false;
                }

                if (!fieldNames.Add(fieldName))
                {
                    errorCode = "invalid_clarify_schema";
                    return false;
                }

                if (fieldElement.TryGetProperty("required", out var requiredElement) &&
                    requiredElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    errorCode = "invalid_clarify_schema";
                    return false;
                }

                if (!string.Equals(fieldType, "string", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(fieldType, "integer", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(fieldType, "number", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(fieldType, "boolean", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(fieldType, "enum", StringComparison.OrdinalIgnoreCase))
                {
                    errorCode = "invalid_clarify_schema";
                    return false;
                }

                if (string.Equals(fieldType, "enum", StringComparison.OrdinalIgnoreCase) && options.Count == 0)
                {
                    errorCode = "invalid_clarify_schema";
                    return false;
                }

                if (!ValidateClarifyFieldConstraints(fieldType, options, minLength, maxLength, min, max, out errorCode))
                {
                    return false;
                }

                JsonElement? defaultValue = null;
                if (fieldElement.TryGetProperty("default", out var defaultElement))
                {
                    if (!IsValidClarifyDefaultValue(fieldType, options, minLength, maxLength, min, max, defaultElement))
                    {
                        errorCode = "invalid_clarify_schema";
                        return false;
                    }

                    defaultValue = defaultElement.Clone();
                }

                fields.Add(new MetaClarifyField
                {
                    Name = fieldName,
                    Type = fieldType,
                    Required = fieldElement.TryGetProperty("required", out var requiredFlagElement) && requiredFlagElement.ValueKind == JsonValueKind.True,
                    DefaultValue = defaultValue,
                    Options = options,
                    MinLength = minLength,
                    MaxLength = maxLength,
                    Min = min,
                    Max = max
                });
            }
        }

        if (string.Equals(mode, "form", StringComparison.OrdinalIgnoreCase) && fields.Count == 0)
        {
            errorCode = "invalid_clarify_schema";
            return false;
        }

        clarify = new MetaClarifySchema
        {
            Mode = mode,
            ExtractNaturalLanguage = extractNaturalLanguage,
            Fields = fields,
            CancelWords = cancelWords,
            SkipIf = skipIf,
            TimeoutSeconds = timeoutSeconds
        };

        return true;
    }

    private static bool TryGetClarifyElement(
        JsonElement stepElement,
        string? withJson,
        out JsonElement? clarifyElement,
        out string? errorCode)
    {
        clarifyElement = null;
        errorCode = null;

        var hasDirectClarify = stepElement.TryGetProperty("clarify", out var directClarifyElement);
        JsonElement? withClarifyElement = null;

        if (!string.IsNullOrWhiteSpace(withJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(withJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("clarify", out var parsedWithClarifyElement))
                {
                    withClarifyElement = parsedWithClarifyElement.Clone();
                }
            }
            catch
            {
                errorCode = "invalid_clarify_schema";
                return false;
            }
        }

        if (hasDirectClarify)
        {
            clarifyElement = directClarifyElement;
            return true;
        }

        clarifyElement = withClarifyElement;
        return true;
    }

    private static bool ValidateClarifyFieldConstraints(
        string fieldType,
        IReadOnlyList<string> options,
        int? minLength,
        int? maxLength,
        double? min,
        double? max,
        out string? errorCode)
    {
        errorCode = null;

        var isString = string.Equals(fieldType, "string", StringComparison.OrdinalIgnoreCase);
        var isInteger = string.Equals(fieldType, "integer", StringComparison.OrdinalIgnoreCase);
        var isNumber = string.Equals(fieldType, "number", StringComparison.OrdinalIgnoreCase);
        var isEnum = string.Equals(fieldType, "enum", StringComparison.OrdinalIgnoreCase);

        if ((!isString && (minLength.HasValue || maxLength.HasValue)) ||
            (!(isInteger || isNumber) && (min.HasValue || max.HasValue)) ||
            (!isEnum && options.Count > 0))
        {
            errorCode = "invalid_clarify_schema";
            return false;
        }

        if (minLength.HasValue && maxLength.HasValue && minLength.Value > maxLength.Value)
        {
            errorCode = "invalid_clarify_schema";
            return false;
        }

        if (min.HasValue && max.HasValue && min.Value > max.Value)
        {
            errorCode = "invalid_clarify_schema";
            return false;
        }

        return true;
    }

    private static bool IsValidClarifyDefaultValue(
        string fieldType,
        IReadOnlyList<string> options,
        int? minLength,
        int? maxLength,
        double? min,
        double? max,
        JsonElement defaultElement)
    {
        if (string.Equals(fieldType, "string", StringComparison.OrdinalIgnoreCase))
        {
            if (defaultElement.ValueKind != JsonValueKind.String)
                return false;

            var value = defaultElement.GetString() ?? string.Empty;
            return (!minLength.HasValue || value.Length >= minLength.Value) &&
                   (!maxLength.HasValue || value.Length <= maxLength.Value);
        }

        if (string.Equals(fieldType, "integer", StringComparison.OrdinalIgnoreCase))
        {
            if (defaultElement.ValueKind != JsonValueKind.Number || !defaultElement.TryGetInt64(out var intValue))
                return false;

            return (!min.HasValue || intValue >= min.Value) &&
                   (!max.HasValue || intValue <= max.Value);
        }

        if (string.Equals(fieldType, "number", StringComparison.OrdinalIgnoreCase))
        {
            if (defaultElement.ValueKind != JsonValueKind.Number || !defaultElement.TryGetDouble(out var doubleValue))
                return false;

            return (!min.HasValue || doubleValue >= min.Value) &&
                   (!max.HasValue || doubleValue <= max.Value);
        }

        if (string.Equals(fieldType, "boolean", StringComparison.OrdinalIgnoreCase))
            return defaultElement.ValueKind is JsonValueKind.True or JsonValueKind.False;

        if (string.Equals(fieldType, "enum", StringComparison.OrdinalIgnoreCase))
        {
            if (defaultElement.ValueKind != JsonValueKind.String)
                return false;

            var defaultText = defaultElement.GetString();
            return !string.IsNullOrWhiteSpace(defaultText) && options.Contains(defaultText, StringComparer.Ordinal);
        }

        return false;
    }

    private static bool IsSupportedClarifyProperty(string propertyName)
        => propertyName is "mode" or "extract_natural_language" or "cancel_words" or "skip_if" or "timeout_seconds" or "fields";

    private static bool IsSupportedClarifyFieldProperty(string propertyName)
        => propertyName is "name" or "type" or "required" or "options" or "min_length" or "max_length" or "min" or "max" or "default";

    private static bool IsSupportedRetryProperty(string propertyName)
        => propertyName is "max_attempts" or "backoff_ms";

    private static bool IsSupportedOutputContractProperty(string propertyName)
        => propertyName is "format" or "required_properties";

    private static bool IsSupportedRouteProperty(string propertyName)
        => propertyName is "when" or "to";

    private static bool HasLegacyRouteObject(string? withJson)
    {
        if (string.IsNullOrWhiteSpace(withJson))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(withJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("route", out var routeElement) &&
                   routeElement.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            return false;
        }
    }

    private static bool ValidateRouteArrays(IReadOnlyList<MetaSkillStepDefinition> steps, out string? errorCode)
    {
        errorCode = null;
        var ids = steps.Select(static step => step.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var step in steps)
        {
            var fallbackCount = 0;
            for (var i = 0; i < step.Routes.Count; i++)
            {
                var route = step.Routes[i];
                if (!ids.Contains(route.To))
                {
                    errorCode = "invalid_route_target";
                    return false;
                }

                if (string.Equals(route.To, step.Id, StringComparison.OrdinalIgnoreCase))
                {
                    errorCode = "invalid_route_scope";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(route.When) && ++fallbackCount > 1)
                {
                    errorCode = "invalid_route_fallback";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(route.When) && i != step.Routes.Count - 1)
                {
                    errorCode = "invalid_route_fallback";
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsToolStepKind(string? stepKind)
        => string.Equals(stepKind, "tool_call", StringComparison.OrdinalIgnoreCase);

    private static bool IsOutputChoicesStepKind(string? stepKind)
        => string.Equals(stepKind, "agent", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(stepKind, "skill_exec", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(stepKind, "tool_call", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(stepKind, "llm_chat", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(stepKind, "llm_classify", StringComparison.OrdinalIgnoreCase);

    private static bool ValidateClassifyStep(string? withJson, ISet<string> knownStepIds)
    {
        if (string.IsNullOrWhiteSpace(withJson))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(withJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            if (!doc.RootElement.TryGetProperty("options", out var optionsElement) ||
                optionsElement.ValueKind != JsonValueKind.Array)
                return false;

            var hasOption = false;
            var options = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var option in optionsElement.EnumerateArray())
            {
                if (option.ValueKind != JsonValueKind.String)
                    return false;

                var optionValue = option.GetString();
                if (string.IsNullOrWhiteSpace(optionValue))
                    return false;

                options.Add(optionValue);
                hasOption = true;
            }

            if (!hasOption)
                return false;

            if (!doc.RootElement.TryGetProperty("route", out var routeElement))
                return true;

            if (routeElement.ValueKind != JsonValueKind.Object)
                return false;

            foreach (var routeProperty in routeElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(routeProperty.Name))
                    return false;

                if (!options.Contains(routeProperty.Name))
                    return false;

                if (routeProperty.Value.ValueKind == JsonValueKind.String)
                {
                    var targetStep = routeProperty.Value.GetString();
                    if (string.IsNullOrWhiteSpace(targetStep))
                        return false;

                    if (!knownStepIds.Contains(targetStep))
                        return false;

                    continue;
                }

                if (routeProperty.Value.ValueKind != JsonValueKind.Array)
                    return false;

                foreach (var target in routeProperty.Value.EnumerateArray())
                {
                    if (target.ValueKind != JsonValueKind.String)
                        return false;

                    var targetStep = target.GetString();
                    if (string.IsNullOrWhiteSpace(targetStep))
                        return false;

                    if (!knownStepIds.Contains(targetStep))
                        return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool ValidateFinalTextMode(string? finalTextMode, IReadOnlyList<MetaSkillStepDefinition> steps)
    {
        if (string.IsNullOrWhiteSpace(finalTextMode))
            return true;

        var mode = finalTextMode.Trim();
        if (mode.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
            mode.Equals("raw", StringComparison.OrdinalIgnoreCase) ||
            mode.Equals("structured", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!mode.StartsWith("step:", StringComparison.OrdinalIgnoreCase))
            return false;

        var stepId = mode[5..].Trim();
        if (string.IsNullOrWhiteSpace(stepId))
            return false;

        return steps.Any(step => string.Equals(step.Id, stepId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasDependencyCycle(IReadOnlyList<MetaSkillStepDefinition> steps)
    {
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stepById = steps.ToDictionary(static step => step.Id, StringComparer.OrdinalIgnoreCase);

        bool Dfs(string stepId)
        {
            if (state.TryGetValue(stepId, out var currentState))
                return currentState == 1;

            state[stepId] = 1;
            foreach (var dependency in stepById[stepId].DependsOn)
            {
                if (Dfs(dependency))
                    return true;
            }

            state[stepId] = 2;
            return false;
        }

        foreach (var step in steps)
        {
            if (state.TryGetValue(step.Id, out var currentState) && currentState == 2)
                continue;

            if (Dfs(step.Id))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Scan a skill directory for auxiliary resources under <c>references/</c> and <c>scripts/</c>.
    /// Returns an empty list if the skill directory does not exist (e.g. in unit tests).
    /// </summary>
    internal static IReadOnlyList<SkillResource> ScanSkillResources(string skillDir)
    {
        if (string.IsNullOrWhiteSpace(skillDir) || !TryDirectoryExists(skillDir))
            return [];

        var list = new List<SkillResource>();
        AppendResourcesFromSubdir(list, skillDir, "references", SkillResourceKind.Reference);
        AppendResourcesFromSubdir(list, skillDir, "scripts", SkillResourceKind.Script);
        return list;
    }

    private static void AppendResourcesFromSubdir(
        List<SkillResource> sink,
        string skillDir,
        string subDir,
        SkillResourceKind kind)
    {
        var dir = Path.Combine(skillDir, subDir);
        if (!TryDirectoryExists(dir))
            return;

        try
        {
            var enumerationOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden
            };

            foreach (var file in Directory.EnumerateFiles(dir, "*", enumerationOptions))
            {
                string relative;
                try
                {
                    relative = Path.GetRelativePath(skillDir, file).Replace('\\', '/');
                }
                catch (Exception ex) when (IsPathException(ex))
                {
                    continue;
                }

                sink.Add(new SkillResource
                {
                    Name = Path.GetFileName(file),
                    RelativePath = relative,
                    AbsolutePath = Path.GetFullPath(file),
                    Kind = kind
                });
            }
        }
        catch (Exception ex) when (IsPathException(ex))
        {
            // Inaccessible subdirectory: ignore, resources stay as already-collected.
        }
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

            JsonElement oc;
            if (!doc.RootElement.TryGetProperty("openclaw", out oc) &&
                !doc.RootElement.TryGetProperty("opensquilla", out oc))
            {
                return new SkillMetadata();
            }

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
            if (oc.TryGetProperty("risk", out var risk) && risk.ValueKind == JsonValueKind.String)
                meta.Risk = risk.GetString();
            if (oc.TryGetProperty("capabilities", out var capabilities))
                meta.Capabilities = ReadStringArray(capabilities);

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

        if (skill.Kind == SkillKind.Meta && config.MetaSkill.Enabled)
        {
            if (config.MetaSkill.AllowedRiskLevels.Length > 0)
            {
                var risk = skill.Metadata.Risk ?? string.Empty;
                if (!config.MetaSkill.AllowedRiskLevels.Contains(risk, StringComparer.OrdinalIgnoreCase))
                {
                    logger.LogDebug("Skill '{Name}' skipped (risk '{Risk}' not allowed)", skill.Name, string.IsNullOrWhiteSpace(risk) ? "(unset)" : risk);
                    return false;
                }
            }

            if (config.MetaSkill.RequiredCapabilities.Length > 0)
            {
                var declaredCapabilities = new HashSet<string>(skill.Metadata.Capabilities, StringComparer.OrdinalIgnoreCase);
                foreach (var requiredCapability in config.MetaSkill.RequiredCapabilities)
                {
                    if (declaredCapabilities.Contains(requiredCapability))
                        continue;

                    logger.LogDebug("Skill '{Name}' skipped (required capability '{Capability}' missing)", skill.Name, requiredCapability);
                    return false;
                }
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

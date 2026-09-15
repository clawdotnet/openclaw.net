using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Models;
using Microsoft.Extensions.Logging;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

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
    /// Owner-qualified ClawHub paths (for example @owner/skill/SKILL.md) are
    /// always recognized. When <paramref name="scanSubdirectories"/> is true,
    /// also descends into arbitrary nested subdirectories so parent skill
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
            if (source == SkillSource.Plugin &&
                string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(rootDir)), "commands", StringComparison.OrdinalIgnoreCase))
            {
                ScanBundleCommandFiles(rootDir, results, logger);
            }

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

            var enumerationOptions = new EnumerationOptions
            {
                RecurseSubdirectories = scanSubdirectories,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            var topLevelDirectories = EnumerateSkillDirectories(rootDir, "*", enumerationOptions, logger);
            var ownerQualifiedDirectories = scanSubdirectories
                ? []
                : EnumerateSkillDirectories(rootDir, "@*", enumerationOptions, logger)
                    .SelectMany(ownerDir => EnumerateSkillDirectories(ownerDir, "*", enumerationOptions, logger))
                    .ToArray();
            var duplicateOwnerSlugs = ownerQualifiedDirectories
                .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Where(static group => group.Count() > 1)
                .Select(static group => group.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var duplicateSlug in duplicateOwnerSlugs.OrderBy(static slug => slug, StringComparer.OrdinalIgnoreCase))
                logger.LogWarning("Skipping duplicate owner-qualified skill slug '{Slug}' under {Dir}", duplicateSlug, rootDir);

            var skillDirectories = topLevelDirectories
                .Concat(ownerQualifiedDirectories.Where(dir => !duplicateOwnerSlugs.Contains(Path.GetFileName(dir))))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static dir => dir, StringComparer.Ordinal);

            foreach (var skillDir in skillDirectories)
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

    private static void ScanBundleCommandFiles(
        string commandRoot,
        IDictionary<string, SkillDefinition> results,
        ILogger logger)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (var commandFile in Directory.EnumerateFiles(commandRoot, "*.md", options)
                     .Where(static path => !string.Equals(
                         Path.GetFileName(path),
                         "SKILL.md",
                         StringComparison.OrdinalIgnoreCase))
                     .OrderBy(static path => path, StringComparer.Ordinal))
        {
            try
            {
                var commandName = Path.GetFileNameWithoutExtension(commandFile);
                var content = File.ReadAllText(commandFile);
                var normalized = NormalizeBundleCommandContent(content, commandName);
                var commandDir = Path.GetDirectoryName(commandFile) ?? commandRoot;
                var skill = ParseSkillContent(normalized, commandDir, SkillSource.Plugin);
                if (skill is null)
                {
                    logger.LogWarning("Failed to map bundle command at {Path} into a skill", commandFile);
                    continue;
                }

                if (results.ContainsKey(skill.Name))
                {
                    logger.LogWarning(
                        "Bundle command at {Path} overwrites existing skill '{Name}'",
                        commandFile,
                        skill.Name);
                }

                results[skill.Name] = skill;
            }
            catch (Exception ex) when (IsPathException(ex))
            {
                logger.LogWarning(ex, "Skipping inaccessible bundle command at {Path}", commandFile);
            }
        }
    }

    private static string NormalizeBundleCommandContent(string content, string commandName)
    {
        if (content.StartsWith("---", StringComparison.Ordinal) &&
            content.IndexOf("\n---", 3, StringComparison.Ordinal) >= 0)
        {
            var frontmatterEnd = content.IndexOf("\n---", 3, StringComparison.Ordinal);
            var frontmatter = content[3..frontmatterEnd];
            if (!frontmatter.Split('\n').Any(static line =>
                    line.TrimEnd('\r').StartsWith("name:", StringComparison.OrdinalIgnoreCase)))
            {
                var firstNewline = content.IndexOf('\n');
                return firstNewline >= 0
                    ? content.Insert(firstNewline + 1, $"name: {commandName}\n")
                    : content;
            }

            return content;
        }

        return $"""
            ---
            name: {commandName}
            description: Imported bundle command {commandName}
            user-invocable: true
            ---
            {content}
            """;
    }

    private static string[] EnumerateSkillDirectories(
        string rootDir,
        string searchPattern,
        EnumerationOptions options,
        ILogger logger)
    {
        try
        {
            return Directory.EnumerateDirectories(rootDir, searchPattern, options).ToArray();
        }
        catch (Exception ex) when (IsPathException(ex))
        {
            logger.LogWarning(ex, "Skipping inaccessible skill directory {Dir}", rootDir);
            return [];
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

        var artifactContract = TryLoadArtifactContract(skillDir, null);
        var projectionContractDiscovery = TryLoadProjectionContracts(skillDir, null);

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
            Resources = resources,
            ArtifactContract = artifactContract,
            ProjectionContracts = projectionContractDiscovery.Contracts,
            ProjectionDiscovery = projectionContractDiscovery.Discovery
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

        try
        {
            using var reader = new StringReader(yaml);
            var stream = new YamlStream();
            stream.Load(reader);

            if (stream.Documents.Count == 0)
                return false;

            var root = stream.Documents[0].RootNode;
            if (root is null)
                return false;

            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output))
            {
                if (!WriteYamlNode(writer, root, depth: 0, ancestors: new HashSet<YamlNode>(ReferenceEqualityComparer.Instance)))
                {
                    // Abandon the partial document. Reset() clears the writer's
                    // state so Dispose() does not throw on the unbalanced output.
                    writer.Reset();
                    return false;
                }
            }

            json = Encoding.UTF8.GetString(output.ToArray());
            return true;
        }
        catch (YamlException)
        {
            return false;
        }
    }

    private const int MaxYamlNodeDepth = 64;

    private static bool WriteYamlNode(
        Utf8JsonWriter writer,
        YamlNode node,
        int depth,
        HashSet<YamlNode> ancestors)
    {
        // Guard against absurd nesting and anchor cycles (a node that
        // references itself would otherwise recurse forever).
        if (depth >= MaxYamlNodeDepth || !ancestors.Add(node))
            return false;

        var ok = true;
        switch (node)
        {
            case YamlMappingNode mapping:
                writer.WriteStartObject();
                foreach (var entry in mapping.Children)
                {
                    var keyNode = entry.Key;
                    var key = keyNode is YamlScalarNode keyScalar && keyScalar.Value is not null
                        ? keyScalar.Value
                        : keyNode.ToString() ?? string.Empty;
                    writer.WritePropertyName(key);
                    if (!WriteYamlNode(writer, entry.Value, depth + 1, ancestors))
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok)
                    writer.WriteEndObject();
                break;
            case YamlSequenceNode sequence:
                writer.WriteStartArray();
                foreach (var item in sequence.Children)
                {
                    if (!WriteYamlNode(writer, item, depth + 1, ancestors))
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok)
                    writer.WriteEndArray();
                break;
            case YamlScalarNode scalar:
                WriteYamlScalar(writer, scalar);
                break;
            default:
                writer.WriteNullValue();
                break;
        }

        ancestors.Remove(node);
        return ok;
    }

    private static void WriteYamlScalar(Utf8JsonWriter writer, YamlScalarNode scalar)
    {
        var value = scalar.Value;
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        // Quoted and block scalars are always JSON strings; only plain scalars
        // get boolean/number/null-like inference.
        if (scalar.Style != ScalarStyle.Plain)
        {
            writer.WriteStringValue(value);
            return;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            writer.WriteStringValue(value);
            return;
        }

        if (trimmed.Equals("null", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("~", StringComparison.Ordinal))
        {
            writer.WriteNullValue();
            return;
        }

        if (bool.TryParse(trimmed, out var boolValue))
        {
            writer.WriteBooleanValue(boolValue);
            return;
        }

        if (long.TryParse(trimmed, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var longValue))
        {
            writer.WriteNumberValue(longValue);
            return;
        }

        if (double.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var doubleValue))
        {
            writer.WriteNumberValue(doubleValue);
            return;
        }

        writer.WriteStringValue(value);
    }

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

    private static int GetIndent(string line)
    {
        var indent = 0;
        while (indent < line.Length && line[indent] == ' ')
            indent++;

        return indent;
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

                if (!TryParseCapabilityRef(stepElement, onFailure, out var capabilityRef, out errorCode))
                    return null;

                if (!TryParseTimeoutSeconds(stepElement, out var timeoutSeconds, out errorCode))
                    return null;

                if (!TryParseRetryPolicy(stepElement, out var retry, out errorCode))
                    return null;

                if (!TryParseOutputContract(stepElement, out var outputContract, out errorCode))
                    return null;

                if (!TryParseSkillExecOptions(stepElement, kind, out var skillExecEntrypoint, out var skillExecArgs, out var skillExecStdin, out var skillExecCwd, out var skillExecParseMode, out errorCode))
                    return null;

                // ── fan_out fields ──
                string? iterable = null;
                if (stepElement.TryGetProperty("iterable", out var iterableElement) && iterableElement.ValueKind == JsonValueKind.String)
                    iterable = iterableElement.GetString();

                var fanOutMaxConcurrency = 4;
                if (stepElement.TryGetProperty("fan_out_max_concurrency", out var concurrencyElement) && concurrencyElement.ValueKind == JsonValueKind.Number)
                    fanOutMaxConcurrency = concurrencyElement.GetInt32();

                string? fanOutMergeMode = null;
                if (stepElement.TryGetProperty("fan_out_merge_mode", out var mergeElement) && mergeElement.ValueKind == JsonValueKind.String)
                    fanOutMergeMode = mergeElement.GetString();

                MetaSkillStepDefinition? fanOutTemplate = null;
                if (stepElement.TryGetProperty("fan_out_template", out var templateElement) && templateElement.ValueKind == JsonValueKind.Object)
                {
                    fanOutTemplate = ParseSingleFanOutTemplate(templateElement);
                }

                steps.Add(new MetaSkillStepDefinition
                {
                    Id = idElement.GetString()!,
                    Kind = kind,
                    Skill = skill,
                    Tool = tool,
                    CapabilityRef = capabilityRef,
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
                    OnFailure = onFailure ?? capabilityRef?.Fallback,
                    TimeoutSeconds = timeoutSeconds,
                    Retry = retry,
                    OutputContract = outputContract,
                    Iterable = iterable,
                    FanOutMaxConcurrency = fanOutMaxConcurrency,
                    FanOutMergeMode = fanOutMergeMode ?? "concat",
                    FanOutTemplate = fanOutTemplate
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
            "user_input",
            "fan_out"
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

            if (step.CapabilityRef is not null && !step.Kind.Equals("tool_call", StringComparison.OrdinalIgnoreCase))
            {
                errorCode = "invalid_capability_ref";
                return false;
            }

            if (step.CapabilityRef is not null && !string.IsNullOrWhiteSpace(step.Tool))
            {
                errorCode = "invalid_capability_ref";
                return false;
            }

            if (step.Kind.Equals("tool_call", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(step.Tool) &&
                step.CapabilityRef is null)
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

            if (step.Kind.Equals("fan_out", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(step.Iterable))
                {
                    errorCode = "invalid_step_kind_fields";
                    return false;
                }

                if (step.FanOutTemplate is null)
                {
                    errorCode = "invalid_step_kind_fields";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(step.FanOutTemplate.Kind))
                {
                    errorCode = "invalid_step_kind_fields";
                    return false;
                }

                var normalizedTemplateKind = step.FanOutTemplate.Kind.Trim().ToLowerInvariant();
                if (normalizedTemplateKind != "tool_call" && normalizedTemplateKind != "llm_chat")
                {
                    errorCode = "invalid_step_kind_fields";
                    return false;
                }

                if (normalizedTemplateKind == "tool_call" && string.IsNullOrWhiteSpace(step.FanOutTemplate.Tool))
                {
                    errorCode = "invalid_step_kind_fields";
                    return false;
                }
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

    private static bool TryParseCapabilityRef(JsonElement stepElement, string? onFailure, out MetaCapabilityRefDefinition? capabilityRef, out string? errorCode)
    {
        capabilityRef = null;
        errorCode = null;

        if (!stepElement.TryGetProperty("capability_ref", out var refElement) ||
            refElement.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (refElement.ValueKind != JsonValueKind.Object)
        {
            errorCode = "invalid_capability_ref";
            return false;
        }

        if (refElement.TryGetProperty("provider", out var provider) && (provider.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(provider.GetString())))
        { errorCode = "invalid_capability_provider"; return false; }

        // Reserved for the capability resolver: writing these fields is rejected
        // until the Resolver supports them (issue #231).
        if (refElement.TryGetProperty("top_k", out var topK) && topK.ValueKind != JsonValueKind.Null)
        {
            errorCode = "capabilityref_reserved_field";
            return false;
        }

        if (refElement.TryGetProperty("prefer_version", out var preferVersion) && preferVersion.ValueKind != JsonValueKind.Null)
        {
            errorCode = "capabilityref_reserved_field";
            return false;
        }

        if (!refElement.TryGetProperty("binding", out var bindingElement) ||
            bindingElement.ValueKind != JsonValueKind.String ||
            bindingElement.GetString() is not { Length: > 0 } binding)
        {
            errorCode = "invalid_capability_ref";
            return false;
        }

        binding = binding.Trim().ToLowerInvariant();
        if (binding is not ("static" or "dynamic"))
        {
            errorCode = "invalid_capability_ref";
            return false;
        }

        MetaCapabilityStaticBinding? staticBinding = null;
        MetaCapabilityIntent? intent = null;

        if (binding == "static")
        {
            if (!refElement.TryGetProperty("static", out var staticElement) ||
                staticElement.ValueKind != JsonValueKind.Object)
            {
                errorCode = "invalid_capability_ref";
                return false;
            }

            if (!(staticElement.TryGetProperty("target", out var serverName) || staticElement.TryGetProperty("mcp_server_name", out serverName)) ||
                serverName.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(serverName.GetString()))
            {
                errorCode = "invalid_capability_ref";
                return false;
            }

            if (!staticElement.TryGetProperty("tool_name", out var toolName) ||
                toolName.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(toolName.GetString()))
            {
                errorCode = "invalid_capability_ref";
                return false;
            }

            staticBinding = new MetaCapabilityStaticBinding
            {
                Target = serverName.GetString()!.Trim(),
                ToolName = toolName.GetString()!.Trim(),
            };
        }
        else
        {
            if (!refElement.TryGetProperty("intent", out var intentElement) ||
                intentElement.ValueKind != JsonValueKind.Object)
            {
                errorCode = "invalid_capability_ref";
                return false;
            }

            if (!intentElement.TryGetProperty("task_description", out var taskDescription) ||
                taskDescription.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(taskDescription.GetString()))
            {
                errorCode = "invalid_capability_ref";
                return false;
            }

            string? intentType = null;
            if (intentElement.TryGetProperty("type", out var typeElement) &&
                typeElement.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(typeElement.GetString()))
            {
                intentType = typeElement.GetString()!.Trim();
            }

            var keywords = new List<string>();
            if (intentElement.TryGetProperty("keywords", out var keywordsElement) &&
                keywordsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var keyword in keywordsElement.EnumerateArray())
                {
                    if (keyword.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(keyword.GetString()))
                        keywords.Add(keyword.GetString()!.Trim());
                }
            }

            intent = new MetaCapabilityIntent
            {
                Type = intentType,
                TaskDescription = taskDescription.GetString()!.Trim(),
                Keywords = keywords,
            };
        }

        var selectionPolicy = "first";
        if (refElement.TryGetProperty("selection_policy", out var policyElement))
        {
            if (policyElement.ValueKind != JsonValueKind.String ||
                policyElement.GetString() is not { Length: > 0 } policy ||
                (policy = policy.Trim().ToLowerInvariant()) is not ("first" or "exact_name"))
            {
                errorCode = "invalid_capability_ref";
                return false;
            }

            selectionPolicy = policy;
        }

        string? fallback = null;
        if (refElement.TryGetProperty("fallback", out var fallbackElement))
        {
            if (fallbackElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(fallbackElement.GetString()))
            {
                errorCode = "invalid_capability_ref";
                return false;
            }

            fallback = fallbackElement.GetString()!.Trim();
        }

        // capability_ref.fallback and step-level on_failure are the same failure
        // branch; declaring both is ambiguous.
        if (fallback is not null && !string.IsNullOrWhiteSpace(onFailure))
        {
            errorCode = "invalid_capability_ref";
            return false;
        }

        capabilityRef = new MetaCapabilityRefDefinition
        {
            Provider = refElement.TryGetProperty("provider", out var providerElement) && providerElement.ValueKind == JsonValueKind.String ? providerElement.GetString() ?? "" : "",
            Binding = binding,
            Static = staticBinding,
            Intent = intent,
            SelectionPolicy = selectionPolicy,
            Fallback = fallback,
        };
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

        if (!TryGetSkillExecProperty(stepElement, "entrypoint", "skill_exec_entrypoint", isSkillExec, out var entrypointElement, out var hasEntrypoint))
        {
            errorCode = "invalid_skill_exec";
            return false;
        }

        if (hasEntrypoint)
        {
            if (!isSkillExec || entrypointElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(entrypointElement.GetString()))
            {
                errorCode = "invalid_skill_exec";
                return false;
            }

            entrypoint = entrypointElement.GetString()!.Trim();
        }

        if (!TryGetSkillExecProperty(stepElement, "args", "skill_exec_args", isSkillExec, out var argsElement, out var hasArgs))
        {
            errorCode = "invalid_skill_exec";
            return false;
        }

        if (hasArgs)
        {
            if (!isSkillExec || argsElement.ValueKind != JsonValueKind.Array)
            {
                errorCode = "invalid_skill_exec";
                return false;
            }

            var parsedArgs = new List<string>();
            foreach (var itemElement in argsElement.EnumerateArray())
            {
                if (itemElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(itemElement.GetString()))
                {
                    errorCode = "invalid_skill_exec";
                    return false;
                }

                parsedArgs.Add(itemElement.GetString()!);
            }

            args = parsedArgs;
        }

        if (!TryGetSkillExecProperty(stepElement, "stdin", "skill_exec_stdin", isSkillExec, out var stdinElement, out var hasStdin))
        {
            errorCode = "invalid_skill_exec";
            return false;
        }

        if (hasStdin)
        {
            if (!isSkillExec || stdinElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(stdinElement.GetString()))
            {
                errorCode = "invalid_skill_exec";
                return false;
            }

            stdin = stdinElement.GetString();
        }

        if (!TryGetSkillExecProperty(stepElement, "cwd", "skill_exec_cwd", isSkillExec, out var cwdElement, out var hasCwd))
        {
            errorCode = "invalid_skill_exec";
            return false;
        }

        if (hasCwd)
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

        if (!TryGetSkillExecProperty(stepElement, "parse_mode", "skill_exec_parse_mode", isSkillExec, out var parseModeElement, out var hasParseMode))
        {
            errorCode = "invalid_skill_exec";
            return false;
        }

        if (hasParseMode)
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

    private static bool TryGetSkillExecProperty(
        JsonElement stepElement,
        string canonicalName,
        string legacyName,
        bool isSkillExec,
        out JsonElement value,
        out bool found)
    {
        var hasCanonical = stepElement.TryGetProperty(canonicalName, out var canonicalValue);
        JsonElement legacyValue = default;
        var hasLegacy = isSkillExec && stepElement.TryGetProperty(legacyName, out legacyValue);

        if (hasCanonical && hasLegacy)
        {
            value = default;
            found = false;
            return false;
        }

        found = hasCanonical || hasLegacy;
        value = hasCanonical ? canonicalValue : hasLegacy ? legacyValue : default;
        return true;
    }

    private static MetaSkillStepDefinition? ParseSingleFanOutTemplate(JsonElement templateElement)
    {
        var kindElement = templateElement.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
            ? k.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(kindElement))
            return null;

        var skill = templateElement.TryGetProperty("skill", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString()
            : null;
        var tool = templateElement.TryGetProperty("tool", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;

        string? withJson = null;
        if (templateElement.TryGetProperty("with", out var withElement) && withElement.ValueKind == JsonValueKind.Object)
            withJson = withElement.GetRawText();

        string? toolArgsJson = null;
        if (templateElement.TryGetProperty("tool_args", out var toolArgsElement) && toolArgsElement.ValueKind == JsonValueKind.Object)
            toolArgsJson = toolArgsElement.GetRawText();

        int? timeoutSeconds = null;
        if (templateElement.TryGetProperty("timeout_seconds", out var ts) && ts.ValueKind == JsonValueKind.Number && ts.TryGetInt32(out var parsed) && parsed > 0)
            timeoutSeconds = parsed;

        return new MetaSkillStepDefinition
        {
            Id = "_template_",
            Kind = kindElement,
            Skill = skill,
            Tool = tool,
            WithJson = withJson,
            ToolArgsJson = toolArgsJson,
            Retry = new MetaStepRetryPolicy { MaxAttempts = 1, BackoffMs = 0 },
            TimeoutSeconds = timeoutSeconds,
        };
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

    private static string[] ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return [];

        return ReadStringArray(property);
    }

    private static SkillArtifactContract? TryLoadArtifactContract(string skillDir, ILogger? logger)
    {
        var contractPath = Path.Combine(skillDir, "contracts", "artifacts.json");
        if (!File.Exists(contractPath))
            return null;

        try
        {
            using var stream = File.OpenRead(contractPath);
            var contract = JsonSerializer.Deserialize(stream, CoreJsonContext.Default.SkillArtifactContract);
            if (contract is null)
            {
                logger?.LogWarning("Artifact contract at {Path} is empty or invalid.", contractPath);
                return null;
            }

            return contract;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to load artifact contract from {Path}.", contractPath);
            return null;
        }
    }

    private sealed record ProjectionContractDiscoveryResult(
        IReadOnlyList<SkillProjectionContractSet> Contracts,
        SkillProjectionDiscovery Discovery);

    private static ProjectionContractDiscoveryResult TryLoadProjectionContracts(string skillDir, ILogger? logger)
    {
        var projectionsRoot = Path.Combine(skillDir, "contracts", "projections");
        if (!Directory.Exists(projectionsRoot))
        {
            return new ProjectionContractDiscoveryResult(
                [],
                new SkillProjectionDiscovery
                {
                    Status = "none",
                    IndexCount = 0,
                    BoundCount = 0
                });
        }

        string[] indexFiles;
        try
        {
            indexFiles = Directory.GetFiles(projectionsRoot, "contract-index.json", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to enumerate projection contract indexes under {Path}", projectionsRoot);
            return new ProjectionContractDiscoveryResult(
                [],
                new SkillProjectionDiscovery
                {
                    Status = "enumeration-failed",
                    IndexCount = 0,
                    BoundCount = 0,
                    Message = ex.Message
                });
        }

        if (indexFiles.Length == 0)
        {
            return new ProjectionContractDiscoveryResult(
                [],
                new SkillProjectionDiscovery
                {
                    Status = "none",
                    IndexCount = 0,
                    BoundCount = 0
                });
        }

        var contracts = new List<SkillProjectionContractSet>(indexFiles.Length);
        var failedIndexes = new List<string>();

        foreach (var indexPath in indexFiles)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(indexPath));
                var index = ParseProjectionContractIndex(document.RootElement);
                if (index is null)
                {
                    logger?.LogWarning("Failed to parse projection contract index at {Path}: missing required fields.", indexPath);
                    failedIndexes.Add(indexPath);
                    continue;
                }

                contracts.Add(new SkillProjectionContractSet
                {
                    ProducerName = index.ProducerSkill ?? Path.GetFileName(Path.GetDirectoryName(indexPath)),
                    ProducerPriority = index.ProducerPriority,
                    RootPath = Path.GetDirectoryName(indexPath) ?? projectionsRoot,
                    Index = index
                });
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to parse projection contract index at {Path}", indexPath);
                failedIndexes.Add(indexPath);
            }
        }

        if (contracts.Count == 0)
        {
            return new ProjectionContractDiscoveryResult(
                [],
                new SkillProjectionDiscovery
                {
                    Status = "parse-failed",
                    IndexCount = indexFiles.Length,
                    BoundCount = 0,
                    IndexPaths = [.. indexFiles],
                    Message = failedIndexes.Count > 0
                        ? $"Failed to parse {failedIndexes.Count} projection contract index file(s)."
                        : "Projection contract index is missing required fields."
                });
        }

        return new ProjectionContractDiscoveryResult(
            contracts,
            new SkillProjectionDiscovery
            {
                Status = failedIndexes.Count == 0 ? "bound" : "partial",
                IndexCount = indexFiles.Length,
                BoundCount = contracts.Count,
                IndexPaths = [.. indexFiles],
                Message = failedIndexes.Count == 0
                    ? null
                    : $"Bound {contracts.Count} of {indexFiles.Length} projection contract index file(s)."
            });
    }

    private static ProjectionContractIndex? ParseProjectionContractIndex(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        var index = new ProjectionContractIndex
        {
            ProducerSkill = ReadString(root, "producer_skill"),
            ProducerPriority = ReadInt32(root, "producer_priority", ReadInt32(root, "producer_precedence", 0)),
            DefaultSelectionPolicy = root.TryGetProperty("default_selection_policy", out var selectionPolicy)
                ? ParseProjectionSelectionPolicy(selectionPolicy)
                : new ProjectionSelectionPolicy(),
            TopicScoring = root.TryGetProperty("topic_scoring", out var topicScoring)
                ? ParseProjectionTopicScoring(topicScoring)
                : null,
            TargetViewScoring = root.TryGetProperty("target_view_scoring", out var targetViewScoring)
                ? ParseProjectionTargetViewScoring(targetViewScoring)
                : null,
            Topics = root.TryGetProperty("topics", out var topics)
                ? ParseProjectionTopics(topics)
                : []
        };

        return IsUsableProjectionContractIndex(index) ? index : null;
    }

    private static bool IsUsableProjectionContractIndex(ProjectionContractIndex index)
    {
        var usableTopics = index.Topics
            .Where(static topic =>
                !string.IsNullOrWhiteSpace(topic.DomainSlug) &&
                !string.IsNullOrWhiteSpace(topic.DefaultTargetView) &&
                topic.Views.Any(static view =>
                    !string.IsNullOrWhiteSpace(view.TargetView) &&
                    !string.IsNullOrWhiteSpace(view.Path)))
            .ToList();
        if (usableTopics.Count == 0)
            return false;

        var fallbackViews = index.DefaultSelectionPolicy.FallbackOrderByTargetView
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasUsableFallback = fallbackViews.Count > 0 &&
            usableTopics.Any(topic => topic.Views.Any(view => fallbackViews.Contains(view.TargetView)));
        var hasUsableTopicSignals = index.TopicScoring?.Topics.Any(HasUsableTopicSignals) == true;

        return hasUsableFallback || hasUsableTopicSignals;
    }

    private static bool HasUsableTopicSignals(ProjectionTopicSignals signals)
        => !string.IsNullOrWhiteSpace(signals.DomainSlug) &&
           (signals.PrimaryIntentSignals.Any(static value => !string.IsNullOrWhiteSpace(value)) ||
            signals.SupportingSignals.Any(static value => !string.IsNullOrWhiteSpace(value)) ||
            signals.ExplicitArtifactSignals.Any(static value => !string.IsNullOrWhiteSpace(value)));

    private static ProjectionSelectionPolicy ParseProjectionSelectionPolicy(JsonElement element)
        => new()
        {
            PreferReadyOnly = ReadBoolean(element, "prefer_ready_only"),
            BlockOnOpenQuestions = ReadBoolean(element, "block_on_open_questions"),
            FallbackOrderByTargetView = ReadStringArray(element, "fallback_order_by_target_view")
        };

    private static ProjectionTopicScoring ParseProjectionTopicScoring(JsonElement element)
        => new()
        {
            ClarifyWhenScoreGapBelow = ReadInt32(element, "clarify_when_score_gap_below", 2),
            ScoreDimensions = element.TryGetProperty("score_dimensions", out var scoreDimensions)
                ? ParseProjectionScoreDimensions(scoreDimensions)
                : [],
            Topics = element.TryGetProperty("topics", out var topics)
                ? ParseProjectionTopicSignals(topics)
                : []
        };

    private static ProjectionTargetViewScoring ParseProjectionTargetViewScoring(JsonElement element)
        => new()
        {
            ClarifyWhenScoreGapBelow = ReadInt32(element, "clarify_when_score_gap_below", 2),
            PreferExplicitUserArtifactRequests = ReadBoolean(element, "prefer_explicit_user_artifact_requests"),
            ScoreDimensions = element.TryGetProperty("score_dimensions", out var scoreDimensions)
                ? ParseProjectionScoreDimensions(scoreDimensions)
                : [],
            Views = element.TryGetProperty("views", out var views)
                ? ParseProjectionViewSignals(views)
                : [],
            WithinTopicOverrides = element.TryGetProperty("within_topic_overrides", out var overrides)
                ? ParseProjectionTopicViewOverrides(overrides)
                : []
        };

    private static IReadOnlyList<ProjectionScoreDimension> ParseProjectionScoreDimensions(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            return [];

        var values = new List<ProjectionScoreDimension>();
        foreach (var item in element.EnumerateArray())
        {
            var dimension = ReadString(item, "dimension");
            if (string.IsNullOrWhiteSpace(dimension))
                continue;

            values.Add(new ProjectionScoreDimension
            {
                Dimension = dimension,
                Score = ReadInt32(item, "score", 0)
            });
        }

        return values;
    }

    private static IReadOnlyList<ProjectionTopicSignals> ParseProjectionTopicSignals(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            return [];

        var values = new List<ProjectionTopicSignals>();
        foreach (var item in element.EnumerateArray())
        {
            var domainSlug = ReadString(item, "domain_slug");
            if (string.IsNullOrWhiteSpace(domainSlug))
                continue;

            values.Add(new ProjectionTopicSignals
            {
                DomainSlug = domainSlug,
                PrimaryIntentSignals = ReadStringArray(item, "primary_intent_signals"),
                SupportingSignals = ReadStringArray(item, "supporting_signals"),
                ExplicitArtifactSignals = ReadStringArray(item, "explicit_artifact_signals"),
                DemoteWhenCompetingTopicSignals = ReadStringArray(item, "demote_when_competing_topic_signals")
            });
        }

        return values;
    }

    private static IReadOnlyList<ProjectionViewSignals> ParseProjectionViewSignals(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            return [];

        var values = new List<ProjectionViewSignals>();
        foreach (var item in element.EnumerateArray())
        {
            var targetView = ReadString(item, "target_view");
            if (string.IsNullOrWhiteSpace(targetView))
                continue;

            values.Add(new ProjectionViewSignals
            {
                TargetView = targetView,
                ExplicitOutputSignals = ReadStringArray(item, "explicit_output_signals"),
                StrongSignals = ReadStringArray(item, "strong_signals"),
                SupportingSignals = ReadStringArray(item, "supporting_signals"),
                DemoteWhenCompetingViewSignals = ReadStringArray(item, "demote_when_competing_view_signals")
            });
        }

        return values;
    }

    private static IReadOnlyList<ProjectionTopicViewOverride> ParseProjectionTopicViewOverrides(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            return [];

        var values = new List<ProjectionTopicViewOverride>();
        foreach (var item in element.EnumerateArray())
        {
            var domainSlug = ReadString(item, "domain_slug");
            if (string.IsNullOrWhiteSpace(domainSlug))
                continue;

            values.Add(new ProjectionTopicViewOverride
            {
                DomainSlug = domainSlug,
                Bonuses = item.TryGetProperty("bonuses", out var bonuses)
                    ? ParseProjectionTopicViewBonuses(bonuses)
                    : []
            });
        }

        return values;
    }

    private static IReadOnlyList<ProjectionTopicViewBonus> ParseProjectionTopicViewBonuses(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            return [];

        var values = new List<ProjectionTopicViewBonus>();
        foreach (var item in element.EnumerateArray())
        {
            var targetView = ReadString(item, "target_view");
            if (string.IsNullOrWhiteSpace(targetView))
                continue;

            values.Add(new ProjectionTopicViewBonus
            {
                TargetView = targetView,
                WhenRequestSignals = ReadStringArray(item, "when_request_signals"),
                Score = ReadInt32(item, "score", 0)
            });
        }

        return values;
    }

    private static IReadOnlyList<ProjectionTopicRecord> ParseProjectionTopics(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            return [];

        var values = new List<ProjectionTopicRecord>();
        foreach (var item in element.EnumerateArray())
        {
            var domainSlug = ReadString(item, "domain_slug");
            var defaultTargetView = ReadString(item, "default_target_view");
            if (string.IsNullOrWhiteSpace(domainSlug) || string.IsNullOrWhiteSpace(defaultTargetView))
                continue;

            values.Add(new ProjectionTopicRecord
            {
                DomainSlug = domainSlug,
                DefaultTargetView = defaultTargetView,
                Views = item.TryGetProperty("views", out var views)
                    ? ParseProjectionViews(views)
                    : []
            });
        }

        return values;
    }

    private static IReadOnlyList<ProjectionViewRecord> ParseProjectionViews(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            return [];

        var values = new List<ProjectionViewRecord>();
        foreach (var item in element.EnumerateArray())
        {
            var targetView = ReadString(item, "target_view");
            var status = ReadString(item, "status");
            var path = ReadString(item, "path");
            if (string.IsNullOrWhiteSpace(targetView) || string.IsNullOrWhiteSpace(status) || string.IsNullOrWhiteSpace(path))
                continue;

            values.Add(new ProjectionViewRecord
            {
                TargetView = targetView,
                Status = status,
                Path = path
            });
        }

        return values;
    }

    private static string ReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static bool ReadBoolean(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) &&
           (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False) &&
           property.GetBoolean();

    private static int ReadInt32(JsonElement element, string propertyName, int fallback)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
            ? value
            : fallback;
}

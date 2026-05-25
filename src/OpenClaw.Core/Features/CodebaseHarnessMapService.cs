using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Features;

public sealed class CodebaseHarnessMapService
{
    public const string GeneratorVersion = "codebase-harness-map-mvp-1";
    private const long MaxTextScanBytes = 512 * 1024;
    private const long MaxHashBytes = 8 * 1024 * 1024;

    private static readonly Regex MapEndpointPattern = new(@"(?:\b\w+\.)?Map(?<method>Get|Post|Put|Delete|Patch)\s*\(\s*""(?<path>[^""]+)""", RegexOptions.CultureInvariant);
    private static readonly Regex HttpAttributePattern = new(@"\[(?<attr>HttpGet|HttpPost|HttpPut|HttpDelete|HttpPatch)(?:Attribute)?(?:\s*\(\s*""(?<path>[^""]+)"")?", RegexOptions.CultureInvariant);
    private static readonly Regex ClassPattern = new(@"\b(?:public|internal|private|sealed|abstract|static|\s)*\s*class\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ConfigFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "global.json",
        "nuget.config",
        "launchsettings.json",
        "directory.build.props",
        "directory.build.targets",
        ".env.example"
    };

    public async Task<CodebaseHarnessMap> GenerateAsync(string? repositoryRoot, CodebaseMapOptions? options = null, CancellationToken ct = default)
    {
        await Task.Yield();
        var started = DateTimeOffset.UtcNow;
        options = NormalizeOptions(options);
        var diagnostics = new List<CodebaseMapDiagnostic>();
        var root = ResolveRoot(repositoryRoot, diagnostics);
        var repositoryName = new DirectoryInfo(root).Name;

        if (!Directory.Exists(root))
        {
            diagnostics.Add(new CodebaseMapDiagnostic
            {
                Severity = CodebaseMapDiagnosticSeverity.Error,
                Code = "root_missing",
                Message = $"Repository root '{root}' does not exist.",
                Recommendation = "Pass --root with an existing repository or workspace path."
            });

            return BuildMap(root, repositoryName, started, [], [], [], [], [], [], [], [], [], diagnostics, options, new HashSet<string>(StringComparer.Ordinal));
        }

        var files = EnumerateFiles(root, options, diagnostics, ct);
        var recentPaths = options.IncludeRecentChanges
            ? CollectRecentPaths(files, options)
            : new HashSet<string>(StringComparer.Ordinal);

        var projects = DetectProjects(root, files, diagnostics);
        var modules = DetectModules(root, projects, files);
        var artifacts = DetectArtifacts(root, files, projects, modules, recentPaths, options, diagnostics);
        var endpoints = options.IncludeEndpoints ? DetectEndpoints(root, files, diagnostics, ct) : [];
        var tools = options.IncludeToolSurfaces ? DetectToolSurfaces(root, files, diagnostics, ct) : [];
        var providers = options.IncludeProviderSurfaces ? DetectProviderSurfaces(root, files, diagnostics, ct) : [];
        var channels = options.IncludeChannelSurfaces ? DetectChannelSurfaces(root, files, diagnostics, ct) : [];
        var configs = options.IncludeConfigSurfaces ? DetectConfigSurfaces(root, files, diagnostics, ct) : [];
        var tests = options.IncludeTests ? DetectTestSurfaces(projects, modules) : [];

        return BuildMap(root, repositoryName, started, projects, modules, artifacts, endpoints, tools, providers, channels, configs, tests, diagnostics, options, recentPaths);
    }

    private static CodebaseHarnessMap BuildMap(
        string root,
        string repositoryName,
        DateTimeOffset generatedAt,
        IReadOnlyList<CodebaseProject> projects,
        IReadOnlyList<CodebaseModule> modules,
        IReadOnlyList<CodebaseArtifact> artifacts,
        IReadOnlyList<CodebaseEndpoint> endpoints,
        IReadOnlyList<CodebaseToolSurface> tools,
        IReadOnlyList<CodebaseProviderSurface> providers,
        IReadOnlyList<CodebaseChannelSurface> channels,
        IReadOnlyList<CodebaseConfigSurface> configs,
        IReadOnlyList<CodebaseTestSurface> tests,
        IReadOnlyList<CodebaseMapDiagnostic> diagnostics,
        CodebaseMapOptions options,
        IReadOnlySet<string> recentPaths)
    {
        var warningCount = diagnostics.Count(static item =>
            item.Severity is CodebaseMapDiagnosticSeverity.Warning or CodebaseMapDiagnosticSeverity.Error);

        return new CodebaseHarnessMap
        {
            Id = $"cbhm_{Guid.NewGuid():N}"[..20],
            RepositoryRoot = root,
            RepositoryName = repositoryName,
            GeneratedAtUtc = generatedAt,
            GeneratorVersion = GeneratorVersion,
            Summary = new CodebaseMapSummary
            {
                SolutionFilesCount = artifacts.Count(static item => item.Kind == CodebaseMapArtifactKinds.Solution),
                ProjectFilesCount = projects.Count,
                SourceFilesCount = artifacts.Count(static item => item.Kind == CodebaseMapArtifactKinds.Source),
                TestProjectsCount = projects.Count(static item => item.IsTestProject),
                EndpointCount = endpoints.Count,
                ToolSurfaceCount = tools.Count,
                ChannelSurfaceCount = channels.Count,
                ProviderSurfaceCount = providers.Count,
                ConfigFileCount = artifacts.Count(static item => item.Kind == CodebaseMapArtifactKinds.Config),
                RecentChangeCount = recentPaths.Count,
                WarningCount = warningCount
            },
            Projects = projects,
            Modules = modules,
            Artifacts = artifacts,
            Endpoints = endpoints,
            ToolSurfaces = tools,
            ProviderSurfaces = providers,
            ChannelSurfaces = channels,
            ConfigSurfaces = configs,
            TestSurfaces = tests,
            Diagnostics = diagnostics,
            Tags = [options.Category],
            Metadata = new Dictionary<string, string>
            {
                ["category"] = options.Category,
                ["maxFiles"] = options.MaxFiles.ToString(CultureInfo.InvariantCulture),
                ["maxDepth"] = options.MaxDepth.ToString(CultureInfo.InvariantCulture)
            }
        };
    }

    private static string ResolveRoot(string? repositoryRoot, List<CodebaseMapDiagnostic> diagnostics)
    {
        var root = string.IsNullOrWhiteSpace(repositoryRoot)
            ? Directory.GetCurrentDirectory()
            : repositoryRoot.Trim();

        try
        {
            return Path.GetFullPath(root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            diagnostics.Add(new CodebaseMapDiagnostic
            {
                Severity = CodebaseMapDiagnosticSeverity.Error,
                Code = "root_invalid",
                Message = $"Repository root '{root}' is invalid: {ex.Message}",
                Recommendation = "Pass a normal local filesystem path."
            });
            return root;
        }
    }

    private static CodebaseMapOptions NormalizeOptions(CodebaseMapOptions? input)
    {
        input ??= new CodebaseMapOptions();
        var category = NormalizeCategory(input.Category);
        var options = new CodebaseMapOptions
        {
            IncludeHashes = input.IncludeHashes,
            IncludeRecentChanges = input.IncludeRecentChanges,
            IncludeEndpoints = input.IncludeEndpoints,
            IncludeToolSurfaces = input.IncludeToolSurfaces,
            IncludeProviderSurfaces = input.IncludeProviderSurfaces,
            IncludeChannelSurfaces = input.IncludeChannelSurfaces,
            IncludeConfigSurfaces = input.IncludeConfigSurfaces,
            IncludeTests = input.IncludeTests,
            IncludeDocs = input.IncludeDocs,
            MaxFiles = Math.Clamp(input.MaxFiles, 1, 50_000),
            MaxDepth = Math.Clamp(input.MaxDepth, 1, 64),
            RecentDays = Math.Clamp(input.RecentDays, 1, 3650),
            Category = category
        };

        return category switch
        {
            CodebaseMapCategories.Projects => WithCategory(options, category, includeEndpoints: false, includeTools: false, includeProviders: false, includeChannels: false, includeConfig: false, includeTests: false),
            CodebaseMapCategories.Endpoints => WithCategory(options, category, includeEndpoints: true, includeTools: false, includeProviders: false, includeChannels: false, includeConfig: false, includeTests: false),
            CodebaseMapCategories.Tools => WithCategory(options, category, includeEndpoints: false, includeTools: true, includeProviders: false, includeChannels: false, includeConfig: false, includeTests: false),
            CodebaseMapCategories.Providers => WithCategory(options, category, includeEndpoints: false, includeTools: false, includeProviders: true, includeChannels: false, includeConfig: false, includeTests: false),
            CodebaseMapCategories.Channels => WithCategory(options, category, includeEndpoints: false, includeTools: false, includeProviders: false, includeChannels: true, includeConfig: false, includeTests: false),
            CodebaseMapCategories.Config => WithCategory(options, category, includeEndpoints: false, includeTools: false, includeProviders: false, includeChannels: false, includeConfig: true, includeTests: false),
            CodebaseMapCategories.Tests => WithCategory(options, category, includeEndpoints: false, includeTools: false, includeProviders: false, includeChannels: false, includeConfig: false, includeTests: true),
            _ => options
        };
    }

    private static CodebaseMapOptions WithCategory(
        CodebaseMapOptions options,
        string category,
        bool includeEndpoints,
        bool includeTools,
        bool includeProviders,
        bool includeChannels,
        bool includeConfig,
        bool includeTests)
        => new()
        {
            IncludeHashes = options.IncludeHashes,
            IncludeRecentChanges = options.IncludeRecentChanges,
            IncludeEndpoints = includeEndpoints,
            IncludeToolSurfaces = includeTools,
            IncludeProviderSurfaces = includeProviders,
            IncludeChannelSurfaces = includeChannels,
            IncludeConfigSurfaces = includeConfig,
            IncludeTests = includeTests,
            IncludeDocs = options.IncludeDocs,
            MaxFiles = options.MaxFiles,
            MaxDepth = options.MaxDepth,
            RecentDays = options.RecentDays,
            Category = category
        };

    public static string NormalizeCategory(string? category)
    {
        var value = string.IsNullOrWhiteSpace(category)
            ? CodebaseMapCategories.All
            : category.Trim().ToLowerInvariant();

        return value switch
        {
            CodebaseMapCategories.All => CodebaseMapCategories.All,
            CodebaseMapCategories.Projects => CodebaseMapCategories.Projects,
            CodebaseMapCategories.Endpoints => CodebaseMapCategories.Endpoints,
            CodebaseMapCategories.Tools => CodebaseMapCategories.Tools,
            CodebaseMapCategories.Providers => CodebaseMapCategories.Providers,
            CodebaseMapCategories.Channels => CodebaseMapCategories.Channels,
            CodebaseMapCategories.Config => CodebaseMapCategories.Config,
            CodebaseMapCategories.Tests => CodebaseMapCategories.Tests,
            _ => CodebaseMapCategories.All
        };
    }

    private static IReadOnlyList<ScannedFile> EnumerateFiles(string root, CodebaseMapOptions options, List<CodebaseMapDiagnostic> diagnostics, CancellationToken ct)
    {
        var results = new List<ScannedFile>();
        var stack = new Stack<(DirectoryInfo Directory, int Depth)>();
        stack.Push((new DirectoryInfo(root), 0));

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (directory, depth) = stack.Pop();
            FileInfo[] files;
            DirectoryInfo[] directories;
            try
            {
                files = directory.EnumerateFiles().OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                directories = directory.EnumerateDirectories().OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                diagnostics.Add(new CodebaseMapDiagnostic
                {
                    Severity = CodebaseMapDiagnosticSeverity.Warning,
                    Code = "directory_unreadable",
                    Message = $"Could not read directory '{RelativePath(root, directory.FullName)}': {ex.Message}",
                    Path = RelativePath(root, directory.FullName)
                });
                continue;
            }

            foreach (var file in files)
            {
                if (results.Count >= options.MaxFiles)
                {
                    diagnostics.Add(new CodebaseMapDiagnostic
                    {
                        Severity = CodebaseMapDiagnosticSeverity.Warning,
                        Code = "max_files_reached",
                        Message = $"Stopped scanning after {options.MaxFiles} files.",
                        Recommendation = "Increase --max-files for a larger repository map."
                    });
                    return results;
                }

                results.Add(new ScannedFile(file, depth, RelativePath(root, file.FullName)));
            }

            if (depth >= options.MaxDepth)
                continue;

            for (var i = directories.Length - 1; i >= 0; i--)
            {
                var child = directories[i];
                if (ShouldSkipDirectory(child.Name))
                    continue;

                if (IsReparsePoint(child))
                {
                    diagnostics.Add(new CodebaseMapDiagnostic
                    {
                        Severity = CodebaseMapDiagnosticSeverity.Warning,
                        Code = "directory_reparse_point_skipped",
                        Message = $"Skipped symlink or reparse-point directory '{RelativePath(root, child.FullName)}'.",
                        Path = RelativePath(root, child.FullName),
                        Recommendation = "Codebase map scanning does not follow symlinked directories."
                    });
                    continue;
                }

                stack.Push((child, depth + 1));
            }
        }

        return results;
    }

    private static bool ShouldSkipDirectory(string name)
        => name is ".git" or ".hg" or ".svn" or "bin" or "obj" or "node_modules" or ".idea" or ".vs" or ".vscode" or "TestResults" or "artifacts";

    private static bool IsReparsePoint(FileSystemInfo info)
    {
        try
        {
            return (info.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }

    private static IReadOnlyList<CodebaseProject> DetectProjects(string root, IReadOnlyList<ScannedFile> files, List<CodebaseMapDiagnostic> diagnostics)
    {
        var projects = new List<CodebaseProject>();
        foreach (var file in files.Where(static item => item.File.Extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            var name = Path.GetFileNameWithoutExtension(file.File.Name);
            try
            {
                var document = XDocument.Load(file.File.FullName, LoadOptions.None);
                var targetFrameworks = document
                    .Descendants()
                    .Where(static item => item.Name.LocalName is "TargetFramework" or "TargetFrameworks")
                    .SelectMany(static item => (item.Value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var packages = document
                    .Descendants()
                    .Where(static item => item.Name.LocalName == "PackageReference")
                    .Select(static item => (string?)item.Attribute("Include") ?? (string?)item.Attribute("Update"))
                    .Where(static item => !string.IsNullOrWhiteSpace(item))
                    .Select(static item => item!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var references = document
                    .Descendants()
                    .Where(static item => item.Name.LocalName == "ProjectReference")
                    .Select(static item => (string?)item.Attribute("Include"))
                    .Where(static item => !string.IsNullOrWhiteSpace(item))
                    .Select(static item => NormalizeSlashes(item!))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var sdk = ((string?)document.Root?.Attribute("Sdk")) ?? "";
                var outputType = document
                    .Descendants()
                    .FirstOrDefault(static item => item.Name.LocalName == "OutputType")
                    ?.Value ?? "";
                var isTest = IsTestProject(name, file.RelativePath, packages);

                projects.Add(new CodebaseProject
                {
                    Id = StableId("project", file.RelativePath),
                    Name = name,
                    Path = file.RelativePath,
                    ProjectType = InferProjectType(sdk, outputType, isTest),
                    TargetFrameworks = targetFrameworks,
                    IsTestProject = isTest,
                    PackageReferences = packages,
                    ProjectReferences = references,
                    Tags = isTest ? ["test"] : []
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Xml.XmlException)
            {
                diagnostics.Add(new CodebaseMapDiagnostic
                {
                    Severity = CodebaseMapDiagnosticSeverity.Warning,
                    Code = "project_parse_failed",
                    Message = $"Could not parse project '{file.RelativePath}': {ex.Message}",
                    Path = file.RelativePath,
                    Recommendation = "Inspect the project file XML."
                });

                projects.Add(new CodebaseProject
                {
                    Id = StableId("project", file.RelativePath),
                    Name = name,
                    Path = file.RelativePath,
                    ProjectType = "unknown"
                });
            }
        }

        return projects.OrderBy(static item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsTestProject(string name, string path, IReadOnlyList<string> packages)
        => name.Contains("Test", StringComparison.OrdinalIgnoreCase) ||
           path.Contains("/tests/", StringComparison.OrdinalIgnoreCase) ||
           packages.Any(static package =>
               package.Contains("xunit", StringComparison.OrdinalIgnoreCase) ||
               package.Contains("nunit", StringComparison.OrdinalIgnoreCase) ||
               package.Contains("mstest", StringComparison.OrdinalIgnoreCase) ||
               package.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase));

    private static string InferProjectType(string sdk, string outputType, bool isTest)
    {
        if (isTest)
            return "test";
        if (sdk.Contains("Web", StringComparison.OrdinalIgnoreCase))
            return "web";
        if (outputType.Contains("Exe", StringComparison.OrdinalIgnoreCase))
            return "executable";
        return "library";
    }

    private static IReadOnlyList<CodebaseModule> DetectModules(string root, IReadOnlyList<CodebaseProject> projects, IReadOnlyList<ScannedFile> files)
    {
        var modules = new List<CodebaseModule>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            var directory = Path.GetDirectoryName(project.Path)?.Replace('\\', '/') ?? "";
            var kind = InferModuleKind(project.Name, directory);
            modules.Add(new CodebaseModule
            {
                Id = UniqueId(StableId("module", directory.Length == 0 ? project.Path : directory), used),
                Name = project.Name,
                Path = directory,
                Kind = kind,
                ProjectId = project.Id,
                Description = $"{project.Name} project"
            });
        }

        foreach (var folder in new[] { "docs", "samples", "skills" }.Where(folder =>
                     Directory.Exists(Path.Join(root, folder)) ||
                     files.Any(file => file.RelativePath.StartsWith($"{folder}/", StringComparison.OrdinalIgnoreCase))))
        {
            modules.Add(new CodebaseModule
            {
                Id = UniqueId(StableId("module", folder), used),
                Name = folder,
                Path = folder,
                Kind = folder switch
                {
                    "docs" => CodebaseMapModuleKinds.Docs,
                    "samples" => CodebaseMapModuleKinds.Samples,
                    "skills" => CodebaseMapModuleKinds.Skills,
                    _ => CodebaseMapModuleKinds.Unknown
                }
            });
        }

        return modules.OrderBy(static item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string InferModuleKind(string name, string path)
    {
        var haystack = $"{path}/{name}";
        if (haystack.Contains("OpenClaw.Gateway", StringComparison.OrdinalIgnoreCase) || haystack.Contains("Gateway", StringComparison.OrdinalIgnoreCase))
            return CodebaseMapModuleKinds.Gateway;
        if (haystack.Contains("OpenClaw.Core", StringComparison.OrdinalIgnoreCase))
            return CodebaseMapModuleKinds.Core;
        if (haystack.Contains("OpenClaw.Agent", StringComparison.OrdinalIgnoreCase))
            return CodebaseMapModuleKinds.Agent;
        if (haystack.Contains("OpenClaw.Client", StringComparison.OrdinalIgnoreCase))
            return CodebaseMapModuleKinds.Client;
        if (haystack.Contains("OpenClaw.Cli", StringComparison.OrdinalIgnoreCase))
            return CodebaseMapModuleKinds.Cli;
        if (haystack.Contains("Companion", StringComparison.OrdinalIgnoreCase))
            return CodebaseMapModuleKinds.Companion;
        if (haystack.Contains("Tui", StringComparison.OrdinalIgnoreCase))
            return CodebaseMapModuleKinds.Tui;
        if (haystack.Contains("Plugin", StringComparison.OrdinalIgnoreCase))
            return CodebaseMapModuleKinds.Plugin;
        if (haystack.Contains("Adapter", StringComparison.OrdinalIgnoreCase))
            return CodebaseMapModuleKinds.Adapter;
        if (haystack.Contains("Test", StringComparison.OrdinalIgnoreCase))
            return CodebaseMapModuleKinds.Tests;
        if (path.StartsWith("samples/", StringComparison.OrdinalIgnoreCase) || string.Equals(path, "samples", StringComparison.OrdinalIgnoreCase))
            return CodebaseMapModuleKinds.Samples;
        if (path.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) || string.Equals(path, "docs", StringComparison.OrdinalIgnoreCase))
            return CodebaseMapModuleKinds.Docs;
        if (path.StartsWith("skills/", StringComparison.OrdinalIgnoreCase) || string.Equals(path, "skills", StringComparison.OrdinalIgnoreCase))
            return CodebaseMapModuleKinds.Skills;
        return CodebaseMapModuleKinds.Unknown;
    }

    private static IReadOnlyList<CodebaseArtifact> DetectArtifacts(
        string root,
        IReadOnlyList<ScannedFile> files,
        IReadOnlyList<CodebaseProject> projects,
        IReadOnlyList<CodebaseModule> modules,
        IReadOnlySet<string> recentPaths,
        CodebaseMapOptions options,
        List<CodebaseMapDiagnostic> diagnostics)
    {
        var projectDirs = projects
            .Select(project => (Project: project, Directory: NormalizeSlashes(Path.GetDirectoryName(project.Path) ?? "")))
            .OrderByDescending(static item => item.Directory.Length)
            .ToArray();
        var moduleByProject = modules
            .Where(static module => !string.IsNullOrWhiteSpace(module.ProjectId))
            .ToDictionary(static module => module.ProjectId!, static module => module.Id, StringComparer.Ordinal);
        var artifactIds = new HashSet<string>(StringComparer.Ordinal);

        return files
            .Select(file =>
            {
                var project = projectDirs.FirstOrDefault(item =>
                    file.RelativePath.Equals(item.Project.Path, StringComparison.OrdinalIgnoreCase) ||
                    (item.Directory.Length > 0 && file.RelativePath.StartsWith($"{item.Directory}/", StringComparison.OrdinalIgnoreCase))).Project;
                var kind = ArtifactKind(file.RelativePath, project?.IsTestProject == true);
                var tags = new List<string>();
                if (recentPaths.Contains(file.RelativePath))
                    tags.Add("recent");
                if (project?.IsTestProject == true)
                    tags.Add("test");

                return new CodebaseArtifact
                {
                    Id = UniqueId(StableId("artifact", file.RelativePath), artifactIds),
                    Path = file.RelativePath,
                    Kind = kind,
                    ProjectId = project?.Id,
                    ModuleId = project is not null && moduleByProject.TryGetValue(project.Id, out var moduleId) ? moduleId : null,
                    SizeBytes = SafeLength(file.File),
                    LastModifiedUtc = SafeLastModified(file.File),
                    Hash = options.IncludeHashes ? TryHash(file, diagnostics) : null,
                    Tags = tags,
                    Summary = ArtifactSummary(file.RelativePath, kind)
                };
            })
            .OrderBy(static item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ArtifactKind(string relativePath, bool isTestProject)
    {
        var extension = Path.GetExtension(relativePath);
        var fileName = Path.GetFileName(relativePath);
        extension = extension.ToLowerInvariant();
        if (extension is ".sln" or ".slnx")
            return CodebaseMapArtifactKinds.Solution;
        if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            return CodebaseMapArtifactKinds.Project;
        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
            return isTestProject || relativePath.Contains("Tests", StringComparison.OrdinalIgnoreCase)
                ? CodebaseMapArtifactKinds.Test
                : CodebaseMapArtifactKinds.Source;
        if (extension is ".md" or ".mdx")
            return CodebaseMapArtifactKinds.Docs;
        if (relativePath.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase))
            return CodebaseMapArtifactKinds.Workflow;
        if (extension is ".ps1" or ".sh" or ".cmd" or ".bat")
            return CodebaseMapArtifactKinds.Script;
        if (fileName.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
            return CodebaseMapArtifactKinds.Skill;
        if (fileName.Equals("plugin.json", StringComparison.OrdinalIgnoreCase))
            return CodebaseMapArtifactKinds.Plugin;
        if (IsConfigFile(relativePath))
            return CodebaseMapArtifactKinds.Config;
        if (relativePath.StartsWith("samples/", StringComparison.OrdinalIgnoreCase))
            return CodebaseMapArtifactKinds.Sample;
        return CodebaseMapArtifactKinds.Unknown;
    }

    private static string? ArtifactSummary(string relativePath, string kind)
        => kind switch
        {
            CodebaseMapArtifactKinds.Solution => ".NET solution file",
            CodebaseMapArtifactKinds.Project => ".NET project file",
            CodebaseMapArtifactKinds.Config => "Configuration surface; values are not included in the map",
            CodebaseMapArtifactKinds.Workflow => "GitHub Actions workflow",
            _ => null
        };

    private static IReadOnlyList<CodebaseEndpoint> DetectEndpoints(string root, IReadOnlyList<ScannedFile> files, List<CodebaseMapDiagnostic> diagnostics, CancellationToken ct)
    {
        var endpoints = new List<CodebaseEndpoint>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files.Where(static item => item.File.Extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            var text = TryReadText(file, diagnostics);
            if (text is null)
                continue;

            foreach (Match match in MapEndpointPattern.Matches(text))
            {
                var method = match.Groups["method"].Value.ToUpperInvariant();
                var path = match.Groups["path"].Value;
                endpoints.Add(new CodebaseEndpoint
                {
                    Id = UniqueId(StableId("endpoint", $"{file.RelativePath}:{method}:{path}"), ids),
                    Method = method,
                    Path = path,
                    SourceFile = file.RelativePath,
                    AuthRequired = HasNearbyAuthorization(text, match.Index),
                    Tags = ["minimal-api"]
                });
            }

            foreach (Match match in HttpAttributePattern.Matches(text))
            {
                var path = match.Groups["path"].Success ? match.Groups["path"].Value : "";
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                var method = match.Groups["attr"].Value["Http".Length..].ToUpperInvariant();
                endpoints.Add(new CodebaseEndpoint
                {
                    Id = UniqueId(StableId("endpoint", $"{file.RelativePath}:{method}:{path}"), ids),
                    Method = method,
                    Path = path,
                    SourceFile = file.RelativePath,
                    AuthRequired = HasNearbyAuthorization(text, match.Index),
                    Tags = ["attribute-route"]
                });
            }
        }

        return endpoints.OrderBy(static item => item.SourceFile, StringComparer.OrdinalIgnoreCase).ThenBy(static item => item.Path, StringComparer.Ordinal).ToArray();
    }

    private static bool? HasNearbyAuthorization(string text, int index)
    {
        var length = Math.Min(400, Math.Max(0, text.Length - index));
        var after = length > 0 ? text.Substring(index, length) : "";
        if (after.Contains("AllowAnonymous", StringComparison.Ordinal))
            return false;
        if (after.Contains("RequireAuthorization", StringComparison.Ordinal) || after.Contains("[Authorize", StringComparison.Ordinal))
            return true;
        return null;
    }

    private static IReadOnlyList<CodebaseToolSurface> DetectToolSurfaces(string root, IReadOnlyList<ScannedFile> files, List<CodebaseMapDiagnostic> diagnostics, CancellationToken ct)
    {
        var surfaces = new List<CodebaseToolSurface>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.Where(static item => item.File.Extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            if (!LooksToolRelated(file.RelativePath))
                continue;

            var text = TryReadText(file, diagnostics);
            if (text is null)
                continue;

            foreach (var className in ClassNames(text).Where(static name => name.EndsWith("Tool", StringComparison.Ordinal) || name.Contains("Tool", StringComparison.Ordinal)))
            {
                var name = ToSnakeCase(className.EndsWith("Tool", StringComparison.Ordinal) ? className[..^"Tool".Length] : className);
                var key = $"{name}:{file.RelativePath}";
                if (!seen.Add(key))
                    continue;

                var mutating = IsMutatingSurface(name, text);
                surfaces.Add(new CodebaseToolSurface
                {
                    Name = name,
                    SourceFile = file.RelativePath,
                    Category = InferSurfaceCategory(file.RelativePath),
                    ReadOnly = !mutating,
                    Mutating = mutating,
                    ApprovalRequired = text.Contains("ApprovalRequired", StringComparison.OrdinalIgnoreCase) ||
                                       text.Contains("RequireApproval", StringComparison.OrdinalIgnoreCase),
                    SandboxCapable = text.Contains("Sandbox", StringComparison.OrdinalIgnoreCase),
                    Tags = ["static-scan"]
                });
            }
        }

        return surfaces.OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool LooksToolRelated(string path)
        => path.Contains("Tool", StringComparison.OrdinalIgnoreCase) ||
           path.Contains("/Tools/", StringComparison.OrdinalIgnoreCase) ||
           path.Contains("/Plugins/", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<CodebaseProviderSurface> DetectProviderSurfaces(string root, IReadOnlyList<ScannedFile> files, List<CodebaseMapDiagnostic> diagnostics, CancellationToken ct)
    {
        var surfaces = new List<CodebaseProviderSurface>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.Where(static item => item.File.Extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            if (!file.RelativePath.Contains("Provider", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = TryReadText(file, diagnostics);
            if (text is null)
                continue;

            foreach (var className in ClassNames(text).Where(static name => name.Contains("Provider", StringComparison.Ordinal)))
            {
                var key = $"{className}:{file.RelativePath}";
                if (!seen.Add(key))
                    continue;

                surfaces.Add(new CodebaseProviderSurface
                {
                    Name = className,
                    SourceFile = file.RelativePath,
                    ProviderType = InferProviderType(className, file.RelativePath),
                    SupportsStreaming = text.Contains("stream", StringComparison.OrdinalIgnoreCase),
                    SupportsTools = text.Contains("tool", StringComparison.OrdinalIgnoreCase),
                    Tags = ["static-scan"]
                });
            }
        }

        return surfaces.OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<CodebaseChannelSurface> DetectChannelSurfaces(string root, IReadOnlyList<ScannedFile> files, List<CodebaseMapDiagnostic> diagnostics, CancellationToken ct)
    {
        var surfaces = new List<CodebaseChannelSurface>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.Where(static item => item.File.Extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            if (!file.RelativePath.Contains("Channel", StringComparison.OrdinalIgnoreCase) &&
                !file.RelativePath.Contains("/Channels/", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = TryReadText(file, diagnostics);
            if (text is null)
                continue;

            foreach (var className in ClassNames(text).Where(static name => name.Contains("Channel", StringComparison.Ordinal)))
            {
                var key = $"{className}:{file.RelativePath}";
                if (!seen.Add(key))
                    continue;

                surfaces.Add(new CodebaseChannelSurface
                {
                    Name = className,
                    SourceFile = file.RelativePath,
                    Direction = text.Contains("IChannelAdapter", StringComparison.Ordinal) ? "bidirectional" : null,
                    AuthOrSignatureRequired = text.Contains("signature", StringComparison.OrdinalIgnoreCase) ||
                                              text.Contains("auth", StringComparison.OrdinalIgnoreCase),
                    Tags = ["static-scan"]
                });
            }
        }

        return surfaces.OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<CodebaseConfigSurface> DetectConfigSurfaces(string root, IReadOnlyList<ScannedFile> files, List<CodebaseMapDiagnostic> diagnostics, CancellationToken ct)
    {
        var configs = new List<CodebaseConfigSurface>();
        foreach (var file in files.Where(static item => IsConfigFile(item.RelativePath)))
        {
            ct.ThrowIfCancellationRequested();
            var extension = file.File.Extension;
            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                AddJsonConfigSurfaces(file, configs, diagnostics, ct);
            }
            else
            {
                configs.Add(new CodebaseConfigSurface
                {
                    Path = file.RelativePath,
                    Section = Path.GetFileNameWithoutExtension(file.RelativePath),
                    Key = Path.GetFileName(file.RelativePath),
                    Sensitive = IsSensitiveKey(file.RelativePath)
                });
            }
        }

        return configs.OrderBy(static item => item.Path, StringComparer.OrdinalIgnoreCase).ThenBy(static item => item.Key, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddJsonConfigSurfaces(ScannedFile file, List<CodebaseConfigSurface> configs, List<CodebaseMapDiagnostic> diagnostics, CancellationToken ct)
    {
        var text = TryReadText(file, diagnostics);
        if (text is null)
            return;

        try
        {
            using var document = JsonDocument.Parse(text);
            WalkJsonConfig(file.RelativePath, document.RootElement, [], configs, ct);
        }
        catch (JsonException ex)
        {
            diagnostics.Add(new CodebaseMapDiagnostic
            {
                Severity = CodebaseMapDiagnosticSeverity.Warning,
                Code = "config_parse_failed",
                Message = $"Could not parse config '{file.RelativePath}': {ex.Message}",
                Path = file.RelativePath
            });
        }
    }

    private static void WalkJsonConfig(string path, JsonElement element, IReadOnlyList<string> segments, List<CodebaseConfigSurface> configs, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (element.ValueKind != JsonValueKind.Object)
        {
            if (segments.Count > 0)
                configs.Add(ConfigSurface(path, segments));
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            var next = segments.Concat([property.Name]).ToArray();
            configs.Add(ConfigSurface(path, next));
            if (property.Value.ValueKind == JsonValueKind.Object)
                WalkJsonConfig(path, property.Value, next, configs, ct);
        }
    }

    private static CodebaseConfigSurface ConfigSurface(string path, IReadOnlyList<string> segments)
        => new()
        {
            Path = path,
            Section = segments.Count > 0 ? segments[0] : null,
            Key = string.Join(':', segments),
            Sensitive = segments.Any(IsSensitiveKey)
        };

    private static IReadOnlyList<CodebaseTestSurface> DetectTestSurfaces(IReadOnlyList<CodebaseProject> projects, IReadOnlyList<CodebaseModule> modules)
        => projects
            .Where(static project => project.IsTestProject)
            .Select(project => new CodebaseTestSurface
            {
                ProjectName = project.Name,
                ProjectPath = project.Path,
                TestFramework = InferTestFramework(project.PackageReferences),
                RelatedModule = InferRelatedModule(project, modules)
            })
            .OrderBy(static item => item.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string? InferTestFramework(IReadOnlyList<string> packages)
    {
        if (packages.Any(static item => item.Contains("xunit", StringComparison.OrdinalIgnoreCase)))
            return "xunit";
        if (packages.Any(static item => item.Contains("nunit", StringComparison.OrdinalIgnoreCase)))
            return "nunit";
        if (packages.Any(static item => item.Contains("mstest", StringComparison.OrdinalIgnoreCase)))
            return "mstest";
        return null;
    }

    private static string? InferRelatedModule(CodebaseProject project, IReadOnlyList<CodebaseModule> modules)
    {
        var name = project.Name.Replace(".Tests", "", StringComparison.OrdinalIgnoreCase).Replace("Tests", "", StringComparison.OrdinalIgnoreCase);
        return modules.FirstOrDefault(module =>
            !string.Equals(module.ProjectId, project.Id, StringComparison.Ordinal) &&
            (module.Name.Contains(name, StringComparison.OrdinalIgnoreCase) ||
             name.Contains(module.Name, StringComparison.OrdinalIgnoreCase)))?.Id;
    }

    private static HashSet<string> CollectRecentPaths(
        IReadOnlyList<ScannedFile> files,
        CodebaseMapOptions options)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-options.RecentDays);
        return files
            .Where(file => SafeLastModified(file.File) >= cutoff)
            .Select(static file => file.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ClassNames(string text)
    {
        foreach (Match match in ClassPattern.Matches(text))
            yield return match.Groups["name"].Value;
    }

    private static string? TryReadText(ScannedFile file, List<CodebaseMapDiagnostic> diagnostics)
    {
        if (SafeLength(file.File) > MaxTextScanBytes)
            return null;

        try
        {
            return File.ReadAllText(file.File.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new CodebaseMapDiagnostic
            {
                Severity = CodebaseMapDiagnosticSeverity.Warning,
                Code = "file_read_failed",
                Message = $"Could not read '{file.RelativePath}': {ex.Message}",
                Path = file.RelativePath
            });
            return null;
        }
    }

    private static string? TryHash(ScannedFile file, List<CodebaseMapDiagnostic> diagnostics)
    {
        if (SafeLength(file.File) > MaxHashBytes)
            return null;

        try
        {
            using var stream = file.File.OpenRead();
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new CodebaseMapDiagnostic
            {
                Severity = CodebaseMapDiagnosticSeverity.Warning,
                Code = "hash_failed",
                Message = $"Could not hash '{file.RelativePath}': {ex.Message}",
                Path = file.RelativePath
            });
            return null;
        }
    }

    private static bool IsConfigFile(string path)
    {
        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(path);
        return ConfigFileNames.Contains(fileName) ||
               fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) && extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
               fileName.StartsWith("openclaw", StringComparison.OrdinalIgnoreCase) && extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
               extension is ".yaml" or ".yml";
    }

    private static bool IsSensitiveKey(string key)
        => key.Contains("key", StringComparison.OrdinalIgnoreCase) ||
           key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
           key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
           key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
           key.Contains("credential", StringComparison.OrdinalIgnoreCase);

    private static string InferSurfaceCategory(string path)
    {
        if (path.Contains("Memory", StringComparison.OrdinalIgnoreCase))
            return "memory";
        if (path.Contains("Browser", StringComparison.OrdinalIgnoreCase))
            return "browser";
        if (path.Contains("Payment", StringComparison.OrdinalIgnoreCase))
            return "payments";
        if (path.Contains("External", StringComparison.OrdinalIgnoreCase))
            return "external";
        return "tools";
    }

    private static bool IsMutatingSurface(string name, string text)
        => name.Contains("write", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("delete", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("execute", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("shell", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("payment", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("Mutating", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("FileAccess.Write", StringComparison.OrdinalIgnoreCase);

    private static string InferProviderType(string className, string path)
    {
        if (className.Contains("OpenAi", StringComparison.OrdinalIgnoreCase) || path.Contains("OpenAi", StringComparison.OrdinalIgnoreCase))
            return "openai_compatible";
        if (className.Contains("Ollama", StringComparison.OrdinalIgnoreCase))
            return "ollama";
        if (className.Contains("Gemini", StringComparison.OrdinalIgnoreCase))
            return "gemini";
        if (className.Contains("Anthropic", StringComparison.OrdinalIgnoreCase))
            return "anthropic";
        return "provider";
    }

    private static long SafeLength(FileInfo file)
    {
        try { return file.Length; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private static DateTimeOffset SafeLastModified(FileInfo file)
    {
        try { return file.LastWriteTimeUtc; }
        catch (IOException) { return DateTimeOffset.MinValue; }
        catch (UnauthorizedAccessException) { return DateTimeOffset.MinValue; }
    }

    private static string StableId(string prefix, string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant()[..8];
        var name = SanitizeId(Path.GetFileNameWithoutExtension(key));
        return $"{prefix}_{name}_{hash}";
    }

    private static string UniqueId(string id, HashSet<string> used)
    {
        if (used.Add(id))
            return id;

        for (var index = 2; index < int.MaxValue; index++)
        {
            var candidate = $"{id}_{index}";
            if (used.Add(candidate))
                return candidate;
        }

        throw new InvalidOperationException("Could not allocate a unique codebase map id.");
    }

    private static string SanitizeId(string value)
    {
        var builder = new StringBuilder();
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(char.ToLowerInvariant(ch));
            else if (builder.Length == 0 || builder[^1] != '_')
                builder.Append('_');
        }

        var id = builder.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(id) ? "item" : id;
    }

    private static string ToSnakeCase(string value)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < value.Length; index++)
        {
            var ch = value[index];
            if (char.IsUpper(ch) && index > 0 && builder.Length > 0 && builder[^1] != '_')
                builder.Append('_');
            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString().Trim('_');
    }

    private static string RelativePath(string root, string path)
    {
        try
        {
            return NormalizeSlashes(Path.GetRelativePath(root, path));
        }
        catch (ArgumentException)
        {
            return NormalizeSlashes(path);
        }
    }

    private static string NormalizeSlashes(string path)
        => path.Replace('\\', '/');

    private sealed record ScannedFile(FileInfo File, int Depth, string RelativePath);
}

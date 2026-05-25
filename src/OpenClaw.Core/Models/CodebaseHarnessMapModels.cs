namespace OpenClaw.Core.Models;

public static class CodebaseMapModuleKinds
{
    public const string Gateway = "gateway";
    public const string Core = "core";
    public const string Agent = "agent";
    public const string Cli = "cli";
    public const string Companion = "companion";
    public const string Tui = "tui";
    public const string Client = "client";
    public const string Plugin = "plugin";
    public const string Adapter = "adapter";
    public const string Tests = "tests";
    public const string Docs = "docs";
    public const string Samples = "samples";
    public const string Skills = "skills";
    public const string Unknown = "unknown";
}

public static class CodebaseMapArtifactKinds
{
    public const string Solution = "solution";
    public const string Project = "project";
    public const string Source = "source";
    public const string Config = "config";
    public const string Docs = "docs";
    public const string Test = "test";
    public const string Sample = "sample";
    public const string Workflow = "workflow";
    public const string Script = "script";
    public const string Skill = "skill";
    public const string Plugin = "plugin";
    public const string Unknown = "unknown";
}

public static class CodebaseMapDiagnosticSeverity
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Error = "error";
}

public static class CodebaseMapCategories
{
    public const string All = "all";
    public const string Projects = "projects";
    public const string Endpoints = "endpoints";
    public const string Tools = "tools";
    public const string Providers = "providers";
    public const string Channels = "channels";
    public const string Config = "config";
    public const string Tests = "tests";
}

public sealed class CodebaseHarnessMap
{
    public string Id { get; init; } = "";
    public string RepositoryRoot { get; init; } = "";
    public string RepositoryName { get; init; } = "";
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string GeneratorVersion { get; init; } = "";
    public CodebaseMapSummary Summary { get; init; } = new();
    public IReadOnlyList<CodebaseProject> Projects { get; init; } = [];
    public IReadOnlyList<CodebaseModule> Modules { get; init; } = [];
    public IReadOnlyList<CodebaseArtifact> Artifacts { get; init; } = [];
    public IReadOnlyList<CodebaseEndpoint> Endpoints { get; init; } = [];
    public IReadOnlyList<CodebaseToolSurface> ToolSurfaces { get; init; } = [];
    public IReadOnlyList<CodebaseProviderSurface> ProviderSurfaces { get; init; } = [];
    public IReadOnlyList<CodebaseChannelSurface> ChannelSurfaces { get; init; } = [];
    public IReadOnlyList<CodebaseConfigSurface> ConfigSurfaces { get; init; } = [];
    public IReadOnlyList<CodebaseTestSurface> TestSurfaces { get; init; } = [];
    public IReadOnlyList<CodebaseEvidenceLink> EvidenceLinks { get; init; } = [];
    public IReadOnlyList<CodebaseContractLink> ContractLinks { get; init; } = [];
    public IReadOnlyList<CodebaseSharedStateLink> SharedStateLinks { get; init; } = [];
    public IReadOnlyList<CodebaseRuntimeTraceLink> RuntimeTraceLinks { get; init; } = [];
    public IReadOnlyList<CodebaseMapDiagnostic> Diagnostics { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public Dictionary<string, string> Metadata { get; init; } = [];
}

public sealed class CodebaseMapSummary
{
    public int SolutionFilesCount { get; init; }
    public int ProjectFilesCount { get; init; }
    public int SourceFilesCount { get; init; }
    public int TestProjectsCount { get; init; }
    public int EndpointCount { get; init; }
    public int ToolSurfaceCount { get; init; }
    public int ChannelSurfaceCount { get; init; }
    public int ProviderSurfaceCount { get; init; }
    public int ConfigFileCount { get; init; }
    public int RecentChangeCount { get; init; }
    public int WarningCount { get; init; }
}

public sealed class CodebaseProject
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string ProjectType { get; init; } = "unknown";
    public IReadOnlyList<string> TargetFrameworks { get; init; } = [];
    public bool IsTestProject { get; init; }
    public IReadOnlyList<string> PackageReferences { get; init; } = [];
    public IReadOnlyList<string> ProjectReferences { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
}

public sealed class CodebaseModule
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string Kind { get; init; } = CodebaseMapModuleKinds.Unknown;
    public string? ProjectId { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
}

public sealed class CodebaseArtifact
{
    public string Id { get; init; } = "";
    public string Path { get; init; } = "";
    public string Kind { get; init; } = CodebaseMapArtifactKinds.Unknown;
    public string? ProjectId { get; init; }
    public string? ModuleId { get; init; }
    public long SizeBytes { get; init; }
    public DateTimeOffset LastModifiedUtc { get; init; }
    public string? Hash { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? Summary { get; init; }
}

public sealed class CodebaseEndpoint
{
    public string Id { get; init; } = "";
    public string Method { get; init; } = "";
    public string Path { get; init; } = "";
    public string SourceFile { get; init; } = "";
    public bool? AuthRequired { get; init; }
    public string? Scope { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
}

public sealed class CodebaseToolSurface
{
    public string Name { get; init; } = "";
    public string SourceFile { get; init; } = "";
    public string? Category { get; init; }
    public bool ReadOnly { get; init; }
    public bool Mutating { get; init; }
    public bool ApprovalRequired { get; init; }
    public bool SandboxCapable { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
}

public sealed class CodebaseProviderSurface
{
    public string Name { get; init; } = "";
    public string SourceFile { get; init; } = "";
    public string? ProviderType { get; init; }
    public bool? SupportsStreaming { get; init; }
    public bool? SupportsTools { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
}

public sealed class CodebaseChannelSurface
{
    public string Name { get; init; } = "";
    public string SourceFile { get; init; } = "";
    public string? Direction { get; init; }
    public bool? AuthOrSignatureRequired { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
}

public sealed class CodebaseConfigSurface
{
    public string Path { get; init; } = "";
    public string? Section { get; init; }
    public string Key { get; init; } = "";
    public string? Description { get; init; }
    public bool Sensitive { get; init; }
}

public sealed class CodebaseTestSurface
{
    public string ProjectName { get; init; } = "";
    public string ProjectPath { get; init; } = "";
    public string? TestFramework { get; init; }
    public string? RelatedModule { get; init; }
}

public sealed class CodebaseEvidenceLink
{
    public string EvidenceBundleId { get; init; } = "";
    public string? Path { get; init; }
    public string? Summary { get; init; }
}

public sealed class CodebaseContractLink
{
    public string HarnessContractId { get; init; } = "";
    public string? Path { get; init; }
    public string? Summary { get; init; }
}

public sealed class CodebaseSharedStateLink
{
    public string SharedStateId { get; init; } = "";
    public string? SessionId { get; init; }
    public string? Path { get; init; }
    public string? Summary { get; init; }
}

public sealed class CodebaseRuntimeTraceLink
{
    public string RuntimeEventId { get; init; } = "";
    public string? Component { get; init; }
    public string? Action { get; init; }
    public string? Path { get; init; }
    public string? Summary { get; init; }
}

public sealed class CodebaseMapDiagnostic
{
    public string Severity { get; init; } = CodebaseMapDiagnosticSeverity.Warning;
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
    public string? Path { get; init; }
    public string? Recommendation { get; init; }
}

public sealed class CodebaseMapOptions
{
    public bool IncludeHashes { get; init; }
    public bool IncludeRecentChanges { get; init; } = true;
    public bool IncludeEndpoints { get; init; } = true;
    public bool IncludeToolSurfaces { get; init; } = true;
    public bool IncludeProviderSurfaces { get; init; } = true;
    public bool IncludeChannelSurfaces { get; init; } = true;
    public bool IncludeConfigSurfaces { get; init; } = true;
    public bool IncludeTests { get; init; } = true;
    public bool IncludeDocs { get; init; } = true;
    public int MaxFiles { get; init; } = 5000;
    public int MaxDepth { get; init; } = 12;
    public int RecentDays { get; init; } = 30;
    public string Category { get; init; } = CodebaseMapCategories.All;
}

public sealed class CodebaseMapQuery
{
    public string? Root { get; init; }
    public string? Category { get; init; }
    public bool IncludeHashes { get; init; }
    public int RecentDays { get; init; } = 30;
    public int MaxFiles { get; init; } = 5000;
}

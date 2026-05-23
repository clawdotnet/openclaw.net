namespace OpenClaw.Core.Models;

public sealed class StructuredMemoryStatusResponse
{
    public bool Enabled { get; set; }
    public string Mode { get; set; } = "mcp";
    public string RepositoryRoot { get; set; } = "";
    public string ResolvedRepositoryRoot { get; set; } = "";
    public string McpCommand { get; set; } = "fractalmem-mcp";
    public string AutoContextMode { get; set; } = "off";
    public bool AllowWrites { get; set; }
    public bool WriteToolsAvailable { get; set; }
    public bool Available { get; set; }
    public string Status { get; set; } = "disabled";
    public string? Error { get; set; }
    public IReadOnlyList<string> Warnings { get; set; } = [];
    public StructuredMemoryValidationResult? Validation { get; set; }
}

public sealed class StructuredMemorySearchResult
{
    public bool Success { get; set; }
    public string Query { get; set; } = "";
    public string? Scope { get; set; }
    public IReadOnlyList<StructuredMemorySourceRef> Items { get; set; } = [];
    public string? Error { get; set; }
}

public sealed class StructuredMemoryOpenResult
{
    public bool Success { get; set; }
    public string Path { get; set; } = "";
    public string? Title { get; set; }
    public string? Summary { get; set; }
    public int Depth { get; set; }
    public string View { get; set; } = "index";
    public string? Content { get; set; }
    public IReadOnlyList<StructuredMemorySourceRef> Children { get; set; } = [];
    public IReadOnlyList<StructuredMemorySourceRef> SuggestedReads { get; set; } = [];
    public IReadOnlyList<StructuredMemorySourceRef> RecentTimeline { get; set; } = [];
    public IReadOnlyList<StructuredMemorySourceRef> RecentDecisions { get; set; } = [];
    public IReadOnlyList<StructuredMemorySourceRef> Sources { get; set; } = [];
    public string? Error { get; set; }
}

public sealed class StructuredMemoryRecentResult
{
    public bool Success { get; set; }
    public int Days { get; set; }
    public string? Scope { get; set; }
    public IReadOnlyList<StructuredMemorySourceRef> Items { get; set; } = [];
    public string? Error { get; set; }
}

public sealed class StructuredMemoryExportResult
{
    public bool Success { get; set; }
    public string Path { get; set; } = "";
    public string Mode { get; set; } = "compact";
    public string? Title { get; set; }
    public string? Content { get; set; }
    public IReadOnlyList<StructuredMemorySourceRef> Sources { get; set; } = [];
    public int CharCount { get; set; }
    public bool Truncated { get; set; }
    public string? Error { get; set; }
}

public sealed class StructuredMemoryHandoffResult
{
    public bool Success { get; set; }
    public string Path { get; set; } = "";
    public string? HandoffFilePath { get; set; }
    public string? Content { get; set; }
    public IReadOnlyList<StructuredMemorySourceRef> Sources { get; set; } = [];
    public string? Error { get; set; }
}

public sealed class StructuredMemoryValidationResult
{
    public bool Success { get; set; }
    public bool HasErrors { get; set; }
    public IReadOnlyList<StructuredMemoryValidationIssue> Issues { get; set; } = [];
    public string? Summary { get; set; }
    public string? Error { get; set; }
}

public sealed class StructuredMemoryValidationIssue
{
    public string Severity { get; set; } = "";
    public string? Path { get; set; }
    public string Message { get; set; } = "";
}

public sealed class StructuredMemorySourceRef
{
    public string Path { get; set; } = "";
    public string? Title { get; set; }
    public string? FileName { get; set; }
    public string? SourcePath { get; set; }
    public string? SectionHeading { get; set; }
    public int? StartLine { get; set; }
    public int? EndLine { get; set; }
    public string? Snippet { get; set; }
    public double? Score { get; set; }
    public DateTimeOffset? LastModifiedUtc { get; set; }
}

public sealed class StructuredMemoryContextRequest
{
    public string Query { get; set; } = "";
    public string? PathHint { get; set; }
    public string? SessionId { get; set; }
    public string Mode { get; set; } = "manual";
    public int? MaxChars { get; set; }
    public int? MaxTokens { get; set; }
    public string? Scope { get; set; }
}

public sealed class StructuredMemoryContextResult
{
    public bool Success { get; set; }
    public string? Context { get; set; }
    public string? SourcePath { get; set; }
    public string Mode { get; set; } = "compact";
    public bool Truncated { get; set; }
    public IReadOnlyList<StructuredMemorySourceRef> Sources { get; set; } = [];
    public string? Error { get; set; }
}

public sealed class StructuredMemoryPathRequest
{
    public string Path { get; set; } = "";
}

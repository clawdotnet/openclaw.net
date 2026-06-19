using System.Text.Json.Serialization;

namespace OpenClaw.Plugins.TokenJuice.Rules;

[JsonSourceGenerationOptions(WriteIndented = false, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(TokenJuiceRule))]
[JsonSerializable(typeof(List<TokenJuiceRule>))]
internal partial class TokenJuiceJsonContext : JsonSerializerContext { }

public sealed class TokenJuiceRule
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("family")] public string Family { get; set; } = "generic";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("priority")] public int Priority { get; set; }

    [JsonPropertyName("match")] public RuleMatchBlock? Match { get; set; }
    [JsonPropertyName("transforms")] public RuleTransformsBlock? Transforms { get; set; }
    [JsonPropertyName("summarize")] public RuleSummarizeBlock? Summarize { get; set; }
    [JsonPropertyName("failure")] public RuleFailureBlock? Failure { get; set; }
    [JsonPropertyName("counters")] public List<RuleCounter>? Counters { get; set; }
    [JsonPropertyName("filters")] public RuleFiltersBlock? Filters { get; set; }
    [JsonPropertyName("outputMatches")] public List<RuleOutputMatch>? OutputMatches { get; set; }
    [JsonPropertyName("onEmpty")] public string? OnEmpty { get; set; }
    [JsonPropertyName("counterSource")] public string? CounterSource { get; set; }
}

public sealed class RuleMatchBlock
{
    [JsonPropertyName("toolNames")] public List<string>? ToolNames { get; set; }
    [JsonPropertyName("argv0")] public List<string>? Argv0 { get; set; }
    [JsonPropertyName("argvIncludes")] public List<List<string>>? ArgvIncludes { get; set; }
    [JsonPropertyName("argvIncludesAny")] public List<List<string>>? ArgvIncludesAny { get; set; }
    [JsonPropertyName("commandIncludes")] public List<string>? CommandIncludes { get; set; }
    [JsonPropertyName("commandIncludesAny")] public List<string>? CommandIncludesAny { get; set; }
    [JsonPropertyName("commandRegex")] public string? CommandRegex { get; set; }
    [JsonPropertyName("exitCodes")] public List<int>? ExitCodes { get; set; }
    [JsonPropertyName("outputRegex")] public string? OutputRegex { get; set; }
}

public sealed class RuleTransformsBlock
{
    [JsonPropertyName("stripAnsi")] public bool StripAnsi { get; set; }
    [JsonPropertyName("dedupeAdjacent")] public bool DedupeAdjacent { get; set; }
    [JsonPropertyName("trimEmptyEdges")] public bool TrimEmptyEdges { get; set; }
}

public sealed class RuleSummarizeBlock
{
    [JsonPropertyName("head")] public int Head { get; set; } = 8;
    [JsonPropertyName("tail")] public int Tail { get; set; } = 8;
}

public sealed class RuleFailureBlock
{
    [JsonPropertyName("preserveOnFailure")] public bool PreserveOnFailure { get; set; }
    [JsonPropertyName("head")] public int Head { get; set; } = 12;
    [JsonPropertyName("tail")] public int Tail { get; set; } = 12;
}

public sealed class RuleCounter
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("pattern")] public string Pattern { get; set; } = string.Empty;
    [JsonPropertyName("flags")] public string? Flags { get; set; }
}

public sealed class RuleFiltersBlock
{
    [JsonPropertyName("skipPatterns")] public List<string>? SkipPatterns { get; set; }
    [JsonPropertyName("keepPatterns")] public List<string>? KeepPatterns { get; set; }
}

public sealed class RuleOutputMatch
{
    [JsonPropertyName("pattern")] public string Pattern { get; set; } = string.Empty;
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
}

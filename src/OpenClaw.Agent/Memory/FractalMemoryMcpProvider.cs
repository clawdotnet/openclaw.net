using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent.Memory;

public sealed class FractalMemoryMcpProvider : IStructuredMemoryProvider, IStructuredMemoryWorkflowProvider, IAsyncDisposable, IDisposable
{
    private readonly GatewayConfig _config;
    private readonly string? _workspacePath;
    private readonly ILogger<FractalMemoryMcpProvider> _logger;
    private readonly SemaphoreSlim _clientGate = new(1, 1);
    private McpClient? _client;
    private IReadOnlyList<string> _availableTools = [];
    private bool _disposed;

    public FractalMemoryMcpProvider(
        GatewayConfig config,
        string? workspacePath,
        ILogger<FractalMemoryMcpProvider> logger)
    {
        _config = config;
        _workspacePath = workspacePath;
        _logger = logger;
    }

    public async Task<StructuredMemoryStatusResponse> GetStatusAsync(CancellationToken ct)
    {
        var fractal = _config.Memory.Fractal;
        var resolvedRoot = ResolveRepositoryRoot(fractal);
        var warnings = BuildRepositoryWarnings(resolvedRoot);
        var response = new StructuredMemoryStatusResponse
        {
            Enabled = fractal.Enabled,
            Mode = Normalize(fractal.Mode, "mcp"),
            RepositoryRoot = fractal.RepositoryRoot,
            ResolvedRepositoryRoot = resolvedRoot,
            McpCommand = fractal.McpCommand,
            AutoContextMode = Normalize(fractal.AutoContextMode, "off"),
            AllowWrites = fractal.AllowWrites,
            WriteToolsAvailable = false,
            Available = false,
            Status = fractal.Enabled ? "unavailable" : "disabled",
            Warnings = warnings
        };

        if (!fractal.Enabled)
            return response;

        try
        {
            _ = await EnsureClientAsync(ct);
            response.AvailableTools = _availableTools;
            // A successful MCP handshake alone does not prove a repository can be read.
            response.Validation = await ValidateAsync(ct);
            response.Available = response.Validation.Success;
            response.Error = response.Validation.Error;
            response.WriteToolsAvailable = response.Available && fractal.AllowWrites &&
                _availableTools.Contains("memory_handoff_create", StringComparer.Ordinal);
            response.Status = !response.Available ? "unavailable" :
                warnings.Count == 0 && !response.Validation.HasErrors ? "available" : "available_with_warnings";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            response.Error = FriendlyError(ex);
            response.Status = "unavailable";
        }

        return response;
    }

    public async Task<StructuredMemorySearchResult> SearchAsync(string query, int limit, string? scope, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new StructuredMemorySearchResult { Success = false, Error = "query is required." };

        var result = await CallToolAsync(
            "memory_search",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["query"] = query.Trim(),
                ["limit"] = Math.Clamp(limit, 1, 50),
                ["scope"] = NormalizeOptional(scope)
            },
            ct);

        if (!result.Success)
            return new StructuredMemorySearchResult { Success = false, Query = query.Trim(), Scope = NormalizeOptional(scope), Error = result.Error };

        return new StructuredMemorySearchResult
        {
            Success = true,
            Query = query.Trim(),
            Scope = NormalizeOptional(scope),
            Items = ParseSearchItems(result.StructuredContent) ?? ParseSourceRefsFromText(result.Text)
        };
    }

    public async Task<StructuredMemoryOpenResult> OpenAsync(string path, int depth, string view, CancellationToken ct)
    {
        path = RequirePath(path);
        view = NormalizeView(view);
        var depthName = DepthName(depth);
        var result = await CallToolAsync(
            "memory_open",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["path"] = path,
                ["depth"] = depthName,
                ["view"] = ToPascal(view)
            },
            ct);

        if (!result.Success)
            return new StructuredMemoryOpenResult { Success = false, Path = path, Depth = depth, View = view, Error = result.Error };

        return ParseOpenResult(result.StructuredContent, path, depth, view, result.Text);
    }

    public async Task<StructuredMemoryRecentResult> RecentAsync(int days, int limit, string? scope, CancellationToken ct)
    {
        var result = await CallToolAsync(
            "memory_recent",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["days"] = Math.Clamp(days, 1, 3650),
                ["limit"] = Math.Clamp(limit, 1, 100),
                ["scope"] = NormalizeOptional(scope)
            },
            ct);

        if (!result.Success)
            return new StructuredMemoryRecentResult { Success = false, Days = Math.Clamp(days, 1, 3650), Scope = NormalizeOptional(scope), Error = result.Error };

        return new StructuredMemoryRecentResult
        {
            Success = true,
            Days = Math.Clamp(days, 1, 3650),
            Scope = NormalizeOptional(scope),
            Items = ParseRecentItems(result.StructuredContent) ?? ParseSourceRefsFromText(result.Text)
        };
    }

    public async Task<StructuredMemoryExportResult> ExportAsync(string path, string mode, CancellationToken ct)
    {
        path = RequirePath(path);
        mode = NormalizeExportMode(mode);
        var result = await CallToolAsync(
            "memory_export",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["path"] = path,
                ["mode"] = ToPascal(mode)
            },
            ct);

        if (!result.Success)
            return new StructuredMemoryExportResult { Success = false, Path = path, Mode = mode, Error = result.Error };

        return ParseExportResult(result.StructuredContent, path, mode, result.Text);
    }

    public async Task<StructuredMemoryHandoffResult> CreateHandoffAsync(string path, CancellationToken ct)
    {
        path = RequirePath(path);
        if (!_config.Memory.Fractal.AllowWrites)
            return new StructuredMemoryHandoffResult { Success = false, Path = path, Error = "Fractal Memory writes are disabled by configuration." };

        var result = await CallToolAsync(
            "memory_handoff_create",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["path"] = path
            },
            ct);

        if (!result.Success)
            return new StructuredMemoryHandoffResult { Success = false, Path = path, Error = result.Error };

        return ParseHandoffResult(result.StructuredContent, path, result.Text);
    }

    public async Task<StructuredMemoryValidationResult> ValidateAsync(CancellationToken ct)
    {
        var result = await CallToolAsync("memory_validate", new Dictionary<string, object?>(StringComparer.Ordinal), ct);
        return result.Success
            ? ParseValidationResult(result.StructuredContent, result.Text)
            : new StructuredMemoryValidationResult { Success = false, Error = result.Error };
    }

    public async Task<StructuredMemoryValidationResult> RefreshIndexAsync(CancellationToken ct)
    {
        if (!_config.Memory.Fractal.AllowWrites)
            return new StructuredMemoryValidationResult { Success = false, Error = "Fractal Memory index refresh is disabled by configuration." };

        var result = await CallToolAsync("memory_index_refresh", new Dictionary<string, object?>(StringComparer.Ordinal), ct);
        return result.Success
            ? ParseValidationResult(result.StructuredContent, result.Text, successSummary: "Fractal Memory index refresh completed.")
            : new StructuredMemoryValidationResult { Success = false, Error = result.Error };
    }

    public async Task<StructuredMemoryWorkflowResult> ExecuteWorkflowAsync(string operation, JsonElement arguments, CancellationToken ct)
    {
        var workflow = FractalMemoryWorkflows.Find(operation);
        if (workflow is null)
            return new() { Error = $"Unsupported Fractal Memory workflow '{operation}'." };
        if (workflow.Validate(arguments) is { } error)
            return new() { Error = error };
        if (workflow.IsMutation(arguments) && !_config.Memory.Fractal.AllowWrites)
            return new() { Error = "Fractal Memory writes are disabled by configuration." };

        var result = await CallToolAsync(workflow.McpToolName,
            arguments.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone(), StringComparer.Ordinal), ct);
        return new StructuredMemoryWorkflowResult
        {
            Success = result.Success,
            Data = result.StructuredContent,
            Text = result.Text,
            Resources = result.Resources,
            Error = result.Error
        };
    }

    public async Task<StructuredMemoryExportResult> BuildContextAsync(string path, int maxCharacters, CancellationToken ct)
    {
        // Discover once, retaining compatibility with the original seven-tool server.
        try
        {
            await EnsureClientAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new() { Path = path, Error = FriendlyError(ex) };
        }
        if (!_availableTools.Contains("memory_context", StringComparer.Ordinal))
            return await ExportAsync(path, _config.Memory.Fractal.DefaultExportMode, ct);

        var result = await CallToolAsync("memory_context", new Dictionary<string, object?>
        {
            ["path"] = path,
            ["maxCharacters"] = Math.Clamp(maxCharacters, 256, 1_000_000)
        }, ct);
        if (!result.Success)
            return new() { Path = path, Mode = "context", Error = result.Error };
        if (!TryGetObject(result.StructuredContent, out var data) || GetString(data, "text") is not { } content)
            return new() { Path = path, Mode = "context", Error = "Fractal Memory returned no structured context text." };

        return new StructuredMemoryExportResult
        {
            Success = true,
            Path = GetString(data, "nodePath") ?? path,
            Mode = "context",
            Content = content,
            CharCount = content.Length,
            Truncated = GetBool(data, "truncated") ?? false,
            Sources = ParseSourceArray(data, "sources")
        };
    }

    public void Dispose()
    {
        // Prefer DisposeAsync; synchronous disposal runs async cleanup off the current context.
        Task.Run(async () => await DisposeAsync().ConfigureAwait(false)).GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        var clientToDispose = System.Threading.Volatile.Read(ref _client);
        if (clientToDispose is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else if (clientToDispose is IDisposable disposable)
            disposable.Dispose();
        _clientGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<ToolCallOutcome> CallToolAsync(
        string toolName,
        Dictionary<string, object?> arguments,
        CancellationToken ct)
    {
        try
        {
            var client = await EnsureClientAsync(ct);
            if (!_availableTools.Contains(toolName, StringComparer.Ordinal))
                return ToolCallOutcome.Fail($"The Fractal Memory MCP server does not provide '{toolName}'. Update FractalMemory.McpServer to a version supporting this operation.");
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));
            var response = await client.CallToolAsync(toolName, CompactArguments(arguments), progress: null, cancellationToken: timeoutCts.Token);
            var text = FormatResponseContent(response);
            if (response.IsError is true)
                return ToolCallOutcome.Fail(string.IsNullOrWhiteSpace(text) ? $"Fractal Memory MCP tool '{toolName}' returned an error." : text);

            return new ToolCallOutcome(true, text, response.StructuredContent?.Clone(), null)
            {
                Resources = (response.Content ?? []).OfType<ResourceLinkBlock>()
                    .Select(link => new StructuredMemoryResourceLink { Uri = link.Uri, Name = link.Name, MimeType = link.MimeType })
                    .ToArray()
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ToolCallOutcome.Fail($"Timed out calling Fractal Memory MCP tool '{toolName}'.");
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogDebug(ex, "Fractal Memory MCP tool {ToolName} failed", toolName);
            return ToolCallOutcome.Fail(FriendlyError(ex));
        }
        catch (McpException ex)
        {
            _logger.LogDebug(ex, "Fractal Memory MCP tool {ToolName} failed", toolName);
            return ToolCallOutcome.Fail(FriendlyError(ex));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug(ex, "Fractal Memory MCP tool {ToolName} failed", toolName);
            return ToolCallOutcome.Fail(FriendlyError(ex));
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Fractal Memory MCP tool {ToolName} failed", toolName);
            return ToolCallOutcome.Fail(FriendlyError(ex));
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Fractal Memory MCP tool {ToolName} failed", toolName);
            return ToolCallOutcome.Fail(FriendlyError(ex));
        }
        catch (TimeoutException ex)
        {
            _logger.LogDebug(ex, "Fractal Memory MCP tool {ToolName} failed", toolName);
            return ToolCallOutcome.Fail(FriendlyError(ex));
        }
        catch (Win32Exception ex)
        {
            _logger.LogDebug(ex, "Fractal Memory MCP tool {ToolName} failed", toolName);
            return ToolCallOutcome.Fail(FriendlyError(ex));
        }
    }

    private async Task<McpClient> EnsureClientAsync(CancellationToken ct)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FractalMemoryMcpProvider));

        var fractal = _config.Memory.Fractal;
        if (!fractal.Enabled)
            throw new InvalidOperationException("Fractal Memory is disabled.");
        if (!string.Equals(Normalize(fractal.Mode, "mcp"), "mcp", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsupported Fractal Memory mode '{fractal.Mode}'.");
        if (string.IsNullOrWhiteSpace(fractal.McpCommand))
            throw new InvalidOperationException("Memory.Fractal.McpCommand is not configured.");

        var root = ResolveRepositoryRoot(fractal);
        if (!string.IsNullOrWhiteSpace(fractal.RepositoryRoot) && !Directory.Exists(root))
            throw new DirectoryNotFoundException($"Fractal Memory repository root was not found: {root}");

        await _clientGate.WaitAsync(ct);
        try
        {
            var existingClient = System.Threading.Volatile.Read(ref _client);
            if (existingClient is not null)
                return existingClient;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = fractal.McpCommand,
                Arguments = fractal.McpArguments,
                WorkingDirectory = string.IsNullOrWhiteSpace(root) ? null : root,
                EnvironmentVariables = string.IsNullOrWhiteSpace(root)
                    ? null
                    : new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["FRACTALMEM_REPOSITORY_ROOT"] = root
                    },
                Name = "fractal-memory"
            });

            var client = await McpClient.CreateAsync(transport, cancellationToken: timeoutCts.Token);
            try
            {
                var tools = await client.ListToolsAsync(cancellationToken: timeoutCts.Token);
                _availableTools = tools.Select(tool => tool.Name).ToArray();
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }
            System.Threading.Volatile.Write(ref _client, client);
            return client;
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"Fractal Memory MCP command '{fractal.McpCommand}' could not be started. Install the MCP server or adjust OpenClaw:Memory:Fractal:McpCommand. {ex.Message}", ex);
        }
        finally
        {
            _clientGate.Release();
        }
    }

    private string ResolveRepositoryRoot(FractalMemoryConfig fractal)
    {
        var workspaceRoot = string.IsNullOrWhiteSpace(_workspacePath)
            ? Directory.GetCurrentDirectory() : Path.GetFullPath(_workspacePath);
        return string.IsNullOrWhiteSpace(fractal.RepositoryRoot)
            ? workspaceRoot : Path.GetFullPath(fractal.RepositoryRoot, workspaceRoot);
    }

    private static IReadOnlyList<string> BuildRepositoryWarnings(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return ["No repository root could be resolved."];
        if (!Directory.Exists(root))
            return [$"Repository root does not exist: {root}"];
        if (!File.Exists(Path.Join(root, ".fractal-memory", "config.yaml")))
            return [$"No .fractal-memory/config.yaml was found under {root}; the MCP server may walk parent directories or report repository discovery errors."];
        return [];
    }

    private static StructuredMemoryOpenResult ParseOpenResult(JsonElement? structured, string path, int depth, string view, string text)
    {
        if (TryGetObject(structured, out var root))
        {
            var children = ParseSourceArray(root, "children");
            var suggestedReads = ParseSourceArray(root, "suggestedReads");
            var timeline = ParseStringArray(root, "recentTimeline")
                .Select(item => new StructuredMemorySourceRef { Path = path, Snippet = item })
                .ToArray();
            var decisions = ParseStringArray(root, "recentDecisions")
                .Select(item => new StructuredMemorySourceRef { Path = path, Snippet = item })
                .ToArray();

            return new StructuredMemoryOpenResult
            {
                Success = true,
                Path = GetString(root, "relativePath") ?? path,
                Title = GetString(root, "title"),
                Summary = GetString(root, "summary"),
                Depth = GetInt(root, "depth") ?? depth,
                View = ReadEnum(root, "view", view, ["index", "state", "timeline", "decisions", "children"]),
                Content = BuildOpenContent(root, text),
                StateTruncated = GetBool(root, "stateTruncated") ?? false,
                Children = children,
                SuggestedReads = suggestedReads,
                RecentTimeline = timeline,
                RecentDecisions = decisions,
                Sources = [.. children, .. suggestedReads, .. timeline, .. decisions]
            };
        }

        return new StructuredMemoryOpenResult
        {
            Success = true,
            Path = path,
            Depth = depth,
            View = view,
            Content = text,
            Sources = [new StructuredMemorySourceRef { Path = path, Snippet = Truncate(text, 500) }]
        };
    }

    private static StructuredMemoryExportResult ParseExportResult(JsonElement? structured, string path, string mode, string text)
    {
        if (TryGetObject(structured, out var root))
        {
            var exportPath = GetString(root, "relativePath") ?? path;
            var sources = ParseAnswerContextSources(root);
            var content = BuildExportContent(root, text);
            return new StructuredMemoryExportResult
            {
                Success = true,
                Path = exportPath,
                Mode = ReadEnum(root, "mode", mode, ["compact", "standard", "verbose"]),
                Title = GetString(root, "title"),
                Content = content,
                Sources = sources.Count == 0 ? [new StructuredMemorySourceRef { Path = exportPath }] : sources,
                CharCount = content.Length
            };
        }

        return new StructuredMemoryExportResult
        {
            Success = true,
            Path = path,
            Mode = mode,
            Content = text,
            Sources = [new StructuredMemorySourceRef { Path = path, Snippet = Truncate(text, 500) }],
            CharCount = text.Length
        };
    }

    private static StructuredMemoryHandoffResult ParseHandoffResult(JsonElement? structured, string path, string text)
    {
        if (TryGetObject(structured, out var root))
        {
            var handoffPath = GetString(root, "handoffFilePath");
            var content = GetString(root, "renderedContent") ?? text;
            return new StructuredMemoryHandoffResult
            {
                Success = true,
                Path = GetString(root, "relativePath") ?? path,
                HandoffFilePath = handoffPath,
                Content = content,
                Sources = ParseSourceArray(root, "sourceReferences")
            };
        }

        return new StructuredMemoryHandoffResult
        {
            Success = true,
            Path = path,
            Content = text,
            Sources = [new StructuredMemorySourceRef { Path = path, Snippet = Truncate(text, 500) }]
        };
    }

    private static StructuredMemoryValidationResult ParseValidationResult(JsonElement? structured, string text, string? successSummary = null)
    {
        if (TryGetObject(structured, out var root))
        {
            var issues = ParseValidationIssues(root);
            return new StructuredMemoryValidationResult
            {
                Success = true,
                HasErrors = GetBool(root, "hasErrors") ?? issues.Any(static issue => string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase)),
                Issues = issues,
                Summary = successSummary ?? BuildValidationSummary(issues)
            };
        }

        return new StructuredMemoryValidationResult
        {
            Success = true,
            HasErrors = text.Contains("error", StringComparison.OrdinalIgnoreCase),
            Summary = string.IsNullOrWhiteSpace(successSummary) ? text : successSummary
        };
    }

    private static IReadOnlyList<StructuredMemorySourceRef>? ParseSearchItems(JsonElement? structured)
    {
        if (!TryGetArrayOrObjectArray(structured, "items", out var items) &&
            !TryGetArrayOrObjectArray(structured, "results", out items))
        {
            return null;
        }

        return items
            .Where(static item => item.ValueKind == JsonValueKind.Object)
            .Select(static item => new StructuredMemorySourceRef
            {
                Path = GetString(item, "relativePath") ?? GetString(item, "path") ?? "",
                Title = GetString(item, "title"),
                SourcePath = GetString(item, "sourcePath"),
                FileName = GetString(item, "matchedFile"),
                SectionHeading = GetString(item, "sectionHeading"),
                StartLine = GetInt(item, "startLine"),
                EndLine = GetInt(item, "endLine"),
                Snippet = GetString(item, "snippet"),
                Score = GetDouble(item, "score")
            })
            .Where(static item => !string.IsNullOrWhiteSpace(item.Path))
            .ToArray();
    }

    private static IReadOnlyList<StructuredMemorySourceRef>? ParseRecentItems(JsonElement? structured)
    {
        if (!TryGetArrayOrObjectArray(structured, "items", out var items) &&
            !TryGetArrayOrObjectArray(structured, "results", out items))
        {
            return null;
        }

        return items
            .Where(static item => item.ValueKind == JsonValueKind.Object)
            .Select(static item => new StructuredMemorySourceRef
            {
                Path = GetString(item, "relativePath") ?? GetString(item, "path") ?? "",
                Title = GetString(item, "title"),
                FileName = GetString(item, "fileName"),
                LastModifiedUtc = GetDateTimeOffset(item, "lastModified")
            })
            .Where(static item => !string.IsNullOrWhiteSpace(item.Path))
            .ToArray();
    }

    private static IReadOnlyList<StructuredMemorySourceRef> ParseSourceArray(JsonElement root, string propertyName)
    {
        if (!TryGetProperty(root, propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
            return [];

        return array.EnumerateArray()
            .Select(item =>
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var value = item.GetString() ?? "";
                    return new StructuredMemorySourceRef { Path = value, Title = value };
                }

                if (item.ValueKind != JsonValueKind.Object)
                    return null;

                return new StructuredMemorySourceRef
                {
                    Path = GetString(item, "nodePath") ?? GetString(item, "relativePath") ?? GetString(item, "path") ?? GetString(item, "sourcePath") ?? "",
                    Title = GetString(item, "title"),
                    SourcePath = GetString(item, "sourcePath"),
                    SectionHeading = GetString(item, "sectionHeading"),
                    StartLine = GetInt(item, "startLine"),
                    EndLine = GetInt(item, "endLine"),
                    Snippet = GetString(item, "excerpt") ?? GetString(item, "snippet")
                };
            })
            .Where(static item => item is not null && (!string.IsNullOrWhiteSpace(item.Path) || !string.IsNullOrWhiteSpace(item.Snippet)))
            .Select(static item => item!)
            .ToArray();
    }

    private static IReadOnlyList<StructuredMemorySourceRef> ParseAnswerContextSources(JsonElement root)
    {
        if (!TryGetProperty(root, "answerContext", out var answerContext) || answerContext.ValueKind != JsonValueKind.Object)
            return [];
        return ParseSourceArray(answerContext, "supportingSources");
    }

    private static IReadOnlyList<StructuredMemoryValidationIssue> ParseValidationIssues(JsonElement root)
    {
        if (!TryGetProperty(root, "issues", out var array) || array.ValueKind != JsonValueKind.Array)
            return [];

        return array.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.Object)
            .Select(static item => new StructuredMemoryValidationIssue
            {
                Severity = ReadEnum(item, "severity", "", ["warning", "error"]),
                Path = GetString(item, "relativePath") ?? GetString(item, "path"),
                Message = GetString(item, "message") ?? ""
            })
            .Where(static issue => !string.IsNullOrWhiteSpace(issue.Message))
            .ToArray();
    }

    private static IReadOnlyList<string> ParseStringArray(JsonElement root, string propertyName)
    {
        if (!TryGetProperty(root, propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
            return [];
        return array.EnumerateArray()
            .Select(static item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText())
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item!)
            .ToArray();
    }

    private static IReadOnlyList<StructuredMemorySourceRef> ParseSourceRefsFromText(string text)
        => string.IsNullOrWhiteSpace(text)
            ? []
            : [new StructuredMemorySourceRef { Path = "unknown", Snippet = Truncate(text, 500) }];

    private static string BuildOpenContent(JsonElement root, string fallback)
    {
        var sb = new StringBuilder();
        AppendField(sb, "Title", GetString(root, "title"));
        AppendField(sb, "Summary", GetString(root, "summary"));
        AppendField(sb, "Index", GetString(root, "indexSummary"));
        AppendField(sb, "Current state", GetString(root, "currentState"));
        AppendList(sb, "Recent timeline", ParseStringArray(root, "recentTimeline"));
        AppendList(sb, "Recent decisions", ParseStringArray(root, "recentDecisions"));
        var text = sb.ToString().Trim();
        return text.Length == 0 ? fallback : text;
    }

    private static string BuildExportContent(JsonElement root, string fallback)
    {
        var sb = new StringBuilder();
        AppendField(sb, "Title", GetString(root, "title"));
        AppendField(sb, "Summary", GetString(root, "summary"));
        AppendField(sb, "Current state", GetString(root, "currentState"));
        AppendList(sb, "Children", ParseStringArray(root, "children"));
        AppendList(sb, "Timeline highlights", ParseStringArray(root, "timelineHighlights"));
        AppendList(sb, "Decision highlights", ParseStringArray(root, "decisionHighlights"));

        if (TryGetProperty(root, "answerContext", out var answerContext) && answerContext.ValueKind == JsonValueKind.Object)
        {
            AppendField(sb, "Project branch", GetString(answerContext, "projectBranch"));
            AppendField(sb, "Current objective", GetString(answerContext, "currentObjective"));
            AppendList(sb, "Key prior decisions", ParseStringArray(answerContext, "keyPriorDecisions"));
            AppendList(sb, "Active constraints", ParseStringArray(answerContext, "activeConstraints"));
            AppendList(sb, "Next best actions", ParseStringArray(answerContext, "nextBestActions"));
            AppendList(sb, "Missing information", ParseStringArray(answerContext, "missingInformation"));
        }

        var text = sb.ToString().Trim();
        return text.Length == 0 ? fallback : text;
    }

    private static string FormatResponseContent(CallToolResult response)
    {
        var parts = new List<string>();
        foreach (var item in response.Content ?? [])
        {
            switch (item)
            {
                case TextContentBlock textBlock when !string.IsNullOrEmpty(textBlock.Text):
                    parts.Add(textBlock.Text);
                    break;
                case EmbeddedResourceBlock { Resource: TextResourceContents resource } when !string.IsNullOrEmpty(resource.Text):
                    parts.Add(resource.Text);
                    break;
            }
        }

        return string.Join("\n\n", parts).Trim();
    }

    private static Dictionary<string, object?> CompactArguments(Dictionary<string, object?> arguments)
        => arguments
            .Where(static pair => pair.Value is not null)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);

    private static bool TryGetObject(JsonElement? element, out JsonElement root)
    {
        root = default;
        if (element is not { } value || value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return false;

        if (value.ValueKind == JsonValueKind.Object)
        {
            root = value;
            return true;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var first = value.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object)
            {
                root = first;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetArrayOrObjectArray(JsonElement? element, string propertyName, out IReadOnlyList<JsonElement> items)
    {
        items = [];
        if (element is not { } value || value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return false;

        if (value.ValueKind == JsonValueKind.Array)
        {
            items = value.EnumerateArray().ToArray();
            return true;
        }

        if (value.ValueKind == JsonValueKind.Object &&
            TryGetProperty(value, propertyName, out var array) &&
            array.ValueKind == JsonValueKind.Array)
        {
            items = array.EnumerateArray().ToArray();
            return true;
        }

        return false;
    }

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        if (root.TryGetProperty(name, out value))
            return true;
        var pascal = char.ToUpperInvariant(name[0]) + name[1..];
        return root.TryGetProperty(pascal, out value);
    }

    private static string? GetString(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var value) || value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }

    private static int? GetInt(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number) ? number : null;
    }

    private static string ReadEnum(JsonElement root, string name, string fallback, string[] names)
    {
        var value = GetString(root, name);
        if (int.TryParse(value, out var ordinal))
            return ordinal >= 0 && ordinal < names.Length ? names[ordinal] : fallback;
        return names.FirstOrDefault(candidate => string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase)) ?? fallback;
    }

    private static double? GetDouble(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            return number;
        return value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), out number) ? number : null;
    }

    private static bool? GetBool(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static DateTimeOffset? GetDateTimeOffset(JsonElement root, string name)
    {
        var value = GetString(root, name);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static void AppendField(StringBuilder sb, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        sb.AppendLine($"{label}:");
        sb.AppendLine(value.Trim());
        sb.AppendLine();
    }

    private static void AppendList(StringBuilder sb, string label, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
            return;
        sb.AppendLine($"{label}:");
        foreach (var value in values)
            sb.AppendLine($"- {value}");
        sb.AppendLine();
    }

    private static string BuildValidationSummary(IReadOnlyList<StructuredMemoryValidationIssue> issues)
        => issues.Count == 0
            ? "Fractal Memory validation completed with no reported issues."
            : $"Fractal Memory validation reported {issues.Count} issue(s).";

    private static string FriendlyError(Exception ex)
        => ex is OperationCanceledException
            ? "Timed out connecting to the Fractal Memory MCP server."
            : ex is InvalidOperationException or DirectoryNotFoundException
            ? ex.Message
            : $"Fractal Memory MCP provider is unavailable: {ex.Message}";

    private static string RequirePath(string path)
        => string.IsNullOrWhiteSpace(path) ? throw new ArgumentException("path is required.", nameof(path)) : path.Trim();

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Normalize(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();

    private static string NormalizeView(string? view)
        => Normalize(view, "index") switch
        {
            "state" => "state",
            "timeline" => "timeline",
            "decisions" => "decisions",
            "children" => "children",
            _ => "index"
        };

    private static string NormalizeExportMode(string? mode)
        => Normalize(mode, "compact") switch
        {
            "standard" => "standard",
            "verbose" => "verbose",
            _ => "compact"
        };

    private static string DepthName(int depth)
        => Math.Clamp(depth, 0, 3) switch
        {
            0 => "Pointer",
            2 => "Working",
            3 => "Deep",
            _ => "Orientation"
        };

    private static string ToPascal(string value)
        => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max];

    private sealed record ToolCallOutcome(bool Success, string Text, JsonElement? StructuredContent, string? Error)
    {
        public IReadOnlyList<StructuredMemoryResourceLink> Resources { get; init; } = [];
        public static ToolCallOutcome Fail(string? error) => new(false, "", null, error ?? "Fractal Memory MCP call failed.");
    }
}

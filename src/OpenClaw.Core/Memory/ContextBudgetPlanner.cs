using System.Text;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Memory;

public sealed class ContextBudgetPlanner
{
    private const int TokenCharEstimate = 4;
    private readonly GatewayConfig _config;
    private readonly IStructuredMemoryProvider _provider;

    public ContextBudgetPlanner(GatewayConfig config, IStructuredMemoryProvider provider)
    {
        _config = config;
        _provider = provider;
    }

    public async Task<StructuredMemoryContextResult> BuildContextAsync(
        StructuredMemoryContextRequest request,
        CancellationToken ct)
    {
        var fractal = _config.Memory.Fractal;
        if (!fractal.Enabled)
            return Fail("Fractal Memory is disabled.");

        var autoMode = NormalizeAutoContextMode(fractal.AutoContextMode);
        var requestedMode = NormalizeAutoContextMode(request.Mode);
        if (requestedMode is not ("off" or "manual" or "pulse" or "auto"))
            return Fail($"Unsupported Fractal Memory context mode '{request.Mode}'.");
        if (requestedMode == "off")
            return Fail("Fractal Memory context request mode is off.");
        if (autoMode == "off" && requestedMode is not "manual")
            return Fail("Fractal Memory automatic context is disabled.");
        if (autoMode == "manual" && requestedMode is not "manual")
            return Fail("Fractal Memory automatic context is set to manual only.");
        if (requestedMode == "auto" && autoMode is not "auto")
            return Fail($"Fractal Memory auto context is configured for '{autoMode}', not 'auto'.");
        if (requestedMode == "pulse" && autoMode is not ("pulse" or "auto"))
            return Fail($"Fractal Memory pulse context is configured for '{autoMode}', not 'pulse' or 'auto'.");

        var mode = NormalizeExportMode(fractal.DefaultExportMode);
        StructuredMemoryExportResult export;
        var sourcePath = NormalizePath(request.PathHint);

        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            export = await _provider.ExportAsync(sourcePath, mode, ct);
        }
        else
        {
            sourcePath = await ResolveBestPathAsync(request, ct);
            if (string.IsNullOrWhiteSpace(sourcePath))
                return Fail("No Fractal Memory node matched the context request.");

            export = await _provider.ExportAsync(sourcePath, mode, ct);
        }

        if (!export.Success)
            return Fail(export.Error ?? "Fractal Memory export failed.", sourcePath);

        var context = BuildContextBlock(export, fractal.DefaultDepth);
        var maxChars = ResolveMaxChars(request, fractal);
        var truncated = export.Truncated;
        if (context.Length > maxChars)
        {
            const string marker = "\n...[truncated]\n</fractal_memory_context>";
            var contentBudget = Math.Max(0, maxChars - marker.Length);
            context = contentBudget == 0
                ? marker[..Math.Min(marker.Length, maxChars)]
                : context[..Math.Min(context.Length, contentBudget)].TrimEnd() + marker;
            truncated = true;
        }

        return new StructuredMemoryContextResult
        {
            Success = true,
            Context = context,
            SourcePath = sourcePath,
            Mode = mode,
            Truncated = truncated,
            Sources = export.Sources
        };
    }

    private async Task<string?> ResolveBestPathAsync(StructuredMemoryContextRequest request, CancellationToken ct)
    {
        var query = string.IsNullOrWhiteSpace(request.Query) ? request.SessionId ?? "" : request.Query.Trim();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var search = await _provider.SearchAsync(query, limit: 3, scope: request.Scope, ct: ct);
            var hit = search.Success
                ? search.Items.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Path))
                : null;
            if (hit is not null)
                return hit.Path;
        }

        var recent = await _provider.RecentAsync(days: 14, limit: 1, scope: request.Scope, ct: ct);
        return recent.Success
            ? recent.Items.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Path))?.Path
            : null;
    }

    private static string BuildContextBlock(StructuredMemoryExportResult export, int depth)
    {
        var generatedAt = DateTimeOffset.UtcNow;
        var sb = new StringBuilder();
        sb.AppendLine("<fractal_memory_context>");
        sb.AppendLine($"Source: {export.Path}");
        sb.AppendLine($"Mode: {export.Mode}");
        sb.AppendLine($"Depth: {depth}");
        sb.AppendLine($"GeneratedAtUtc: {generatedAt:O}");
        sb.AppendLine("Trust: untrusted_reference_data");
        sb.AppendLine();
        if (export.Sources.Count > 0)
        {
            sb.AppendLine("Source labels:");
            foreach (var source in export.Sources.Take(20))
            {
                var label = !string.IsNullOrWhiteSpace(source.SourcePath) ? source.SourcePath : source.Path;
                var line = source.StartLine.HasValue && source.EndLine.HasValue
                    ? $":{source.StartLine}-{source.EndLine}"
                    : "";
                sb.AppendLine($"- {label}{line}");
            }
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(export.Content))
            sb.AppendLine(export.Content.Trim());

        sb.AppendLine("</fractal_memory_context>");
        return sb.ToString();
    }

    private static int ResolveMaxChars(StructuredMemoryContextRequest request, FractalMemoryConfig config)
    {
        static long SafeTokenChars(int tokens)
            => Math.Max(1L, tokens) * TokenCharEstimate;

        var maxChars = Math.Max(1L, request.MaxChars ?? config.MaxContextChars);
        var maxTokenChars = SafeTokenChars(request.MaxTokens ?? config.MaxContextTokens);
        var configMaxChars = Math.Max(1L, config.MaxContextChars);
        var configTokenChars = SafeTokenChars(config.MaxContextTokens);
        var result = Math.Min(maxChars, Math.Min(maxTokenChars, Math.Min(configMaxChars, configTokenChars)));
        return (int)Math.Clamp(result, 1L, int.MaxValue);
    }

    private static string NormalizeExportMode(string? mode)
        => string.IsNullOrWhiteSpace(mode) ? "compact" : mode.Trim().ToLowerInvariant();

    private static string NormalizeAutoContextMode(string? mode)
        => string.IsNullOrWhiteSpace(mode) ? "off" : mode.Trim().ToLowerInvariant();

    private static string? NormalizePath(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : path.Trim();

    private static StructuredMemoryContextResult Fail(string error, string? sourcePath = null)
        => new()
        {
            Success = false,
            SourcePath = sourcePath,
            Error = error
        };
}

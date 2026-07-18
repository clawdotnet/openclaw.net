using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Http;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Enhanced PDF parsing tool that sends a PDF to an external MinerU FastAPI service
/// and converts the result to a structured Markdown file.
///
/// MinerU preserves tables, formulas (LaTeX), headings, and image references.
/// When <see cref="MinerUPdfConfig.ExtractImages"/> is true, extracted images are saved
/// to an "images/" subfolder next to the Markdown file, and image references in the
/// Markdown are rewritten to absolute disk paths so the agent can analyze them visually.
///
/// Workflow:
///   1. POST the PDF as multipart/form-data to <see cref="MinerUPdfConfig.Url"/>/file_parse.
///   2. Receive the Markdown (and optionally base64 images) from the JSON response.
///   3. Save the Markdown and images to disk.
///   4. Return file paths so downstream tools can analyze content and images.
/// </summary>
public sealed class MinerUPdfTool : ITool, IDisposable
{
    private readonly MinerUPdfConfig _config;
    private readonly ToolingConfig _toolingConfig;
    private readonly HttpClient _http;

    public MinerUPdfTool(MinerUPdfConfig config, ToolingConfig? toolingConfig = null)
    {
        _config = config;
        _toolingConfig = toolingConfig ?? new ToolingConfig();
        _http = HttpClientFactory.Create();
        _http.Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds);
    }

    public string Name => "pdf_parse";

    public string Description =>
        "Parse a PDF using MinerU and save the result as a Markdown file. " +
        "Preserves tables, formulas, headings, and image references. " +
        "Returns the path of the saved Markdown file — use read_file to then read and analyze it. " +
        "Use this for all PDFs — especially those with complex layouts, tables, or math formulas. " +
        "Never guess or construct a file path from file name, content, or context. Only use the exact value provided by the system in a [FILE_PATH:...] marker, a /media/... URL, or an absolute path. If none is available, return an error.";

        public string ParameterSchema => """
                {
                    "type": "object",
                    "properties": {
                        "path": {
                            "type": "string",
                            "description": "Path to the PDF. Accepted formats (in priority order): (1) the value inside a [FILE_PATH:...] marker — copy it verbatim; (2) a media URL such as /media/media_71b1bd069d5d4c1a — pass it as-is; (3) a local absolute path. Never guess or construct a path — use whichever form the system provides. If none of these are available, return an error and do not attempt to guess or reconstruct the path from file name, content, or context."
                        },
                        "output_path": {
                            "type": "string",
                            "description": "Where to save the resulting Markdown file. Defaults to <pdf_name>.md in the same directory as the PDF."
                        }
                    },
                    "required": ["path"]
                }
                """;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(argumentsJson);
        var root = doc.RootElement;

        var path = root.GetProperty("path").GetString()!;
        var outputPath = root.TryGetProperty("output_path", out var op) && !string.IsNullOrWhiteSpace(op.GetString())
            ? op.GetString()!
            : null;

        var fullPath = ResolveFilePath(path);

        if (fullPath is null)
            return $"Error: File not found: {path}";

        if (!ToolPathPolicy.IsReadAllowed(_toolingConfig, fullPath))
            return $"Error: Read access denied for path: {path}";

        // Determine output path (next to the PDF by default)
        var mdPath = !string.IsNullOrWhiteSpace(outputPath)
            ? outputPath
            : Path.ChangeExtension(fullPath, ".md");

        var imagesDir = Path.Combine(
            Path.GetDirectoryName(mdPath)!,
            "images",
            Path.GetFileNameWithoutExtension(fullPath));  // per-PDF subdirectory

        var mdDir = Path.GetDirectoryName(mdPath)!;

        // Call MinerU FastAPI
        MinerUParseResult parseResult;
        try
        {
            parseResult = await CallMinerUAsync(fullPath, imagesDir, mdDir, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"Error: MinerU request timed out after {_config.TimeoutSeconds}s. " +
                   "The PDF may be too large or the service is under load.";
        }
        catch (Exception ex)
        {
            return $"Error: MinerU parsing failed — {ex.Message}";
        }

        var mdContent = parseResult.Markdown;

        // Try to save the Markdown to disk
        if (ToolPathPolicy.IsWriteAllowed(_toolingConfig, mdPath))
        {
            try
            {
                await File.WriteAllTextAsync(mdPath, mdContent, Encoding.UTF8, ct);
                var wordCount = CountWords(mdContent);
                var preview = mdContent.Length > 600
                    ? mdContent[..600].TrimEnd() + "\n..."
                    : mdContent;

                var sb = new StringBuilder();
                sb.AppendLine($"Markdown saved to: {mdPath}");
                sb.AppendLine($"Size: {mdContent.Length:N0} chars, ~{wordCount:N0} words");

                if (parseResult.SavedImagePaths.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"Extracted images ({parseResult.SavedImagePaths.Count}):");
                    foreach (var imgPath in parseResult.SavedImagePaths)
                        sb.AppendLine($"[IMAGE_PATH:{imgPath}]");
                    sb.AppendLine();
                    sb.AppendLine("To build a comprehensive document: analyze each image above with your vision capability, ");
                    sb.AppendLine("then read the Markdown file and integrate the image descriptions into the final output.");
                }

                sb.AppendLine();
                sb.AppendLine($"Preview:");
                sb.Append(preview);
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"Error: Failed to write Markdown file '{mdPath}': {ex.Message}";
            }
        }

        // Write not permitted — return content directly, truncated
        var truncated = mdContent.Length > _config.MaxOutputChars
            ? mdContent[.._config.MaxOutputChars] + "\n\n[Output truncated — content exceeds MaxOutputChars]"
            : mdContent;
        return $"Note: Write access not allowed for '{mdPath}'. Returning Markdown content directly:\n\n{truncated}";
    }

    private readonly record struct MinerUParseResult(string Markdown, IReadOnlyList<string> SavedImagePaths);

    /// <summary>
    /// Resolves the PDF path with fallback search in the media-cache directory.
    /// Accepted input formats:
    ///   - Exact disk path (absolute or relative)
    ///   - /media/{id} URL (e.g. /media/media_71b1bd069d5d4c1a)
    ///   - [FILE_URL:/media/{id}] marker (raw unresolved gateway marker)
    ///   - Any path where only the filename is correct (searches media-cache by filename)
    /// Search order:
    ///   1. Exact path as given.
    ///   2. If the value is a /media/{id} URL or a [FILE_URL:/media/{id}] marker,
    ///      glob {media-cache-dir}/{id}.* and return the first match.
    ///   3. Same filename in OPENCLAW_WORKSPACE/memory/media-cache/.
    ///   4. Same filename in {cwd}/memory/media-cache/.
    /// </summary>
    private static string? ResolveFilePath(string path)
    {
        // Strip [FILE_URL:...] wrapper if present
        var normalized = path.Trim();
        if (normalized.StartsWith("[FILE_URL:", StringComparison.OrdinalIgnoreCase) &&
            normalized.EndsWith("]", StringComparison.Ordinal))
        {
            normalized = normalized[10..^1].Trim(); // strip "[FILE_URL:" and "]"
        }

        var exact = ToolPathPolicy.ResolveRealPath(normalized);
        if (File.Exists(exact))
            return exact;

        var workspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE")
                     ?? Directory.GetCurrentDirectory();

        string[] mediaCacheDirs =
        [
            Path.Combine(workspace, "memory", "media-cache"),
            Path.Combine(Directory.GetCurrentDirectory(), "memory", "media-cache"),
        ];

        // Handle "/media/{id}" URL format — extract the ID and glob in media-cache
        if (normalized.StartsWith("/media/", StringComparison.OrdinalIgnoreCase))
        {
            var mediaId = normalized["/media/".Length..].Trim('/');
            if (!string.IsNullOrWhiteSpace(mediaId) &&
                !mediaId.Contains('/') && !mediaId.Contains('\\') && !mediaId.Contains('.'))
            {
                foreach (var dir in mediaCacheDirs)
                {
                    if (!Directory.Exists(dir)) continue;
                    var matches = Directory.GetFiles(dir, mediaId + ".*");
                    var pdf = matches.FirstOrDefault(f =>
                        string.Equals(Path.GetExtension(f), ".pdf", StringComparison.OrdinalIgnoreCase));
                    if (pdf is not null) return pdf;
                    if (matches.Length > 0) return matches[0];
                }
            }
        }

        // Try to find by bare filename in known media-cache locations
        var fileName = Path.GetFileName(normalized);
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        foreach (var dir in mediaCacheDirs)
        {
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private async Task<MinerUParseResult> CallMinerUAsync(string pdfPath, string imagesDir, string mdDir, CancellationToken ct)
    {
        var endpoint = $"{_config.Url.TrimEnd('/')}/file_parse";

        using var form = new MultipartFormDataContent();

        // API requires field name "files" (array) — NOT "file"
        var pdfBytes = await File.ReadAllBytesAsync(pdfPath, ct);
        var fileContent = new ByteArrayContent(pdfBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(fileContent, "files", Path.GetFileName(pdfPath));

        // Common parameters
        form.Add(new StringContent(_config.Backend), "backend");
        form.Add(new StringContent("true"),          "return_md");      // request markdown output

        // Optionally request extracted images as base64 in the response
        if (_config.ExtractImages)
            form.Add(new StringContent("true"), "return_images");

        // Language list — API expects "lang_list" (repeatable form field, treated as array)
        var lang = string.IsNullOrWhiteSpace(_config.Lang) ? "ch" : _config.Lang;
        form.Add(new StringContent(lang), "lang_list");

        // Pipeline / hybrid backend options
        var isPipelineOrHybrid = _config.Backend.StartsWith("pipeline", StringComparison.OrdinalIgnoreCase)
                              || _config.Backend.StartsWith("hybrid",   StringComparison.OrdinalIgnoreCase);
        if (isPipelineOrHybrid)
        {
            form.Add(new StringContent(_config.ParseMethod), "parse_method");
            form.Add(new StringContent(_config.FormulaEnable ? "true" : "false"), "formula_enable");
            form.Add(new StringContent(_config.TableEnable   ? "true" : "false"), "table_enable");
        }

        // HTTP-client backends need a remote inference server URL
        var isHttpClient = _config.Backend.EndsWith("http-client", StringComparison.OrdinalIgnoreCase);
        if (isHttpClient && !string.IsNullOrWhiteSpace(_config.SglangServerUrl))
            form.Add(new StringContent(_config.SglangServerUrl), "server_url");

        var response = await _http.PostAsync(endpoint, form, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} from MinerU — {body}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);

        using var result = JsonDocument.Parse(json);

        if (!TryExtractMarkdown(result.RootElement, out var markdown))
            throw new InvalidOperationException(
                $"MinerU response did not contain 'md_content' or 'md' field. Keys present: " +
                string.Join(", ", result.RootElement.EnumerateObject().Select(p => p.Name)));

        // Extract and save images if enabled
        IReadOnlyList<string> savedImages = [];
        if (_config.ExtractImages)
        {
            var base64Images = ExtractImagesFromResponse(result.RootElement);
            if (base64Images.Count > 0)
            {
                savedImages = await SaveImagesAsync(base64Images, imagesDir, ct);
                // Rewrite image refs in Markdown to paths relative to the md file
                markdown = RewriteMarkdownImagePaths(markdown, imagesDir, mdDir);
            }
        }

        return new MinerUParseResult(markdown, savedImages);
    }

    /// <summary>
    /// Walks the JSON response and collects all image name → base64-data-URI pairs.
    /// Handles both flat { "images": {...} } and nested { "results": { "id": { "images": {...} } } } forms.
    /// </summary>
    private static Dictionary<string, string> ExtractImagesFromResponse(JsonElement element)
    {
        var collected = new Dictionary<string, string>(StringComparer.Ordinal);
        CollectImages(element, collected);
        return collected;
    }

    private static void CollectImages(JsonElement element, Dictionary<string, string> collected)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("images", out var imagesEl) && imagesEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in imagesEl.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        collected.TryAdd(prop.Name, prop.Value.GetString()!);
                }
            }

            foreach (var prop in element.EnumerateObject())
                CollectImages(prop.Value, collected);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectImages(item, collected);
        }
    }

    /// <summary>
    /// Saves base64-encoded images to <paramref name="imagesDir"/> and returns their absolute paths.
    /// </summary>
    private static async Task<IReadOnlyList<string>> SaveImagesAsync(
        Dictionary<string, string> base64Images, string imagesDir, CancellationToken ct)
    {
        Directory.CreateDirectory(imagesDir);
        var paths = new List<string>(base64Images.Count);

        foreach (var (name, dataUri) in base64Images)
        {
            try
            {
                // Strip data URI prefix: "data:image/png;base64,"
                var base64 = dataUri.Contains(',') ? dataUri[(dataUri.IndexOf(',') + 1)..] : dataUri;
                var bytes = Convert.FromBase64String(base64);

                // Sanitize filename — keep only safe characters
                var safeName = Regex.Replace(name, @"[^\w.\-]", "_");
                var destPath = Path.Combine(imagesDir, safeName);

                await File.WriteAllBytesAsync(destPath, bytes, ct);
                paths.Add(destPath);
            }
            catch
            {
                // Skip images that cannot be decoded — don't fail the whole parse
            }
        }

        return paths;
    }

    /// <summary>
    /// Rewrites relative Markdown image references to paths relative to <paramref name="mdDir"/>.
    /// Relative paths work in VS Code preview and are portable across machines.
    /// E.g. <c>![](images/fig-001.png)</c> → <c>![](images/media_xxx/fig-001.png)</c>
    /// </summary>
    private static string RewriteMarkdownImagePaths(string markdown, string imagesDir, string mdDir)
    {
        // Match ![alt](path) where path does not start with http or data:
        return Regex.Replace(
            markdown,
            @"(!\[[^\]]*\]\()(?!https?://|data:)([^)]+)(\))",
            m =>
            {
                // Strip any embedded whitespace MinerU may have added to long paths
                var rawPath = Regex.Replace(m.Groups[2].Value, @"\s+", "").Trim();
                // Apply the same sanitization SaveImagesAsync used when saving to disk
                var fileName = Path.GetFileName(rawPath);
                var safeFileName = Regex.Replace(fileName, @"[^\w.\-]", "_");
                var candidate = Path.Combine(imagesDir, safeFileName);
                // Prefer a relative path so Markdown renders in VS Code and is portable
                var resolvedPath = File.Exists(candidate)
                    ? Path.GetRelativePath(mdDir, candidate).Replace('\\', '/')
                    : Path.GetRelativePath(mdDir, Path.Combine(imagesDir, rawPath)).Replace('\\', '/');
                return $"{m.Groups[1].Value}{resolvedPath}{m.Groups[3].Value}";
            },
            RegexOptions.None);
    }

    private static bool TryExtractMarkdown(JsonElement element, out string markdown)
    {
        // Direct form: { "md_content": "..." } or { "md": "..." }
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("md_content", out var mdContent) && mdContent.ValueKind == JsonValueKind.String)
            {
                markdown = mdContent.GetString() ?? string.Empty;
                return true;
            }

            if (element.TryGetProperty("md", out var md) && md.ValueKind == JsonValueKind.String)
            {
                markdown = md.GetString() ?? string.Empty;
                return true;
            }

            // Nested MinerU form: { "results": { "media_xxx": { "md_content": "..." } } }
            if (element.TryGetProperty("results", out var results) && TryExtractMarkdown(results, out markdown))
                return true;

            foreach (var property in element.EnumerateObject())
            {
                if (TryExtractMarkdown(property.Value, out markdown))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryExtractMarkdown(item, out markdown))
                    return true;
            }
        }

        markdown = string.Empty;
        return false;
    }

    private static int CountWords(string text)
    {
        var count = 0;
        var inWord = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
                inWord = false;
            else if (!inWord)
            {
                inWord = true;
                count++;
            }
        }
        return count;
    }

    public void Dispose() => _http.Dispose();
}

using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Native replica of the OpenClaw pdf-read plugin.
/// Extracts text content from PDF files using a lightweight approach:
/// reads raw PDF streams and extracts text between BT/ET markers.
/// For better accuracy, falls back to external tools (pdftotext) when available.
/// </summary>
public sealed class PdfReadTool : ITool
{
    private readonly PdfReadConfig _config;
    private readonly ToolingConfig _toolingConfig;

    public PdfReadTool(PdfReadConfig config, ToolingConfig? toolingConfig = null)
    {
        _config = config;
        _toolingConfig = toolingConfig ?? new ToolingConfig();
    }

    public string Name => "pdf_read";
    public string Description =>
        "Extract text content from a PDF file. Returns the text from all or specified pages.";
    public string ParameterSchema => """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Path to the PDF file"
            },
            "pages": {
              "type": "string",
              "description": "Page range to extract (e.g., '1-5', '1,3,5'). Default: all pages",
              "default": "all"
            },
            "max_pages": {
              "type": "integer",
              "description": "Maximum pages to extract (0 = all)"
            }
          },
          "required": ["path"]
        }
        """;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var args = JsonDocument.Parse(argumentsJson);
        var path = args.RootElement.GetProperty("path").GetString()!;
        var pages = args.RootElement.TryGetProperty("pages", out var p) ? p.GetString() ?? "all" : "all";
        var maxPages = args.RootElement.TryGetProperty("max_pages", out var mp) ? mp.GetInt32() : _config.MaxPages;

        if (!File.Exists(path))
            return $"Error: File not found: {path}";

        var fullPath = Path.GetFullPath(path);

        // Enforce read path policy — same as FileReadTool
        if (!ToolPathPolicy.IsReadAllowed(_toolingConfig, fullPath))
            return $"Error: Read access denied for path: {path}";

        // Try external pdftotext first (better quality)
        var externalResult = await TryPdfToTextAsync(fullPath, pages, maxPages, ct);
        if (externalResult is not null)
            return TruncateOutput(externalResult);

        // Fall back to basic built-in extraction
        try
        {
            var text = await ExtractTextBasicAsync(fullPath, maxPages, ct);
            return TruncateOutput(text);
        }
        catch (Exception ex)
        {
            return $"Error: Failed to extract PDF text — {ex.Message}. " +
                   "Install 'pdftotext' (poppler-utils) for better PDF support.";
        }
    }

    /// <summary>
    /// Try to use the external 'pdftotext' utility (from poppler-utils) for high-quality extraction.
    /// </summary>
    private async Task<string?> TryPdfToTextAsync(string path, string pages, int maxPages, CancellationToken ct)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "where" : "which",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("pdftotext");

            using var check = System.Diagnostics.Process.Start(psi);
            if (check is null) return null;
            await check.WaitForExitAsync(ct);
            if (check.ExitCode != 0) return null;
        }
        catch
        {
            return null; // pdftotext not available
        }

        var extractPsi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "pdftotext",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Page range handling
        if (pages != "all" && !string.IsNullOrWhiteSpace(pages))
        {
            // pdftotext uses -f (first) and -l (last) flags
            if (pages.Contains('-'))
            {
                var parts = pages.Split('-', 2);
                if (int.TryParse(parts[0], out var first))
                {
                    extractPsi.ArgumentList.Add("-f");
                    extractPsi.ArgumentList.Add(first.ToString());
                }
                if (parts.Length > 1 && int.TryParse(parts[1], out var last))
                {
                    extractPsi.ArgumentList.Add("-l");
                    extractPsi.ArgumentList.Add(last.ToString());
                }
            }
        }

        if (maxPages > 0)
        {
            extractPsi.ArgumentList.Add("-l");
            extractPsi.ArgumentList.Add(maxPages.ToString());
        }

        // Layout mode for better formatting
        extractPsi.ArgumentList.Add("-layout");
        extractPsi.ArgumentList.Add(path);
        extractPsi.ArgumentList.Add("-"); // output to stdout

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        using var process = System.Diagnostics.Process.Start(extractPsi);
        if (process is null) return null;

        using var _ = cts.Token.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        });

        var output = await process.StandardOutput.ReadToEndAsync(cts.Token);
        await process.WaitForExitAsync(cts.Token);

        if (process.ExitCode != 0)
            return null;

        var header = $"PDF: {Path.GetFileName(path)}\n" +
                     $"Extracted via pdftotext\n" +
                     $"Length: {output.Length} chars\n\n";

        return header + output;
    }

    /// <summary>
    /// Basic built-in PDF text extraction. Reads raw PDF byte stream and
    /// extracts visible text strings. Works for simple text-based PDFs but
    /// won't handle scanned/image PDFs.
    /// </summary>
    private static async Task<string> ExtractTextBasicAsync(string path, int maxPages, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct);
        var raw = Encoding.Latin1.GetString(bytes);
        var sb = new StringBuilder();

        sb.AppendLine($"PDF: {Path.GetFileName(path)}");
        sb.AppendLine("Extracted via built-in parser (install pdftotext for better results)");
        sb.AppendLine();

        // Extract text strings from PDF content streams
        // Look for text between parentheses in BT...ET blocks, and hex strings
        var pageCount = 0;
        var inText = false;
        var i = 0;

        while (i < raw.Length - 1)
        {
            // Detect page boundaries (rough heuristic)
            if (i < raw.Length - 4 && raw.AsSpan(i, 4).SequenceEqual("/Page".AsSpan(0, 4)))
            {
                pageCount++;
                if (maxPages > 0 && pageCount > maxPages)
                    break;
            }

            // BT = begin text block
            if (raw[i] == 'B' && raw[i + 1] == 'T' && (i + 2 >= raw.Length || !char.IsLetter(raw[i + 2])))
            {
                inText = true;
                i += 2;
                continue;
            }

            // ET = end text block
            if (raw[i] == 'E' && raw[i + 1] == 'T' && (i + 2 >= raw.Length || !char.IsLetter(raw[i + 2])))
            {
                inText = false;
                sb.Append(' ');
                i += 2;
                continue;
            }

            // Extract text from parenthesized strings within text blocks
            if (inText && raw[i] == '(')
            {
                i++;
                while (i < raw.Length && raw[i] != ')')
                {
                    if (raw[i] == '\\' && i + 1 < raw.Length)
                    {
                        i++; // skip escaped char
                        switch (raw[i])
                        {
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            default: sb.Append(raw[i]); break;
                        }
                    }
                    else
                    {
                        var c = raw[i];
                        if (c >= 32 && c < 127) // printable ASCII
                            sb.Append(c);
                        else if (c == '\n' || c == '\r')
                            sb.Append('\n');
                    }
                    i++;
                }
            }

            i++;
        }

        var text = sb.ToString().Trim();
        if (string.IsNullOrWhiteSpace(text))
            return $"PDF: {Path.GetFileName(path)}\nNo extractable text found. " +
                   "The PDF may be image-based or encrypted. Install 'pdftotext' (poppler-utils) for better extraction.";

        return text;
    }

    private string TruncateOutput(string text)
    {
        if (text.Length <= _config.MaxOutputChars)
            return text;
        return text[.._config.MaxOutputChars] + $"\n\n... (truncated at {_config.MaxOutputChars} characters)";
    }
}

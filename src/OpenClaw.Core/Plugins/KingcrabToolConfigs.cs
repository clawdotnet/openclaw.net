namespace OpenClaw.Core.Plugins;

// kingcrab-specific tool plugin configuration types.
// Restored from pre-content-sync state (Plugins/PluginModels.cs.bak) because upstream
// openclaw.net does not include the ImageAnalyze / MinerUPdf tools but kingcrab does
// (see OpenClaw.Agent/Tools/{ImageAnalyzeTool,MinerUPdfTool}.cs).

public sealed class ImageAnalyzeConfig
{
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Provider for the vision model: "openai", "azure-openai", "openai-compatible", etc.
    /// Can differ from the main LLM provider to route vision calls to a dedicated model.
    /// </summary>
    public string Provider { get; set; } = "openai";

    /// <summary>API key (or env: / raw: secret ref). Inherits from main LLM config if null.</summary>
    public string? ApiKey { get; set; }

    /// <summary>API endpoint. Required for openai-compatible providers.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Vision model name (e.g. "gpt-4o", "gpt-4.1").</summary>
    public string Model { get; set; } = "gpt-4o";

    /// <summary>Maximum images per single analyze call.</summary>
    public int MaxImagesPerCall { get; set; } = 5;

    /// <summary>Maximum output characters for the analysis result.</summary>
    public int MaxOutputChars { get; set; } = 8_000;

    /// <summary>Per-call timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 60;
}

public sealed class MinerUPdfConfig
{
    public bool Enabled { get; set; } = false;

    /// <summary>Base URL of the MinerU FastAPI service (e.g. http://14.103.254.121:8000).</summary>
    public string Url { get; set; } = "http://localhost:8888";

    /// <summary>Backend type: "pipeline", "vlm-transformers", "vlm-sglang-engine", "vlm-sglang-client".</summary>
    public string Backend { get; set; } = "pipeline";

    /// <summary>Parse method for the pipeline backend: "auto", "txt", "ocr".</summary>
    public string ParseMethod { get; set; } = "auto";

    /// <summary>OCR language hint (e.g. "ch", "en"). Used by the pipeline backend.</summary>
    public string Lang { get; set; } = "ch";

    /// <summary>Enable formula (LaTeX) detection. Pipeline backend only.</summary>
    public bool FormulaEnable { get; set; } = true;

    /// <summary>Enable table detection. Pipeline backend only.</summary>
    public bool TableEnable { get; set; } = true;

    /// <summary>SGLang inference server URL. Required when Backend is "vlm-sglang-client".</summary>
    public string? SglangServerUrl { get; set; }

    /// <summary>HTTP request timeout in seconds. PDF parsing can be slow for large files.</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>Maximum output characters when the Markdown cannot be written to disk.</summary>
    public int MaxOutputChars { get; set; } = 200_000;

    /// <summary>
    /// When true, requests MinerU to return extracted images as base64 and saves them
    /// to an "images/" subfolder next to the output Markdown file.
    /// The Markdown image references are rewritten to absolute disk paths so the agent
    /// can pass each [IMAGE_PATH:...] to a vision model for analysis.
    /// </summary>
    public bool ExtractImages { get; set; } = false;
}

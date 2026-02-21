using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// A native interactive headless browser tool leveraging Microsoft.Playwright.
/// Allows agents to query dynamic SPAs, fill forms, and click elements.
/// </summary>
public sealed class BrowserTool : ITool, IAsyncDisposable
{
    private readonly ToolingConfig _config;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _initialized;

    public BrowserTool(ToolingConfig config)
    {
        _config = config;
    }

    public string Name => "browser";
    
    public string Description => 
        "An interactive headless browser. Enables navigation to JS-heavy sites, " +
        "clicking elements, filling inputs, taking screenshots, and extracting text or DOM data.";
    
    public string ParameterSchema => """
        {
          "type": "object",
          "properties": {
            "action": { 
              "type": "string", 
              "enum": ["goto", "click", "fill", "get_text", "evaluate", "screenshot"],
              "description": "The browser action to perform."
            },
            "url": { "type": "string", "description": "URL for goto action." },
            "selector": { "type": "string", "description": "CSS/XPath selector for click, fill, or get_text." },
            "value": { "type": "string", "description": "Text to type for fill action." },
            "script": { "type": "string", "description": "JS script to evaluate. Returns string result." }
          },
          "required": ["action"]
        }
        """;

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        
        await _lock.WaitAsync();
        try
        {
            if (_initialized) return;

            // Ensure Chromium is installed locally before proceeding
            var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
            if (exitCode != 0)
                throw new InvalidOperationException($"Playwright CLI install failed with exit code {exitCode}");

            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = _config.BrowserHeadless,
                Timeout = _config.BrowserTimeoutSeconds * 1000
            });
            _page = await _browser.NewPageAsync();
            
            _initialized = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        // Setup playwright lazily
        await EnsureInitializedAsync();
        
        if (_page is null) return "Error: Browser not initialized.";
        
        using var args = JsonDocument.Parse(argumentsJson);
        var action = args.RootElement.GetProperty("action").GetString();
        
        try
        {
            switch (action)
            {
                case "goto":
                {
                    var url = args.RootElement.GetProperty("url").GetString()!;
                    await _page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.Load });
                    return $"Navigated to {url}. Title: '{await _page.TitleAsync()}'";
                }

                case "click":
                {
                    var cSelector = args.RootElement.GetProperty("selector").GetString()!;
                    await _page.ClickAsync(cSelector);
                    return $"Clicked selector: {cSelector}";
                }

                case "fill":
                {
                    var fSelector = args.RootElement.GetProperty("selector").GetString()!;
                    var value = args.RootElement.GetProperty("value").GetString()!;
                    await _page.FillAsync(fSelector, value);
                    return $"Filled {fSelector} with provided value.";
                }

                case "get_text":
                {
                    if (args.RootElement.TryGetProperty("selector", out var textSel) && !string.IsNullOrWhiteSpace(textSel.GetString()))
                    {
                        var content = await _page.TextContentAsync(textSel.GetString()!);
                        return content ?? "No text found for selector.";
                    }
                    
                    var body = await _page.TextContentAsync("body");
                    return body ?? "Body is empty.";
                }

                case "evaluate":
                {
                    var script = args.RootElement.GetProperty("script").GetString()!;
                    // Run script and serialize string
                    var resultElement = await _page.EvaluateAsync<JsonElement>(script);
                    return resultElement.ToString();
                }

                case "screenshot":
                {
                    var bytes = await _page.ScreenshotAsync(new PageScreenshotOptions { FullPage = true });
                    return $"Screenshot taken. Base64: {Convert.ToBase64String(bytes)}";
                }

                default:
                    return $"Error: Unknown action '{action}'";
            }
        }
        catch (Exception ex)
        {
            return $"Browser action '{action}' failed: {ex.Message}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_page != null) await _page.CloseAsync();
        if (_browser != null) await _browser.CloseAsync();
        _playwright?.Dispose();
        _lock.Dispose();
    }
}

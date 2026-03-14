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
public sealed class BrowserTool : ITool, ISandboxCapableTool, IAsyncDisposable
{
    private const string SandboxProfileDir = "/tmp/openclaw-browser-profile";
    private const string SandboxRunnerScript = """
        const { chromium } = require('playwright');

        (async () => {
          const payload = JSON.parse(process.argv[1] || '{}');
          let context;

          try {
            context = await chromium.launchPersistentContext(payload.userDataDir || '/tmp/openclaw-browser-profile', {
              headless: payload.headless !== false,
              timeout: payload.timeoutMs || 30000
            });

            const page = context.pages()[0] || await context.newPage();
            let output = '';

            switch (payload.action) {
              case 'goto': {
                await page.goto(payload.url, { waitUntil: 'load' });
                const title = await page.title();
                output = `Navigated to ${payload.url}. Title: '${title}'`;
                break;
              }

              case 'click':
                await page.click(payload.selector);
                output = `Clicked selector: ${payload.selector}`;
                break;

              case 'fill':
                await page.fill(payload.selector, payload.value ?? '');
                output = `Filled ${payload.selector} with provided value.`;
                break;

              case 'get_text':
                if (payload.selector) {
                  output = await page.textContent(payload.selector) || 'No text found for selector.';
                } else {
                  output = await page.textContent('body') || 'Body is empty.';
                }
                break;

              case 'evaluate': {
                const value = await page.evaluate((source) => globalThis.eval(source), payload.script);
                output = value == null ? '' : String(value);
                break;
              }

              case 'screenshot': {
                const bytes = await page.screenshot({ fullPage: true });
                output = `Screenshot taken. Base64: ${bytes.toString('base64')}`;
                break;
              }

              default:
                throw new Error(`Unknown action '${payload.action}'`);
            }

            process.stdout.write(output);
          } finally {
            if (context) {
              await context.close();
            }
          }
        })().catch((error) => {
          const message = error && error.message ? error.message : String(error);
          process.stderr.write(message);
          process.exit(1);
        });
        """;

    private readonly ToolingConfig _config;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    public BrowserTool(ToolingConfig config)
    {
        _config = config;
    }

    public string Name => "browser";
    
    public string Description => 
        "An interactive headless browser. Enables navigation to JS-heavy sites, " +
        "clicking elements, filling inputs, taking screenshots, and extracting text or DOM data.";
    public ToolSandboxMode DefaultSandboxMode => ToolSandboxMode.Prefer;
    
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

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;
        
        await _lock.WaitAsync(ct);
        try
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(BrowserTool));

            if (_initialized) return;

            // Ensure Chromium is installed locally before proceeding
            var exitCode = await Task.Run(() => Microsoft.Playwright.Program.Main(["install", "chromium"]), ct);
            if (exitCode != 0)
                throw new InvalidOperationException($"Playwright CLI install failed with exit code {exitCode}");

            ct.ThrowIfCancellationRequested();

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
        await EnsureInitializedAsync(ct);

        await _lock.WaitAsync(ct);
        try
        {
            if (_disposed)
                return "Error: Browser tool is disposed.";

            if (_page is null)
                return "Error: Browser not initialized.";

            using var args = JsonDocument.Parse(argumentsJson);
            var action = args.RootElement.GetProperty("action").GetString();

            using var cancellationRegistration = ct.Register(() =>
            {
                var page = _page;
                if (page is not null)
                {
                    var ignoredTask = ClosePageBestEffortAsync(page);
                }
            });

            switch (action)
            {
                case "goto":
                {
                    var url = args.RootElement.GetProperty("url").GetString()!;
                    await WithCancellationAsync(
                        _page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.Load }), ct);
                    var title = await WithCancellationAsync(_page.TitleAsync(), ct);
                    return $"Navigated to {url}. Title: '{title}'";
                }

                case "click":
                {
                    var cSelector = args.RootElement.GetProperty("selector").GetString()!;
                    await WithCancellationAsync(_page.ClickAsync(cSelector), ct);
                    return $"Clicked selector: {cSelector}";
                }

                case "fill":
                {
                    var fSelector = args.RootElement.GetProperty("selector").GetString()!;
                    var value = args.RootElement.GetProperty("value").GetString()!;
                    await WithCancellationAsync(_page.FillAsync(fSelector, value), ct);
                    return $"Filled {fSelector} with provided value.";
                }

                case "get_text":
                {
                    if (args.RootElement.TryGetProperty("selector", out var textSel) && !string.IsNullOrWhiteSpace(textSel.GetString()))
                    {
                        var content = await WithCancellationAsync(_page.TextContentAsync(textSel.GetString()!), ct);
                        return content ?? "No text found for selector.";
                    }
                    
                    var body = await WithCancellationAsync(_page.TextContentAsync("body"), ct);
                    return body ?? "Body is empty.";
                }

                case "evaluate":
                {
                    if (!_config.AllowBrowserEvaluate)
                        return "Error: Browser evaluate is disabled by configuration (Tooling.AllowBrowserEvaluate=false).";

                    var script = args.RootElement.GetProperty("script").GetString()!;
                    // Run script and serialize string
                    var resultElement = await WithCancellationAsync(_page.EvaluateAsync<JsonElement>(script), ct);
                    return resultElement.ToString();
                }

                case "screenshot":
                {
                    var bytes = await WithCancellationAsync(
                        _page.ScreenshotAsync(new PageScreenshotOptions { FullPage = true }), ct);
                    return $"Screenshot taken. Base64: {Convert.ToBase64String(bytes)}";
                }

                default:
                    return $"Error: Unknown action '{action}'";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return "Browser action cancelled.";
        }
        catch (Exception ex)
        {
            return $"Browser action failed: {ex.Message}";
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_disposed)
                return;

            _disposed = true;

            if (_page != null)
                await _page.CloseAsync();
            if (_browser != null)
                await _browser.CloseAsync();

            _playwright?.Dispose();
            _page = null;
            _browser = null;
            _playwright = null;
            _initialized = false;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task ClosePageBestEffortAsync(IPage page)
    {
        try { await page.CloseAsync(); } catch { }
    }

    private static async Task WithCancellationAsync(Task task, CancellationToken ct)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => gate.TrySetResult());

        if (task == await Task.WhenAny(task, gate.Task))
        {
            await task;
            return;
        }

        throw new OperationCanceledException(ct);
    }

    private static async Task<T> WithCancellationAsync<T>(Task<T> task, CancellationToken ct)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => gate.TrySetResult());

        if (task == await Task.WhenAny(task, gate.Task))
            return await task;

        throw new OperationCanceledException(ct);
    }

    public SandboxExecutionRequest CreateSandboxRequest(string argumentsJson)
    {
        var payload = BuildSandboxPayload(argumentsJson);
        var payloadJson = JsonSerializer.Serialize(payload, CoreJsonContext.Default.DictionaryStringObject);
        return new SandboxExecutionRequest
        {
            Command = "node",
            Arguments = ["-e", SandboxRunnerScript, payloadJson]
        };
    }

    public string FormatSandboxResult(string argumentsJson, SandboxResult result)
    {
        if (result.ExitCode == 0)
            return result.Stdout;

        var message = !string.IsNullOrWhiteSpace(result.Stderr)
            ? result.Stderr
            : result.Stdout;

        return string.IsNullOrWhiteSpace(message)
            ? "Browser action failed: Unknown sandbox error."
            : $"Browser action failed: {message}";
    }

    private Dictionary<string, object?> BuildSandboxPayload(string argumentsJson)
    {
        using var args = JsonDocument.Parse(argumentsJson);
        if (!args.RootElement.TryGetProperty("action", out var actionEl) || actionEl.ValueKind != JsonValueKind.String)
            throw new ToolSandboxException("Error: 'action' is required.");

        var action = actionEl.GetString();
        if (string.IsNullOrWhiteSpace(action))
            throw new ToolSandboxException("Error: 'action' is required.");

        if (string.Equals(action, "evaluate", StringComparison.Ordinal) && !_config.AllowBrowserEvaluate)
        {
            throw new ToolSandboxException(
                "Error: Browser evaluate is disabled by configuration (Tooling.AllowBrowserEvaluate=false).");
        }

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["action"] = action,
            ["headless"] = _config.BrowserHeadless,
            ["timeoutMs"] = _config.BrowserTimeoutSeconds * 1000,
            ["userDataDir"] = SandboxProfileDir
        };

        switch (action)
        {
            case "goto":
                payload["url"] = ReadRequiredString(args.RootElement, "url");
                break;

            case "click":
                payload["selector"] = ReadRequiredString(args.RootElement, "selector");
                break;

            case "fill":
                payload["selector"] = ReadRequiredString(args.RootElement, "selector");
                payload["value"] = ReadRequiredString(args.RootElement, "value");
                break;

            case "get_text":
                payload["selector"] = ReadOptionalString(args.RootElement, "selector");
                break;

            case "evaluate":
                payload["script"] = ReadRequiredString(args.RootElement, "script");
                break;

            case "screenshot":
                break;

            default:
                throw new ToolSandboxException($"Error: Unknown action '{action}'");
        }

        return payload;
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            throw new ToolSandboxException($"Error: '{propertyName}' is required.");

        var value = property.GetString();
        if (string.IsNullOrWhiteSpace(value))
            throw new ToolSandboxException($"Error: '{propertyName}' is required.");

        return value;
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}

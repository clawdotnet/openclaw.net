using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Security;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// A native interactive headless browser tool leveraging Microsoft.Playwright.
/// Allows agents to query dynamic SPAs, fill forms, and click elements.
/// </summary>
public sealed class BrowserTool : ITool, ISandboxCapableTool, IAsyncDisposable
{
    private const string SandboxProfileDir = "/tmp/openclaw-browser-profile";
    internal const string LocalExecutionUnavailableMessage =
        "Error: Browser tool requires a configured execution backend or sandbox in this runtime. Local Playwright execution is unavailable.";
    private const string SandboxRunnerScript = """
        const { chromium } = require('playwright');
        const dns = require('dns').promises;
        const net = require('net');

        function globMatches(pattern, value) {
          if (!pattern) return false;
          if (pattern === '*') return true;
          const escaped = String(pattern).replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '.*');
          return new RegExp(`^${escaped}$`, 'i').test(String(value));
        }

        function isBlockedIpv4(address) {
          const parts = address.split('.').map((part) => Number(part));
          if (parts.length !== 4 || parts.some((part) => !Number.isInteger(part) || part < 0 || part > 255)) return true;
          return parts[0] === 0 ||
            parts[0] === 10 ||
            parts[0] === 127 ||
            (parts[0] === 100 && parts[1] >= 64 && parts[1] <= 127) ||
            (parts[0] === 169 && parts[1] === 254) ||
            (parts[0] === 172 && parts[1] >= 16 && parts[1] <= 31) ||
            (parts[0] === 192 && parts[1] === 168) ||
            (parts[0] === 198 && (parts[1] === 18 || parts[1] === 19)) ||
            parts[0] >= 224;
        }

        function ipv4ToInt(address) {
          const parts = address.split('.').map((part) => Number(part));
          if (parts.length !== 4 || parts.some((part) => !Number.isInteger(part) || part < 0 || part > 255)) return null;
          return (((parts[0] << 24) >>> 0) | (parts[1] << 16) | (parts[2] << 8) | parts[3]) >>> 0;
        }

        function cidrMatches(address, cidr) {
          if (net.isIP(address) !== 4) return false;
          const parts = String(cidr || '').split('/');
          if (parts.length !== 2 || net.isIP(parts[0]) !== 4) return false;
          const prefix = Number(parts[1]);
          if (!Number.isInteger(prefix) || prefix < 0 || prefix > 32) return false;
          const value = ipv4ToInt(address);
          const network = ipv4ToInt(parts[0]);
          if (value == null || network == null) return false;
          const mask = prefix === 0 ? 0 : (0xffffffff << (32 - prefix)) >>> 0;
          return (value & mask) === (network & mask);
        }

        function isBlockedIp(address) {
          const version = net.isIP(address);
          if (version === 4) return isBlockedIpv4(address);
          if (version === 6) {
            const value = address.toLowerCase();
            return value === '::' ||
              value === '::1' ||
              value.startsWith('fe80:') ||
              value.startsWith('fc') ||
              value.startsWith('fd') ||
              value.startsWith('ff');
          }
          return true;
        }

        async function validateUrlSafety(rawUrl, policy) {
          if (!policy || policy.enabled === false) return;
          const parsed = new URL(rawUrl);
          if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
            throw new Error('URL safety blocked non-http(s) navigation.');
          }

          const host = parsed.hostname.toLowerCase().replace(/\.$/, '');
          const builtInBlocked = ['localhost', '*.localhost', 'metadata', 'metadata.google.internal'];
          if (policy.blockPrivateNetworkTargets !== false && builtInBlocked.some((pattern) => globMatches(pattern, host))) {
            throw new Error(`URL safety blocked host '${host}'.`);
          }

          for (const pattern of policy.blockedHostGlobs || []) {
            if (globMatches(pattern, host)) throw new Error(`URL safety blocked host '${host}'.`);
          }

          let addresses;
          if (net.isIP(host)) {
            addresses = [host];
          } else if (policy.blockPrivateNetworkTargets !== false || (policy.blockedCidrs || []).length > 0) {
            addresses = (await dns.lookup(host, { all: true })).map((item) => item.address);
          } else {
            addresses = [];
          }

          if (policy.blockPrivateNetworkTargets !== false && addresses.some(isBlockedIp)) {
            throw new Error(`URL safety blocked private or loopback target for '${host}'.`);
          }

          for (const cidr of policy.blockedCidrs || []) {
            if (addresses.some((address) => cidrMatches(address, cidr))) {
              throw new Error(`URL safety blocked CIDR target for '${host}'.`);
            }
          }
        }

        (async () => {
          const payload = JSON.parse(process.argv[1] || '{}');
          payload.urlSafety = JSON.parse(payload.urlSafetyJson || '{}');
          let context;

          try {
            context = await chromium.launchPersistentContext(payload.userDataDir || '/tmp/openclaw-browser-profile', {
              headless: payload.headless !== false,
              timeout: payload.timeoutMs || 30000
            });
            await context.route('**/*', async (route) => {
              try {
                await validateUrlSafety(route.request().url(), payload.urlSafety);
                await route.continue();
              } catch {
                await route.abort();
              }
            });

            const page = context.pages()[0] || await context.newPage();
            let output = '';

            switch (payload.action) {
              case 'goto': {
                await validateUrlSafety(payload.url, payload.urlSafety);
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
    public bool LocalExecutionSupported { get; }
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;
    private readonly RuntimeMetrics? _metrics;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    public BrowserTool(ToolingConfig config, RuntimeMetrics? metrics = null, bool localExecutionSupported = true)
    {
        _config = config;
        _metrics = metrics;
        LocalExecutionSupported = localExecutionSupported;
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
            if (!LocalExecutionSupported)
                throw new InvalidOperationException(LocalExecutionUnavailableMessage);

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
            await ConfigureUrlSafetyAsync(_page);
            
            _initialized = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        if (!LocalExecutionSupported)
            return LocalExecutionUnavailableMessage;

        // Setup playwright lazily
        await EnsureInitializedAsync(ct);

        await _lock.WaitAsync(ct);
        try
        {
            if (_disposed)
                return "Error: Browser tool is disposed.";

            var page = await EnsureActivePageAsync(ct);
            if (page is null)
                return "Error: Browser not initialized.";

            using var args = JsonDocument.Parse(argumentsJson);
            var action = args.RootElement.GetProperty("action").GetString();

            using var cancellationRegistration = ct.Register(() =>
            {
                var currentPage = _page;
                if (!ReferenceEquals(currentPage, page))
                    return;

                _page = null;
                _metrics?.IncrementBrowserCancellationResets();
                _ = ClosePageBestEffortAsync(currentPage);
            });

            switch (action)
            {
                case "goto":
                {
                    var url = args.RootElement.GetProperty("url").GetString()!;
                    var safety = await ValidateBrowserUrlAsync(url, ct);
                    if (!safety.Allowed)
                        return safety.ToToolError();

                    await WithCancellationAsync(
                        page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.Load }), ct);
                    var title = await WithCancellationAsync(page.TitleAsync(), ct);
                    return $"Navigated to {url}. Title: '{title}'";
                }

                case "click":
                {
                    var cSelector = args.RootElement.GetProperty("selector").GetString()!;
                    await WithCancellationAsync(page.ClickAsync(cSelector), ct);
                    return $"Clicked selector: {cSelector}";
                }

                case "fill":
                {
                    var fSelector = args.RootElement.GetProperty("selector").GetString()!;
                    var value = args.RootElement.GetProperty("value").GetString()!;
                    await WithCancellationAsync(page.FillAsync(fSelector, value), ct);
                    return $"Filled {fSelector} with provided value.";
                }

                case "get_text":
                {
                    if (args.RootElement.TryGetProperty("selector", out var textSel) && !string.IsNullOrWhiteSpace(textSel.GetString()))
                    {
                        var content = await WithCancellationAsync(page.TextContentAsync(textSel.GetString()!), ct);
                        return content ?? "No text found for selector.";
                    }
                    
                    var body = await WithCancellationAsync(page.TextContentAsync("body"), ct);
                    return body ?? "Body is empty.";
                }

                case "evaluate":
                {
                    if (!_config.AllowBrowserEvaluate)
                        return "Error: Browser evaluate is disabled by configuration (Tooling.AllowBrowserEvaluate=false).";

                    var script = args.RootElement.GetProperty("script").GetString()!;
                    // Run script and serialize string
                    var resultElement = await WithCancellationAsync(page.EvaluateAsync<JsonElement>(script), ct);
                    return resultElement.ToString();
                }

                case "screenshot":
                {
                    var bytes = await WithCancellationAsync(
                        page.ScreenshotAsync(new PageScreenshotOptions { FullPage = true }), ct);
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
                await ClosePageBestEffortAsync(_page);
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

    private async Task<IPage?> EnsureActivePageAsync(CancellationToken ct)
    {
        if (_browser is null)
            return null;

        if (_page is { IsClosed: false } currentPage)
            return currentPage;

        ct.ThrowIfCancellationRequested();
        _page = await _browser.NewPageAsync();
        await ConfigureUrlSafetyAsync(_page);
        return _page;
    }

    private async Task ConfigureUrlSafetyAsync(IPage page)
    {
        if (!_config.UrlSafety.Enabled)
            return;

        await page.RouteAsync("**/*", async route =>
        {
            try
            {
                if (Uri.TryCreate(route.Request.Url, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    var safety = await UrlSafetyValidator.ValidateHttpUrlAsync(uri, _config.UrlSafety, CancellationToken.None);
                    if (!safety.Allowed)
                    {
                        await route.AbortAsync();
                        return;
                    }
                }

                await route.ContinueAsync();
            }
            catch
            {
                await route.AbortAsync();
            }
        });
    }

    private async ValueTask<UrlSafetyValidationResult> ValidateBrowserUrlAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return UrlSafetyValidationResult.Deny("only absolute http(s) URLs are allowed.");
        }

        return await UrlSafetyValidator.ValidateHttpUrlAsync(uri, _config.UrlSafety, ct);
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
            ["userDataDir"] = SandboxProfileDir,
            ["urlSafetyJson"] = JsonSerializer.Serialize(_config.UrlSafety, CoreJsonContext.Default.UrlSafetyConfig)
        };

        switch (action)
        {
            case "goto":
                payload["url"] = ReadValidatedHttpUrl(args.RootElement, "url");
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

    private string ReadValidatedHttpUrl(JsonElement element, string propertyName)
    {
        var value = ReadRequiredString(element, propertyName);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ToolSandboxException("Error: only absolute http(s) URLs are allowed.");
        }

        var safety = UrlSafetyValidator.ValidateHttpUrl(uri, _config.UrlSafety);
        if (!safety.Allowed)
            throw new ToolSandboxException(safety.ToToolError());

        return value;
    }
}

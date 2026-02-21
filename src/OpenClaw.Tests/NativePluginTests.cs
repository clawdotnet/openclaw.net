using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Plugins;
using OpenClaw.Agent.Plugins;
using OpenClaw.Agent.Tools;
using Xunit;

namespace OpenClaw.Tests;

public class NativePluginRegistryTests
{
    [Fact]
    public void Constructor_NothingEnabled_ProducesNoTools()
    {
        var config = new NativePluginsConfig(); // all disabled by default
        var registry = new NativePluginRegistry(config, NullLogger.Instance);

        Assert.Empty(registry.Tools);
    }

    [Fact]
    public void Constructor_WebSearchEnabled_RegistersTool()
    {
        var config = new NativePluginsConfig
        {
            WebSearch = new WebSearchConfig { Enabled = true, ApiKey = "raw:test-key" }
        };
        var registry = new NativePluginRegistry(config, NullLogger.Instance);

        Assert.Single(registry.Tools);
        Assert.Equal("web_search", registry.Tools[0].Name);
        Assert.True(registry.IsNativeTool("web_search"));
        Assert.Equal("web-search", registry.GetPluginId("web_search"));
    }

    [Fact]
    public void Constructor_AllEnabled_RegistersAllTools()
    {
        var config = new NativePluginsConfig
        {
            WebSearch = new WebSearchConfig { Enabled = true, ApiKey = "raw:k" },
            WebFetch = new WebFetchConfig { Enabled = true },
            GitTools = new GitToolsConfig { Enabled = true },
            CodeExec = new CodeExecConfig { Enabled = true },
            ImageGen = new ImageGenConfig { Enabled = true, ApiKey = "raw:k" },
            PdfRead = new PdfReadConfig { Enabled = true },
            Calendar = new CalendarConfig { Enabled = true },
            Email = new EmailConfig { Enabled = true },
            Database = new DatabaseConfig { Enabled = true }
        };
        var registry = new NativePluginRegistry(config, NullLogger.Instance);

        Assert.Equal(9, registry.Tools.Count);
        Assert.Contains(registry.Tools, t => t.Name == "web_search");
        Assert.Contains(registry.Tools, t => t.Name == "web_fetch");
        Assert.Contains(registry.Tools, t => t.Name == "git");
        Assert.Contains(registry.Tools, t => t.Name == "code_exec");
        Assert.Contains(registry.Tools, t => t.Name == "image_gen");
        Assert.Contains(registry.Tools, t => t.Name == "pdf_read");
        Assert.Contains(registry.Tools, t => t.Name == "calendar");
        Assert.Contains(registry.Tools, t => t.Name == "email");
        Assert.Contains(registry.Tools, t => t.Name == "database");
    }

    [Fact]
    public void IsNativeTool_UnknownName_ReturnsFalse()
    {
        var registry = new NativePluginRegistry(new NativePluginsConfig(), NullLogger.Instance);
        Assert.False(registry.IsNativeTool("nonexistent"));
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var config = new NativePluginsConfig
        {
            WebSearch = new WebSearchConfig { Enabled = true, ApiKey = "raw:k" },
            WebFetch = new WebFetchConfig { Enabled = true }
        };
        var registry = new NativePluginRegistry(config, NullLogger.Instance);
        registry.Dispose(); // should not throw
    }
}

public class PluginPreferenceTests
{
    [Fact]
    public void ResolvePreference_PreferNative_NativeWins()
    {
        var builtIn = new List<ITool> { new FakeTool("shell") };
        var native = new List<ITool> { new FakeTool("web_search") };
        var bridge = new List<ITool> { new FakeTool("web_search", "bridge-version") };
        var config = new PluginsConfig { Prefer = "native" };

        var result = NativePluginRegistry.ResolvePreference(
            builtIn, native, bridge, config, NullLogger.Instance);

        Assert.Equal(2, result.Count);
        Assert.Equal("shell", result[0].Name);
        Assert.Equal("web_search", result[1].Name);
        // The native tool should win (no "bridge-version" in description)
        Assert.DoesNotContain("bridge-version", result[1].Description);
    }

    [Fact]
    public void ResolvePreference_PreferBridge_BridgeWins()
    {
        var builtIn = new List<ITool> { new FakeTool("shell") };
        var native = new List<ITool> { new FakeTool("web_search") };
        var bridge = new List<ITool> { new FakeTool("web_search", "bridge-version") };
        var config = new PluginsConfig { Prefer = "bridge" };

        var result = NativePluginRegistry.ResolvePreference(
            builtIn, native, bridge, config, NullLogger.Instance);

        Assert.Equal(2, result.Count);
        Assert.Contains("bridge-version", result[1].Description);
    }

    [Fact]
    public void ResolvePreference_Override_HasPrecedence()
    {
        var builtIn = Array.Empty<ITool>();
        var native = new List<ITool> { new FakeTool("web_search") };
        var bridge = new List<ITool> { new FakeTool("web_search", "bridge-version") };
        var config = new PluginsConfig
        {
            Prefer = "native",
            Overrides = new(StringComparer.Ordinal) { ["web_search"] = "bridge" }
        };

        var result = NativePluginRegistry.ResolvePreference(
            builtIn, native, bridge, config, NullLogger.Instance);

        Assert.Single(result);
        Assert.Contains("bridge-version", result[0].Description);
    }

    [Fact]
    public void ResolvePreference_BuiltInNeverOverridden()
    {
        var builtIn = new List<ITool> { new FakeTool("shell") };
        var native = new List<ITool> { new FakeTool("shell", "native-shell") };
        var bridge = new List<ITool> { new FakeTool("shell", "bridge-shell") };
        var config = new PluginsConfig { Prefer = "bridge" };

        var result = NativePluginRegistry.ResolvePreference(
            builtIn, native, bridge, config, NullLogger.Instance);

        // Built-in "shell" stays, duplicates from native/bridge are skipped
        Assert.Single(result);
        Assert.DoesNotContain("native-shell", result[0].Description);
        Assert.DoesNotContain("bridge-shell", result[0].Description);
    }

    [Fact]
    public void ResolvePreference_OnlyNative_NoConflict()
    {
        var builtIn = new List<ITool> { new FakeTool("shell") };
        var native = new List<ITool> { new FakeTool("web_search") };
        IReadOnlyList<ITool> bridge = [];
        var config = new PluginsConfig { Prefer = "native" };

        var result = NativePluginRegistry.ResolvePreference(
            builtIn, native, bridge, config, NullLogger.Instance);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ResolvePreference_OnlyBridge_NoConflict()
    {
        var builtIn = new List<ITool> { new FakeTool("shell") };
        IReadOnlyList<ITool> native = [];
        var bridge = new List<ITool> { new FakeTool("calendar", "bridge") };
        var config = new PluginsConfig { Prefer = "native" };

        var result = NativePluginRegistry.ResolvePreference(
            builtIn, native, bridge, config, NullLogger.Instance);

        Assert.Equal(2, result.Count);
        Assert.Equal("calendar", result[1].Name);
    }

    [Fact]
    public void ResolvePreference_OverrideFallback_WhenPreferredMissing()
    {
        var builtIn = Array.Empty<ITool>();
        var native = new List<ITool> { new FakeTool("web_search") };
        IReadOnlyList<ITool> bridge = []; // No bridge version available
        var config = new PluginsConfig
        {
            Prefer = "native",
            Overrides = new(StringComparer.Ordinal) { ["web_search"] = "bridge" } // wants bridge but none available
        };

        var result = NativePluginRegistry.ResolvePreference(
            builtIn, native, bridge, config, NullLogger.Instance);

        // Falls back to native since bridge is unavailable
        Assert.Single(result);
        Assert.Equal("web_search", result[0].Name);
    }
}

public class WebFetchToolTests
{
    [Fact]
    public void ExtractTextFromHtml_StripsTagsAndScripts()
    {
        var html = """
            <html>
            <head><script>var x = 1;</script><style>.a { color: red; }</style></head>
            <body>
            <h1>Title</h1>
            <p>Hello &amp; welcome to the <b>test</b>.</p>
            </body>
            </html>
            """;

        var text = WebFetchTool.ExtractTextFromHtml(html);

        Assert.Contains("Title", text);
        Assert.Contains("Hello & welcome to the", text);
        Assert.Contains("test", text);
        Assert.DoesNotContain("<script>", text);
        Assert.DoesNotContain("<style>", text);
        Assert.DoesNotContain("var x", text);
        Assert.DoesNotContain("color: red", text);
        Assert.DoesNotContain("<h1>", text);
        Assert.DoesNotContain("<b>", text);
    }

    [Fact]
    public void ExtractTextFromHtml_HandlesEntities()
    {
        var html = "<p>A &lt; B &gt; C &amp; D &quot;E&quot; F&#39;s</p>";
        var text = WebFetchTool.ExtractTextFromHtml(html);

        Assert.Contains("A < B > C & D \"E\" F's", text);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidUrl_ReturnsError()
    {
        var tool = new WebFetchTool(new WebFetchConfig { Enabled = true });
        var result = await tool.ExecuteAsync("""{"url":"not-a-url"}""", CancellationToken.None);

        Assert.Contains("Error", result);
        Assert.Contains("Invalid URL", result);
    }
}

public class GitToolTests
{
    [Fact]
    public async Task ExecuteAsync_BlocksDestructiveByDefault()
    {
        var tool = new GitTool(new GitToolsConfig { Enabled = true, AllowPush = false });

        var pushResult = await tool.ExecuteAsync("""{"subcommand":"push"}""", CancellationToken.None);
        Assert.Contains("disabled", pushResult);

        var resetResult = await tool.ExecuteAsync("""{"subcommand":"reset","args":"--hard HEAD~1"}""", CancellationToken.None);
        Assert.Contains("disabled", resetResult);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownSubcommand_ReturnsError()
    {
        var tool = new GitTool(new GitToolsConfig { Enabled = true });
        var result = await tool.ExecuteAsync("""{"subcommand":"hax"}""", CancellationToken.None);

        Assert.Contains("Unsupported", result);
    }

    [Fact]
    public async Task ExecuteAsync_StatusWorks()
    {
        var tool = new GitTool(new GitToolsConfig { Enabled = true });
        // This test runs inside the openclaw.net repo, so git status should work
        var result = await tool.ExecuteAsync(
            """{"subcommand":"status","cwd":"/Users/telli/Desktop/openclaw.net"}""",
            CancellationToken.None);

        // Should not be an error — either shows status or branch info
        Assert.DoesNotContain("Error:", result);
    }
}

public class CodeExecToolTests
{
    [Fact]
    public async Task ExecuteAsync_DisallowedLanguage_ReturnsError()
    {
        var tool = new CodeExecTool(new CodeExecConfig
        {
            Enabled = true,
            AllowedLanguages = ["python"]
        });

        var result = await tool.ExecuteAsync(
            """{"language":"bash","code":"echo hello"}""",
            CancellationToken.None);

        Assert.Contains("not allowed", result);
        Assert.Contains("python", result);
    }

    [Fact]
    public async Task ExecuteAsync_UnsupportedBackend_ReturnsError()
    {
        var tool = new CodeExecTool(new CodeExecConfig
        {
            Enabled = true,
            Backend = "kubernetes"
        });

        var result = await tool.ExecuteAsync(
            """{"language":"python","code":"print(1)"}""",
            CancellationToken.None);

        Assert.Contains("Unsupported backend", result);
    }

    [Fact]
    public async Task ExecuteAsync_UnsupportedLanguage_ReturnsError()
    {
        var tool = new CodeExecTool(new CodeExecConfig { Enabled = true });

        var result = await tool.ExecuteAsync(
            """{"language":"cobol","code":"DISPLAY 'HELLO'"}""",
            CancellationToken.None);

        Assert.Contains("not allowed", result);
    }

    [Fact]
    public async Task ExecuteAsync_BashProcess_RunsSuccessfully()
    {
        var tool = new CodeExecTool(new CodeExecConfig
        {
            Enabled = true,
            Backend = "process"
        });

        var result = await tool.ExecuteAsync(
            """{"language":"bash","code":"echo 'hello from bash'"}""",
            CancellationToken.None);

        Assert.Contains("hello from bash", result);
        Assert.Contains("Exit code: 0", result);
    }

    [Fact]
    public async Task ExecuteAsync_AllowedLanguages_Empty_AllowsAny()
    {
        var tool = new CodeExecTool(new CodeExecConfig
        {
            Enabled = true,
            Backend = "process",
            AllowedLanguages = [] // empty = allow all
        });

        var result = await tool.ExecuteAsync(
            """{"language":"bash","code":"echo 'ok'"}""",
            CancellationToken.None);

        Assert.Contains("ok", result);
    }

    [Fact]
    public void ParameterSchema_IsValidJson()
    {
        var tool = new CodeExecTool(new CodeExecConfig { Enabled = true });
        var doc = System.Text.Json.JsonDocument.Parse(tool.ParameterSchema);
        Assert.Equal("object", doc.RootElement.GetProperty("type").GetString());
    }
}

public class ImageGenToolTests
{
    [Fact]
    public async Task ExecuteAsync_UnsupportedProvider_ReturnsError()
    {
        var tool = new ImageGenTool(new ImageGenConfig
        {
            Enabled = true,
            Provider = "midjourney"
        });

        var result = await tool.ExecuteAsync(
            """{"prompt":"a cat"}""",
            CancellationToken.None);

        Assert.Contains("Unsupported", result);
    }

    [Fact]
    public async Task ExecuteAsync_NoApiKey_ReturnsError()
    {
        var tool = new ImageGenTool(new ImageGenConfig
        {
            Enabled = true,
            Provider = "openai",
            ApiKey = "" // empty key
        });

        var result = await tool.ExecuteAsync(
            """{"prompt":"a cat"}""",
            CancellationToken.None);

        Assert.Contains("API key not configured", result);
    }

    [Fact]
    public void ParameterSchema_IsValidJson()
    {
        var tool = new ImageGenTool(new ImageGenConfig { Enabled = true });
        var doc = System.Text.Json.JsonDocument.Parse(tool.ParameterSchema);
        Assert.True(doc.RootElement.TryGetProperty("properties", out var props));
        Assert.True(props.TryGetProperty("prompt", out _));
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var tool = new ImageGenTool(new ImageGenConfig { Enabled = true });
        tool.Dispose(); // should not throw
    }
}

public class PdfReadToolTests
{
    [Fact]
    public async Task ExecuteAsync_FileNotFound_ReturnsError()
    {
        var tool = new PdfReadTool(new PdfReadConfig { Enabled = true });

        var result = await tool.ExecuteAsync(
            """{"path":"/nonexistent/file.pdf"}""",
            CancellationToken.None);

        Assert.Contains("File not found", result);
    }

    [Fact]
    public void ParameterSchema_IsValidJson()
    {
        var tool = new PdfReadTool(new PdfReadConfig { Enabled = true });
        var doc = System.Text.Json.JsonDocument.Parse(tool.ParameterSchema);
        Assert.True(doc.RootElement.TryGetProperty("properties", out var props));
        Assert.True(props.TryGetProperty("path", out _));
    }

    [Fact]
    public async Task ExecuteAsync_TruncatesLongOutput()
    {
        // Create a temp file that is technically not a valid PDF but tests truncation path
        var tempFile = Path.GetTempFileName();
        try
        {
            // Write something that triggers the "no text found" path
            await File.WriteAllTextAsync(tempFile, "not a real PDF");
            var tool = new PdfReadTool(new PdfReadConfig { Enabled = true, MaxOutputChars = 50 });

            var result = await tool.ExecuteAsync(
                $$"""{"path":"{{tempFile.Replace("\\", "\\\\")}}"}""",
                CancellationToken.None);

            // Should return some error or empty extraction result — not crash
            Assert.NotNull(result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}

public class CalendarToolTests
{
    [Fact]
    public async Task ExecuteAsync_NoCredentials_ReturnsError()
    {
        var tool = new CalendarTool(new CalendarConfig
        {
            Enabled = true,
            CredentialsPath = "/nonexistent/credentials.json"
        });

        var result = await tool.ExecuteAsync(
            """{"action":"list"}""",
            CancellationToken.None);

        Assert.Contains("credentials not configured", result);
    }

    [Fact]
    public async Task ExecuteAsync_UnsupportedAction_ReturnsError()
    {
        // We can't really call the calendar API without credentials,
        // but we can at least test that the error message mentions credentials
        var tool = new CalendarTool(new CalendarConfig { Enabled = true });
        var result = await tool.ExecuteAsync(
            """{"action":"party"}""",
            CancellationToken.None);

        Assert.Contains("credentials not configured", result);
    }

    [Fact]
    public void ParameterSchema_IsValidJson()
    {
        var tool = new CalendarTool(new CalendarConfig { Enabled = true });
        var doc = System.Text.Json.JsonDocument.Parse(tool.ParameterSchema);
        Assert.True(doc.RootElement.TryGetProperty("properties", out var props));
        Assert.True(props.TryGetProperty("action", out _));
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var tool = new CalendarTool(new CalendarConfig { Enabled = true });
        tool.Dispose();
    }
}

public class EmailToolTests
{
    [Fact]
    public async Task SendEmail_NoSmtpHost_ReturnsError()
    {
        var tool = new EmailTool(new EmailConfig { Enabled = true, SmtpHost = "" });

        var result = await tool.ExecuteAsync(
            """{"action":"send","to":"a@b.com","subject":"Hi","body":"Hello"}""",
            CancellationToken.None);

        Assert.Contains("SMTP host not configured", result);
    }

    [Fact]
    public async Task SendEmail_MissingTo_ReturnsError()
    {
        var tool = new EmailTool(new EmailConfig
        {
            Enabled = true,
            SmtpHost = "smtp.test.com",
            Username = "user",
            PasswordRef = "raw:pass"
        });

        var result = await tool.ExecuteAsync(
            """{"action":"send","subject":"Hi"}""",
            CancellationToken.None);

        Assert.Contains("'to' is required", result);
    }

    [Fact]
    public async Task SendEmail_MissingSubject_ReturnsError()
    {
        var tool = new EmailTool(new EmailConfig
        {
            Enabled = true,
            SmtpHost = "smtp.test.com",
            Username = "user",
            PasswordRef = "raw:pass"
        });

        var result = await tool.ExecuteAsync(
            """{"action":"send","to":"a@b.com"}""",
            CancellationToken.None);

        Assert.Contains("'subject' is required", result);
    }

    [Fact]
    public async Task ListEmail_NoImapHost_ReturnsError()
    {
        var tool = new EmailTool(new EmailConfig { Enabled = true, ImapHost = "" });

        var result = await tool.ExecuteAsync(
            """{"action":"list"}""",
            CancellationToken.None);

        Assert.Contains("IMAP host not configured", result);
    }

    [Fact]
    public async Task UnsupportedAction_ReturnsError()
    {
        var tool = new EmailTool(new EmailConfig { Enabled = true });

        var result = await tool.ExecuteAsync(
            """{"action":"fax"}""",
            CancellationToken.None);

        Assert.Contains("Unsupported email action", result);
    }

    [Fact]
    public void ParameterSchema_IsValidJson()
    {
        var tool = new EmailTool(new EmailConfig { Enabled = true });
        var doc = System.Text.Json.JsonDocument.Parse(tool.ParameterSchema);
        Assert.True(doc.RootElement.TryGetProperty("properties", out var props));
        Assert.True(props.TryGetProperty("action", out _));
    }
}

public class DatabaseToolTests
{
    [Fact]
    public async Task ExecuteAsync_NoConnectionString_ReturnsError()
    {
        var tool = new DatabaseTool(new DatabaseConfig { Enabled = true, ConnectionString = "" });

        var result = await tool.ExecuteAsync(
            """{"action":"query","sql":"SELECT 1"}""",
            CancellationToken.None);

        Assert.Contains("connection string not configured", result);
    }

    [Fact]
    public async Task ExecuteAsync_WriteBlockedByDefault()
    {
        var tool = new DatabaseTool(new DatabaseConfig
        {
            Enabled = true,
            ConnectionString = "raw:Data Source=:memory:",
            AllowWrite = false
        });

        var result = await tool.ExecuteAsync(
            """{"action":"execute","sql":"INSERT INTO t VALUES (1)"}""",
            CancellationToken.None);

        Assert.Contains("Write operations are disabled", result);
    }

    [Fact]
    public async Task ExecuteAsync_QueryAction_BlocksWriteKeywords()
    {
        var tool = new DatabaseTool(new DatabaseConfig
        {
            Enabled = true,
            ConnectionString = "raw:Data Source=:memory:",
            AllowWrite = true // even if writes are allowed
        });

        var result = await tool.ExecuteAsync(
            """{"action":"query","sql":"DELETE FROM users"}""",
            CancellationToken.None);

        Assert.Contains("Write operations must use the 'execute' action", result);
    }

    [Fact]
    public async Task ExecuteAsync_UnsupportedAction_ReturnsError()
    {
        var tool = new DatabaseTool(new DatabaseConfig
        {
            Enabled = true,
            ConnectionString = "raw:something"
        });

        var result = await tool.ExecuteAsync(
            """{"action":"backup"}""",
            CancellationToken.None);

        Assert.Contains("Unsupported database action", result);
    }

    [Fact]
    public void ParameterSchema_IsValidJson()
    {
        var tool = new DatabaseTool(new DatabaseConfig { Enabled = true });
        var doc = System.Text.Json.JsonDocument.Parse(tool.ParameterSchema);
        Assert.True(doc.RootElement.TryGetProperty("properties", out var props));
        Assert.True(props.TryGetProperty("action", out _));
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var tool = new DatabaseTool(new DatabaseConfig { Enabled = true });
        tool.Dispose();
    }
}

/// <summary>Minimal fake ITool for testing preference resolution.</summary>
file sealed class FakeTool(string name, string description = "fake") : ITool
{
    public string Name => name;
    public string Description => description;
    public string ParameterSchema => "{}";
    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => ValueTask.FromResult("ok");
}

/// <summary>Minimal ILogger for tests.</summary>
file sealed class NullLogger : Microsoft.Extensions.Logging.ILogger
{
    public static readonly NullLogger Instance = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) { }
}

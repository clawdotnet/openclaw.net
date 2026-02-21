using OpenClaw.Core.Security;
using Xunit;

namespace OpenClaw.Tests;

public class SecurityTests
{
    // ── SecretResolver ─────────────────────────────────────────────

    [Fact]
    public void Resolve_Null_ReturnsNull()
        => Assert.Null(SecretResolver.Resolve(null));

    [Fact]
    public void Resolve_Blank_ReturnsNull()
        => Assert.Null(SecretResolver.Resolve("   "));

    [Fact]
    public void Resolve_RawPrefix_ReturnsLiteral()
        => Assert.Equal("my-secret", SecretResolver.Resolve("raw:my-secret"));

    [Fact]
    public void Resolve_EnvPrefix_ReadsEnvironment()
    {
        Environment.SetEnvironmentVariable("OPENCLAW_TEST_SECRET_321", "env-value");
        try
        {
            Assert.Equal("env-value", SecretResolver.Resolve("env:OPENCLAW_TEST_SECRET_321"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_TEST_SECRET_321", null);
        }
    }

    [Fact]
    public void Resolve_BareString_FallsBackToLiteral()
        => Assert.Equal("some-value", SecretResolver.Resolve("some-value"));

    [Fact]
    public void IsRawRef_True()
        => Assert.True(SecretResolver.IsRawRef("raw:secret"));

    [Fact]
    public void IsRawRef_False()
        => Assert.False(SecretResolver.IsRawRef("env:secret"));

    [Fact]
    public void IsRawRef_Null_False()
        => Assert.False(SecretResolver.IsRawRef(null));

    // ── InputSanitizer ─────────────────────────────────────────────

    [Theory]
    [InlineData(";")]
    [InlineData("|")]
    [InlineData("&")]
    [InlineData("`")]
    [InlineData("$(")]
    [InlineData(">{")]
    [InlineData("\n")]
    [InlineData("\r")]
    public void CheckShellMetaChars_DetectsUnsafeChars(string badInput)
    {
        var result = InputSanitizer.CheckShellMetaChars($"safe-prefix{badInput}safe-suffix", "test");
        Assert.NotNull(result);
        Assert.Contains("disallowed character", result);
    }

    [Theory]
    [InlineData("--oneline")]
    [InlineData("-m 'commit message'")]
    [InlineData("--stat HEAD~3")]
    [InlineData("feature/my-branch")]
    public void CheckShellMetaChars_AllowsSafeStrings(string safeInput)
        => Assert.Null(InputSanitizer.CheckShellMetaChars(safeInput, "test"));

    [Fact]
    public void StripCrlf_RemovesNewlines()
    {
        Assert.Equal("INBOX", InputSanitizer.StripCrlf("IN\r\nBOX"));
        Assert.Equal("test", InputSanitizer.StripCrlf("te\nst"));
        Assert.Equal("test", InputSanitizer.StripCrlf("te\rst"));
    }

    [Fact]
    public void CheckMemoryKey_DetectsPathTraversal()
    {
        Assert.NotNull(InputSanitizer.CheckMemoryKey("../secret"));
        Assert.NotNull(InputSanitizer.CheckMemoryKey("path/to/key"));
        Assert.NotNull(InputSanitizer.CheckMemoryKey("path\\to\\key"));
    }

    [Fact]
    public void CheckMemoryKey_AllowsSafeKeys()
    {
        Assert.Null(InputSanitizer.CheckMemoryKey("my-note"));
        Assert.Null(InputSanitizer.CheckMemoryKey("user_prefs"));
        Assert.Null(InputSanitizer.CheckMemoryKey("project-context-2026"));
    }

    [Fact]
    public void CheckImapFolderName_DetectsControlChars()
    {
        Assert.NotNull(InputSanitizer.CheckImapFolderName("INBOX\r\nA2 LOGOUT"));
        Assert.NotNull(InputSanitizer.CheckImapFolderName("test\0folder"));
    }

    [Fact]
    public void CheckImapFolderName_AllowsNormalFolders()
    {
        Assert.Null(InputSanitizer.CheckImapFolderName("INBOX"));
        Assert.Null(InputSanitizer.CheckImapFolderName("Sent Items"));
        Assert.Null(InputSanitizer.CheckImapFolderName("[Gmail]/All Mail"));
    }

    // ── GitTool shell metachar rejection ────────────────────────────

    [Fact]
    public async Task GitTool_RejectsShellMetaCharsInArgs()
    {
        var config = new OpenClaw.Core.Plugins.GitToolsConfig { Enabled = true };
        var tool = new OpenClaw.Agent.Tools.GitTool(config);
        var result = await tool.ExecuteAsync("""{"subcommand":"status","args":"--porcelain; rm -rf /"}""", CancellationToken.None);
        Assert.Contains("disallowed character", result);
    }

    [Fact]
    public async Task GitTool_AcceptsSafeArgs()
    {
        var config = new OpenClaw.Core.Plugins.GitToolsConfig { Enabled = true };
        var tool = new OpenClaw.Agent.Tools.GitTool(config);
        // This should not error on metachar check (even if git itself errors on missing repo)
        var result = await tool.ExecuteAsync("""{"subcommand":"status","args":"--porcelain"}""", CancellationToken.None);
        Assert.DoesNotContain("disallowed character", result);
    }

    // ── MemoryNoteTool key sanitization ─────────────────────────────

    [Fact]
    public async Task MemoryNoteTool_RejectsPathTraversalKey()
    {
        var store = new OpenClaw.Core.Memory.FileMemoryStore(Path.GetTempPath(), 10);
        var tool = new OpenClaw.Agent.Tools.MemoryNoteTool(store);
        var result = await tool.ExecuteAsync("""{"action":"read","key":"../../etc/passwd"}""", CancellationToken.None);
        Assert.Contains("disallowed characters", result);
    }

    // ── PdfReadTool path policy enforcement ─────────────────────────

    [Fact]
    public async Task PdfReadTool_EnforcesReadPathPolicy()
    {
        var config = new OpenClaw.Core.Plugins.PdfReadConfig { Enabled = true };
        var toolingConfig = new OpenClaw.Core.Models.ToolingConfig
        {
            AllowedReadRoots = ["/allowed-only"]
        };
        var tool = new OpenClaw.Agent.Tools.PdfReadTool(config, toolingConfig);

        // Create a temp PDF-like file outside allowed root
        var tmpFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tmpFile, "%PDF-1.4 dummy");

        try
        {
            var result = await tool.ExecuteAsync($$"""{"path":"{{tmpFile}}"}""", CancellationToken.None);
            Assert.Contains("Read access denied", result);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    // ── DatabaseTool schema sanity ──────────────────────────────────

    [Fact]
    public async Task DatabaseTool_SchemaAction_RejectsUnsafeTableName()
    {
        var config = new OpenClaw.Core.Plugins.DatabaseConfig
        {
            Enabled = true,
            Provider = "sqlite",
            ConnectionString = "raw:Data Source=:memory:"
        };
        var tool = new OpenClaw.Agent.Tools.DatabaseTool(config);

        // Table name with SQL injection attempt — should be caught before any DB call
        var result = await tool.ExecuteAsync("""{"action":"schema","table":"users'; DROP TABLE users;--"}""", CancellationToken.None);
        Assert.Contains("Invalid table name", result);
    }

    [Fact]
    public async Task DatabaseTool_SchemaAction_AcceptsSafeTableName()
    {
        var config = new OpenClaw.Core.Plugins.DatabaseConfig
        {
            Enabled = true,
            Provider = "sqlite",
            ConnectionString = "raw:Data Source=:memory:"
        };
        var tool = new OpenClaw.Agent.Tools.DatabaseTool(config);

        // Valid table name — will fail due to no registered provider, but should NOT say "Invalid table name"
        var result = await tool.ExecuteAsync("""{"action":"schema","table":"users"}""", CancellationToken.None);
        Assert.DoesNotContain("Invalid table name", result);
        // Should fail on provider registration, not table name validation
        Assert.Contains("provider", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── EmailTool IMAP injection ────────────────────────────────────

    [Fact]
    public async Task EmailTool_SearchAction_StripsCrlf()
    {
        var config = new OpenClaw.Core.Plugins.EmailConfig
        {
            Enabled = true,
            ImapHost = "imap.example.com",
            Username = "test",
            PasswordRef = "raw:test"
        };
        var tool = new OpenClaw.Agent.Tools.EmailTool(config);

        // Search with CRLF injection — should get sanitized (will fail on connect, not on injection)
        var result = await tool.ExecuteAsync("""{"action":"search","query":"ALL\r\nA99 LOGOUT","folder":"INBOX"}""", CancellationToken.None);
        // Should not contain unsanitized injection; should fail on IMAP connect, not execute injected command
        Assert.DoesNotContain("A99 LOGOUT", result);
    }

    [Fact]
    public async Task EmailTool_ListAction_RejectsControlCharsInFolder()
    {
        var config = new OpenClaw.Core.Plugins.EmailConfig
        {
            Enabled = true,
            ImapHost = "imap.example.com",
            Username = "test",
            PasswordRef = "raw:test"
        };
        var tool = new OpenClaw.Agent.Tools.EmailTool(config);

        // Folder with null byte
        var result = await tool.ExecuteAsync("{\"action\":\"list\",\"folder\":\"INBOX\\u0000EVIL\"}", CancellationToken.None);
        Assert.Contains("control character", result);
    }
}

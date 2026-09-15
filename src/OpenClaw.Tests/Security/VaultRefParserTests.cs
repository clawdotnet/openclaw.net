using OpenClaw.Security.Vault;
using Xunit;

namespace OpenClaw.Tests.Security;

public sealed class VaultRefParserTests
{
    [Fact]
    public void Parse_FullRef_ReturnsMountPathKey()
    {
        var v = VaultRefParser.Parse("vault:secret/data/openclaw/openai#api_key");
        Assert.Equal("secret", v.Mount);
        Assert.Equal("openclaw/openai", v.Path);
        Assert.Equal("api_key", v.Key);
        Assert.Equal(2, v.KvVersion);
    }

    [Fact]
    public void Parse_DefaultMount_Applied_WhenMissingDataSegment()
    {
        var v = VaultRefParser.Parse("vault:openclaw/openai#api_key", defaultMount: "secret");
        Assert.Equal("secret", v.Mount);
        Assert.Equal("openclaw/openai", v.Path);
    }

    [Fact]
    public void Parse_NestedPath_Preserved()
    {
        var v = VaultRefParser.Parse("vault:secret/data/a/b/c#k");
        Assert.Equal("a/b/c", v.Path);
        Assert.Equal("k", v.Key);
    }

    [Fact]
    public void Parse_MissingHash_Throws()
    {
        Assert.Throws<VaultRefParseException>(() =>
            VaultRefParser.Parse("vault:secret/data/openclaw/openai"));
    }

    [Fact]
    public void Parse_EmptyPath_Throws()
    {
        Assert.Throws<VaultRefParseException>(() =>
            VaultRefParser.Parse("vault:secret/data/#api_key"));
    }

    [Fact]
    public void Parse_EmptyKey_Throws()
    {
        Assert.Throws<VaultRefParseException>(() =>
            VaultRefParser.Parse("vault:secret/data/openclaw/openai#"));
    }

    [Fact]
    public void Parse_NullOrEmpty_Throws()
    {
        Assert.Throws<VaultRefParseException>(() => VaultRefParser.Parse(""));
        Assert.Throws<VaultRefParseException>(() => VaultRefParser.Parse(null!));
    }

    [Fact]
    public void Parse_ExceptionMessage_DoesNotLeakPayload()
    {
        // Spec invariant: exception messages must not echo the resolved value or
        // arbitrary payload segments — only structure hints (path/key wording).
        var ex = Assert.Throws<VaultRefParseException>(() =>
            VaultRefParser.Parse("vault:secret/data/SOMETHING#"));
        // The message must not echo the input payload segment.
        Assert.DoesNotContain("SOMETHING", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("value", ex.Message, StringComparison.OrdinalIgnoreCase);
        // But it must still describe which structural part is wrong.
        Assert.True(
            ex.Message.Contains("path", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("key", StringComparison.OrdinalIgnoreCase));
    }
}

using OpenClaw.StrategosWorkflowHost.Adapters;
using Xunit;

namespace OpenClaw.StrategosWorkflowHost.Tests;

public class EvidenceBundleParserTests
{
    [Fact]
    public void ExtractAuditJson_ReturnsNull_WhenMarkerMissing()
    {
        var result = EvidenceBundleParser.ExtractAuditJson("executed ok\nno audit");
        Assert.Null(result);
    }

    [Fact]
    public void ExtractAuditJson_ReturnsNull_WhenInputIsNull()
        => Assert.Null(EvidenceBundleParser.ExtractAuditJson(null));

    [Fact]
    public void ExtractAuditJson_ReturnsNull_WhenInputIsEmpty()
        => Assert.Null(EvidenceBundleParser.ExtractAuditJson(""));

    [Fact]
    public void ExtractAuditJson_ReturnsBlock_WhenMarkerAtStart()
    {
        var result = EvidenceBundleParser.ExtractAuditJson("AuditTrace:{\"plan\":\"x\",\"reviews\":3,\"approved\":true}");
        Assert.Equal("{\"plan\":\"x\",\"reviews\":3,\"approved\":true}", result);
    }

    [Fact]
    public void ExtractAuditJson_ReturnsBlock_WhenMarkerHasLeadingContent()
    {
        // Mirrors EmitAuditTrace output: prepended ExecutionResult + "\nAuditTrace:..."
        var input = "Executed approved action for: hello\nAuditTrace:{\"plan\":\"p\",\"reviews\":3,\"approved\":true}";
        var result = EvidenceBundleParser.ExtractAuditJson(input);
        Assert.Equal("{\"plan\":\"p\",\"reviews\":3,\"approved\":true}", result);
    }

    [Fact]
    public void ExtractAuditJson_ToleratesTrailingContent()
    {
        var input = "AuditTrace:{\"k\":1}\nsome trailing log line";
        var result = EvidenceBundleParser.ExtractAuditJson(input);
        Assert.Equal("{\"k\":1}", result);
    }

    [Fact]
    public void ExtractAuditJson_IgnoresNestedBraces_AndReturnsOuterBlock()
    {
        // EmitAuditTrace produces a flat JsonObject (no nested objects in current shape),
        // but defend against future contributors who add a nested object.
        var input = "AuditTrace:{\"a\":1,\"b\":{\"c\":2}}";
        var result = EvidenceBundleParser.ExtractAuditJson(input);
        Assert.Equal("{\"a\":1,\"b\":{\"c\":2}}", result);
    }
}
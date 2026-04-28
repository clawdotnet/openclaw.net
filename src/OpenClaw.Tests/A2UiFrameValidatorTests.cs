using OpenClaw.Core.Canvas;
using Xunit;

namespace OpenClaw.Tests;

public sealed class A2UiFrameValidatorTests
{
    [Fact]
    public void ValidateJsonl_AcceptsSupportedV08Frames()
    {
        var frames = string.Join('\n',
            """{"type":"text","id":"txt","text":"Hello"}""",
            """{"type":"button","id":"ok","label":"OK"}""",
            """{"type":"table","id":"tbl","columns":["Name"],"rows":[["A"]]}""",
            """{"type":"progress","id":"prog","value":0.5}""");

        var result = A2UiFrameValidator.ValidateJsonl(frames, maxFrames: 10, maxBytes: 4096);

        Assert.True(result.IsValid);
        Assert.Equal(4, result.FrameCount);
    }

    [Fact]
    public void ValidateJsonl_RejectsInvalidJson()
    {
        var result = A2UiFrameValidator.ValidateJsonl("""{"type":"text","id":"bad","text":""", maxFrames: 10, maxBytes: 4096);

        Assert.False(result.IsValid);
        Assert.Contains("not valid JSON", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateJsonl_RejectsCreateSurfaceV09Frame()
    {
        var result = A2UiFrameValidator.ValidateJsonl("""{"type":"createSurface","id":"surface"}""", maxFrames: 10, maxBytes: 4096);

        Assert.False(result.IsValid);
        Assert.Contains("createSurface", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateJsonl_RejectsFrameAndSizeLimits()
    {
        var tooMany = A2UiFrameValidator.ValidateJsonl(
            """
            {"type":"text","id":"a","text":"A"}
            {"type":"text","id":"b","text":"B"}
            """,
            maxFrames: 1,
            maxBytes: 4096);

        var tooLarge = A2UiFrameValidator.ValidateJsonl("""{"type":"text","id":"a","text":"A"}""", maxFrames: 10, maxBytes: 4);

        Assert.False(tooMany.IsValid);
        Assert.Contains("exceeds 1 frames", tooMany.Error, StringComparison.Ordinal);
        Assert.False(tooLarge.IsValid);
        Assert.Contains("exceeds 4 bytes", tooLarge.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateJsonl_RejectsMissingTypeSpecificFields()
    {
        var result = A2UiFrameValidator.ValidateJsonl("""{"type":"select","id":"choice"}""", maxFrames: 10, maxBytes: 4096);

        Assert.False(result.IsValid);
        Assert.Contains("options", result.Error, StringComparison.Ordinal);
    }
}

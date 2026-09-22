using OpenClaw.Core.Security;
using Xunit;

namespace OpenClaw.Tests.Security;

public sealed class SecretResolutionExceptionTests
{
    [Fact]
    public void Constructor_SetsMessage()
    {
        var ex = new SecretResolutionException("missing key");
        Assert.Equal("missing key", ex.Message);
    }

    [Fact]
    public void IsException()
    {
        Assert.True(new SecretResolutionException("x") is Exception);
    }
}

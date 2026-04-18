using OpenClaw.Core.Security;
using Xunit;

namespace OpenClaw.Tests;

public sealed class BindAddressClassifierTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("*", false)]
    [InlineData("+", false)]
    [InlineData("[::]", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("172.16.0.1", false)]
    public void IsLoopbackBind_ClassifiesCorrectly(string bindAddress, bool expectedIsLoopback)
    {
        Assert.Equal(expectedIsLoopback, BindAddressClassifier.IsLoopbackBind(bindAddress));
    }
}

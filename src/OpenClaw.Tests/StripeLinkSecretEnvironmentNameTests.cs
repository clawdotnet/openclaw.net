using OpenClaw.Payments.Core;
using Xunit;

namespace OpenClaw.Tests;

public sealed class StripeLinkSecretEnvironmentNameTests
{
    [Theory]
    [InlineData("STRIPE_API_KEY", true)]
    [InlineData("STRIPE_SIGNING_SECRET", true)]
    [InlineData("STRIPE_PRIVATE_KEY", true)]
    [InlineData("STRIPE_MODE", false)]
    [InlineData("STRIPE_ACCOUNT_ID", false)]
    public void IsSecretEnvironmentVariableName_ReturnsExpectedClassification(string name, bool expected)
    {
        Assert.Equal(expected, StripeLinkSecretEnvironmentName.IsSensitive(name));
    }
}

using OpenClaw.Core.Security;
using Xunit;

namespace OpenClaw.Tests;

public sealed class GlobMatcherTests
{
    [Theory]
    [InlineData("*", "anything", true)]
    [InlineData("light.*", "light.kitchen", true)]
    [InlineData("light.*", "switch.kitchen", false)]
    [InlineData("sensor.*_temp", "sensor.outdoor_temp", true)]
    [InlineData("sensor.*_temp", "sensor.outdoor_humidity", false)]
    [InlineData("abc*def", "abcdef", true)]
    [InlineData("abc*def", "abcZZZdef", true)]
    [InlineData("abc*def", "abcZZZde", false)]
    [InlineData("no_wildcard", "no_wildcard", true)]
    [InlineData("no_wildcard", "NO_WILDCARD", false)]
    public void IsMatch_Works(string pattern, string value, bool expected)
    {
        Assert.Equal(expected, GlobMatcher.IsMatch(pattern, value));
    }

    [Fact]
    public void IsAllowed_DenyWins()
    {
        var allow = new[] { "light.*" };
        var deny = new[] { "light.bad*" };

        Assert.True(GlobMatcher.IsAllowed(allow, deny, "light.kitchen"));
        Assert.False(GlobMatcher.IsAllowed(allow, deny, "light.bad_room"));
    }
}


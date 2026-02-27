using OpenClaw.Core.Security;
using Xunit;

namespace OpenClaw.Tests;

public sealed class AllowlistPolicyTests
{
    [Fact]
    public void Strict_Empty_DeniesAll()
    {
        Assert.False(AllowlistPolicy.IsAllowed([], "any", AllowlistSemantics.Strict));
    }

    [Fact]
    public void Strict_Wildcard_AllowsAll()
    {
        Assert.True(AllowlistPolicy.IsAllowed(["*"], "any", AllowlistSemantics.Strict));
    }

    [Fact]
    public void Strict_Glob_Matches()
    {
        Assert.True(AllowlistPolicy.IsAllowed(["user*"], "user123", AllowlistSemantics.Strict));
        Assert.False(AllowlistPolicy.IsAllowed(["user*"], "admin", AllowlistSemantics.Strict));
    }

    [Fact]
    public void Legacy_Empty_AllowsAll()
    {
        Assert.True(AllowlistPolicy.IsAllowed([], "any", AllowlistSemantics.Legacy));
    }
}


using OpenClaw.Cli;
using Xunit;

namespace OpenClaw.Tests;

public sealed class UpdateCommandsTests
{
    [Fact]
    public void LaunchComponentIgnoresOptionsAndTheirValues()
    {
        Assert.Equal("companion", UpdateCommands.ResolveLaunchComponent(["launch", "--root", "/tmp/updates", "companion"]));
        Assert.Equal("companion", UpdateCommands.ResolveLaunchComponent(["launch", "--root", "/tmp/updates"]));
    }

    [Fact]
    public async Task UnknownSubcommandReturnsUsageError()
        => Assert.Equal(2, await UpdateCommands.RunAsync(["instal"]));
}

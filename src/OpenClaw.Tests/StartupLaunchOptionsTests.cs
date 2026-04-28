using OpenClaw.Gateway.Bootstrap;
using Xunit;

namespace OpenClaw.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class StartupLaunchOptionsTests
{
    [Fact]
    public void ValidateQuickstart_RejectsDoctorMode()
    {
        var options = StartupLaunchOptions.Parse(["--quickstart", "--doctor"]);

        Assert.Equal("--quickstart cannot be combined with --doctor.", options.ValidateQuickstart());
    }

    [Fact]
    public void ValidateQuickstart_RejectsHealthCheckMode()
    {
        var options = StartupLaunchOptions.Parse(["--quickstart", "--health-check"]);

        Assert.Equal("--quickstart cannot be combined with --health-check.", options.ValidateQuickstart());
    }

    [Fact]
    public void ValidateQuickstart_RejectsExplicitConfigArgument()
    {
        var options = StartupLaunchOptions.Parse(["--quickstart", "--config", "./openclaw.settings.json"]);

        Assert.Equal("--quickstart cannot be combined with --config.", options.ValidateQuickstart());
    }

    [Fact]
    public void ValidateQuickstart_RejectsConfigFlagWithoutValue()
    {
        var options = StartupLaunchOptions.Parse(["--quickstart", "--config"]);

        Assert.Equal("--quickstart cannot be combined with --config.", options.ValidateQuickstart());
    }

    [Fact]
    public void ValidateQuickstart_RejectsEmptyConfigAssignment()
    {
        var options = StartupLaunchOptions.Parse(["--quickstart", "--config="]);

        Assert.Equal("--quickstart cannot be combined with --config.", options.ValidateQuickstart());
    }

    [Fact]
    public void ValidateQuickstart_RejectsEnvironmentConfigOverride()
    {
        var previous = Environment.GetEnvironmentVariable("OPENCLAW_CONFIG_PATH");
        try
        {
            Environment.SetEnvironmentVariable("OPENCLAW_CONFIG_PATH", "./openclaw.settings.json");

            var options = StartupLaunchOptions.Parse(["--quickstart"]);

            Assert.Equal("--quickstart cannot be used while OPENCLAW_CONFIG_PATH is set.", options.ValidateQuickstart());
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_CONFIG_PATH", previous);
        }
    }
}

using OpenClaw.Core.Models;
using OpenClaw.Core.Validation;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ConfigValidatorTests
{
    [Fact]
    public void Validate_CronStepZero_ReturnsError()
    {
        var config = new GatewayConfig
        {
            Cron = new CronConfig
            {
                Enabled = true,
                Jobs =
                [
                    new CronJobConfig
                    {
                        Name = "invalid",
                        CronExpression = "*/0 * * * *",
                        Prompt = "run"
                    }
                ]
            }
        };

        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, error => error.Contains("invalid CronExpression", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_CronValidExpression_NoCronError()
    {
        var config = new GatewayConfig
        {
            Cron = new CronConfig
            {
                Enabled = true,
                Jobs =
                [
                    new CronJobConfig
                    {
                        Name = "valid",
                        CronExpression = "*/5 * * * *",
                        Prompt = "run"
                    }
                ]
            }
        };

        var errors = ConfigValidator.Validate(config);
        Assert.DoesNotContain(errors, error => error.Contains("CronExpression", StringComparison.Ordinal));
    }
}

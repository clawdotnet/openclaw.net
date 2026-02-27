using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Security;
using Xunit;

namespace OpenClaw.Tests;

public sealed class AllowlistManagerTests
{
    [Fact]
    public void DynamicAllowlists_Override_Config()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var mgr = new AllowlistManager(root, NullLogger<AllowlistManager>.Instance);

        var cfg = new ChannelAllowlistFile { AllowedFrom = ["999"] };
        Assert.Equal(["999"], mgr.GetEffective("telegram", cfg).AllowedFrom);

        mgr.AddAllowedFrom("telegram", "123");
        var dyn = mgr.TryGetDynamic("telegram");
        Assert.NotNull(dyn);
        Assert.Contains("123", dyn!.AllowedFrom);

        var eff = mgr.GetEffective("telegram", cfg);
        Assert.Contains("123", eff.AllowedFrom);
        Assert.DoesNotContain("999", eff.AllowedFrom);
    }
}


using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Security;
using Xunit;

namespace OpenClaw.Tests;

public sealed class PairingManagerTests
{
    [Fact]
    public void TryApprove_WithValidCode_ApprovesSender()
    {
        var basePath = Path.Combine(Path.GetTempPath(), $"openclaw-pairing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(basePath);

        try
        {
            var manager = new PairingManager(basePath, NullLogger<PairingManager>.Instance);
            var code = manager.GeneratePairingCode("sms", "+15551234567");

            var approved = manager.TryApprove("sms", "+15551234567", code, out var error);

            Assert.True(approved);
            Assert.True(string.IsNullOrEmpty(error));
            Assert.True(manager.IsApproved("sms", "+15551234567"));
        }
        finally
        {
            try { Directory.Delete(basePath, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TryApprove_TooManyInvalidAttempts_BlocksFurtherAttempts()
    {
        var basePath = Path.Combine(Path.GetTempPath(), $"openclaw-pairing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(basePath);

        try
        {
            var manager = new PairingManager(basePath, NullLogger<PairingManager>.Instance);
            var code = manager.GeneratePairingCode("telegram", "12345");

            for (var i = 0; i < 5; i++)
            {
                var ok = manager.TryApprove("telegram", "12345", "000000", out var error);
                Assert.False(ok);
                Assert.Contains("Invalid pairing code", error);
            }

            var finalAttempt = manager.TryApprove("telegram", "12345", code, out var finalError);
            Assert.False(finalAttempt);
            Assert.Contains("Too many invalid attempts", finalError);
            Assert.False(manager.IsApproved("telegram", "12345"));
        }
        finally
        {
            try { Directory.Delete(basePath, recursive: true); } catch { }
        }
    }
}

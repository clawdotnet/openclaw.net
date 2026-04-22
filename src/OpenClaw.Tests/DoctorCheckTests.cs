using OpenClaw.Core.Models;
using OpenClaw.Core.Validation;
using Xunit;

namespace OpenClaw.Tests;

public sealed class DoctorCheckTests
{
    [Fact]
    public async Task RunAsync_Ipv6LoopbackDoesNotRequirePublicBindAuthToken()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-doctor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var config = new GatewayConfig
            {
                BindAddress = "::1",
                Port = 18789,
                Memory = new MemoryConfig
                {
                    StoragePath = storagePath
                },
                Llm = new LlmProviderConfig
                {
                    Provider = "ollama",
                    Model = "llama3.2"
                }
            };

            var runtimeState = RuntimeModeResolver.Resolve(config.Runtime);

            var ready = await DoctorCheck.RunAsync(config, runtimeState);

            Assert.True(ready);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }
}

using OpenClaw.Core.Models;
using CoreGatewaySetupProfileFactory = OpenClaw.Core.Setup.GatewaySetupProfileFactory;

namespace OpenClaw.Cli;

internal static class BootstrapConfigFactory
{
    public static GatewayConfig CreateProfileConfig(
        string profile,
        string bindAddress,
        int port,
        string authToken,
        string workspacePath,
        string memoryPath,
        string provider,
        string model,
        string apiKey,
        List<string>? warnings = null)
    {
        return CoreGatewaySetupProfileFactory.CreateProfileConfig(
            profile,
            bindAddress,
            port,
            authToken,
            workspacePath,
            memoryPath,
            provider,
            model,
            apiKey,
            warnings);
    }

    public static string NormalizeProfile(string profile) => CoreGatewaySetupProfileFactory.NormalizeProfile(profile);
}

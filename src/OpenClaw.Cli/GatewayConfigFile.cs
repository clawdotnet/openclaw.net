using OpenClaw.Core.Models;
using CoreGatewayConfigFile = OpenClaw.Core.Setup.GatewayConfigFile;
using GatewaySetupPaths = OpenClaw.Core.Setup.GatewaySetupPaths;

namespace OpenClaw.Cli;

internal static class GatewayConfigFile
{
    internal const string DefaultConfigPath = GatewaySetupPaths.DefaultConfigPath;

    public static string ExpandPath(string path) => GatewaySetupPaths.ExpandPath(path);

    public static string QuoteIfNeeded(string path) => GatewaySetupPaths.QuoteIfNeeded(path);

    public static GatewayConfig Load(string configPath) => CoreGatewayConfigFile.Load(configPath);

    public static Task SaveAsync(GatewayConfig config, string configPath) => CoreGatewayConfigFile.SaveAsync(config, configPath);
}

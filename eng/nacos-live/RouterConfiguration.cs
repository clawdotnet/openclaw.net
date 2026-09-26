namespace NacosLiveAcceptance;

internal static class RouterConfiguration
{
    public static Dictionary<string, string?> BuildRouterEnvironment(
        NacosDeployment deployment,
        int port,
        string modelDirectory,
        string dataDirectory) => new(StringComparer.Ordinal)
        {
            ["NACOS_ADDR"] = deployment.ServerAddress,
            ["NACOS_USERNAME"] = deployment.Username,
            ["NACOS_PASSWORD"] = deployment.Password,
            ["NACOS_NAMESPACE"] = string.Empty,
            ["ACCESS_KEY_ID"] = string.Empty,
            ["ACCESS_KEY_SECRET"] = string.Empty,
            ["MODE"] = "router",
            ["TRANSPORT_TYPE"] = "streamable_http",
            ["PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["UPDATE_INTERVAL"] = "10",
            ["ANONYMIZED_TELEMETRY"] = "False",
            ["EMBEDDING_MODEL_DIR"] = Path.GetFullPath(modelDirectory),
            ["SONNETDB_DATA_DIR"] = Path.GetFullPath(dataDirectory),
        };

    public static Dictionary<string, string?> BuildSmokeEnvironment(
        NacosDeployment deployment,
        string routerUrl) => new(StringComparer.Ordinal)
        {
            ["OPENCLAW_NACOS_LIVE"] = "1",
            ["OPENCLAW_NACOS_ROUTER_URL"] = routerUrl,
            ["OPENCLAW_NACOS_SERVER"] = deployment.ServerAddress,
            ["OPENCLAW_NACOS_USERNAME"] = deployment.Username,
            ["OPENCLAW_NACOS_PASSWORD"] = deployment.Password,
        };
}
using System.Globalization;

namespace OpenClaw.Core.Setup;

public static class GatewaySetupArtifacts
{
    public static string BuildEnvExample(string apiKeyRef, string authToken, string workspacePath, string baseUrl)
    {
        var lines = new List<string>
        {
            $"{ResolveProviderEnvVariable(apiKeyRef)}=replace-me",
            $"OPENCLAW_AUTH_TOKEN={authToken}",
            $"OPENCLAW_BASE_URL={baseUrl}",
            $"OPENCLAW_WORKSPACE={workspacePath}"
        };

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    public static string ResolveProviderEnvVariable(string apiKeyRef)
    {
        if (apiKeyRef.StartsWith("env:", StringComparison.OrdinalIgnoreCase) && apiKeyRef.Length > 4)
            return apiKeyRef[4..];

        return "MODEL_PROVIDER_KEY";
    }

    public static string BuildEnvExamplePath(string configPath)
    {
        var directory = Path.GetDirectoryName(configPath)
            ?? throw new InvalidOperationException("Config path must contain a directory.");
        var stem = Path.GetFileNameWithoutExtension(configPath);
        return Path.Combine(directory, $"{stem}.env.example");
    }

    public static string BuildReachableBaseUrl(string bindAddress, int port)
    {
        if (string.Equals(bindAddress, "0.0.0.0", StringComparison.Ordinal) ||
            string.Equals(bindAddress, "::", StringComparison.Ordinal) ||
            string.Equals(bindAddress, "[::]", StringComparison.Ordinal))
        {
            return $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}";
        }

        if (bindAddress.Contains(':') && !bindAddress.StartsWith("[", StringComparison.Ordinal))
            return $"http://[{bindAddress}]:{port.ToString(CultureInfo.InvariantCulture)}";

        return $"http://{bindAddress}:{port.ToString(CultureInfo.InvariantCulture)}";
    }
}

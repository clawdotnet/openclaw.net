using OpenClaw.Core.Models;

namespace OpenClaw.Core.Setup;

public static class GatewaySetupProfileFactory
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
        var normalizedProfile = NormalizeProfile(profile);
        var config = new GatewayConfig
        {
            BindAddress = bindAddress,
            Port = port,
            AuthToken = authToken,
            Llm = new LlmProviderConfig
            {
                Provider = provider,
                Model = model,
                ApiKey = apiKey
            },
            Memory = new MemoryConfig
            {
                Provider = "file",
                StoragePath = memoryPath,
                Retention = new MemoryRetentionConfig
                {
                    ArchivePath = Path.Combine(memoryPath, "archive")
                }
            },
            Tooling = new ToolingConfig
            {
                WorkspaceRoot = workspacePath,
                WorkspaceOnly = true,
                AllowShell = normalizedProfile == "local",
                AllowedReadRoots = [workspacePath],
                AllowedWriteRoots = [workspacePath],
                RequireToolApproval = normalizedProfile == "public"
            },
            Security = new SecurityConfig
            {
                AllowQueryStringToken = false,
                TrustForwardedHeaders = normalizedProfile == "public",
                RequireRequesterMatchForHttpToolApproval = normalizedProfile == "public"
            }
        };

        if (normalizedProfile == "public")
        {
            config.Plugins.Enabled = false;
            warnings?.Add("Public profile disables third-party bridge plugins by default. Re-enable them only after you have a proxy, TLS, and explicit public-bind trust settings in place.");
        }

        if (normalizedProfile == "public" && !apiKey.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
            warnings?.Add("Public profile is using a direct API key value in the config file. Prefer env:... references or OS-backed secret storage.");

        return config;
    }

    public static string NormalizeProfile(string profile)
    {
        var normalized = profile.Trim().ToLowerInvariant();
        if (normalized is not ("local" or "public"))
            throw new ArgumentException("Invalid value for --profile (expected: local|public).");
        return normalized;
    }
}

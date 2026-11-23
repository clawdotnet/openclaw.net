namespace OpenClaw.Payments.Core;

public static class StripeLinkSecretEnvironmentName
{
    public static bool IsSensitive(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var tokens = name.ToUpperInvariant().Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries);
        return tokens.Any(static token => token is "KEY" or "SECRET" or "TOKEN" or "PASSWORD" or "CREDENTIAL" or "CREDENTIALS");
    }
}

namespace OpenClaw.Core.Validation;

public static class OllamaEndpointNormalizer
{
    public const string DefaultBaseUrl = "http://127.0.0.1:11434";

    public static string NormalizeBaseUrl(string? endpoint)
    {
        return Normalize(endpoint).BaseUrl;
    }

    public static bool UsesCompatibilityEndpoint(string? endpoint)
    {
        return Normalize(endpoint).UsesCompatibilityEndpoint;
    }

    public static (string BaseUrl, bool UsesCompatibilityEndpoint) Normalize(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return (DefaultBaseUrl, false);

        var trimmed = endpoint.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return (trimmed, false);

        var path = uri.AbsolutePath.TrimEnd('/');
        if (string.Equals(path, "/v1", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(uri)
            {
                Path = string.Empty
            };
            return (builder.Uri.ToString().TrimEnd('/'), true);
        }

        return (trimmed, false);
    }
}

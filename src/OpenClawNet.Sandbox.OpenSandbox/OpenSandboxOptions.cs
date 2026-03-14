namespace OpenClawNet.Sandbox.OpenSandbox;

public sealed class OpenSandboxOptions
{
    public string Endpoint { get; set; } = string.Empty;
    public string? ApiKey { get; set; }
    public int DefaultTTL { get; set; } = 300;

    public Uri GetApiBaseUri()
    {
        var normalized = Endpoint.Trim().TrimEnd('/');
        if (!normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            normalized += "/v1";

        return new Uri(normalized + "/", UriKind.Absolute);
    }
}


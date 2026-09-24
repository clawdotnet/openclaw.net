using OpenClaw.Core.Models;

namespace OpenClaw.Core.Validation;

public static class JevRoutingConfiguration
{
    public static string NormalizeMode(JevRoutingConfig config)
    {
        var mode = config.Mode?.Trim().ToLowerInvariant();
        if (mode is not ("disabled" or "shadow" or "active"))
            throw new ArgumentException("DynamicTurnRouting.Jev.Mode must be disabled, shadow, or active.");
        return mode;
    }

    public static Uri Validate(JevRoutingConfig config)
    {
        NormalizeMode(config);
        if (!Uri.TryCreate(config.Endpoint, UriKind.Absolute, out var endpoint) ||
            !(endpoint.Scheme == Uri.UriSchemeHttps || endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new ArgumentException("Jev.Endpoint must be an HTTPS URL (HTTP is allowed only on loopback), without credentials, query, or fragment.");
        if (string.IsNullOrWhiteSpace(config.ApiKeyRef) || string.IsNullOrWhiteSpace(config.Model))
            throw new ArgumentException("Jev requires ApiKeyRef and a pinned Model.");
        DecisionRoutingConfiguration.ValidateLimits(config);
        if (config.Model is "jev-latest" or "jev-preview")
            throw new ArgumentException("Jev routing requires a pinned model version so calibrated thresholds do not change silently.");
        return endpoint;
    }

}

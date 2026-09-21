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
        if (config.TimeoutMs is < 1 or > 30000 || config.MaxStateChars is < 256 or > 32000 ||
            config.HistoryMessages is < 0 or > 20 || config.MaxConcurrentRequests is < 1 or > 128 ||
            config.CircuitFailureThreshold is < 1 or > 100 || config.CircuitBreakSeconds is < 1 or > 3600)
            throw new ArgumentException("Jev request, state, concurrency, or circuit limits are outside their supported ranges.");
        if (!Probability(config.MinConfidence) || !Probability(config.DowngradeMinConfidence) ||
            config.DowngradeMinConfidence < config.MinConfidence || !Probability(config.MinProbabilityMargin) ||
            !Probability(config.HighRiskThreshold) || config.InputUsdPerMillionTokens is < 0 or > 1_000_000m)
            throw new ArgumentException("Jev thresholds must be finite probabilities, downgrade confidence cannot be lower than minimum confidence, and price cannot be negative.");
        if (config.Model is "jev-latest" or "jev-preview")
            throw new ArgumentException("Jev routing requires a pinned model version so calibrated thresholds do not change silently.");
        return endpoint;
    }

    private static bool Probability(double value) => value is >= 0 and <= 1;
}

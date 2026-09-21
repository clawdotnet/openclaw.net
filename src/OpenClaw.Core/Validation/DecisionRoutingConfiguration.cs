using System.Net;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Validation;

public static class DecisionRoutingConfiguration
{
    public static string Provider(DecisionRoutingConfig config) => config is LayaRoutingConfig ? "laya" : "jev";

    public static string NormalizeMode(DecisionRoutingConfig config)
    {
        var mode = config.Mode?.Trim().ToLowerInvariant();
        if (mode is not ("disabled" or "shadow" or "active"))
            throw new ArgumentException("Decision routing mode must be disabled, shadow, or active.");
        return mode;
    }

    public static Uri Validate(DecisionRoutingConfig config)
    {
        var mode = NormalizeMode(config);
        if (config is JevRoutingConfig jev)
            return JevRoutingConfiguration.Validate(jev);
        if (config is not LayaRoutingConfig laya)
            throw new ArgumentException("Unsupported decision provider.");
        ValidateLimits(config);
        if (!Uri.TryCreate(config.Endpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https") ||
            !IPAddress.TryParse(endpoint.DnsSafeHost, out var address) || !IPAddress.IsLoopback(address) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new ArgumentException("Laya.Endpoint must use a literal loopback IP, without credentials, query, or fragment.");
        if (config.Model is null || !config.Model.StartsWith("laya@", StringComparison.Ordinal) || !IsHex(config.Model[5..], 40))
            throw new ArgumentException("Laya.Model must be laya@ followed by the pinned 40-character Hugging Face revision.");
        if ((mode == "active" || !string.IsNullOrEmpty(laya.CalibrationId)) && !IsHex(laya.CalibrationId, 64))
            throw new ArgumentException("Laya active routing requires the SHA-256 CalibrationId of an evaluated calibration artifact.");
        if (laya.Language is null || laya.Language.Length > 35 || laya.Language.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Laya.Language must be empty or a language tag such as en or de-DE.");
        if (laya.InputUsdPerMillionTokens != 0)
            throw new ArgumentException("Laya has no metered API token price; account for local compute separately.");
        return endpoint;
    }

    public static void ValidateLimits(DecisionRoutingConfig config)
    {
        if (config.TimeoutMs is < 1 or > 30000 || config.MaxStateChars is < 256 or > 32000 ||
            config.HistoryMessages is < 0 or > 20 || config.MaxConcurrentRequests is < 1 or > 128 ||
            config.CircuitFailureThreshold is < 1 or > 100 || config.CircuitBreakSeconds is < 1 or > 3600)
            throw new ArgumentException("Decision request, state, concurrency, or circuit limits are outside their supported ranges.");
        if (!Probability(config.MinConfidence) || !Probability(config.DowngradeMinConfidence) ||
            config.DowngradeMinConfidence < config.MinConfidence || !Probability(config.MinProbabilityMargin) ||
            !Probability(config.HighRiskThreshold) || config.InputUsdPerMillionTokens is < 0 or > 1_000_000m)
            throw new ArgumentException("Decision thresholds must be finite probabilities, downgrade confidence cannot be lower than minimum confidence, and price cannot be negative.");
    }

    public static bool IsHex(string? value, int length) => value?.Length == length && value.All(char.IsAsciiHexDigitLower);
    private static bool Probability(double value) => value is >= 0 and <= 1;
}

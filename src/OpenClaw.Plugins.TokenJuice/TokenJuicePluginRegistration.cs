using OpenClaw.Plugins.TokenJuice.Reduction;
using OpenClaw.Plugins.TokenJuice.Rules;

namespace OpenClaw.Plugins.TokenJuice;

public static class TokenJuicePluginRegistration
{
    public static TokenJuiceInterceptor CreateInterceptor(
        IReadOnlyList<TokenJuiceRule>? rules = null,
        SemanticDensityCalculator? density = null,
        int? maxInlineChars = null)
    {
        var mergedRules = rules ?? RuleLoader.LoadMergedRules();
        return new TokenJuiceInterceptor(mergedRules, density, maxInlineChars);
    }
}

using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Plugins.TokenJuice.Matching;
using OpenClaw.Plugins.TokenJuice.Rules;

namespace OpenClaw.Plugins.TokenJuice.Reduction;

public sealed class TokenJuiceInterceptor : IToolResultInterceptor
{
    public int Order => 100;
    public string Name => "TokenJuice";

    private readonly IReadOnlyList<TokenJuiceRule> _rules;
    private readonly SemanticDensityCalculator _density;
    private readonly int? _maxInlineChars;

    public TokenJuiceInterceptor(
        IReadOnlyList<TokenJuiceRule> rules,
        SemanticDensityCalculator? density = null,
        int? maxInlineChars = null)
    {
        _rules = rules;
        _density = density ?? new SemanticDensityCalculator();
        _maxInlineChars = maxInlineChars;
    }

    public ValueTask<string> InterceptAsync(ReductionContext context, CancellationToken ct)
    {
        var toolName = context.ToolName;
        var argumentsJson = context.ArgumentsJson;
        var rawOutput = context.RawOutput;

        if (context.BypassReduction)
            return new ValueTask<string>(rawOutput);

        // Escape hatch: --raw / --full
        if (argumentsJson.Contains("--raw") || argumentsJson.Contains("--full"))
            return new ValueTask<string>(rawOutput);

        var command = ExtractCommand(argumentsJson);
        var argv = CommandArgvParser.Parse(command);
        var exitCode = context.ExitCode;

        // Rule matching
        var rule = RuleMatcher.SelectRule(_rules, toolName, command, argv, rawOutput, exitCode);

        if (rule is not null)
        {
            if (context.IsError && (rule.Failure?.PreserveOnFailure ?? false) is false)
                return new ValueTask<string>(rawOutput);

            var (summary, facts) = ReductionStrategies.Reduce(rule, rawOutput, exitCode);
            if (!string.IsNullOrEmpty(summary))
            {
                var formatted = InlineFormatter.Format(summary, facts, exitCode, _maxInlineChars);
                if (formatted.Length < rawOutput.Length)
                    return new ValueTask<string>(formatted);
            }
        }
        else if (!context.IsError && _density.ShouldReduce(rawOutput))
        {
            var fallback = _rules.FirstOrDefault(r => r.Id == "generic/fallback");
            if (fallback is not null)
            {
                var (summary, facts) = ReductionStrategies.Reduce(fallback, rawOutput, exitCode);
                if (!string.IsNullOrEmpty(summary))
                {
                    var formatted = InlineFormatter.Format(summary, facts, exitCode, _maxInlineChars);
                    if (formatted.Length < rawOutput.Length)
                        return new ValueTask<string>(formatted);
                }
            }
        }

        return new ValueTask<string>(rawOutput);
    }

    private static string? ExtractCommand(string argumentsJson)
    {
        if (!argumentsJson.Contains("\"command\""))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("command", out var cmd) &&
                cmd.ValueKind == JsonValueKind.String)
                return cmd.GetString();
        }
        catch { }

        return null;
    }
}

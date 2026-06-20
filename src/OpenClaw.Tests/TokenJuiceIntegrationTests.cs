using OpenClaw.Core.Abstractions;
using OpenClaw.Plugins.TokenJuice.Matching;
using OpenClaw.Plugins.TokenJuice.Reduction;
using OpenClaw.Plugins.TokenJuice.Rules;
using Xunit;

namespace OpenClaw.Tests;

public class TokenJuiceIntegrationTests
{
    private static IReadOnlyList<TokenJuiceRule> LoadRules()
        => RuleLoader.LoadMergedRules();

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenClaw.Net.slnx")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    [Fact]
    public void RuleMatcher_DotnetBuild_MatchesBuildRule()
    {
        var rules = LoadRules();
        var argv = new List<string> { "dotnet", "build" };
        var rule = RuleMatcher.SelectRule(
            rules,
            toolName: "exec",
            command: "dotnet build -c Release",
            argv: argv,
            content: "Build succeeded.\n    0 Warning(s)\n    0 Error(s)",
            exitCode: 0);

        Assert.NotNull(rule);
        Assert.StartsWith("build/", rule!.Id);
    }

    [Fact]
    public void ReductionStrategies_DotnetBuild_ReducesOutput()
    {
        var dotnetOutput = File.ReadAllText("Fixtures/dotnet-build-output.txt");
        var rules = LoadRules();
        var rule = rules.First(r => r.Id == "build/dotnet");

        var (summary, facts) = ReductionStrategies.Reduce(rule, dotnetOutput, exitCode: 0);

        Assert.NotNull(summary);
        Assert.True(summary.Length < dotnetOutput.Length,
            $"Reduced length ({summary.Length}) should be less than original ({dotnetOutput.Length})");
    }

    [Fact]
    public void SemanticDensityCalculator_LowDensity_ShouldReduce()
    {
        var calc = new SemanticDensityCalculator(threshold: 0.3);
        var lowDensityText = string.Join("\n",
            Enumerable.Repeat("  ", 50)) + "\n" +
            string.Join("\n", Enumerable.Repeat("same line", 100));

        Assert.True(calc.ShouldReduce(lowDensityText));
    }

    [Fact]
    public void SemanticDensityCalculator_HighDensity_ShouldNotReduce()
    {
        var calc = new SemanticDensityCalculator(threshold: 0.3);
        var highDensity = "def main():\n    print('hello')\n    return 0";

        Assert.False(calc.ShouldReduce(highDensity));
    }

    [Fact]
    public async Task EscapeHatch_RawArg_ReturnsUnchanged()
    {
        var rules = LoadRules();
        var interceptor = new TokenJuiceInterceptor(rules);
        var input = "some output\n".PadRight(5000, 'x');

        var result = await interceptor.InterceptAsync(
            ReductionContext.From(
                "exec",
                @"{""command"":""echo --raw"",""argv"":[""echo"",""--raw""]}",
                input),
            TestContext.Current.CancellationToken);

        Assert.Equal(input, result);
    }

    [Fact]
    public async Task EscapeHatch_FullArg_ReturnsUnchanged()
    {
        var rules = LoadRules();
        var interceptor = new TokenJuiceInterceptor(rules);
        var input = "important data";

        var result = await interceptor.InterceptAsync(
            ReductionContext.From(
                "exec",
                @"{""command"":""git diff --full""}",
                input),
            TestContext.Current.CancellationToken);

        Assert.Equal(input, result);
    }

    [Fact]
    public void InlineFormatter_InlinesFactsAndExitCode()
    {
        var result = InlineFormatter.Format(
            "Build succeeded. 0 Error(s)",
            new Dictionary<string, int> { ["error"] = 0, ["warning"] = 0 },
            exitCode: 0);

        Assert.Equal("Build succeeded. 0 Error(s)", result);
    }

    [Fact]
    public void InlineFormatter_FailureMode_IncludesExitCode()
    {
        var result = InlineFormatter.Format(
            "Build FAILED.",
            new Dictionary<string, int> { ["error"] = 12, ["warning"] = 3 },
            exitCode: 1);

        Assert.Contains("exit 1", result);
        Assert.Contains("error: 12", result);
        Assert.Contains("warning: 3", result);
    }

    [Fact]
    public void ProjectFile_EmbedsRulesDirectoryWithCorrectCase()
    {
        var projectFile = Path.Combine(
            FindRepositoryRoot(),
            "src", "OpenClaw.Plugins.TokenJuice", "OpenClaw.Plugins.TokenJuice.csproj");
        var projectXml = File.ReadAllText(projectFile);

        Assert.Contains("<EmbeddedResource Include=\"Rules/**/*.json\" />", projectXml);
    }

    [Fact]
    public void RuleLoader_LoadsBuiltinRules()
    {
        var rules = LoadRules();
        Assert.NotEmpty(rules);
        Assert.Contains(rules, r => r.Id == "generic/fallback");
        Assert.Contains(rules, r => r.Id == "build/dotnet");
    }

    [Fact]
    public void RuleLoader_EveryRuleHasId()
    {
        var rules = LoadRules();
        foreach (var rule in rules)
        {
            Assert.False(string.IsNullOrEmpty(rule.Id),
                $"Rule with family '{rule.Family}' has no Id");
        }
    }

    [Fact]
    public async Task Interceptor_ReducesDotnetBuildOutput()
    {
        var dotnetOutput = File.ReadAllText("Fixtures/dotnet-build-output.txt");
        var rules = LoadRules();
        var interceptor = new TokenJuiceInterceptor(rules);

        var result = await interceptor.InterceptAsync(
            ReductionContext.From(
                "exec",
                @"{""command"":""dotnet build"",""argv"":[""dotnet"",""build""]}",
                dotnetOutput),
            TestContext.Current.CancellationToken);

        Assert.True(result.Length < dotnetOutput.Length,
            $"Interceptor did not reduce output. Original: {dotnetOutput.Length}, Result: {result.Length}");
    }

    [Fact]
    public async Task Interceptor_FailedDotnetBuildOutput_IncludesNonZeroExit()
    {
        var failedOutput = string.Join(
            "\n",
            Enumerable.Range(1, 40).Select(i => $"Project {i} restored.").Concat([
                "Program.cs(10,5): error CS1002: ; expected",
                "Build FAILED.",
                "    0 Warning(s)",
                "    1 Error(s)"
            ]));
        var rules = LoadRules();
        var interceptor = new TokenJuiceInterceptor(rules);

        var result = await interceptor.InterceptAsync(
            ReductionContext.From(
                "exec",
                @"{""command"":""dotnet build"",""argv"":[""dotnet"",""build""]}",
                failedOutput,
                isError: true,
                exitCode: 1),
            TestContext.Current.CancellationToken);

        Assert.True(result.Length < failedOutput.Length);
        Assert.Contains("exit 1", result, StringComparison.Ordinal);
        Assert.Contains("error", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Build FAILED", result, StringComparison.OrdinalIgnoreCase);
    }
}

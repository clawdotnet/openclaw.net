using Xunit;

namespace OpenClaw.Tests;

public sealed class GatewayStructureTests
{
    [Fact]
    public void ThinGatewayOrchestratorFiles_StayWithinSizeBudgets()
    {
        var root = FindRepositoryRoot();

        AssertFileLineBudget(root, "src/OpenClaw.Gateway/Extensions/GatewayWorkers.cs", 200);
        AssertFileLineBudget(root, "src/OpenClaw.Gateway/Endpoints/OpenAiEndpoints.cs", 250);
        AssertFileLineBudget(root, "src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.cs", 252);
        AssertFileLineBudget(root, "src/OpenClaw.Gateway/Endpoints/AdminEndpoints.cs", 300);
    }

    [Fact]
    public void LoopCommandCallback_ResolvesSchedulerAtWiringTime()
    {
        var root = FindRepositoryRoot();
        var fullPath = Path.Combine(
            root,
            "src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.LoopCommands.cs".Replace('/', Path.DirectorySeparatorChar));
        var source = File.ReadAllText(fullPath);
        var methodStart = source.IndexOf("private static void WireLoopCommandCallback", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "WireLoopCommandCallback was not found.");

        var setLoopIndex = source.IndexOf("services.CommandProcessor.SetLoopCallback", methodStart, StringComparison.Ordinal);
        var resolveIndex = source.IndexOf("app.Services.GetRequiredService<ClawLoopScheduler>()", methodStart, StringComparison.Ordinal);
        Assert.True(
            resolveIndex >= 0 && resolveIndex < setLoopIndex,
            "ClawLoopScheduler should be resolved before SetLoopCallback so missing DI registration fails during startup.");

        var lambdaStart = source.IndexOf("=>", setLoopIndex, StringComparison.Ordinal);
        var lambdaEnd = source.IndexOf("});", lambdaStart, StringComparison.Ordinal);
        var lambdaBody = source[lambdaStart..lambdaEnd];
        Assert.DoesNotContain("GetRequiredService<ClawLoopScheduler>", lambdaBody);
    }

    private static void AssertFileLineBudget(string root, string relativePath, int maxLines)
    {
        var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var lineCount = File.ReadAllLines(fullPath).Length;
        Assert.True(
            lineCount <= maxLines,
            $"{relativePath} has {lineCount} lines, which exceeds the {maxLines}-line budget.");
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "README.md")) &&
                Directory.Exists(Path.Combine(current.FullName, "src")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test output directory.");
    }
}

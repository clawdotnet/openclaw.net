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

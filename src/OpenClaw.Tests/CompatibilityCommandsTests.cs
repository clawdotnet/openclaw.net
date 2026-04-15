using System.IO;
using OpenClaw.Cli;
using Xunit;

namespace OpenClaw.Tests;

public sealed class CompatibilityCommandsTests
{
    [Fact]
    public void Run_CatalogJson_PrintsFilteredCatalog()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CompatibilityCommands.Run(["catalog", "--status", "compatible", "--kind", "clawhub-skill", "--json"], output, error);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("\"version\":2", text, StringComparison.Ordinal);
        Assert.Contains("\"skillSlug\":\"peekaboo\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"kind\":\"npm-plugin\"", text, StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Run_CatalogText_PrintsScenarioSummary()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CompatibilityCommands.Run(["catalog", "--category", "unsupported-surface-plugin"], output, error);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("supermemory", text, StringComparison.Ordinal);
        Assert.Contains("unsupported upstream plugin surfaces fail explicitly", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unsupported_cli_registration", text, StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }
}

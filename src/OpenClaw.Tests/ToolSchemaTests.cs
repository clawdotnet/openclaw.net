using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenClaw.Core.Abstractions;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ToolSchemaTests
{
    private sealed class TestTool : ITool
    {
        public string Name => "test_tool";
        public string Description => "test";
        public string ParameterSchema => """{"type":"object","properties":{"a":{"type":"string"}},"required":["a"]}""";
        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct) => new("ok");
    }

    [Fact]
    public void CreateDeclaration_FromParameterSchema_DoesNotThrow()
    {
        var tool = new TestTool();
        using var doc = JsonDocument.Parse(tool.ParameterSchema);

        var decl = AIFunctionFactory.CreateDeclaration(
            tool.Name,
            tool.Description,
            doc.RootElement.Clone(),
            returnJsonSchema: null);

        Assert.Equal(tool.Name, decl.Name);
    }
}


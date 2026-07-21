using System.Reflection;
using Xunit;

namespace OpenClaw.Tests;

public sealed class DigitalEmployeeEndpointMappingTests
{
    [Fact]
    public void MapEntryToWorkspaceRelative_AcceptsOntologyJsonSlice()
    {
        var endpointType = Type.GetType("OpenClaw.Gateway.Endpoints.DigitalEmployeeEndpoints, OpenClaw.Gateway", throwOnError: true)!;
        var mapper = endpointType.GetMethod("MapEntryToWorkspaceRelative", BindingFlags.NonPublic | BindingFlags.Static)!;

        var mapped = (string?)mapper.Invoke(null, ["ontology/purchase-doc-domain.slice.json", ""]);

        Assert.Equal(Path.Combine("ontology", "purchase-doc-domain.slice.json"), mapped);
    }
}

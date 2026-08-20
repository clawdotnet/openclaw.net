using OpenClaw.StrategosWorkflowHost.Configuration;
using Xunit;

namespace OpenClaw.StrategosWorkflowHost.Tests;

public class OntologyGraphFactoryTests
{
    [Fact]
    public void Build_Returns_Graph_With_Single_Domain()
    {
        var graph = OntologyGraphFactory.Build(new OntologyOptions());
        Assert.Single(graph.Domains);
        Assert.Equal("AgentReview", graph.Domains[0].DomainName);
    }

    [Fact]
    public void Build_Returns_Graph_With_Three_ObjectTypes()
    {
        var graph = OntologyGraphFactory.Build(new OntologyOptions());
        var names = graph.ObjectTypes.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("ReviewRequest", names);
        Assert.Contains("ReviewDecision", names);
        Assert.Contains("ReviewComment", names);
        Assert.Equal(3, names.Count);
    }

    [Fact]
    public void Build_Includes_Submit_And_Approve_Actions()
    {
        var graph = OntologyGraphFactory.Build(new OntologyOptions());
        var actionNames = graph.ObjectTypes
            .SelectMany(t => t.Actions.Select(a => $"{t.Name}.{a.Name}"))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("ReviewRequest.Submit", actionNames);
        Assert.Contains("ReviewRequest.Approve", actionNames);
        Assert.Contains("ReviewRequest.Reject", actionNames);
    }

    [Fact]
    public void Build_Includes_Comment_Action_With_Text_Constraint()
    {
        var graph = OntologyGraphFactory.Build(new OntologyOptions());
        var comment = graph.ObjectTypes.Single(t => t.Name == "ReviewComment");
        var write = Assert.Single(comment.Actions, a => a.Name == "Write");
        Assert.Contains(write.Preconditions, p => p.Description.Contains("length", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_Graph_Has_No_Cross_Domain_Links()
    {
        // Single-domain graph is intentionally minimal; cross-domain links belong to
        // follow-up plans. The plan's original assertion targeted a DomainDescriptor
        // .Associations property that LevelUp.Strategos.Ontology 2.10.0 does not expose —
        // the graph-level equivalents are CrossDomainLinks and reified association edges.
        var graph = OntologyGraphFactory.Build(new OntologyOptions());
        Assert.Empty(graph.CrossDomainLinks);
        Assert.Empty(graph.GetAssociationEdges());
    }

    [Fact]
    public void Build_Rejects_Null_Options()
        => Assert.Throws<ArgumentNullException>(() => OntologyGraphFactory.Build(null!));
}

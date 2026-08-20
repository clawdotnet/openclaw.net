using Strategos.Ontology;
using Strategos.Ontology.Builder;

namespace OpenClaw.StrategosWorkflowHost.Configuration;

/// <summary>
/// Builds the single-domain ontology graph the sidecar serves over MCP.
/// </summary>
/// <remarks>
/// <para>
/// Kept intentionally minimal: the goal of P2 is to prove the MCP App wiring end-to-end,
/// not to ship a production ontology. Follow-up plans extend the graph as new review
/// primitives land (escalation, compensation audit, etc.).
/// </para>
/// <para>
/// LevelUp.Strategos.Ontology 2.10.0 has no public descriptor-record constructor path to
/// an <see cref="OntologyGraph"/> — the graph's constructor is internal and descriptors
/// carry CLR identity (the DR-1 invariant). The supported authoring surface is a
/// <see cref="DomainOntology"/> subclass composed by <see cref="OntologyGraphBuilder"/>,
/// which is what this factory uses.
/// </para>
/// </remarks>
public static class OntologyGraphFactory
{
    /// <summary>
    /// Composes the AgentReview ontology graph. Side-effect free and deterministic, so the
    /// result is safe to register as a DI singleton.
    /// </summary>
    public static OntologyGraph Build(OntologyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new OntologyGraphBuilder()
            .AddDomain<AgentReviewOntology>()
            .Build();
    }
}

/// <summary>
/// Durable agent review surface: requests, decisions, and comments.
/// </summary>
internal sealed class AgentReviewOntology : DomainOntology
{
    public override string DomainName => "AgentReview";

    protected override void Define(IOntologyBuilder builder)
    {
        builder.Object<ReviewRequest>(o =>
        {
            o.Key(x => x.Id);
            o.Property(x => x.Title).Required();
            o.Property(x => x.Description).Required();
            o.Property(x => x.RiskScore);

            o.Action("Submit")
                .Description("Submit a review request for agent adjudication.")
                // Rendered into the precondition description as an expression string, which
                // is what ontology_action surfaces to the model as a constraint summary.
                .Requires(x => x.Title.Length > 0 && x.Description.Length >= 10);

            o.Action("Approve").Description("Approve the review request.");
            o.Action("Reject").Description("Reject the review request.");
        });

        builder.Object<ReviewDecision>(o =>
        {
            o.Key(x => x.Id);
            o.Property(x => x.Verdict).Required();
            o.Property(x => x.ApproverId).Required();

            o.Action("Record").Description("Record the adjudicated verdict.");
        });

        builder.Object<ReviewComment>(o =>
        {
            o.Key(x => x.Id);
            o.Property(x => x.Body).Required();
            o.Property(x => x.AuthorId).Required();

            o.Action("Write")
                .Description("Write a comment on a review request.")
                .Requires(x => x.Body.Length >= 1 && x.Body.Length <= 4000);
        });
    }
}

/// <summary>CLR identity for the <c>ReviewRequest</c> object type (DR-1 requires one).</summary>
internal sealed class ReviewRequest
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public double RiskScore { get; set; }
}

/// <summary>CLR identity for the <c>ReviewDecision</c> object type.</summary>
internal sealed class ReviewDecision
{
    public string Id { get; set; } = string.Empty;

    public string Verdict { get; set; } = string.Empty;

    public string ApproverId { get; set; } = string.Empty;
}

/// <summary>CLR identity for the <c>ReviewComment</c> object type.</summary>
internal sealed class ReviewComment
{
    public string Id { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public string AuthorId { get; set; } = string.Empty;
}

namespace OpenClaw.StrategosWorkflowHost.Workflows.Models;

// Plain POCO (not part of the Stratego source-gen cycle). A reviewer's verdict for one review axis.
public sealed record ReviewVerdict(
    string Role,        // "security" | "architecture" | "cost"
    string Verdict,     // e.g. "review-required"
    string Summary,
    double Confidence);

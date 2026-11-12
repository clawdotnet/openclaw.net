using System.Text.Json.Serialization;

namespace OpenClaw.Core.Abstractions;

public sealed class WorkflowExecutionContext
{
    internal WorkflowExecutionContext(string backendId, string issuer, string subject, DateTimeOffset expiresAtUtc, string assertion)
    {
        BackendId = backendId;
        Issuer = issuer;
        Subject = subject;
        ExpiresAtUtc = expiresAtUtc;
        Assertion = assertion;
    }

    [JsonIgnore]
    public string BackendId { get; }
    [JsonIgnore]
    public string Issuer { get; }
    [JsonIgnore]
    public string Subject { get; }
    [JsonIgnore]
    public DateTimeOffset ExpiresAtUtc { get; }
    [JsonIgnore]
    internal string Assertion { get; }

    public override string ToString() => "WorkflowExecutionContext { [REDACTED] }";
}

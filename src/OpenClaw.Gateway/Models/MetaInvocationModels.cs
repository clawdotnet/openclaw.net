using System.Text.Json.Serialization;

namespace OpenClaw.Gateway.Models;

[JsonConverter(typeof(JsonStringEnumConverter<MetaInvocationStatus>))]
public enum MetaInvocationStatus
{
    Running,
    Completed,
    Uncertain
}

public sealed record MetaInvocationRecord(
    string CallerId,
    string IdempotencyKey,
    string RequestHash,
    Guid InvocationId,
    MetaInvocationStatus Status,
    string? Result,
    string? Error,
    DateTimeOffset CreatedAtUtc);

public enum MetaInvocationClaimKind
{
    Started,
    Existing,
    Conflict
}

public sealed record MetaInvocationClaim(MetaInvocationClaimKind Kind, MetaInvocationRecord Record);

public sealed record MetaSkillInvocationRequest(string Skill, string? Input, string SessionId);

public sealed record MetaInvocationResponse(
    Guid InvocationId,
    MetaInvocationStatus Status,
    string? Result,
    string? Error,
    DateTimeOffset CreatedAtUtc);

public sealed record MetaInvocationExecutionResult(
    int StatusCode,
    MetaInvocationResponse? Response = null,
    string? Error = null);
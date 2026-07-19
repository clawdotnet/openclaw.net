namespace OpenClaw.Core.Models;

/// <summary>
/// Optional JSON envelope used by WebSocket clients.
/// Raw-text clients may continue sending plain text.
/// </summary>
public sealed record WsClientEnvelope
{
    public required string Type { get; init; }
    public string? RequestId { get; init; }
    public string? ProtocolVersion { get; init; }
    public string? Operation { get; init; }
    public string? CatalogId { get; init; }
    public string[]? SupportedCatalogIds { get; init; }
    public string[]? Components { get; init; }
    public string? DataModelJson { get; init; }
    public string? SurfaceTitle { get; init; }
    public string? SurfaceKind { get; init; }
    public string? ParentSurfaceId { get; init; }
    public string? Action { get; init; }
    public string? ParametersJson { get; init; }
    public string? SyncMode { get; init; }
    public string? DiagnosticCode { get; init; }
    public string? Text { get; init; }
    public string? Content { get; init; }
    public string? SessionId { get; init; }
    public string? MessageId { get; init; }
    public string? ReplyToMessageId { get; init; }
    public string? SurfaceId { get; init; }
    public string? ContentType { get; init; }
    public string? Frames { get; init; }
    public string? Html { get; init; }
    public string? Url { get; init; }
    public string? Script { get; init; }
    public string? SnapshotMode { get; init; }
    public string? SnapshotJson { get; init; }
    public string? ComponentId { get; init; }
    public string? Event { get; init; }
    public string? ValueJson { get; init; }
    public long? Sequence { get; init; }
    public string[]? Capabilities { get; init; }
    public string? Error { get; init; }
    public bool? Success { get; init; }

    // Tool approval decision (client -> server)
    public string? ApprovalId { get; init; }
    public bool? Approved { get; init; }
}

/// <summary>
/// JSON envelope sent by the gateway when a client opts into envelopes.
/// </summary>
public sealed record WsServerEnvelope
{
    public required string Type { get; init; }
    public string? RequestId { get; init; }
    public string? ProtocolVersion { get; init; }
    public string? Operation { get; init; }
    public string? CatalogId { get; init; }
    public string[]? SupportedCatalogIds { get; init; }
    public string[]? Components { get; init; }
    public string? DataModelJson { get; init; }
    public string? SurfaceTitle { get; init; }
    public string? SurfaceKind { get; init; }
    public string? ParentSurfaceId { get; init; }
    public string? Action { get; init; }
    public string? ParametersJson { get; init; }
    public string? SyncMode { get; init; }
    public string? DiagnosticCode { get; init; }
    public string? Text { get; init; }
    public string? InReplyToMessageId { get; init; }
    public string? SessionId { get; init; }
    public string? SurfaceId { get; init; }
    public string? ContentType { get; init; }
    public string? Frames { get; init; }
    public string? Html { get; init; }
    public string? Url { get; init; }
    public string? Script { get; init; }
    public string? SnapshotMode { get; init; }
    public string? SnapshotJson { get; init; }
    public string? ComponentId { get; init; }
    public string? Event { get; init; }
    public string? ValueJson { get; init; }
    public long? Sequence { get; init; }
    public string[]? Capabilities { get; init; }
    public string? Error { get; init; }
    public bool? Success { get; init; }

    // Tool approval request/status (server -> client)
    public string? ApprovalId { get; init; }
    public string? ToolName { get; init; }
    public string? ArgumentsPreview { get; init; }
    public bool? Approved { get; init; }
    public string? ResultStatus { get; init; }
    public string? FailureCode { get; init; }
    public string? FailureMessage { get; init; }
    public string? NextStep { get; init; }

    // Artifact / stage-gate envelope payloads
    /// <summary>Optional semantic type for artifact deliveries emitted by the emit_artifact tool.</summary>
    public string? ArtifactType { get; init; }

    /// <summary>Unified artifact payload (type = "artifact"). Carries the full <see cref="SkillArtifact"/>.</summary>
    public SkillArtifact? Artifact { get; init; }

    /// <summary>Stage gate transition event (type = "skill_stage_gate") emitted after a terminal artifact.</summary>
    public SkillStageGateEvent? StageGate { get; init; }

    // File attachment envelope payloads (type = "file_attachment").
    /// <summary>Relative URL to download the file (e.g. "/media/{id}").</summary>
    public string? FileUrl { get; init; }

    /// <summary>Original file name for the attachment.</summary>
    public string? FileName { get; init; }

    /// <summary>MIME type of the attachment.</summary>
    public string? MimeType { get; init; }

    /// <summary>File size in bytes.</summary>
    public long? FileSizeBytes { get; init; }
}

public sealed record SkillStageGateEvent
{
    public required string SkillName { get; init; }
    public required string CompletedStage { get; init; }
    public required string NextStage { get; init; }
    public required bool CanProceed { get; init; }
    public string? BlockedReason { get; init; }
}

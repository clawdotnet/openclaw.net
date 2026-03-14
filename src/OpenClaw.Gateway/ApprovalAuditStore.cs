using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;

namespace OpenClaw.Gateway;

internal sealed class ApprovalAuditStore
{
    private const string AuditDirectoryName = "admin";
    private const string AuditFileName = "approval-audit.jsonl";
    private const int MaxArgumentPreviewChars = 800;

    private readonly string _path;
    private readonly object _gate = new();
    private readonly ILogger<ApprovalAuditStore> _logger;

    public ApprovalAuditStore(string storagePath, ILogger<ApprovalAuditStore> logger)
    {
        var rootedStoragePath = System.IO.Path.IsPathRooted(storagePath)
            ? storagePath
            : System.IO.Path.GetFullPath(storagePath);
        _path = System.IO.Path.Combine(rootedStoragePath, AuditDirectoryName, AuditFileName);
        _logger = logger;
    }

    public string Path => _path;

    public void RecordCreated(ToolApprovalRequest request)
        => Append(new ApprovalHistoryEntry
        {
            EventType = "created",
            ApprovalId = request.ApprovalId,
            SessionId = request.SessionId,
            ChannelId = request.ChannelId,
            SenderId = request.SenderId,
            ToolName = request.ToolName,
            ArgumentsPreview = Truncate(request.Arguments),
            TimestampUtc = request.CreatedAt
        });

    public void RecordDecision(
        ToolApprovalRequest request,
        bool approved,
        string decisionSource,
        string? actorChannelId,
        string? actorSenderId)
        => Append(new ApprovalHistoryEntry
        {
            EventType = "decision",
            ApprovalId = request.ApprovalId,
            SessionId = request.SessionId,
            ChannelId = request.ChannelId,
            SenderId = request.SenderId,
            ToolName = request.ToolName,
            ArgumentsPreview = Truncate(request.Arguments),
            TimestampUtc = request.CreatedAt,
            DecisionAtUtc = DateTimeOffset.UtcNow,
            ActorChannelId = actorChannelId,
            ActorSenderId = actorSenderId,
            DecisionSource = decisionSource,
            Approved = approved
        });

    public IReadOnlyList<ApprovalHistoryEntry> Query(ApprovalHistoryQuery query)
    {
        var limit = Math.Clamp(query.Limit, 1, 500);
        return JsonlQueryBuffer.ReadLatest(
            _path,
            _gate,
            limit,
            CoreJsonContext.Default.ApprovalHistoryEntry,
            entry =>
            {
                if (!string.IsNullOrWhiteSpace(query.ChannelId) &&
                    !string.Equals(entry.ChannelId, query.ChannelId, StringComparison.Ordinal))
                    return false;
                if (!string.IsNullOrWhiteSpace(query.SenderId) &&
                    !string.Equals(entry.SenderId, query.SenderId, StringComparison.Ordinal))
                    return false;
                if (!string.IsNullOrWhiteSpace(query.ToolName) &&
                    !string.Equals(entry.ToolName, query.ToolName, StringComparison.OrdinalIgnoreCase))
                    return false;

                return true;
            },
            _logger,
            "Failed to parse approval audit line from {Path}");
    }

    private void Append(ApprovalHistoryEntry entry)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var line = JsonSerializer.Serialize(entry, CoreJsonContext.Default.ApprovalHistoryEntry);
            lock (_gate)
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append approval audit entry to {Path}", _path);
        }
    }

    private static string Truncate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var trimmed = value.Trim();
        return trimmed.Length <= MaxArgumentPreviewChars
            ? trimmed
            : trimmed[..MaxArgumentPreviewChars] + "…";
    }
}

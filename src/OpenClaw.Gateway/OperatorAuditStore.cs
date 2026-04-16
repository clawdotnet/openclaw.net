using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;

namespace OpenClaw.Gateway;

internal sealed class OperatorAuditStore
{
    private const string DirectoryName = "admin";
    private const string FileName = "operator-actions.jsonl";

    private readonly string _path;
    private readonly object _gate = new();
    private readonly ILogger<OperatorAuditStore> _logger;
    private readonly RuntimeMetrics? _metrics;
    private bool _chainInitialized;
    private long _lastSequence;
    private string? _lastEntryHash;

    public OperatorAuditStore(string storagePath, ILogger<OperatorAuditStore> logger, RuntimeMetrics? metrics = null)
    {
        var rootedStoragePath = System.IO.Path.IsPathRooted(storagePath)
            ? storagePath
            : System.IO.Path.GetFullPath(storagePath);
        _path = System.IO.Path.Combine(rootedStoragePath, DirectoryName, FileName);
        _logger = logger;
        _metrics = metrics;
    }

    public void Append(OperatorAuditEntry entry)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            lock (_gate)
            {
                EnsureChainInitializedUnsafe();
                var sequence = _lastSequence + 1;
                var timestamp = entry.TimestampUtc == default ? DateTimeOffset.UtcNow : entry.TimestampUtc;
                var actorRole = string.IsNullOrWhiteSpace(entry.ActorRole) ? OperatorRoleNames.Viewer : entry.ActorRole;
                var previousEntryHash = _lastEntryHash;
                var entryHash = ComputeEntryHash(
                    sequence,
                    timestamp,
                    entry.ActorId,
                    actorRole,
                    entry.ActorDisplayName,
                    entry.AuthMode,
                    entry.ActionType,
                    entry.TargetId,
                    entry.Summary,
                    previousEntryHash,
                    entry.Before,
                    entry.After,
                    entry.Success);

                var effectiveEntry = new OperatorAuditEntry
                {
                    Id = entry.Id,
                    Sequence = sequence,
                    TimestampUtc = timestamp,
                    ActorId = entry.ActorId,
                    ActorRole = actorRole,
                    ActorDisplayName = entry.ActorDisplayName,
                    AuthMode = entry.AuthMode,
                    ActionType = entry.ActionType,
                    TargetId = entry.TargetId,
                    Summary = entry.Summary,
                    PreviousEntryHash = previousEntryHash,
                    Before = entry.Before,
                    After = entry.After,
                    Success = entry.Success,
                    EntryHash = entryHash
                };

                var line = JsonSerializer.Serialize(effectiveEntry, CoreJsonContext.Default.OperatorAuditEntry);
                File.AppendAllText(_path, line + Environment.NewLine);
                _lastSequence = effectiveEntry.Sequence;
                _lastEntryHash = effectiveEntry.EntryHash;
            }
        }
        catch (Exception ex)
        {
            _metrics?.IncrementOperatorAuditWriteFailures();
            _logger.LogWarning(ex, "Failed to append operator audit entry to {Path}", _path);
        }
    }

    public string Path => _path;

    public IReadOnlyList<OperatorAuditEntry> Query(OperatorAuditQuery query)
    {
        var limit = Math.Clamp(query.Limit, 1, 500);
        return JsonlQueryBuffer.ReadLatest(
            _path,
            _gate,
            limit,
            CoreJsonContext.Default.OperatorAuditEntry,
            item =>
            {
                if (!string.IsNullOrWhiteSpace(query.ActorId) &&
                    !string.Equals(item.ActorId, query.ActorId, StringComparison.Ordinal))
                    return false;
                if (!string.IsNullOrWhiteSpace(query.ActionType) &&
                    !string.Equals(item.ActionType, query.ActionType, StringComparison.OrdinalIgnoreCase))
                    return false;
                if (!string.IsNullOrWhiteSpace(query.TargetId) &&
                    !string.Equals(item.TargetId, query.TargetId, StringComparison.Ordinal))
                    return false;
                if (query.FromUtc is { } fromUtc && item.TimestampUtc < fromUtc)
                    return false;
                if (query.ToUtc is { } toUtc && item.TimestampUtc > toUtc)
                    return false;

                return true;
            },
            _logger,
            "Failed to parse operator audit line from {Path}");
    }

    private void EnsureChainInitializedUnsafe()
    {
        if (_chainInitialized)
            return;

        _lastSequence = 0;
        _lastEntryHash = null;
        if (File.Exists(_path))
        {
            foreach (var line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    var entry = JsonSerializer.Deserialize(line, CoreJsonContext.Default.OperatorAuditEntry);
                    if (entry is null)
                        continue;

                    if (entry.Sequence > _lastSequence)
                    {
                        _lastSequence = entry.Sequence;
                        _lastEntryHash = entry.EntryHash;
                    }
                }
                catch
                {
                }
            }
        }

        _chainInitialized = true;
    }

    private static string ComputeEntryHash(
        long sequence,
        DateTimeOffset timestampUtc,
        string actorId,
        string actorRole,
        string? actorDisplayName,
        string authMode,
        string actionType,
        string targetId,
        string summary,
        string? previousEntryHash,
        string? before,
        string? after,
        bool success)
    {
        var material = string.Join(
            "|",
            sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            timestampUtc.ToString("O"),
            actorId,
            actorRole,
            actorDisplayName ?? "",
            authMode,
            actionType,
            targetId,
            summary,
            previousEntryHash ?? "",
            before ?? "",
            after ?? "",
            success ? "1" : "0");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}

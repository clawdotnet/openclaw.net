using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal sealed class OperatorAuditStore
{
    private const string DirectoryName = "admin";
    private const string FileName = "operator-actions.jsonl";

    private readonly string _path;
    private readonly object _gate = new();
    private readonly ILogger<OperatorAuditStore> _logger;

    public OperatorAuditStore(string storagePath, ILogger<OperatorAuditStore> logger)
    {
        var rootedStoragePath = Path.IsPathRooted(storagePath)
            ? storagePath
            : Path.GetFullPath(storagePath);
        _path = Path.Combine(rootedStoragePath, DirectoryName, FileName);
        _logger = logger;
    }

    public void Append(OperatorAuditEntry entry)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var line = JsonSerializer.Serialize(entry, CoreJsonContext.Default.OperatorAuditEntry);
            lock (_gate)
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append operator audit entry to {Path}", _path);
        }
    }

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

                return true;
            },
            _logger,
            "Failed to parse operator audit line from {Path}");
    }
}

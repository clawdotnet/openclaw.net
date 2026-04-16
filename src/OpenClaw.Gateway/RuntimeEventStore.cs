using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;

namespace OpenClaw.Gateway;

internal sealed class RuntimeEventStore
{
    private const string DirectoryName = "admin";
    private const string FileName = "runtime-events.jsonl";

    private readonly string _path;
    private readonly object _gate = new();
    private readonly ILogger<RuntimeEventStore> _logger;
    private readonly RuntimeMetrics? _metrics;

    public RuntimeEventStore(string storagePath, ILogger<RuntimeEventStore> logger, RuntimeMetrics? metrics = null)
    {
        var rootedStoragePath = System.IO.Path.IsPathRooted(storagePath)
            ? storagePath
            : System.IO.Path.GetFullPath(storagePath);
        _path = System.IO.Path.Combine(rootedStoragePath, DirectoryName, FileName);
        _logger = logger;
        _metrics = metrics;
    }

    public void Append(RuntimeEventEntry entry)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var line = JsonSerializer.Serialize(entry, CoreJsonContext.Default.RuntimeEventEntry);
            lock (_gate)
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            _metrics?.IncrementRuntimeEventWriteFailures();
            _logger.LogWarning(ex, "Failed to append runtime event to {Path}", _path);
        }
    }

    public string Path => _path;

    public IReadOnlyList<RuntimeEventEntry> Query(RuntimeEventQuery query)
    {
        var limit = Math.Clamp(query.Limit, 1, 500);
        return JsonlQueryBuffer.ReadLatest(
            _path,
            _gate,
            limit,
            CoreJsonContext.Default.RuntimeEventEntry,
            item =>
            {
                if (!string.IsNullOrWhiteSpace(query.SessionId) &&
                    !string.Equals(item.SessionId, query.SessionId, StringComparison.Ordinal))
                    return false;
                if (!string.IsNullOrWhiteSpace(query.ChannelId) &&
                    !string.Equals(item.ChannelId, query.ChannelId, StringComparison.Ordinal))
                    return false;
                if (!string.IsNullOrWhiteSpace(query.SenderId) &&
                    !string.Equals(item.SenderId, query.SenderId, StringComparison.Ordinal))
                    return false;
                if (!string.IsNullOrWhiteSpace(query.Component) &&
                    !string.Equals(item.Component, query.Component, StringComparison.OrdinalIgnoreCase))
                    return false;
                if (!string.IsNullOrWhiteSpace(query.Action) &&
                    !string.Equals(item.Action, query.Action, StringComparison.OrdinalIgnoreCase))
                    return false;
                if (query.FromUtc is { } fromUtc && item.TimestampUtc < fromUtc)
                    return false;
                if (query.ToUtc is { } toUtc && item.TimestampUtc > toUtc)
                    return false;

                return true;
            },
            _logger,
            "Failed to parse runtime event line from {Path}");
    }
}

using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal sealed class RuntimeEventStore
{
    private const string DirectoryName = "admin";
    private const string FileName = "runtime-events.jsonl";

    private readonly string _path;
    private readonly object _gate = new();
    private readonly ILogger<RuntimeEventStore> _logger;

    public RuntimeEventStore(string storagePath, ILogger<RuntimeEventStore> logger)
    {
        var rootedStoragePath = Path.IsPathRooted(storagePath)
            ? storagePath
            : Path.GetFullPath(storagePath);
        _path = Path.Combine(rootedStoragePath, DirectoryName, FileName);
        _logger = logger;
    }

    public void Append(RuntimeEventEntry entry)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
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
            _logger.LogWarning(ex, "Failed to append runtime event to {Path}", _path);
        }
    }

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

                return true;
            },
            _logger,
            "Failed to parse runtime event line from {Path}");
    }
}

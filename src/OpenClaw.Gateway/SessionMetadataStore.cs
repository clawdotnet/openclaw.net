using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal sealed class SessionMetadataStore
{
    private const string DirectoryName = "admin";
    private const string FileName = "session-metadata.json";

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly ILogger<SessionMetadataStore> _logger;
    private List<SessionMetadataSnapshot>? _cached;

    public SessionMetadataStore(string storagePath, ILogger<SessionMetadataStore> logger)
    {
        var rootedStoragePath = Path.IsPathRooted(storagePath)
            ? storagePath
            : Path.GetFullPath(storagePath);
        _path = Path.Combine(rootedStoragePath, DirectoryName, FileName);
        _logger = logger;
    }

    public SessionMetadataSnapshot Get(string sessionId)
    {
        lock (_gate)
        {
            return LoadUnsafe().FirstOrDefault(item => string.Equals(item.SessionId, sessionId, StringComparison.Ordinal))
                ?? new SessionMetadataSnapshot
                {
                    SessionId = sessionId,
                    Starred = false,
                    Tags = []
                };
        }
    }

    public IReadOnlyDictionary<string, SessionMetadataSnapshot> GetAll()
    {
        lock (_gate)
        {
            return LoadUnsafe().ToDictionary(static item => item.SessionId, StringComparer.Ordinal);
        }
    }

    public SessionMetadataSnapshot Set(string sessionId, SessionMetadataUpdateRequest request)
    {
        lock (_gate)
        {
            var items = LoadUnsafe();
            var current = items.FirstOrDefault(item => string.Equals(item.SessionId, sessionId, StringComparison.Ordinal))
                ?? new SessionMetadataSnapshot
                {
                    SessionId = sessionId,
                    Starred = false,
                    Tags = []
                };

            var updated = new SessionMetadataSnapshot
            {
                SessionId = sessionId,
                Starred = request.Starred ?? current.Starred,
                Tags = (request.Tags ?? current.Tags)
                    .Where(static tag => !string.IsNullOrWhiteSpace(tag))
                    .Select(static tag => tag.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(static tag => tag, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };

            items.RemoveAll(item => string.Equals(item.SessionId, sessionId, StringComparison.Ordinal));
            items.Add(updated);
            SaveUnsafe(items);
            return updated;
        }
    }

    private List<SessionMetadataSnapshot> LoadUnsafe()
    {
        if (_cached is not null)
            return _cached;

        try
        {
            if (!File.Exists(_path))
            {
                _cached = [];
                return _cached;
            }

            var json = File.ReadAllText(_path);
            _cached = JsonSerializer.Deserialize(json, CoreJsonContext.Default.ListSessionMetadataSnapshot) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load session metadata from {Path}", _path);
            _cached = [];
        }

        return _cached;
    }

    private void SaveUnsafe(List<SessionMetadataSnapshot> items)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(items, CoreJsonContext.Default.ListSessionMetadataSnapshot);
            File.WriteAllText(_path, json);
            _cached = items;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save session metadata to {Path}", _path);
        }
    }
}

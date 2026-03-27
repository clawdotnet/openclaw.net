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

        if (AtomicJsonFileStore.TryLoad(_path, CoreJsonContext.Default.ListSessionMetadataSnapshot, out List<SessionMetadataSnapshot>? items, out var error))
        {
            _cached = items ?? [];
            return _cached;
        }

        _logger.LogWarning("Failed to load session metadata from {Path}: {Error}", _path, error);
        _cached = [];

        return _cached;
    }

    private void SaveUnsafe(List<SessionMetadataSnapshot> items)
    {
        if (!AtomicJsonFileStore.TryWriteAtomic(_path, items, CoreJsonContext.Default.ListSessionMetadataSnapshot, out var error))
        {
            _logger.LogWarning("Failed to save session metadata to {Path}: {Error}", _path, error);
            throw new InvalidOperationException($"Failed to persist session metadata: {error}");
        }

        _cached = items;
    }
}

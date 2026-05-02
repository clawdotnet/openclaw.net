using System.Text.Json;
using MemPalace.Backends.Sqlite;
using MemPalace.Core.Backends;
using MemPalace.Core.Model;
using MemPalace.KnowledgeGraph;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using MempalaceCollection = MemPalace.Core.Backends.ICollection;

namespace OpenClaw.Gateway.Memory;

internal sealed class MempalaceMemoryStore :
    IMemoryStore,
    IMemoryNoteSearch,
    IMemoryNoteCatalog,
    IMemoryRetentionStore,
    ISessionAdminStore,
    ISessionSearchStore,
    IAsyncDisposable,
    IDisposable
{
    private const int MinEmbeddingDimensions = 16;
    private const int DefaultCatalogLimit = 1000;

    private readonly SqliteBackend _backend;
    private readonly PalaceRef _palace;
    private readonly HashingMempalaceEmbedder _embedder;
    private readonly SqliteMemoryStore _sessionStore;
    private readonly SqliteKnowledgeGraph _knowledgeGraph;
    private readonly string _collectionName;
    private readonly string _defaultWing;
    private readonly string _defaultRoom;
    private readonly int _maxSearchCandidates;
    private readonly SemaphoreSlim _collectionGate = new(1, 1);
    private MempalaceCollection? _collection;
    private bool _disposed;

    public MempalaceMemoryStore(GatewayConfig config, RuntimeMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(metrics);

        var mempalace = config.Memory.Mempalace;
        _collectionName = ValidateCollectionName(mempalace.CollectionName);
        _defaultWing = string.IsNullOrWhiteSpace(mempalace.DefaultWing) ? "openclaw" : mempalace.DefaultWing.Trim();
        _defaultRoom = string.IsNullOrWhiteSpace(mempalace.DefaultRoom) ? "notes" : mempalace.DefaultRoom.Trim();
        _maxSearchCandidates = Math.Clamp(mempalace.MaxSearchCandidates, 10, 10_000);

        var basePath = ResolvePath(mempalace.BasePath, config.Memory.StoragePath);
        Directory.CreateDirectory(basePath);

        var palaceId = string.IsNullOrWhiteSpace(mempalace.PalaceId) ? "openclaw" : mempalace.PalaceId.Trim();
        _palace = new PalaceRef(palaceId, Path.Combine(basePath, palaceId), mempalace.Namespace);
        _backend = new SqliteBackend(basePath);
        _embedder = new HashingMempalaceEmbedder(
            Math.Max(MinEmbeddingDimensions, mempalace.EmbeddingDimensions),
            string.IsNullOrWhiteSpace(mempalace.EmbedderIdentifier)
                ? "openclaw:mempalace:hash-v1"
                : mempalace.EmbedderIdentifier.Trim());

        _sessionStore = new SqliteMemoryStore(
            ResolvePath(mempalace.SessionDbPath, config.Memory.StoragePath),
            enableFts: config.Memory.Sqlite.EnableFts);
        _knowledgeGraph = new SqliteKnowledgeGraph(
            ResolvePath(mempalace.KnowledgeGraphDbPath, config.Memory.StoragePath));
    }

    public IKnowledgeGraph KnowledgeGraph => _knowledgeGraph;

    public async ValueTask<Session?> GetSessionAsync(string sessionId, CancellationToken ct)
        => await _sessionStore.GetSessionAsync(sessionId, ct);

    public async ValueTask SaveSessionAsync(Session session, CancellationToken ct)
        => await _sessionStore.SaveSessionAsync(session, ct);

    public async ValueTask<string?> LoadNoteAsync(string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        var collection = await GetCollectionAsync(ct);
        var result = await collection.GetAsync(
            ids: [key],
            include: IncludeFields.Documents | IncludeFields.Metadatas,
            ct: ct);

        return result.Documents.Count == 0 ? null : result.Documents[0];
    }

    public async ValueTask SaveNoteAsync(string key, string content, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Note key cannot be empty", nameof(key));

        content ??= string.Empty;
        var collection = await GetCollectionAsync(ct);
        var embedding = (await _embedder.EmbedAsync([content], ct))[0];
        var now = DateTimeOffset.UtcNow;
        var (wing, room, drawer) = ResolvePalaceLocation(key);
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["key"] = key,
            ["wing"] = wing,
            ["room"] = room,
            ["drawer"] = drawer,
            ["timestamp"] = now.ToUnixTimeSeconds(),
            ["updated_at"] = now.ToString("O"),
            ["source"] = "openclaw"
        };

        await collection.UpsertAsync(
            [new EmbeddedRecord(key, content, metadata, embedding)],
            ct);
        await RecordNoteGraphAsync(key, wing, room, drawer, now, ct);
    }

    public async ValueTask DeleteNoteAsync(string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        var collection = await GetCollectionAsync(ct);
        await collection.DeleteAsync(ids: [key], ct: ct);
        var now = DateTimeOffset.UtcNow;
        await _knowledgeGraph.AddAsync(
            new TemporalTriple(
                new Triple(
                    new EntityRef("memory", key),
                    "deleted-at",
                    new EntityRef("palace", _palace.Id)),
                now,
                null,
                now),
            ct);
    }

    public async ValueTask<IReadOnlyList<string>> ListNotesWithPrefixAsync(string prefix, CancellationToken ct)
    {
        var entries = await ReadCatalogEntriesAsync(prefix ?? string.Empty, DefaultCatalogLimit, ct);
        return entries.Select(static entry => entry.Key).ToArray();
    }

    public async ValueTask SaveBranchAsync(SessionBranch branch, CancellationToken ct)
        => await _sessionStore.SaveBranchAsync(branch, ct);

    public async ValueTask<SessionBranch?> LoadBranchAsync(string branchId, CancellationToken ct)
        => await _sessionStore.LoadBranchAsync(branchId, ct);

    public async ValueTask<IReadOnlyList<SessionBranch>> ListBranchesAsync(string sessionId, CancellationToken ct)
        => await _sessionStore.ListBranchesAsync(sessionId, ct);

    public async ValueTask DeleteBranchAsync(string branchId, CancellationToken ct)
        => await _sessionStore.DeleteBranchAsync(branchId, ct);

    public async ValueTask<IReadOnlyList<MemoryNoteHit>> SearchNotesAsync(string query, string? prefix, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        limit = Math.Clamp(limit, 1, 50);
        var collection = await GetCollectionAsync(ct);
        var queryEmbedding = (await _embedder.EmbedAsync([query], ct))[0];
        var candidateCount = prefix is null ? limit : Math.Max(limit, _maxSearchCandidates);
        candidateCount = Math.Clamp(candidateCount, limit, _maxSearchCandidates);
        var result = await collection.QueryAsync(
            [queryEmbedding],
            nResults: candidateCount,
            include: IncludeFields.Documents | IncludeFields.Metadatas | IncludeFields.Distances,
            ct: ct);

        if (result.Ids.Count == 0)
            return [];

        var ids = result.Ids[0];
        var documents = result.Documents[0];
        var metadatas = result.Metadatas[0];
        var distances = result.Distances[0];
        var hits = new List<MemoryNoteHit>(Math.Min(limit, ids.Count));

        for (var i = 0; i < ids.Count && hits.Count < limit; i++)
        {
            var key = ids[i];
            if (!string.IsNullOrEmpty(prefix) && !key.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            hits.Add(new MemoryNoteHit
            {
                Key = key,
                Content = documents.Count > i ? documents[i] : string.Empty,
                UpdatedAt = metadatas.Count > i ? ReadUpdatedAt(metadatas[i]) : DateTimeOffset.MinValue,
                Score = distances.Count > i ? Math.Max(0f, 1f - distances[i]) : 0f
            });
        }

        return hits;
    }

    public async ValueTask<IReadOnlyList<MemoryNoteCatalogEntry>> ListNotesAsync(string prefix, int limit, CancellationToken ct)
        => await ReadCatalogEntriesAsync(prefix, limit, ct);

    public async ValueTask<MemoryNoteCatalogEntry?> GetNoteEntryAsync(string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        var collection = await GetCollectionAsync(ct);
        var result = await collection.GetAsync(
            ids: [key],
            include: IncludeFields.Documents | IncludeFields.Metadatas,
            ct: ct);
        if (result.Ids.Count == 0)
            return null;

        return new MemoryNoteCatalogEntry
        {
            Key = result.Ids[0],
            PreviewContent = Truncate(result.Documents.Count == 0 ? string.Empty : result.Documents[0], 240),
            UpdatedAt = result.Metadatas.Count == 0 ? DateTimeOffset.MinValue : ReadUpdatedAt(result.Metadatas[0])
        };
    }

    public async ValueTask<RetentionSweepResult> SweepAsync(
        RetentionSweepRequest request,
        IReadOnlySet<string> protectedSessionIds,
        CancellationToken ct)
        => await ((IMemoryRetentionStore)_sessionStore).SweepAsync(request, protectedSessionIds, ct);

    public async ValueTask<RetentionStoreStats> GetRetentionStatsAsync(CancellationToken ct)
        => await ((IMemoryRetentionStore)_sessionStore).GetRetentionStatsAsync(ct);

    public async ValueTask<PagedSessionList> ListSessionsAsync(int page, int pageSize, SessionListQuery query, CancellationToken ct)
        => await ((ISessionAdminStore)_sessionStore).ListSessionsAsync(page, pageSize, query, ct);

    public async ValueTask<SessionSearchResult> SearchSessionsAsync(SessionSearchQuery query, CancellationToken ct)
        => await ((ISessionSearchStore)_sessionStore).SearchSessionsAsync(query, ct);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _collectionGate.Dispose();
        _sessionStore.Dispose();
        await _knowledgeGraph.DisposeAsync();
        await _backend.DisposeAsync();
    }

    public void Dispose()
        => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private async ValueTask<MempalaceCollection> GetCollectionAsync(CancellationToken ct)
    {
        if (_collection is not null)
            return _collection;

        await _collectionGate.WaitAsync(ct);
        try
        {
            _collection ??= await _backend.GetCollectionAsync(
                _palace,
                _collectionName,
                create: true,
                embedder: _embedder,
                ct: ct);
            return _collection;
        }
        finally
        {
            _collectionGate.Release();
        }
    }

    private async ValueTask<IReadOnlyList<MemoryNoteCatalogEntry>> ReadCatalogEntriesAsync(string? prefix, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 10_000);
        var collection = await GetCollectionAsync(ct);
        var result = await collection.GetAsync(
            include: IncludeFields.Documents | IncludeFields.Metadatas,
            ct: ct);

        var entries = new List<MemoryNoteCatalogEntry>(Math.Min(limit, result.Ids.Count));
        for (var i = 0; i < result.Ids.Count && entries.Count < limit; i++)
        {
            var key = result.Ids[i];
            if (!string.IsNullOrEmpty(prefix) && !key.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            entries.Add(new MemoryNoteCatalogEntry
            {
                Key = key,
                PreviewContent = Truncate(result.Documents.Count > i ? result.Documents[i] : string.Empty, 240),
                UpdatedAt = result.Metadatas.Count > i ? ReadUpdatedAt(result.Metadatas[i]) : DateTimeOffset.MinValue
            });
        }

        return entries
            .OrderByDescending(static entry => entry.UpdatedAt)
            .ThenBy(static entry => entry.Key, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task RecordNoteGraphAsync(string key, string wing, string room, string drawer, DateTimeOffset at, CancellationToken ct)
    {
        var memory = new EntityRef("memory", key);
        var drawerRef = new EntityRef("drawer", drawer);
        var roomRef = new EntityRef("room", room);
        var wingRef = new EntityRef("wing", wing);

        await _knowledgeGraph.AddManyAsync(
            [
                new TemporalTriple(new Triple(memory, "stored-in", drawerRef), at, null, at),
                new TemporalTriple(new Triple(drawerRef, "located-in", roomRef), at, null, at),
                new TemporalTriple(new Triple(roomRef, "located-in", wingRef), at, null, at)
            ],
            ct);
    }

    private (string Wing, string Room, string Drawer) ResolvePalaceLocation(string key)
    {
        var parts = key.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 3)
            return (SanitizeEntityId(parts[0]), SanitizeEntityId(parts[1]), SanitizeEntityId(string.Join(':', parts[2..])));
        if (parts.Length == 2)
            return (_defaultWing, SanitizeEntityId(parts[0]), SanitizeEntityId(parts[1]));
        return (_defaultWing, _defaultRoom, SanitizeEntityId(key));
    }

    private static string ResolvePath(string path, string fallbackBasePath)
    {
        var effective = string.IsNullOrWhiteSpace(path) ? fallbackBasePath : path.Trim();
        if (!Path.IsPathRooted(effective))
            effective = Path.Combine(Directory.GetCurrentDirectory(), effective);
        return Path.GetFullPath(effective);
    }

    private static string ValidateCollectionName(string value)
    {
        var collectionName = string.IsNullOrWhiteSpace(value) ? "memories" : value.Trim();
        if (collectionName.Length > 64)
            throw new InvalidOperationException("OpenClaw:Memory:Mempalace:CollectionName cannot exceed 64 characters.");
        if (collectionName.Any(static ch => !char.IsAsciiLetterOrDigit(ch) && ch != '_'))
            throw new InvalidOperationException("OpenClaw:Memory:Mempalace:CollectionName may contain only ASCII letters, digits, and underscores.");
        return collectionName;
    }

    private static string SanitizeEntityId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "default";

        Span<char> buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[256];
        var index = 0;
        foreach (var ch in value)
        {
            if (index == buffer.Length)
                break;
            buffer[index++] = char.IsControl(ch) ? '_' : ch;
        }

        return new string(buffer[..index]);
    }

    private static DateTimeOffset ReadUpdatedAt(IReadOnlyDictionary<string, object?> metadata)
    {
        if (metadata.TryGetValue("updated_at", out var updatedAt))
        {
            if (updatedAt is string text && DateTimeOffset.TryParse(text, out var parsed))
                return parsed;
            if (updatedAt is JsonElement { ValueKind: JsonValueKind.String } element
                && DateTimeOffset.TryParse(element.GetString(), out parsed))
                return parsed;
        }

        if (metadata.TryGetValue("timestamp", out var timestamp))
        {
            if (timestamp is long seconds)
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            if (timestamp is int intSeconds)
                return DateTimeOffset.FromUnixTimeSeconds(intSeconds);
            if (timestamp is JsonElement element && element.TryGetInt64(out seconds))
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        return DateTimeOffset.MinValue;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";

    private sealed class HashingMempalaceEmbedder : IEmbedder
    {
        private readonly string _identity;

        public HashingMempalaceEmbedder(int dimensions, string identity)
        {
            Dimensions = dimensions;
            _identity = identity;
        }

        public string ModelIdentity => $"{_identity}:{Dimensions}";
        public int Dimensions { get; }

        public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            var embeddings = new ReadOnlyMemory<float>[texts.Count];
            for (var i = 0; i < texts.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                embeddings[i] = EmbedOne(texts[i]);
            }

            return ValueTask.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(embeddings);
        }

        private ReadOnlyMemory<float> EmbedOne(string? text)
        {
            var vector = new float[Dimensions];
            foreach (var token in Tokenize(text ?? string.Empty))
            {
                var hash = StableHash(token);
                var index = (int)(hash % (uint)Dimensions);
                vector[index] += (hash & 0x8000_0000u) == 0 ? 1f : -1f;
            }

            var magnitude = 0f;
            for (var i = 0; i < vector.Length; i++)
                magnitude += vector[i] * vector[i];

            if (magnitude > 0f)
            {
                var scale = 1f / MathF.Sqrt(magnitude);
                for (var i = 0; i < vector.Length; i++)
                    vector[i] *= scale;
            }

            return vector;
        }

        private static IEnumerable<string> Tokenize(string text)
        {
            var start = -1;
            for (var i = 0; i < text.Length; i++)
            {
                if (char.IsLetterOrDigit(text[i]))
                {
                    if (start < 0)
                        start = i;
                    continue;
                }

                if (start >= 0)
                {
                    yield return text[start..i].ToLowerInvariant();
                    start = -1;
                }
            }

            if (start >= 0)
                yield return text[start..].ToLowerInvariant();
        }

        private static uint StableHash(string value)
        {
            var hash = 2166136261u;
            foreach (var ch in value)
            {
                hash ^= ch;
                hash *= 16777619u;
            }

            return hash;
        }
    }
}

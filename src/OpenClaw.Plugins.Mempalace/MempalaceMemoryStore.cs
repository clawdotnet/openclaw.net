using System.Security.Cryptography;
using System.Text;
using MemPalace.Backends.Sqlite;
using MemPalace.Core.Backends;
using MemPalace.Core.Model;
using MemPalace.KnowledgeGraph;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using MempalaceCollection = MemPalace.Core.Backends.ICollection;

namespace OpenClaw.Plugins.Mempalace;

internal sealed class MempalaceMemoryStore :
    IMemoryStore,
    IMemoryNoteSearch,
    IMemoryNoteCatalog,
    ISessionAdminStore,
    ISessionSearchStore,
    IAsyncDisposable,
    IDisposable
{
    private readonly MemoryMempalaceConfig _config;
    private readonly SqliteMemoryStore _sessionStore;
    private readonly SqliteBackend _backend;
    private readonly PalaceRef _palace;
    private readonly HashingEmbedder _embedder;
    private readonly SemaphoreSlim _collectionGate = new(1, 1);
    private MempalaceCollection? _collection;
    private bool _disposed;

    public MempalaceMemoryStore(GatewayConfig gatewayConfig, RuntimeMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(gatewayConfig);

        _config = gatewayConfig.Memory.Mempalace;
        var basePath = Path.GetFullPath(_config.BasePath);
        Directory.CreateDirectory(basePath);

        var sessionDbPath = Path.GetFullPath(_config.SessionDbPath);
        var knowledgeGraphDbPath = Path.GetFullPath(_config.KnowledgeGraphDbPath);
        var collectionDbPath = Path.Join(basePath, "collections.db");

        _sessionStore = new SqliteMemoryStore(sessionDbPath, enableFts: true);
        _backend = new SqliteBackend(collectionDbPath);
        _palace = new PalaceRef(
            string.IsNullOrWhiteSpace(_config.PalaceId) ? "openclaw" : _config.PalaceId,
            basePath,
            string.IsNullOrWhiteSpace(_config.Namespace) ? "default" : _config.Namespace);
        _embedder = new HashingEmbedder(
            string.IsNullOrWhiteSpace(_config.EmbedderIdentifier)
                ? "openclaw:mempalace:hash-v1"
                : _config.EmbedderIdentifier,
            Math.Max(16, _config.EmbeddingDimensions));
        KnowledgeGraph = new SqliteKnowledgeGraph(knowledgeGraphDbPath);
    }

    public IKnowledgeGraph KnowledgeGraph { get; }

    public ValueTask<Session?> GetSessionAsync(string sessionId, CancellationToken ct)
        => _sessionStore.GetSessionAsync(sessionId, ct);

    public ValueTask SaveSessionAsync(Session session, CancellationToken ct)
        => _sessionStore.SaveSessionAsync(session, ct);

    public ValueTask DeleteSessionAsync(string sessionId, CancellationToken ct)
      => _sessionStore.DeleteSessionAsync(sessionId, ct);

    public async ValueTask<string?> LoadNoteAsync(string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        var collection = await GetCollectionAsync(ct);
        var result = await collection.GetAsync(
            [key],
            null!,
            limit: 1,
            offset: 0,
            IncludeFields.Documents | IncludeFields.Metadatas,
            ct);

        if (result.Documents.Count > 0)
            return result.Documents[0];

        return await _sessionStore.LoadNoteAsync(key, ct);
    }

    public async ValueTask SaveNoteAsync(string key, string content, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Note key cannot be empty.", nameof(key));

        content ??= string.Empty;
        var placement = NotePlacement.FromKey(key, _config);
        var now = DateTimeOffset.UtcNow;
        var embeddings = await _embedder.EmbedAsync([content], ct);
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["key"] = key,
            ["wing"] = placement.Wing,
            ["room"] = placement.Room,
            ["drawer"] = placement.Drawer,
            ["updatedAt"] = now.ToUnixTimeSeconds()
        };

        var collection = await GetCollectionAsync(ct);
        await collection.UpsertAsync(
            [new EmbeddedRecord(key, content, metadata, embeddings[0])],
            ct);
        await _sessionStore.SaveNoteAsync(key, content, ct);
        await RecordPlacementAsync(key, placement, now, ct);
    }

    public async ValueTask DeleteNoteAsync(string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        var collection = await GetCollectionAsync(ct);
        await collection.DeleteAsync([key], null!, ct);
        await _sessionStore.DeleteNoteAsync(key, ct);
    }

    public ValueTask<IReadOnlyList<string>> ListNotesWithPrefixAsync(string prefix, CancellationToken ct)
        => _sessionStore.ListNotesWithPrefixAsync(prefix, ct);

    public async ValueTask<IReadOnlyList<MemoryNoteHit>> SearchNotesAsync(string query, string? prefix, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
            return [];

        limit = Math.Clamp(limit, 1, 50);
        prefix ??= string.Empty;

        var collection = await GetCollectionAsync(ct);
        var queryEmbeddings = await _embedder.EmbedAsync([query], ct);
        var result = await collection.QueryAsync(
            queryEmbeddings,
            Math.Max(limit, Math.Min(_config.MaxSearchCandidates, limit * 4)),
            null!,
            IncludeFields.Documents | IncludeFields.Metadatas | IncludeFields.Distances,
            ct);

        if (result.Ids.Count == 0)
            return await _sessionStore.SearchNotesAsync(query, prefix, limit, ct);

        var ids = result.Ids[0];
        var documents = result.Documents.Count > 0 ? result.Documents[0] : [];
        var distances = result.Distances.Count > 0 ? result.Distances[0] : [];
        var metadatas = result.Metadatas.Count > 0 ? result.Metadatas[0] : [];
        var hits = new List<MemoryNoteHit>(Math.Min(limit, ids.Count));

        for (var i = 0; i < ids.Count && hits.Count < limit; i++)
        {
            ct.ThrowIfCancellationRequested();

            var key = ids[i];
            if (!string.IsNullOrEmpty(prefix) && !key.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var content = i < documents.Count ? documents[i] : await LoadNoteAsync(key, ct) ?? string.Empty;
            hits.Add(new MemoryNoteHit
            {
                Key = key,
                Content = content,
                UpdatedAt = TryReadUpdatedAt(i < metadatas.Count ? metadatas[i] : null),
                Score = i < distances.Count ? 1.0f / (1.0f + Math.Max(0, distances[i])) : 1.0f
            });
        }

        return hits.Count > 0
            ? hits
            : await _sessionStore.SearchNotesAsync(query, prefix, limit, ct);
    }

    public ValueTask<IReadOnlyList<MemoryNoteCatalogEntry>> ListNotesAsync(string prefix, int limit, CancellationToken ct)
        => _sessionStore.ListNotesAsync(prefix, limit, ct);

    public ValueTask<MemoryNoteCatalogEntry?> GetNoteEntryAsync(string key, CancellationToken ct)
        => _sessionStore.GetNoteEntryAsync(key, ct);

    public ValueTask SaveBranchAsync(SessionBranch branch, CancellationToken ct)
        => _sessionStore.SaveBranchAsync(branch, ct);

    public ValueTask<SessionBranch?> LoadBranchAsync(string branchId, CancellationToken ct)
        => _sessionStore.LoadBranchAsync(branchId, ct);

    public ValueTask<IReadOnlyList<SessionBranch>> ListBranchesAsync(string sessionId, CancellationToken ct)
        => _sessionStore.ListBranchesAsync(sessionId, ct);

    public ValueTask DeleteBranchAsync(string branchId, CancellationToken ct)
        => _sessionStore.DeleteBranchAsync(branchId, ct);

    public ValueTask<PagedSessionList> ListSessionsAsync(int page, int pageSize, SessionListQuery query, CancellationToken ct)
        => _sessionStore.ListSessionsAsync(page, pageSize, query, ct);

    public ValueTask<SessionSearchResult> SearchSessionsAsync(SessionSearchQuery query, CancellationToken ct)
        => _sessionStore.SearchSessionsAsync(query, ct);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_collection is IAsyncDisposable collection)
            await collection.DisposeAsync();

        await _backend.DisposeAsync();
        if (KnowledgeGraph is IAsyncDisposable graph)
            await graph.DisposeAsync();

        _sessionStore.Dispose();
        _collectionGate.Dispose();
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
                string.IsNullOrWhiteSpace(_config.CollectionName) ? "memories" : _config.CollectionName,
                true,
                _embedder,
                ct);
            return _collection;
        }
        finally
        {
            _collectionGate.Release();
        }
    }

    private async Task RecordPlacementAsync(string key, NotePlacement placement, DateTimeOffset now, CancellationToken ct)
    {
        var validFrom = now;
        var facts = new[]
        {
            new TemporalTriple(
                new Triple(new EntityRef("memory", key), "stored-in", new EntityRef("drawer", placement.Drawer)),
                validFrom,
                null,
                now),
            new TemporalTriple(
                new Triple(new EntityRef("drawer", placement.Drawer), "located-in", new EntityRef("room", placement.Room)),
                validFrom,
                null,
                now),
            new TemporalTriple(
                new Triple(new EntityRef("room", placement.Room), "located-in", new EntityRef("wing", placement.Wing)),
                validFrom,
                null,
                now)
        };

        await KnowledgeGraph.AddManyAsync(facts, ct);
    }

    private static DateTimeOffset TryReadUpdatedAt(IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata is not null &&
            metadata.TryGetValue("updatedAt", out var value) &&
            TryConvertToUnixSeconds(value, out var unixSeconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }

        return DateTimeOffset.UtcNow;
    }

    private static bool TryConvertToUnixSeconds(object? value, out long unixSeconds)
    {
        switch (value)
        {
            case long longValue:
                unixSeconds = longValue;
                return true;
            case int intValue:
                unixSeconds = intValue;
                return true;
            case string text when long.TryParse(text, out var parsed):
                unixSeconds = parsed;
                return true;
            default:
                unixSeconds = 0;
                return false;
        }
    }

    private readonly record struct NotePlacement(string Wing, string Room, string Drawer)
    {
        public static NotePlacement FromKey(string key, MemoryMempalaceConfig config)
        {
            var segments = key.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var defaultWing = string.IsNullOrWhiteSpace(config.DefaultWing) ? "openclaw" : config.DefaultWing;
            var defaultRoom = string.IsNullOrWhiteSpace(config.DefaultRoom) ? "notes" : config.DefaultRoom;

            return segments.Length switch
            {
                >= 3 => new NotePlacement(segments[0], segments[1], segments[^1]),
                2 => new NotePlacement(defaultWing, segments[0], segments[1]),
                _ => new NotePlacement(defaultWing, defaultRoom, string.IsNullOrWhiteSpace(key) ? "note" : key)
            };
        }
    }

    private sealed class HashingEmbedder(string modelIdentity, int dimensions) : IEmbedder
    {
        public string ModelIdentity { get; } = modelIdentity;
        public int Dimensions { get; } = dimensions;

        public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
        {
            var results = new ReadOnlyMemory<float>[inputs.Count];
            for (var i = 0; i < inputs.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                results[i] = BuildEmbedding(inputs[i] ?? string.Empty);
            }

            return ValueTask.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(results);
        }

        private ReadOnlyMemory<float> BuildEmbedding(string input)
        {
            var vector = new float[Dimensions];
            foreach (var token in Tokenize(input))
            {
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
                var index = BitConverter.ToUInt32(hash, 0) % (uint)Dimensions;
                var sign = (hash[4] & 1) == 0 ? 1.0f : -1.0f;
                vector[index] += sign;
            }

            var norm = MathF.Sqrt(vector.Sum(static value => value * value));
            if (norm <= 0)
                vector[0] = 1;
            else
            {
                for (var i = 0; i < vector.Length; i++)
                    vector[i] /= norm;
            }

            return vector;
        }

        private static IEnumerable<string> Tokenize(string input)
        {
            var start = -1;
            for (var i = 0; i <= input.Length; i++)
            {
                var isTokenChar = i < input.Length && char.IsLetterOrDigit(input[i]);
                if (isTokenChar && start < 0)
                    start = i;
                else if (!isTokenChar && start >= 0)
                {
                    yield return input[start..i].ToLowerInvariant();
                    start = -1;
                }
            }
        }
    }
}

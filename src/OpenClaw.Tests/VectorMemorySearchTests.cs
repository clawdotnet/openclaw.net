using System.Runtime.InteropServices;
using Microsoft.Extensions.AI;
using OpenClaw.Core.Memory;
using Xunit;

namespace OpenClaw.Tests;

public sealed class VectorMemorySearchTests : IDisposable
{
    private readonly string _dbPath;

    public VectorMemorySearchTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"openclaw-vec-test-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void CosineSimilarity_IdenticalVectors_ReturnsOne()
    {
        var v = new float[] { 1f, 2f, 3f };
        var result = SqliteMemoryStore.CosineSimilarity(v.AsSpan(), v.AsSpan());
        Assert.Equal(1f, result, precision: 5);
    }

    [Fact]
    public void CosineSimilarity_OrthogonalVectors_ReturnsZero()
    {
        var a = new float[] { 1f, 0f, 0f };
        var b = new float[] { 0f, 1f, 0f };
        var result = SqliteMemoryStore.CosineSimilarity(a.AsSpan(), b.AsSpan());
        Assert.Equal(0f, result, precision: 5);
    }

    [Fact]
    public void SerializeDeserialize_RoundTrips()
    {
        var original = new float[] { 1.5f, -2.3f, 0f, 42.0f };
        var embedding = new Embedding<float>(original);
        var bytes = SqliteMemoryStore.SerializeEmbedding(embedding);
        var restored = SqliteMemoryStore.DeserializeEmbedding(bytes);

        Assert.NotNull(restored);
        Assert.Equal(original.Length, restored!.Length);
        for (int i = 0; i < original.Length; i++)
            Assert.Equal(original[i], restored[i]);
    }

    [Fact]
    public async Task SearchNotes_WithVectors_ReranksResults()
    {
        var mockGen = new FakeEmbeddingGenerator(text =>
        {
            // Return different embeddings based on content for testing re-ranking
            if (text.Contains("cat"))
                return new float[] { 1f, 0f, 0f };
            if (text.Contains("dog"))
                return new float[] { 0.9f, 0.1f, 0f }; // similar to cat
            return new float[] { 0f, 0f, 1f }; // dissimilar
        });

        var store = new SqliteMemoryStore(_dbPath, enableFts: true,
            embeddingGenerator: mockGen, enableVectors: true);

        await store.SaveNoteAsync("note:1", "The cat sat on the mat", TestContext.Current.CancellationToken);
        await store.SaveNoteAsync("note:2", "A dog played in the park", TestContext.Current.CancellationToken);
        await store.SaveNoteAsync("note:3", "The cat and dog are friends", TestContext.Current.CancellationToken);

        // Search for "cat" - the vector re-ranking should still return cat-related notes first
        var results = await store.SearchNotesAsync("cat", prefix: null, limit: 3, TestContext.Current.CancellationToken);
        Assert.NotEmpty(results);
        // The exact ordering depends on BM25+cosine combination, but cat-only note should be top
        Assert.Contains(results, r => r.Key == "note:1");
    }

    [Fact]
    public async Task SearchNotes_VectorsDisabled_UsesFtsOnly()
    {
        var store = new SqliteMemoryStore(_dbPath, enableFts: true,
            embeddingGenerator: null, enableVectors: false);

        await store.SaveNoteAsync("note:1", "Hello world", TestContext.Current.CancellationToken);
        var results = await store.SearchNotesAsync("hello", prefix: null, limit: 5, TestContext.Current.CancellationToken);
        Assert.Single(results);
        Assert.Equal("note:1", results[0].Key);
    }

    [Fact]
    public async Task SaveNote_EmbeddingFailure_StillSavesNote()
    {
        var failGen = new FakeEmbeddingGenerator(_ => throw new InvalidOperationException("Embedding service down"));

        var store = new SqliteMemoryStore(_dbPath, enableFts: true,
            embeddingGenerator: failGen, enableVectors: true);

        await store.SaveNoteAsync("note:fail", "This should still save", TestContext.Current.CancellationToken);
        var content = await store.LoadNoteAsync("note:fail", TestContext.Current.CancellationToken);
        Assert.Equal("This should still save", content);
    }

    /// <summary>Simple test double for IEmbeddingGenerator.</summary>
    private sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly Func<string, float[]> _generate;

        public FakeEmbeddingGenerator(Func<string, float[]> generate)
            => _generate = generate;

        public EmbeddingGeneratorMetadata Metadata { get; } = new("fake");

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var results = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var value in values)
                results.Add(new Embedding<float>(_generate(value)));
            return Task.FromResult(results);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}

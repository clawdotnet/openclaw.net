using System.Linq;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace OpenClaw.Routing.Onnx;

public interface ILocalEmbeddingGenerator
{
    ValueTask<float[]> GenerateAsync(string text, CancellationToken cancellationToken);
}

internal interface IEmbeddingModelRunner : IDisposable
{
    IReadOnlyCollection<string> InputNames { get; }

    float[] Run(IReadOnlyCollection<NamedOnnxValue> inputs, long[] attentionMask, CancellationToken cancellationToken);
}

public sealed class LocalOnnxEmbeddingGenerator : ILocalEmbeddingGenerator, IDisposable
{
    private readonly IEmbeddingModelRunner _runner;
    private readonly Tokenizer _tokenizer;
    private readonly int _dimensions;
    private readonly string? _tokenizerWorkingDirectory;
    private readonly string _inputIdsName;
    private readonly string? _attentionMaskName;
    private readonly string? _tokenTypeIdsName;
    private int _disposed;

    public LocalOnnxEmbeddingGenerator(string modelPath, string tokenizerPath, int dimensions = 384)
    {
        ModelPath = modelPath;
        TokenizerPath = tokenizerPath;
        _dimensions = dimensions;
        _runner = new OnnxEmbeddingModelRunner(modelPath, dimensions);
        try
        {
            (_tokenizer, _tokenizerWorkingDirectory) = HuggingFaceTokenizerLoader.Load(tokenizerPath);
            _inputIdsName = FindRequiredInputName(["input_ids", "inputIds", "ids", "input", "tokens", "token_ids"]);
            _attentionMaskName = FindOptionalInputName(["attention_mask", "attentionMask", "mask"]);
            _tokenTypeIdsName = FindOptionalInputName(["token_type_ids", "tokenTypeIds"]);
        }
        catch
        {
            _runner.Dispose();
            if (_tokenizerWorkingDirectory is not null && Directory.Exists(_tokenizerWorkingDirectory))
            {
                try { Directory.Delete(_tokenizerWorkingDirectory, recursive: true); }
                catch (Exception ex) when (IsExpectedCleanupException(ex)) { /* best-effort */ }
            }
            throw;
        }
    }

    internal LocalOnnxEmbeddingGenerator(
        IEmbeddingModelRunner runner,
        Tokenizer tokenizer,
        int dimensions,
        string modelPath,
        string tokenizerPath,
        string? tokenizerWorkingDirectory = null)
    {
        _runner = runner;
        _tokenizer = tokenizer;
        _dimensions = dimensions;
        _tokenizerWorkingDirectory = tokenizerWorkingDirectory;
        ModelPath = modelPath;
        TokenizerPath = tokenizerPath;

        try
        {
            _inputIdsName = FindRequiredInputName(["input_ids", "inputIds", "ids", "input", "tokens", "token_ids"]);
            _attentionMaskName = FindOptionalInputName(["attention_mask", "attentionMask", "mask"]);
            _tokenTypeIdsName = FindOptionalInputName(["token_type_ids", "tokenTypeIds"]);
        }
        catch
        {
            _runner.Dispose();
            if (_tokenizerWorkingDirectory is not null && Directory.Exists(_tokenizerWorkingDirectory))
            {
                try { Directory.Delete(_tokenizerWorkingDirectory, recursive: true); }
                catch (Exception ex) when (IsExpectedCleanupException(ex)) { /* best-effort */ }
            }
            throw;
        }
    }

    public string ModelPath { get; }

    public string TokenizerPath { get; }

    public ValueTask<float[]> GenerateAsync(string text, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        return new ValueTask<float[]>(Task.Run(() => GenerateCore(text, cancellationToken), cancellationToken));
    }

    private float[] GenerateCore(string text, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        var tokenIds = _tokenizer.EncodeToIds(text, considerPreTokenization: true, considerNormalization: true);

        // Return a zero-vector for text that the tokenizer cannot represent rather than
        // feeding a meaningless PAD token (id=0) whose embedding is model-dependent.
        if (tokenIds.Count == 0)
            return new float[_dimensions];

        // Most BERT-family models have a hard max sequence length of 512.
        const int maxTokens = 512;
        var inputTokenIds = tokenIds
            .Take(maxTokens)
            .Select(static id => (long)id)
            .ToArray();
        var attentionMask = Enumerable.Repeat(1L, inputTokenIds.Length).ToArray();
        var tokenTypeIds = new long[inputTokenIds.Length];

        var inputIdsTensor = new DenseTensor<long>(inputTokenIds, [1, inputTokenIds.Length]);
        var attentionMaskTensor = new DenseTensor<long>(attentionMask, [1, attentionMask.Length]);
        var tokenTypeIdsTensor = new DenseTensor<long>(tokenTypeIds, [1, tokenTypeIds.Length]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputIdsName, inputIdsTensor)
        };

        if (_attentionMaskName is not null)
            inputs.Add(NamedOnnxValue.CreateFromTensor(_attentionMaskName, attentionMaskTensor));

        if (_tokenTypeIdsName is not null)
            inputs.Add(NamedOnnxValue.CreateFromTensor(_tokenTypeIdsName, tokenTypeIdsTensor));

        return _runner.Run(inputs, attentionMask, cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _runner.Dispose();

        if (_tokenizerWorkingDirectory is not null && Directory.Exists(_tokenizerWorkingDirectory))
        {
            try { Directory.Delete(_tokenizerWorkingDirectory, recursive: true); }
            catch (Exception ex) when (IsExpectedCleanupException(ex)) { /* best-effort; OS file locks or access restrictions may delay deletion */ }
        }
    }

    private string FindRequiredInputName(string[] candidates)
        => FindOptionalInputName(candidates)
            ?? throw new InvalidOperationException($"Embedding model '{ModelPath}' does not expose a supported input tensor.");

    private string? FindOptionalInputName(string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var match = _runner.InputNames.FirstOrDefault(name => string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        return null;
    }

    private static bool IsExpectedCleanupException(Exception ex)
        => ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException;

    internal static class HuggingFaceTokenizerLoader
    {
        public static (Tokenizer Tokenizer, string WorkingDirectory) Load(string tokenizerPath)
        {
            using var stream = File.OpenRead(tokenizerPath);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;

            if (!root.TryGetProperty("model", out var modelElement))
                throw new InvalidOperationException($"Tokenizer file '{tokenizerPath}' is missing the required 'model' section.");
            if (!modelElement.TryGetProperty("type", out var typeElement))
                throw new InvalidOperationException($"Tokenizer file '{tokenizerPath}' does not specify a model type under 'model.type'.");
            var modelType = typeElement.GetString();

            var tempDir = Path.Join(Path.GetTempPath(), "openclaw-routing-tokenizers", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                return modelType?.Trim().ToUpperInvariant() switch
                {
                    "BPE" => LoadBpe(root, modelElement, tempDir),
                    "WORDPIECE" => LoadWordPiece(modelElement, tempDir),
                    _ => throw new NotSupportedException($"Tokenizer model type '{modelType}' is not supported yet.")
                };
            }
            catch
            {
                try { Directory.Delete(tempDir, recursive: true); }
                catch (Exception ex) when (IsExpectedCleanupException(ex)) { /* best-effort */ }
                throw;
            }
        }

        private static (Tokenizer Tokenizer, string WorkingDirectory) LoadBpe(JsonElement root, JsonElement modelElement, string tempDir)
        {
            var vocabPath = Path.Join(tempDir, "vocab.json");
            var mergesPath = Path.Join(tempDir, "merges.txt");

            if (!modelElement.TryGetProperty("vocab", out var vocabElement))
                throw new InvalidOperationException("BPE tokenizer model is missing the required 'vocab' field.");
            File.WriteAllText(vocabPath, vocabElement.GetRawText());

            if (!modelElement.TryGetProperty("merges", out var mergesElement))
                throw new InvalidOperationException("BPE tokenizer model is missing the required 'merges' field.");
            var mergeLines = mergesElement
                .EnumerateArray()
                .Select(static item => item.ValueKind == JsonValueKind.String
                    ? item.GetString() ?? string.Empty
                    : string.Join(" ", item.EnumerateArray().Select(static part => part.GetString() ?? string.Empty)))
                .Where(static line => !string.IsNullOrWhiteSpace(line));
            File.WriteAllLines(mergesPath, ["#version: 0.2", .. mergeLines]);

            var unknownToken = modelElement.TryGetProperty("unk_token", out var unkTokenElement)
                ? unkTokenElement.GetString()
                : "[UNK]";

            var continuingSubwordPrefix = modelElement.TryGetProperty("continuing_subword_prefix", out var prefixElement)
                ? prefixElement.GetString()
                : null;

            var endOfWordSuffix = modelElement.TryGetProperty("end_of_word_suffix", out var suffixElement)
                ? suffixElement.GetString()
                : null;

            var preTokenizer = ResolvePreTokenizer(root, out var byteLevel);
            var tokenizer = BpeTokenizer.Create(new BpeOptions(vocabPath, mergesPath)
            {
                PreTokenizer = preTokenizer,
                SpecialTokens = new Dictionary<string, int>(),
                UnknownToken = unknownToken ?? "[UNK]",
                ContinuingSubwordPrefix = continuingSubwordPrefix ?? string.Empty,
                EndOfWordSuffix = endOfWordSuffix ?? string.Empty,
                FuseUnknownTokens = false,
                ByteLevel = byteLevel
            });

            return (tokenizer, tempDir);
        }

        private static (Tokenizer Tokenizer, string WorkingDirectory) LoadWordPiece(JsonElement modelElement, string tempDir)
        {
            var vocabPath = Path.Join(tempDir, "vocab.txt");
            if (!modelElement.TryGetProperty("vocab", out var vocabElement))
                throw new InvalidOperationException("WordPiece tokenizer model is missing the required 'vocab' field.");

            var unknownToken = modelElement.TryGetProperty("unk_token", out var unkTokenElement)
                ? unkTokenElement.GetString()
                : "[UNK]";

            WriteWordPieceVocab(vocabElement, vocabPath, unknownToken ?? "[UNK]");

            var tokenizer = BertTokenizer.Create(vocabPath);
            return (tokenizer, tempDir);
        }

        private static void WriteWordPieceVocab(JsonElement vocabElement, string vocabPath, string unknownToken)
        {
            if (vocabElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("WordPiece tokenizer 'vocab' must be a JSON object mapping token to id.");

            var pairs = vocabElement
                .EnumerateObject()
                .Select(static property => TryReadWordPieceTokenId(property))
                .Where(static pair => pair.HasValue)
                .Select(static pair => pair!.Value)
                .ToList();

            if (pairs.Count == 0)
                throw new InvalidOperationException("WordPiece tokenizer vocab does not contain any valid token-id entries.");

            var maxId = pairs.Max(static pair => pair.Id);
            var tokens = Enumerable.Repeat(unknownToken, maxId + 1).ToArray();
            foreach (var (token, id) in pairs)
            {
                if (id >= 0 && id < tokens.Length)
                    tokens[id] = token;
            }

            File.WriteAllLines(vocabPath, tokens);
        }

        private static (string Token, int Id)? TryReadWordPieceTokenId(JsonProperty property)
        {
            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var id) || id < 0)
                return null;

            return (property.Name, id);
        }

        private static Dictionary<string, int> ExtractSpecialTokens(JsonElement root)
        {
            var specialTokens = new Dictionary<string, int>(StringComparer.Ordinal);
            if (!root.TryGetProperty("added_tokens", out var addedTokensElement) || addedTokensElement.ValueKind != JsonValueKind.Array)
                return specialTokens;

            foreach (var tokenElement in addedTokensElement.EnumerateArray())
            {
                if (!tokenElement.TryGetProperty("content", out var contentElement)
                    || contentElement.ValueKind != JsonValueKind.String
                    || !tokenElement.TryGetProperty("id", out var idElement)
                    || idElement.ValueKind != JsonValueKind.Number
                    || !idElement.TryGetInt32(out var id)
                    || id < 0)
                {
                    continue;
                }

                var content = contentElement.GetString();
                if (!string.IsNullOrWhiteSpace(content) && !specialTokens.ContainsKey(content))
                    specialTokens[content] = id;
            }

            return specialTokens;
        }

        private static PreTokenizer ResolvePreTokenizer(JsonElement root, out bool byteLevel)
        {
            if (!root.TryGetProperty("pre_tokenizer", out var preTokenizerElement))
            {
                byteLevel = false;
                return PreTokenizer.CreateWhiteSpace(new Dictionary<string, int>());
            }

            return ResolvePreTokenizerElement(preTokenizerElement, out byteLevel);
        }

        private static PreTokenizer ResolvePreTokenizerElement(JsonElement preTokenizerElement, out bool byteLevel)
        {
            if (!preTokenizerElement.TryGetProperty("type", out var typeElement))
                throw new InvalidOperationException("Tokenizer pre_tokenizer section is missing the required 'type' field.");

            var preTokenizerType = typeElement.GetString();
            switch (preTokenizerType?.Trim().ToUpperInvariant())
            {
                case "BYTELEVEL" or "ROBERTA":
                    byteLevel = true;
                    return RobertaPreTokenizer.Instance;
                case "WHITESPACE" or "WHITESPACESPLIT":
                    byteLevel = false;
                    return PreTokenizer.CreateWhiteSpace(new Dictionary<string, int>());
                case "BERTPRETOKENIZER":
                    byteLevel = false;
                    return PreTokenizer.CreateWhiteSpace(new Dictionary<string, int>());
                case "SEQUENCE":
                    return ResolveSequencePreTokenizer(preTokenizerElement, out byteLevel);
                default:
                    throw new NotSupportedException($"Tokenizer pre-tokenizer type '{preTokenizerType}' is not supported yet.");
            }
        }

        private static PreTokenizer ResolveSequencePreTokenizer(JsonElement preTokenizerElement, out bool byteLevel)
        {
            if (!TryGetPreTokenizerSequence(preTokenizerElement, out var preTokenizers))
                throw new InvalidOperationException("Tokenizer pre_tokenizer sequence is missing its pretokenizers array.");

            byteLevel = false;
            foreach (var item in preTokenizers.EnumerateArray())
            {
                _ = ResolvePreTokenizerElement(item, out var itemByteLevel);
                byteLevel |= itemByteLevel;
            }

            if (byteLevel)
                return RobertaPreTokenizer.Instance;

            return PreTokenizer.CreateWhiteSpace(new Dictionary<string, int>());
        }

        private static bool TryGetPreTokenizerSequence(JsonElement preTokenizerElement, out JsonElement preTokenizers)
            => preTokenizerElement.TryGetProperty("pretokenizers", out preTokenizers)
               || preTokenizerElement.TryGetProperty("pre_tokenizers", out preTokenizers);
    }
}

internal sealed class OnnxEmbeddingModelRunner : IEmbeddingModelRunner
{
    private readonly InferenceSession _session;
    private readonly int _dimensions;
    private readonly string _modelPath;
    private readonly string[] _outputNames;

    public OnnxEmbeddingModelRunner(string modelPath, int dimensions)
    {
        _modelPath = modelPath;
        _dimensions = dimensions;
        _session = new InferenceSession(modelPath);
        InputNames = _session.InputMetadata.Keys.ToArray();
        _outputNames = _session.OutputMetadata.Keys.ToArray();
    }

    public IReadOnlyCollection<string> InputNames { get; }

    public float[] Run(IReadOnlyCollection<NamedOnnxValue> inputs, long[] attentionMask, CancellationToken cancellationToken)
    {
        using var runOptions = new RunOptions();
        using var cancellationRegistration = cancellationToken.Register(static state => ((RunOptions)state!).Terminate = true, runOptions);

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var results = _session.Run(inputs, _outputNames, runOptions);
            cancellationToken.ThrowIfCancellationRequested();
            return ExtractEmbedding(results, attentionMask);
        }
        catch (OnnxRuntimeException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Embedding inference was canceled.", ex, cancellationToken);
        }
    }

    public void Dispose() => _session.Dispose();

    private float[] ExtractEmbedding(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, long[] attentionMask)
    {
        var seenOutputs = new List<string>();

        // First pass: prefer direct embeddings (rank 1 or 2) - no pooling needed.
        var directEmbedding = results
            .Select(result =>
            {
                seenOutputs.Add(DescribeOutput(result));
                return TryGetDirectEmbedding(result, out var embedding) ? embedding : null;
            })
            .FirstOrDefault(static embedding => embedding is not null);
        if (directEmbedding is not null)
            return directEmbedding;

        // Second pass: fall back to mean-pooling over a rank-3 hidden-state tensor.
        var pooledEmbedding = results
            .Where(static result =>
                result.Value is Tensor<float> tensor &&
                tensor.Rank == 3 &&
                tensor.Dimensions[0] == 1)
            .Select(result => TryGetPooledEmbedding(result, attentionMask, out var embedding) ? embedding : null)
            .FirstOrDefault(static embedding => embedding is not null);
        if (pooledEmbedding is not null)
            return pooledEmbedding;

        throw new InvalidOperationException($"Embedding model '{_modelPath}' did not return a supported output tensor. Observed outputs: {string.Join("; ", seenOutputs)}.");
    }

    private static string DescribeOutput(DisposableNamedOnnxValue result)
    {
        if (result.Value is Tensor<float> tensor)
            return $"{result.Name}:Tensor<float>[{string.Join(",", tensor.Dimensions.ToArray())}]";

        return $"{result.Name}:{result.Value.GetType().Name}";
    }

    private bool TryGetDirectEmbedding(DisposableNamedOnnxValue result, out float[] embedding)
    {
        if (result.Value is Tensor<float> floatTensor)
        {
            if (floatTensor.Rank == 1)
            {
                embedding = NormalizeDimensions(floatTensor.ToArray());
                return true;
            }

            if (floatTensor.Rank == 2 && floatTensor.Dimensions[0] == 1)
            {
                embedding = NormalizeDimensions(floatTensor.ToArray());
                return true;
            }
        }

        embedding = [];
        return false;
    }

    private bool TryGetPooledEmbedding(DisposableNamedOnnxValue result, long[] attentionMask, out float[] embedding)
    {
        if (result.Value is not Tensor<float> floatTensor || floatTensor.Rank != 3 || floatTensor.Dimensions[0] != 1)
        {
            embedding = [];
            return false;
        }

        var sequenceLength = floatTensor.Dimensions[1];
        var hiddenSize = floatTensor.Dimensions[2];
        var pooled = new float[hiddenSize];
        var tokenCount = 0f;

        for (var tokenIndex = 0; tokenIndex < sequenceLength && tokenIndex < attentionMask.Length; tokenIndex++)
        {
            if (attentionMask[tokenIndex] == 0)
                continue;

            tokenCount += 1f;
            for (var featureIndex = 0; featureIndex < hiddenSize; featureIndex++)
                pooled[featureIndex] += floatTensor[0, tokenIndex, featureIndex];
        }

        if (tokenCount <= 0f)
            tokenCount = 1f;

        for (var featureIndex = 0; featureIndex < pooled.Length; featureIndex++)
            pooled[featureIndex] /= tokenCount;

        embedding = NormalizeDimensions(pooled);
        return true;
    }

    private float[] NormalizeDimensions(float[] values)
    {
        if (values.Length == 0)
            throw new InvalidOperationException($"Embedding model '{_modelPath}' returned an empty output tensor.");
        if (values.Length == _dimensions)
            return values;

        throw new InvalidOperationException(
            $"Embedding model '{_modelPath}' returned {values.Length} dimensions but {_dimensions} were configured. " +
            $"Update the 'Dimensions' setting to match the model output.");
    }
}

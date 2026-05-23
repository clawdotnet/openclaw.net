using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Features;

public sealed class FileEvidenceBundleStore : IEvidenceBundleStore
{
    private readonly string _evidencePath;
    private readonly string _evidencePathPrefix;

    public FileEvidenceBundleStore(string storagePath)
    {
        var root = Path.GetFullPath(storagePath);
        _evidencePath = Path.GetFullPath(Path.Join(root, "harness", "evidence"));
        _evidencePathPrefix = _evidencePath.EndsWith(Path.DirectorySeparatorChar)
            ? _evidencePath
            : _evidencePath + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(_evidencePath);
    }

    public ValueTask SaveAsync(EvidenceBundle bundle, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        EnsureSafeId(bundle.Id);
        return SaveOneAsync(FileForId(bundle.Id), bundle, ct);
    }

    public ValueTask<EvidenceBundle?> GetAsync(string id, CancellationToken ct)
    {
        EnsureSafeId(id);
        return LoadOneAsync(FileForId(id), ct);
    }

    public async ValueTask<IReadOnlyList<EvidenceBundle>> ListAsync(EvidenceBundleListQuery query, CancellationToken ct)
    {
        query ??= new EvidenceBundleListQuery();
        var results = new List<EvidenceBundle>();
        IEnumerable<FileInfo> files;
        try
        {
            files = new DirectoryInfo(_evidencePath).EnumerateFiles("*.json");
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var bundle = await LoadOneAsync(file, ct);
                if (bundle is not null && Matches(bundle, query))
                    results.Add(bundle);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InvalidOperationException ex)
            {
                Trace.TraceWarning("Skipping invalid evidence bundle file '{0}': {1}", file.FullName, ex.Message);
            }
        }

        var limit = Math.Clamp(query.Limit, 1, 5000);
        return results
            .OrderByDescending(static item => item.UpdatedAtUtc)
            .ThenByDescending(static item => item.CreatedAtUtc)
            .Take(limit)
            .ToArray();
    }

    public ValueTask DeleteAsync(string id, CancellationToken ct)
    {
        EnsureSafeId(id);
        var file = FileForId(id);
        if (file.Exists)
            file.Delete();
        return ValueTask.CompletedTask;
    }

    private FileInfo FileForId(string id)
    {
        var expectedFileName = $"{EncodeKey(id)}.json";
        var fileName = Path.GetFileName(expectedFileName);
        if (string.IsNullOrWhiteSpace(fileName) || !string.Equals(fileName, expectedFileName, StringComparison.Ordinal))
            throw new ArgumentException("Evidence bundle id resolves to an unsafe file name.", nameof(id));

        var path = Path.GetFullPath(Path.Join(_evidencePath, fileName));
        if (!path.StartsWith(_evidencePathPrefix, StringComparison.Ordinal))
            throw new ArgumentException("Evidence bundle id resolves outside the evidence store.", nameof(id));

        return new FileInfo(path);
    }

    private static bool Matches(EvidenceBundle bundle, EvidenceBundleListQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.SourceSessionId) &&
            !string.Equals(bundle.SourceSessionId, query.SourceSessionId, StringComparison.Ordinal))
            return false;

        if (!string.IsNullOrWhiteSpace(query.HarnessContractId) &&
            !string.Equals(bundle.HarnessContractId, query.HarnessContractId, StringComparison.Ordinal))
            return false;

        if (!string.IsNullOrWhiteSpace(query.LearningProposalId) &&
            !string.Equals(bundle.LearningProposalId, query.LearningProposalId, StringComparison.Ordinal))
            return false;

        if (!string.IsNullOrWhiteSpace(query.ActorId) &&
            !string.Equals(bundle.ActorId, query.ActorId, StringComparison.Ordinal))
            return false;

        if (!string.IsNullOrWhiteSpace(query.ChannelId) &&
            !string.Equals(bundle.ChannelId, query.ChannelId, StringComparison.Ordinal))
            return false;

        if (!string.IsNullOrWhiteSpace(query.Confidence) &&
            !string.Equals(bundle.Confidence, query.Confidence, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(query.Tag) &&
            (bundle.Tags?.Any(tag => string.Equals(tag, query.Tag, StringComparison.OrdinalIgnoreCase)) != true))
            return false;

        if (query.CreatedFromUtc is { } fromUtc && bundle.CreatedAtUtc < fromUtc)
            return false;

        if (query.CreatedToUtc is { } toUtc && bundle.CreatedAtUtc > toUtc)
            return false;

        return true;
    }

    private static async ValueTask<EvidenceBundle?> LoadOneAsync(FileInfo file, CancellationToken ct)
    {
        if (!file.Exists)
            return default;

        try
        {
            await using var stream = file.OpenRead();
            return await JsonSerializer.DeserializeAsync(stream, CoreJsonContext.Default.EvidenceBundle, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException)
        {
            return default;
        }
        catch (IOException)
        {
            return default;
        }
        catch (UnauthorizedAccessException)
        {
            return default;
        }
    }

    private static async ValueTask SaveOneAsync(FileInfo file, EvidenceBundle bundle, CancellationToken ct)
    {
        file.Directory?.Create();
        var tempFile = new FileInfo($"{file.FullName}.{Guid.NewGuid():N}.tmp");
        var tempPath = tempFile.FullName;
        try
        {
            await using (var stream = tempFile.Open(FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, bundle, CoreJsonContext.Default.EvidenceBundle, ct);
            }

            tempFile.MoveTo(file.FullName, overwrite: true);
        }
        finally
        {
            var cleanupFile = new FileInfo(tempPath);
            if (cleanupFile.Exists)
                cleanupFile.Delete();
        }
    }

    private static void EnsureSafeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Evidence bundle id is required.", nameof(id));

        if (id.Length > 128)
            throw new ArgumentException("Evidence bundle id is too long.", nameof(id));

        if (!id.All(static ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.'))
            throw new ArgumentException("Evidence bundle id contains unsafe characters.", nameof(id));
    }

    private static string EncodeKey(string key)
    {
        var bytes = Encoding.UTF8.GetBytes(key);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}

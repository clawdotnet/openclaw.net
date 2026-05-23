using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Features;

public sealed class FileHarnessContractStore : IHarnessContractStore
{
    private readonly string _contractsPath;
    private readonly string _contractsPathPrefix;

    public FileHarnessContractStore(string storagePath)
    {
        var root = Path.GetFullPath(storagePath);
        _contractsPath = Path.GetFullPath(Path.Join(root, "harness", "contracts"));
        _contractsPathPrefix = _contractsPath.EndsWith(Path.DirectorySeparatorChar)
            ? _contractsPath
            : _contractsPath + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(_contractsPath);
    }

    public ValueTask SaveAsync(HarnessContract contract, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contract);
        EnsureSafeId(contract.Id);
        return SaveOneAsync(FileForId(contract.Id), contract, ct);
    }

    public ValueTask<HarnessContract?> GetAsync(string id, CancellationToken ct)
    {
        EnsureSafeId(id);
        return LoadOneAsync(FileForId(id), ct);
    }

    public async ValueTask<IReadOnlyList<HarnessContract>> ListAsync(HarnessContractListQuery query, CancellationToken ct)
    {
        query ??= new HarnessContractListQuery();
        var results = new List<HarnessContract>();
        IEnumerable<FileInfo> files;
        try
        {
            files = new DirectoryInfo(_contractsPath).EnumerateFiles("*.json");
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
                var contract = await LoadOneAsync(file, ct);
                if (contract is not null && Matches(contract, query))
                    results.Add(contract);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InvalidOperationException ex)
            {
                Trace.TraceWarning("Skipping invalid harness contract file '{0}': {1}", file.FullName, ex.Message);
            }
        }

        var limit = Math.Clamp(query.Limit, 1, 500);
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
            throw new ArgumentException("Harness contract id resolves to an unsafe file name.", nameof(id));

        var path = Path.GetFullPath(Path.Join(_contractsPath, fileName));
        if (!path.StartsWith(_contractsPathPrefix, StringComparison.Ordinal))
            throw new ArgumentException("Harness contract id resolves outside the contract store.", nameof(id));

        return new FileInfo(path);
    }

    private static bool Matches(HarnessContract contract, HarnessContractListQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.Status) &&
            !string.Equals(contract.Status, query.Status, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(query.RiskLevel) &&
            !string.Equals(contract.RiskLevel, query.RiskLevel, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(query.SourceSessionId) &&
            !string.Equals(contract.SourceSessionId, query.SourceSessionId, StringComparison.Ordinal))
            return false;

        if (!string.IsNullOrWhiteSpace(query.ActorId) &&
            !string.Equals(contract.ActorId, query.ActorId, StringComparison.Ordinal))
            return false;

        if (!string.IsNullOrWhiteSpace(query.ChannelId) &&
            !string.Equals(contract.ChannelId, query.ChannelId, StringComparison.Ordinal))
            return false;

        if (!string.IsNullOrWhiteSpace(query.Tag) &&
            (contract.Tags?.Any(tag => string.Equals(tag, query.Tag, StringComparison.OrdinalIgnoreCase)) != true))
            return false;

        if (query.CreatedFromUtc is { } fromUtc && contract.CreatedAtUtc < fromUtc)
            return false;

        if (query.CreatedToUtc is { } toUtc && contract.CreatedAtUtc > toUtc)
            return false;

        return true;
    }

    private static async ValueTask<HarnessContract?> LoadOneAsync(FileInfo file, CancellationToken ct)
    {
        if (!file.Exists)
            return default;

        try
        {
            await using var stream = file.OpenRead();
            return await JsonSerializer.DeserializeAsync(stream, CoreJsonContext.Default.HarnessContract, ct);
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

    private static async ValueTask SaveOneAsync(FileInfo file, HarnessContract contract, CancellationToken ct)
    {
        file.Directory?.Create();
        var tempFile = new FileInfo($"{file.FullName}.{Guid.NewGuid():N}.tmp");
        var tempPath = tempFile.FullName;
        try
        {
            await using (var stream = tempFile.Open(FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, contract, CoreJsonContext.Default.HarnessContract, ct);
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
            throw new ArgumentException("Harness contract id is required.", nameof(id));

        if (id.Length > 128)
            throw new ArgumentException("Harness contract id is too long.", nameof(id));

        if (!id.All(static ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.'))
            throw new ArgumentException("Harness contract id contains unsafe characters.", nameof(id));
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

using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Features;

public sealed class FileSharedHarnessStateStore : ISharedHarnessStateStore
{
    private readonly string _statesPath;
    private readonly string _statesPathPrefix;

    public FileSharedHarnessStateStore(string storagePath)
    {
        var root = Path.GetFullPath(storagePath);
        _statesPath = Path.GetFullPath(Path.Join(root, "harness", "shared-state"));
        _statesPathPrefix = _statesPath.EndsWith(Path.DirectorySeparatorChar)
            ? _statesPath
            : _statesPath + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(_statesPath);
    }

    public ValueTask SaveAsync(SharedHarnessState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureSafeId(state.Id);
        return SaveOneAsync(FileForId(state.Id), state, ct);
    }

    public ValueTask<SharedHarnessState?> GetAsync(string id, CancellationToken ct)
    {
        EnsureSafeId(id);
        return LoadOneAsync(FileForId(id), ct);
    }

    public async ValueTask<SharedHarnessState?> GetBySessionAsync(string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session id is required.", nameof(sessionId));

        var matches = await ListAsync(new SharedHarnessStateListQuery { SessionId = sessionId, Limit = 1 }, ct);
        return matches.FirstOrDefault();
    }

    public async ValueTask<IReadOnlyList<SharedHarnessState>> ListAsync(SharedHarnessStateListQuery query, CancellationToken ct)
    {
        query ??= new SharedHarnessStateListQuery();
        var results = new List<SharedHarnessState>();
        FileInfo[] files;
        try
        {
            files = new DirectoryInfo(_statesPath).EnumerateFiles("*.json").ToArray();
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new IOException($"Shared harness state directory '{_statesPath}' was not found.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException($"Access denied while enumerating shared harness state directory '{_statesPath}'.", ex);
        }
        catch (IOException ex)
        {
            throw new IOException($"Failed to enumerate shared harness state directory '{_statesPath}'.", ex);
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var state = await LoadOneAsync(file, ct);
                if (state is not null && Matches(state, query))
                    results.Add(state);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InvalidOperationException ex)
            {
                Trace.TraceWarning("Skipping invalid shared harness state file '{0}': {1}", file.FullName, ex.Message);
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
            throw new ArgumentException("Shared harness state id resolves to an unsafe file name.", nameof(id));

        var path = Path.GetFullPath(Path.Join(_statesPath, fileName));
        if (!path.StartsWith(_statesPathPrefix, StringComparison.Ordinal))
            throw new ArgumentException("Shared harness state id resolves outside the state store.", nameof(id));

        return new FileInfo(path);
    }

    private static bool Matches(SharedHarnessState state, SharedHarnessStateListQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.SessionId) &&
            !string.Equals(state.SessionId, query.SessionId, StringComparison.Ordinal))
            return false;

        if (!string.IsNullOrWhiteSpace(query.ParentSessionId) &&
            !string.Equals(state.ParentSessionId, query.ParentSessionId, StringComparison.Ordinal))
            return false;

        if (!string.IsNullOrWhiteSpace(query.HarnessContractId) &&
            !string.Equals(state.HarnessContractId, query.HarnessContractId, StringComparison.Ordinal))
            return false;

        if (!string.IsNullOrWhiteSpace(query.Status) &&
            !string.Equals(state.Status, query.Status, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(query.Tag) &&
            (state.Tags?.Any(tag => string.Equals(tag, query.Tag, StringComparison.OrdinalIgnoreCase)) != true))
            return false;

        if (query.CreatedFromUtc is { } fromUtc && state.CreatedAtUtc < fromUtc)
            return false;

        if (query.CreatedToUtc is { } toUtc && state.CreatedAtUtc > toUtc)
            return false;

        return true;
    }

    private static async ValueTask<SharedHarnessState?> LoadOneAsync(FileInfo file, CancellationToken ct)
    {
        if (!file.Exists)
            return default;

        try
        {
            await using var stream = file.OpenRead();
            return await JsonSerializer.DeserializeAsync(stream, CoreJsonContext.Default.SharedHarnessState, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Shared harness state file '{file.FullName}' contains invalid JSON.", ex);
        }
        catch (IOException ex)
        {
            throw new IOException($"Failed to read shared harness state file '{file.FullName}'.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException($"Access denied while reading shared harness state file '{file.FullName}'.", ex);
        }
    }

    private static async ValueTask SaveOneAsync(FileInfo file, SharedHarnessState state, CancellationToken ct)
    {
        file.Directory?.Create();
        var tempFile = new FileInfo($"{file.FullName}.{Guid.NewGuid():N}.tmp");
        var tempPath = tempFile.FullName;
        try
        {
            await using (var stream = tempFile.Open(FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, state, CoreJsonContext.Default.SharedHarnessState, ct);
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
            throw new ArgumentException("Shared harness state id is required.", nameof(id));

        if (id.Length > 128)
            throw new ArgumentException("Shared harness state id is too long.", nameof(id));

        if (!id.All(static ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.'))
            throw new ArgumentException("Shared harness state id contains unsafe characters.", nameof(id));
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

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClaw.Payments.Abstractions;

namespace OpenClaw.Payments.Core;

public interface IPaymentIdempotencyStore
{
    ValueTask<MachinePaymentResult?> TryReplayAsync(
        string scope, string key, string fingerprint, CancellationToken ct);

    ValueTask<MachinePaymentResult> ExecuteOnceAsync(
        string scope, string key, string fingerprint,
        Func<CancellationToken, ValueTask<MachinePaymentResult>> execute, CancellationToken ct);
}

/// <summary>Process-local store for embedded runtimes. Gateways use the durable file store.</summary>
public sealed class InMemoryPaymentIdempotencyStore : IPaymentIdempotencyStore
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private readonly ConcurrentDictionary<string, PaymentIdempotencyRecord> _records = new();
    private long _nextCleanupTimestamp;

    public ValueTask<MachinePaymentResult?> TryReplayAsync(
        string scope, string key, string fingerprint, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CleanupExpiredRecords();
        var id = FilePaymentIdempotencyStore.GetId(scope, key);
        return ValueTask.FromResult(_records.TryGetValue(id, out var existing)
            ? existing.Replay(fingerprint)
            : null);
    }

    public async ValueTask<MachinePaymentResult> ExecuteOnceAsync(
        string scope, string key, string fingerprint,
        Func<CancellationToken, ValueTask<MachinePaymentResult>> execute, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var id = FilePaymentIdempotencyStore.GetId(scope, key);
        CleanupExpiredRecords();
        var pending = new PaymentIdempotencyRecord
        {
            Fingerprint = fingerprint,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        if (!_records.TryAdd(id, pending))
            return _records[id].Replay(fingerprint);

        // Never release a reservation after dispatch, even on timeout or cancellation.
        var result = await execute(ct);
        _records[id] = pending with { Result = result, CompletedAtUtc = DateTimeOffset.UtcNow };
        return result;
    }

    private void CleanupExpiredRecords()
    {
        var now = DateTimeOffset.UtcNow;
        var nextCleanup = Interlocked.Read(ref _nextCleanupTimestamp);
        if (nextCleanup > now.UtcTicks ||
            Interlocked.CompareExchange(ref _nextCleanupTimestamp, now.AddHours(1).UtcTicks, nextCleanup) != nextCleanup)
        {
            return;
        }

        foreach (var (id, record) in _records)
        {
            if (record.CompletedAtUtc is { } completedAt && completedAt <= now - Retention)
                _records.TryRemove(new KeyValuePair<string, PaymentIdempotencyRecord>(id, record));
        }
    }
}

/// <summary>
/// Durable at-most-once dispatch on a local filesystem, shared by gateway processes.
/// An unfinished reservation requires provider reconciliation, never blind re-execution.
/// </summary>
public sealed class FilePaymentIdempotencyStore : IPaymentIdempotencyStore
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private readonly string _directory;
    private readonly string _coordinationMutexName;
    private long _nextCleanupTimestamp;

    public FilePaymentIdempotencyStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        var coordinationPath = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? _directory.ToUpperInvariant()
            : _directory;
        _coordinationMutexName = "OpenClaw.PaymentIdempotency." +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(coordinationPath)));
    }

    public async ValueTask<MachinePaymentResult?> TryReplayAsync(
        string scope, string key, string fingerprint, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(_directory))
            return null;
        CleanupExpiredRecords();
        var path = GetRecordPath(scope, key);
        return File.Exists(path) ? await ReadReplayAsync(path, fingerprint, ct) : null;
    }

    public async ValueTask<MachinePaymentResult> ExecuteOnceAsync(
        string scope, string key, string fingerprint,
        Func<CancellationToken, ValueTask<MachinePaymentResult>> execute, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        EnsureStorageDirectory();
        CleanupExpiredRecords();
        var path = GetRecordPath(scope, key);
        var pending = new PaymentIdempotencyRecord
        {
            Fingerprint = fingerprint,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        var temporary = await WriteTemporaryAsync(pending, ct);
        try
        {
            // Publication is atomic and exclusive: the complete, flushed temporary
            // file becomes visible only if the key is still absent.
            var published = UnderCoordinationLock(() => DirectoryDurability.TryPublishExclusive(temporary, path));
            if (!published)
                return await ReadReplayAsync(path, fingerprint, ct);
            DirectoryDurability.Flush(_directory);

            var result = await execute(ct);
            // Persist a known result even if the HTTP caller disconnected after dispatch.
            var completed = await WriteTemporaryAsync(pending with
            {
                Result = result,
                CompletedAtUtc = DateTimeOffset.UtcNow
            }, CancellationToken.None);
            try
            {
                DirectoryDurability.Replace(completed, path);
                DirectoryDurability.Flush(_directory);
            }
            finally
            {
                File.Delete(completed);
            }
            return result;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal static string GetId(string scope, string key)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{scope.Length}:{scope}{key}")));

    private string GetRecordPath(string scope, string key)
        => Path.Join(_directory, GetId(scope, key) + ".json");

    private async Task<MachinePaymentResult> ReadReplayAsync(string path, string fingerprint, CancellationToken ct)
    {
        var json = await ReadAllTextAllowDeleteAsync(path, ct);
        PaymentIdempotencyRecord? existing;
        try
        {
            existing = JsonSerializer.Deserialize(json, PaymentIdempotencyJsonContext.Default.PaymentIdempotencyRecord);
        }
        catch (JsonException)
        {
            throw new PaymentIdempotencyException("payment_outcome_unknown",
                "Payment record is unreadable. Reconcile with the provider before making another payment.");
        }

        return (existing ?? throw new PaymentIdempotencyException(
            "payment_outcome_unknown", "Payment record is empty.")).Replay(fingerprint);
    }

    private void EnsureStorageDirectory()
    {
        if (Directory.Exists(_directory))
            return;

        var missing = new Stack<string>();
        var current = new DirectoryInfo(_directory);
        while (!current.Exists)
        {
            missing.Push(current.FullName);
            current = current.Parent ?? throw new DirectoryNotFoundException(
                $"Payment idempotency storage path has no existing parent: {_directory}");
        }

        while (missing.TryPop(out var path))
        {
            Directory.CreateDirectory(path);
            DirectoryDurability.Flush(Directory.GetParent(path)!.FullName);
            DirectoryDurability.Flush(path);
        }
    }

    private void CleanupExpiredRecords()
    {
        var now = DateTimeOffset.UtcNow;
        var nextCleanup = Interlocked.Read(ref _nextCleanupTimestamp);
        if (nextCleanup > now.UtcTicks ||
            Interlocked.CompareExchange(ref _nextCleanupTimestamp, now.AddHours(1).UtcTicks, nextCleanup) != nextCleanup)
        {
            return;
        }

        UnderCoordinationLock(() =>
        {
            var changed = false;
            foreach (var path in Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var record = JsonSerializer.Deserialize(
                        ReadAllTextAllowDelete(path), PaymentIdempotencyJsonContext.Default.PaymentIdempotencyRecord);
                    if (record?.CompletedAtUtc is { } completedAt && completedAt <= now - Retention)
                    {
                        File.Delete(path);
                        changed = true;
                    }
                }
                catch (IOException)
                {
                    // Retention is best effort. Never delete an unreadable or inaccessible payment record.
                }
                catch (UnauthorizedAccessException)
                {
                    // Retention is best effort. Never delete an unreadable or inaccessible payment record.
                }
                catch (JsonException)
                {
                    // Retention is best effort. Never delete an unreadable or inaccessible payment record.
                }
            }

            if (changed)
                DirectoryDurability.Flush(_directory);
            return true;
        });
    }

    private T UnderCoordinationLock<T>(Func<T> action)
    {
        using var mutex = new Mutex(initiallyOwned: false, _coordinationMutexName);
        var ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = mutex.WaitOne();
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }
            return action();
        }
        finally
        {
            if (ownsMutex)
                mutex.ReleaseMutex();
        }
    }

    private static async Task<string> ReadAllTextAllowDeleteAsync(string path, CancellationToken ct)
    {
        await using var stream = OpenReadAllowDelete(path, FileOptions.Asynchronous);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct);
    }

    private static string ReadAllTextAllowDelete(string path)
    {
        using var stream = OpenReadAllowDelete(path, FileOptions.None);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static FileStream OpenReadAllowDelete(string path, FileOptions options)
        => new(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read | FileShare.Delete,
            Options = options
        });

    private async Task<string> WriteTemporaryAsync(PaymentIdempotencyRecord record, CancellationToken ct)
    {
        var temporary = Path.Join(_directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using var stream = new FileStream(temporary, options);
            await JsonSerializer.SerializeAsync(stream, record, PaymentIdempotencyJsonContext.Default.PaymentIdempotencyRecord, ct);
            stream.Flush(flushToDisk: true);
            return temporary;
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }
}

internal sealed record PaymentIdempotencyRecord
{
    public required string Fingerprint { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public MachinePaymentResult? Result { get; init; }

    public MachinePaymentResult Replay(string fingerprint)
    {
        if (!string.Equals(Fingerprint, fingerprint, StringComparison.Ordinal))
            throw new PaymentIdempotencyException("idempotency_conflict", "Idempotency key was already used for a different payment request.");
        return Result ?? throw new PaymentIdempotencyException("payment_outcome_unknown",
            "Payment is in progress or its outcome is unknown. Reconcile with the provider before making another payment. Do not retry with a new key.");
    }
}

internal static partial class DirectoryDurability
{
    private const uint MoveFileReplaceExisting = 0x00000001;
    private const uint MoveFileWriteThrough = 0x00000008;

    internal static void Flush(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        var descriptor = Open(path, 0);
        if (descriptor < 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to open directory '{path}' for durable payment storage.");
        try
        {
            if (Fsync(descriptor) != 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to flush directory '{path}' for durable payment storage.");
        }
        finally
        {
            Close(descriptor);
        }
    }

    internal static bool TryPublishExclusive(string existingPath, string newPath)
    {
        var created = OperatingSystem.IsWindows()
            ? MoveFileEx(existingPath, newPath, MoveFileWriteThrough)
            : Link(existingPath, newPath) == 0;
        if (created)
            return true;
        if (File.Exists(newPath))
            return false;
        throw new Win32Exception(Marshal.GetLastWin32Error(),
            $"Failed to publish durable payment reservation '{newPath}'.");
    }

    internal static void Replace(string existingPath, string newPath)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!MoveFileEx(existingPath, newPath, MoveFileReplaceExisting | MoveFileWriteThrough))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"Failed to publish completed payment record '{newPath}'.");
            }
            return;
        }

        File.Move(existingPath, newPath, overwrite: true);
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int descriptor);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int descriptor);

    [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Link(string existingPath, string newPath);

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(string existingFileName, string newFileName, uint flags);
}

public sealed class PaymentIdempotencyException(string code, string message, Exception? innerException = null) : InvalidOperationException(message, innerException)
{
    public string Code { get; } = code;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PaymentIdempotencyRecord))]
internal partial class PaymentIdempotencyJsonContext : JsonSerializerContext;

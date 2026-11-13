using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClaw.Payments.Abstractions;

namespace OpenClaw.Payments.Core;

public interface IPaymentIdempotencyStore
{
    ValueTask<MachinePaymentResult> ExecuteOnceAsync(
        string scope, string key, string fingerprint,
        Func<CancellationToken, ValueTask<MachinePaymentResult>> execute, CancellationToken ct);
}

/// <summary>Process-local store for embedded runtimes. Gateways use the durable file store.</summary>
public sealed class InMemoryPaymentIdempotencyStore : IPaymentIdempotencyStore
{
    private readonly ConcurrentDictionary<string, PaymentIdempotencyRecord> _records = new();

    public async ValueTask<MachinePaymentResult> ExecuteOnceAsync(
        string scope, string key, string fingerprint,
        Func<CancellationToken, ValueTask<MachinePaymentResult>> execute, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var id = FilePaymentIdempotencyStore.GetId(scope, key);
        var pending = new PaymentIdempotencyRecord { Fingerprint = fingerprint };
        if (!_records.TryAdd(id, pending))
            return _records[id].Replay(fingerprint);

        // Never release a reservation after dispatch, even on timeout or cancellation.
        var result = await execute(ct);
        _records[id] = pending with { Result = result };
        return result;
    }
}

/// <summary>
/// Durable at-most-once dispatch on a local filesystem, shared by gateway processes.
/// An unfinished reservation requires provider reconciliation, never blind re-execution.
/// </summary>
public sealed class FilePaymentIdempotencyStore(string directory) : IPaymentIdempotencyStore
{
    public async ValueTask<MachinePaymentResult> ExecuteOnceAsync(
        string scope, string key, string fingerprint,
        Func<CancellationToken, ValueTask<MachinePaymentResult>> execute, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, GetId(scope, key) + ".json");
        var pending = new PaymentIdempotencyRecord { Fingerprint = fingerprint };
        var temporary = await WriteTemporaryAsync(pending, ct);
        try
        {
            try
            {
                // Atomic create, including across processes. Readers never see partial JSON.
                File.Move(temporary, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                var json = await File.ReadAllTextAsync(path, ct);
                PaymentIdempotencyRecord? existing;
                try
                {
                    existing = JsonSerializer.Deserialize(json, PaymentIdempotencyJsonContext.Default.PaymentIdempotencyRecord);
                }
                catch (JsonException)
                {
                    throw new PaymentIdempotencyException("payment_outcome_unknown", "Payment record is unreadable. Reconcile with the provider before making another payment.");
                }
                return (existing ?? throw new PaymentIdempotencyException("payment_outcome_unknown", "Payment record is empty.")).Replay(fingerprint);
            }

            var result = await execute(ct);
            // Persist a known result even if the HTTP caller disconnected after dispatch.
            var completed = await WriteTemporaryAsync(pending with { Result = result }, CancellationToken.None);
            try
            {
                File.Move(completed, path, overwrite: true);
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

    private async Task<string> WriteTemporaryAsync(PaymentIdempotencyRecord record, CancellationToken ct)
    {
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
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
    public MachinePaymentResult? Result { get; init; }

    public MachinePaymentResult Replay(string fingerprint)
    {
        if (!string.Equals(Fingerprint, fingerprint, StringComparison.Ordinal))
            throw new PaymentIdempotencyException("idempotency_conflict", "Idempotency key was already used for a different payment request.");
        return Result ?? throw new PaymentIdempotencyException("payment_outcome_unknown",
            "Payment is in progress or its outcome is unknown. Reconcile with the provider before making another payment. Do not retry with a new key.");
    }
}

public sealed class PaymentIdempotencyException(string code, string message, Exception? innerException = null) : InvalidOperationException(message, innerException)
{
    public string Code { get; } = code;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PaymentIdempotencyRecord))]
internal partial class PaymentIdempotencyJsonContext : JsonSerializerContext;

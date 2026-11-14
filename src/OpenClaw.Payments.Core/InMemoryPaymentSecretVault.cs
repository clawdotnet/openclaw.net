using System.Collections.Concurrent;
using System.Linq;
using OpenClaw.Payments.Abstractions;

namespace OpenClaw.Payments.Core;

public sealed class InMemoryPaymentSecretVault : IPaymentSecretVault
{
    private sealed class Entry
    {
        public required PaymentSecret Secret { get; init; }
        public required DateTimeOffset ExpiresAtUtc { get; init; }
        public bool RetrieveOnce { get; init; }
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public ValueTask<string> StoreAsync(PaymentSecret secret, TimeSpan ttl, bool retrieveOnce, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ct.ThrowIfCancellationRequested();

        CleanupExpired(DateTimeOffset.UtcNow);
        var expiresAt = DateTimeOffset.UtcNow.Add(ttl <= TimeSpan.Zero ? TimeSpan.FromMinutes(5) : ttl);
        _entries[secret.HandleId] = new Entry
        {
            Secret = secret,
            ExpiresAtUtc = expiresAt,
            RetrieveOnce = retrieveOnce
        };
        return ValueTask.FromResult(secret.HandleId);
    }

    public ValueTask<PaymentSecret?> TryRetrieveAsync(string handleId, string purpose, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(handleId))
            return ValueTask.FromResult<PaymentSecret?>(null);

        ct.ThrowIfCancellationRequested();
        if (!_entries.TryGetValue(handleId, out var entry))
            return ValueTask.FromResult<PaymentSecret?>(null);

        if (entry.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            if (_entries.TryRemove(new KeyValuePair<string, Entry>(handleId, entry)))
                entry.Secret.Clear();
            return ValueTask.FromResult<PaymentSecret?>(null);
        }

        if (entry.RetrieveOnce && !_entries.TryRemove(new KeyValuePair<string, Entry>(handleId, entry)))
            return ValueTask.FromResult<PaymentSecret?>(null);

        return ValueTask.FromResult<PaymentSecret?>(entry.Secret);
    }

    public ValueTask RevokeAsync(string handleId, string reason, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(handleId) && _entries.TryRemove(handleId, out var entry))
            entry.Secret.Clear();

        return ValueTask.CompletedTask;
    }

    private void CleanupExpired(DateTimeOffset nowUtc)
    {
        foreach (var item in _entries.Where(item => item.Value.ExpiresAtUtc <= nowUtc))
        {
            if (_entries.TryRemove(item))
                item.Value.Secret.Clear();
        }
    }
}

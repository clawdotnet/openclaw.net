using System.Security.Cryptography;
using System.Text;
using OpenClaw.Core.Models;
namespace OpenClaw.Gateway;

internal sealed class DeviceEnrollmentService(OperatorAccountService accounts, TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private sealed record Pending(string AccountId, string Name, DateTimeOffset Expires, string SecurityRevision);

    public DeviceEnrollmentCode Create(DeviceEnrollmentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceName) || request.DeviceName.Length > 80)
            throw new ArgumentException("A device name of 1–80 characters is required.");
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            foreach (var key in _pending.Where(x => x.Value.Expires <= now).Select(x => x.Key).ToArray()) _pending.Remove(key);
            if (_pending.Count >= 100) throw new InvalidOperationException("Too many pending enrollments.");
            var account = accounts.GetEnrollmentSecuritySnapshot(request.AccountId);
            if (account is null) throw new ArgumentException("An enabled operator account is required.");
            var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var expires = now.AddMinutes(5);
            _pending.Add(Hash(code), new(account.Value.Id, request.DeviceName.Trim(), expires, account.Value.Revision));
            return new(code, expires);
        }
    }

    public OperatorAccountTokenCreateResponse? Exchange(string code)
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            if (string.IsNullOrWhiteSpace(code) || code.Length > 64 ||
                !_pending.TryGetValue(Hash(code.Trim().ToUpperInvariant()), out var pending)) return null;
            if (pending.Expires <= now) { _pending.Remove(Hash(code.Trim().ToUpperInvariant())); return null; }
            var result = accounts.CreateEnrollmentToken(pending.AccountId, pending.Name, pending.SecurityRevision, now.AddDays(30));
            // A security mismatch permanently invalidates this code, even if the
            // account's role or enabled state is restored before its expiry.
            _pending.Remove(Hash(code.Trim().ToUpperInvariant()));
            return result;
        }
    }
    private static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
}

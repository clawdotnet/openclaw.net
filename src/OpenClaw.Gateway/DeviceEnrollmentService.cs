using System.Security.Cryptography;
using System.Text;
using OpenClaw.Core.Models;
namespace OpenClaw.Gateway;

internal sealed class DeviceEnrollmentService(OperatorAccountService accounts, TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _attempts = new();
    private sealed record Pending(string AccountId, string Name, DateTimeOffset Expires, DateTimeOffset AccountRevision);

    public DeviceEnrollmentCode Create(DeviceEnrollmentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceName) || request.DeviceName.Length > 80)
            throw new ArgumentException("A device name of 1–80 characters is required.");
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            foreach (var key in _pending.Where(x => x.Value.Expires <= now).Select(x => x.Key).ToArray()) _pending.Remove(key);
            if (_pending.Count >= 100) throw new InvalidOperationException("Too many pending enrollments.");
            var account = accounts.Get(request.AccountId)?.Account;
            if (account is not { Enabled: true }) throw new ArgumentException("An enabled operator account is required.");
            var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var expires = now.AddMinutes(5);
            _pending.Add(Hash(code), new(account.Id, request.DeviceName.Trim(), expires, account.UpdatedAtUtc));
            return new(code, expires);
        }
    }

    public OperatorAccountTokenCreateResponse? Exchange(string code)
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            while (_attempts.TryPeek(out var at) && at <= now.AddMinutes(-1)) _attempts.Dequeue();
            if (_attempts.Count >= 30) throw new InvalidOperationException("Enrollment rate limit exceeded. Try again in one minute.");
            _attempts.Enqueue(now);
            if (string.IsNullOrWhiteSpace(code) || code.Length > 64 || !_pending.Remove(Hash(code.Trim().ToUpperInvariant()), out var pending) || pending.Expires <= now) return null;
            return accounts.CreateEnrollmentToken(pending.AccountId, pending.Name, pending.AccountRevision, now.AddDays(30));
        }
    }
    private static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
}
